using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;

namespace GeorgeValdez.Edge510Installer;

public partial class MainWindow : Window
{
    private static readonly string AppFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GeorgeValdez", "Edge510OPNsense");
    private const string OfficialMirror = "https://pkg.opnsense.org";
    private const string PinnedVersion = "26.7";
    private const string PinnedPublicKey = """
-----BEGIN PUBLIC KEY-----
MIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAziSNKuzrL2cwLx5LXmLn
cWS5Lk+i9CzRMXO/4xQYBQCaSnd8GBg/HA/g4aPoTUa6ovAI0AHfW8KQJQyBkFzn
pi6MLZJ9tEaFcn0CiV+tSTJd1RV4bB8jtpKl5oTkgFrPsyaB7iBlG5Cd49VCW19h
DxClQ24lkWkVoYfsfCQEt4ADNGLygWCPyf4bxGD/t6/tiW9SsOs2+gfOZ9C/G2d/
EBhJFoBEoz5lvULVxTdfY5PScYrHD/waZnk3rGc2A+9pI/SM2JAwKqsgZ6MSFbXO
DNocSjqFUUkdqhty+Qcc0OJ+hMbKKVE+f3QJBQIwT3ayys8QK0m5CCo91/f+DjoN
noj+t5YN9x8GREkF0wrdIi7hevkwrL2/SJQbq1bL1BLB+mMSXYR611lgT8YfYjyZ
7tmpNVC3O5Pj7l20snm1lVUSqS0PsFBvh6HQtBRwQDGppaIIhH1Nt9yIatmSiGZt
2YrMVNBzbQrJzSX+vWcAulkaPIt4t+XxmpO5IDNZ+4uMZ7XyJq1lAhIeyXx+Falf
v7S+ZpJWFVNz0/N5z6lBbADD855i+gFY6B5209xGyhd6FwaPOjISgQKkgBwF1AiW
MDuTuP9lkh/U5gGBZIFTnbdEMgOAL4P+Hsw9Nozav+3QIpiU3Pv9F29a1erCkq09
rpQyNglY7Jqme/RipzbYia8CAwEAAQ==
-----END PUBLIC KEY-----
""";

    private int _page;
    private List<DiskInfo> _disks = [];
    private DiskInfo? _selectedDisk;
    private string? _imageFile;
    private string? _imageSignatureFile;
    private bool _imageVerified;
    private string _verifiedImageHash = "";
    private SerialPort? _serial;
    private string _targetSerial = "";
    private ComboBox? _diskPicker;
    private TextBox? _serialId;
    private ComboBox? _comPicker;
    private TextBox? _serialLog;
    private TextBox? _mediaStatus;

    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (internalBusy)
            {
                e.Cancel = true;
                SetStatus("Espera a que termine la operación del disco interno.");
            }
            else internalInstaller?.Dispose();
        };
        Directory.CreateDirectory(AppFolder);
        ShowPage(0);
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && int.TryParse(Convert.ToString(b.Tag), out var page)) ShowPage(page);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowPage(Math.Max(0, _page - 1));
    private void Next_Click(object sender, RoutedEventArgs e) => ShowPage(Math.Min(5, _page + 1));

    private void ShowPage(int page)
    {
        if (internalBusy) return;
        _page = page;
        var titles = new[] { "Antes de empezar", "Conectar y diagnosticar", "Revisar el firmware", "Preparar OPNsense", "Crear el USB", "Convertir el appliance" };
        Eyebrow.Text = $"PASO {page + 1} DE 6";
        PageTitle.Text = titles[page];
        BackButton.IsEnabled = page > 0;
        NextButton.IsEnabled = page < 5;
        NextButton.Content = page == 4 ? "Ver pasos del Edge" : page == 5 ? "Final" : "Siguiente";
        var nav = new[] { Nav1, Nav2, Nav3, Nav4, Nav5, Nav6 };
        var dots = new[] { Dot1, Dot2, Dot3, Dot4, Dot5, Dot6 };
        for (var i = 0; i < nav.Length; i++)
        {
            nav[i].Foreground = i == page ? Brushes.White : new SolidColorBrush(Color.FromRgb(199, 208, 211));
            dots[i].Background = i == page ? new SolidColorBrush(Color.FromRgb(11, 130, 116)) : new SolidColorBrush(Color.FromRgb(52, 69, 76));
        }
        PageHost.Children.Clear();
        switch (page)
        {
            case 0: BuildStartPage(); break;
            case 1: BuildSerialPage(); break;
            case 2: BuildFirmwarePage(); break;
            case 3: BuildImagePage(); break;
            case 4: BuildUsbPage(); break;
            case 5: BuildConversionPage(); break;
        }
        StatusText.Text = page switch
        {
            0 => "Primero confirmaremos qué USB es cuál.",
            1 => "La consola solo escucha hasta que elijas capturar diagnóstico.",
            2 => "La revisión protege el firmware y los datos del appliance.",
            3 => _imageVerified ? "Imagen verificada con clave de publicación fijada." : "La imagen todavía no está verificada.",
            4 => "La grabación borra únicamente el USB seleccionado.",
            _ => "Respaldar, instalar y verificar el arranque desde la memoria interna."
        };
    }

    private void BuildStartPage()
    {
        PageHost.Children.Add(Card("Qué hará este asistente", "Te guiará para revisar el PC, identificar el USB, observar la consola serial, preparar una imagen oficial verificada y después convertir el Edge con controles. No cambia nada hasta que la pantalla de grabación te pida confirmar el borrado del USB."));
        PageHost.Children.Add(Notice("Seguridad primero", "El firmware y la memoria interna del Edge no se modificarán en esta fase. Primero veremos la identidad exacta, la compatibilidad y las opciones reales de recuperación.", "#E3F4F0", "#0B675D"));
        var card = NewCard();
        AddTitle(card, "1 · Comprobar el PC y localizar el USB");
        AddText(card, "Conecta la memoria de instalación al PC. Pulsa el botón y compara el modelo, la capacidad y el número de serie con la etiqueta de la unidad.", true);
        _diskPicker = new ComboBox { MinHeight = 38, FontSize = 14, Margin = new Thickness(0, 12, 0, 10), DisplayMemberPath = "Display" };
        card.Children.Add(_diskPicker);
        AddText(card, "Serie de USB esperada (la comprobamos antes de grabar)", false, 11, "#718096");
        _serialId = new TextBox { Text = _targetSerial, MinHeight = 38, FontSize = 14, Padding = new Thickness(10, 8, 10, 8), ToolTip = "Escribe el número de serie que muestra la etiqueta o la información del fabricante" };
        _serialId.TextChanged += (_, _) => { _targetSerial = _serialId.Text.Trim(); };
        card.Children.Add(_serialId);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var scan = PrimaryButton("Analizar PC y unidades USB");
        scan.Click += async (_, _) => await ScanDisksAsync();
        row.Children.Add(scan);
        var refresh = QuietButton("Actualizar puertos COM");
        refresh.Click += (_, _) => { if (_comPicker is not null) LoadComPorts(_comPicker); SetStatus("Lista de puertos COM actualizada."); };
        row.Children.Add(refresh);
        card.Children.Add(row);
        PageHost.Children.Add(card);
        if (_disks.Count > 0 && _diskPicker is not null) BindDisks(_diskPicker);
        if (!string.IsNullOrWhiteSpace(_selectedDisk?.Serial)) _serialId.Text = _selectedDisk.Serial;
        PageHost.Children.Add(Notice("Tu información privada", "El informe se guarda únicamente en este PC, dentro de %LOCALAPPDATA%\\GeorgeValdez\\Edge510OPNsense. No se envía a ninguna página web.", "#EEF2FF", "#334E86"));
    }

    private void BuildSerialPage()
    {
        PageHost.Children.Add(Card("Consola serial COM5 · 115200 8N1", "Elige el puerto que ya usas para ver `vc-edge login:`. Al conectar, la aplicación solo recibe texto; no inicia sesión ni envía contraseñas."));
        var card = NewCard();
        AddTitle(card, "Conexión de solo escucha");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _comPicker = new ComboBox { Width = 200, MinHeight = 38, FontSize = 14, Margin = new Thickness(0, 0, 10, 0) };
        LoadComPorts(_comPicker);
        row.Children.Add(_comPicker);
        var connect = PrimaryButton(_serial?.IsOpen == true ? "Desconectar" : "Conectar y escuchar");
        connect.Click += (_, _) => ToggleSerial(connect);
        row.Children.Add(connect);
        card.Children.Add(row);
        _serialLog = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 245, FontFamily = new FontFamily("Consolas"), FontSize = 12, Background = new SolidColorBrush(Color.FromRgb(20, 33, 39)), Foreground = new SolidColorBrush(Color.FromRgb(213, 230, 226)), Padding = new Thickness(13), Margin = new Thickness(0, 15, 0, 8) };
        _serialLog.Text = "La salida de la consola aparecerá aquí cuando conectes COM5.\r\n";
        card.Children.Add(_serialLog);
        var capture = QuietButton("Ya inicié sesión: capturar inventario de solo lectura");
        capture.Margin = new Thickness(0, 4, 0, 0);
        capture.Click += async (_, _) => await CaptureInventoryAsync();
        card.Children.Add(capture);
        PageHost.Children.Add(card);
        var loginHelp = new Expander
        {
            Header = "¿Cómo inicio sesión en mi Edge 510?",
            Margin = new Thickness(0, 0, 0, 14),
            Content = Card("Acceso local de fábrica",
                "Si tu unidad conserva las credenciales de fábrica, el usuario es root y la contraseña sigue el formato VeloHelloXXX. Sustituye XXX por los últimos tres caracteres del número de serie de la etiqueta inferior del Edge. Si ya cambiaste la contraseña, usa la actual. Para iniciar sesión en otra terminal, desconecta aquí el puerto COM y vuelve a conectarlo después.")
        };
        PageHost.Children.Add(loginHelp);
        PageHost.Children.Add(Notice("Antes de capturar", "Para consultar CPU, memoria, discos, BIOS y tarjetas de red debes iniciar sesión con una cuenta autorizada en la consola. Hazlo tú mismo en el Edge; la app no conoce ni guarda la contraseña. El comando enviado después es solo de consulta.", "#FFF4DE", "#805A16"));
    }

    private void BuildFirmwarePage()
    {
        PageHost.Children.Add(Card("Perfil del Edge 510",
            "La instalación interna comprueba automáticamente la placa EDGE510, el BIOS VELOCLOUD-EDGE-01.00.00.05 y la memoria Generic Ultra HS-COMBO de 7,125 GiB. También exige que el sistema haya arrancado desde el USB de instalación."));
        PageHost.Children.Add(Card("Preparación del hardware",
            "La consola del Edge usa UART1 a 0x2F8 y 115200 8N1. El asistente adapta la imagen Serial para esa consola. Antes del respaldo y la instalación, guarda los valores originales del watchdog del PIC, lo desactiva y verifica su lectura. El instalador UFS y el servicio de luces quedan preparados para el arranque interno."));
        PageHost.Children.Add(Card("Qué conservar",
            "El paso de instalación guarda el disco de fábrica completo, su SHA-256, la configuración original del PIC y los registros de la sesión. Consérvalos fuera del repositorio público porque pueden contener información privada de tu unidad."));
    }

    private void BuildImagePage()
    {
        PageHost.Children.Add(Card("Imagen serial oficial", "El Edge ya se administra por consola serial a 115200. Por eso el candidato para arrancar la instalación es la imagen `serial-amd64` de OPNsense. La app consulta la versión estable publicada y comprueba la firma del archivo antes de permitir grabarlo."));
        var card = NewCard();
        AddTitle(card, "Descargar y comprobar");
        AddText(card, "Origen: pkg.opnsense.org · arquitectura amd64 · consola 115200. La firma RSA se verifica con la clave pública de la versión fijada en esta compilación.", true);
        var download = PrimaryButton(_imageVerified ? "Imagen verificada · descargar de nuevo" : "Descargar y verificar imagen oficial");
        download.Margin = new Thickness(0, 15, 0, 12);
        download.Click += async (_, _) => await DownloadAndVerifyAsync();
        card.Children.Add(download);
        _mediaStatus = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, Padding = new Thickness(12), Background = new SolidColorBrush(Color.FromRgb(246, 248, 249)), BorderBrush = new SolidColorBrush(Color.FromRgb(227, 233, 235)), FontSize = 12, Text = _imageVerified ? $"Verificada: {_imageFile}" : "Aún no se ha descargado la imagen.\r\nNo se grabará un archivo cuya versión, clave o firma no coincida." };
        card.Children.Add(_mediaStatus);
        PageHost.Children.Add(card);
        PageHost.Children.Add(Notice("Acerca del tamaño del disco interno", "Este paso prepara solo el USB. Antes de instalar en la memoria del Edge comprobaremos la capacidad real y la salud del almacenamiento. Si la unidad interna es de 8 GB, no asumir que la imagen completa cabe: la documentación de OPNsense recomienda más espacio y el perfil embebido requiere compatibilidad confirmada.", "#FFF4DE", "#805A16"));
    }

    private void BuildUsbPage()
    {
        PageHost.Children.Add(Card("Crear la memoria de arranque", "Al grabar la imagen se borrará el USB elegido. La aplicación revisa otra vez que sea extraíble, de tipo USB, coincida el número de serie y tenga espacio. Luego lee los datos escritos para comprobarlos."));
        var card = NewCard();
        AddTitle(card, "Unidad que va a borrarse");
        AddText(card, _selectedDisk is null ? "Vuelve al paso 1, analiza las unidades y selecciona el USB." : _selectedDisk.Display, true);
        var write = PrimaryButton("Grabar imagen en el USB");
        write.Margin = new Thickness(0, 14, 0, 0);
        write.IsEnabled = _imageVerified && _selectedDisk is not null && string.Equals(_targetSerial, _selectedDisk.Serial, StringComparison.OrdinalIgnoreCase);
        write.Click += async (_, _) => await WriteUsbAsync();
        card.Children.Add(write);
        PageHost.Children.Add(card);
        PageHost.Children.Add(Notice("Antes de pulsar grabar", "Desconecta los demás discos externos. Cierra el Explorador si tiene abierta la memoria. La siguiente pantalla mostrará de nuevo modelo, serie y capacidad y te pedirá escribir la frase exacta de borrado.", "#FCEBEA", "#9B3B35"));
    }

    private bool internalBusy;
    private InternalInstaller? internalInstaller;

    private void BuildConversionPage()
    {
        PageHost.Children.Add(Card("Instalar en la memoria interna",
            "Arranca el Edge desde el USB Serial y conéctalo a una red de administración. Preparar comprueba la placa y el destino, guarda la configuración del PIC, desactiva su watchdog y respalda el disco. Instalar borra el destino confirmado y ejecuta el instalador oficial UFS. Al terminar, retira el USB del Edge apagado y vuelve a encenderlo."));
        var card = NewCard();
        AddTitle(card, "Conectar con OPNsense");
        var address = new TextBox { Text = "192.168.1.1", MinHeight = 32, Margin = new Thickness(0, 5, 0, 8) };
        card.Children.Add(new TextBlock { Text = "Dirección IP del Edge" });
        card.Children.Add(address);
        var password = new PasswordBox { Password = "opnsense", MinHeight = 32, Margin = new Thickness(0, 5, 0, 8) };
        card.Children.Add(new TextBlock { Text = "Contraseña de root del entorno live" });
        card.Children.Add(password);
        var pcNetwork = new CheckBox
        {
            Content = "Usar gateway y DNS del PC y conservar el DHCP existente de la LAN",
            IsChecked = true,
            Margin = new Thickness(0, 5, 0, 10)
        };
        card.Children.Add(pcNetwork);
        var report = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
        var prepare = PrimaryButton("Preparar Edge y guardar respaldo");
        var install = PrimaryButton("Instalar OPNsense dentro del Edge");
        var verify = PrimaryButton("Verificar el arranque interno sin USB");
        verify.IsEnabled = false;
        install.IsEnabled = false;
        var confirmation = new CheckBox
        {
            Content = "Autorizo borrar el disco interno identificado, después de verificar el respaldo.",
            Margin = new Thickness(0, 10, 0, 10)
        };
        var ready = false;
        confirmation.Checked += (_, _) => install.IsEnabled = ready && !internalBusy;
        confirmation.Unchecked += (_, _) => install.IsEnabled = false;
        prepare.Click += async (_, _) =>
        {
            if (internalBusy) return;
            if (!System.Net.IPAddress.TryParse(address.Text.Trim(), out _))
            {
                SetStatus("Introduce una dirección IP válida.");
                return;
            }
            var host = address.Text.Trim();
            var secret = password.Password;
            var usePcNetwork = pcNetwork.IsChecked == true;
            password.Clear();
            internalBusy = true;
            prepare.IsEnabled = false;
            address.IsEnabled = false;
            password.IsEnabled = false;
            pcNetwork.IsEnabled = false;
            try
            {
                internalInstaller?.Dispose();
                var sessionFolder = Path.Combine(AppFolder, "instalacion-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                report.Text = await Task.Run(() =>
                {
                    internalInstaller = new InternalInstaller(host, secret, sessionFolder,
                        text => Dispatcher.Invoke(() => SetStatus(text)), usePcNetwork);
                    return internalInstaller.Prepare();
                });
                ready = true;
                SetStatus("Respaldo verificado. Revisa el destino y confirma el borrado.");
            }
            catch (Exception ex)
            {
                report.Text = ex.Message;
                SetStatus("Preparación detenida. Conserva el USB y revisa el informe.");
            }
            finally
            {
                internalBusy = false;
                prepare.IsEnabled = true;
                if (!ready)
                {
                    address.IsEnabled = true;
                    password.IsEnabled = true;
                    pcNetwork.IsEnabled = true;
                }
                install.IsEnabled = ready && confirmation.IsChecked == true;
            }
        };
        install.Click += async (_, _) =>
        {
            if (internalBusy || !ready || confirmation.IsChecked != true || internalInstaller is null) return;
            internalBusy = true;
            install.IsEnabled = false;
            prepare.IsEnabled = false;
            try
            {
                await Task.Run(internalInstaller.Install);
                report.Text = "Instalación interna terminada. Espera a que el Edge se apague, retira el USB y vuelve a encenderlo. Conserva COM5 conectado para verificar el nuevo arranque.";
                SetStatus("Retira el USB del Edge apagado y vuelve a encenderlo.");
                verify.IsEnabled = true;
            }
            catch (Exception ex)
            {
                report.Text = "La instalación necesita revisión. " + ex.Message;
                SetStatus("Conserva COM5 y el USB conectados. Revisa el registro antes de reintentar.");
            }
            finally
            {
                internalBusy = false;
                ready = false;
            }
        };
        verify.Click += async (_, _) =>
        {
            if (internalBusy || internalInstaller is null) return;
            internalBusy = true;
            verify.IsEnabled = false;
            try
            {
                await Task.Run(internalInstaller.VerifyInstalled);
                report.Text = "OPNsense funciona desde la memoria interna. Se confirmaron más de diez minutos, las cuatro interfaces Intel, la web disponible y el watchdog desactivado.";
            }
            catch (Exception ex)
            {
                report.Text = "Verificación pendiente. " + ex.Message;
                verify.IsEnabled = true;
            }
            finally { internalBusy = false; }
        };
        card.Children.Add(prepare);
        card.Children.Add(report);
        card.Children.Add(confirmation);
        card.Children.Add(install);
        card.Children.Add(verify);
        PageHost.Children.Add(card);
        PageHost.Children.Add(Card("Qué significa la luz",
            "Durante la preparación de OPNsense, azul, violeta y ámbar en respiración. Verde cuando responde la web y hay enlace LAN. Ámbar cuando falta enlace LAN. Rojo cuando falla la web o se detecta un problema con el PIC. Antes de iniciar OPNsense, la luz depende del firmware."));
    }

    private async Task ScanDisksAsync()
    {
        SetStatus("Analizando unidades conectadas…");
        try
        {
            _disks = await Task.Run(ReadDisks);
            if (_diskPicker is not null) BindDisks(_diskPicker);
            var report = new { createdUtc = DateTime.UtcNow, ports = SerialPort.GetPortNames(), disks = _disks.Select(d => new { d.Number, d.Model, d.Serial, d.Bus, d.SizeBytes, d.Letters }) };
            var path = Path.Combine(AppFolder, $"revision-pc-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            SetStatus($"Análisis listo. Informe local: {path}");
        }
        catch (Exception ex) { SetStatus("No se pudo completar el análisis: " + ex.Message); }
    }

    private static List<DiskInfo> ReadDisks()
    {
        const string command = "$ErrorActionPreference='Stop'; Get-Disk | Where-Object { $_.BusType -eq 'USB' -and -not $_.IsBoot -and -not $_.IsSystem -and -not $_.IsReadOnly -and -not $_.IsOffline } | ForEach-Object { $d=$_; $v=@(Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue | Get-Volume -ErrorAction SilentlyContinue | Where-Object DriveLetter | ForEach-Object { $_.DriveLetter }); [pscustomobject]@{ Number=$d.Number; Serial=([string]$d.SerialNumber).Trim(); Model=$d.FriendlyName; Bus=$d.BusType.ToString(); SizeBytes=[long]$d.Size; Letters=($v -join ',') } } | ConvertTo-Json -Compress";
        var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(ps) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add(command);
        using var p = Process.Start(start) ?? throw new InvalidOperationException("No se pudo iniciar Windows PowerShell.");
        var output = p.StandardOutput.ReadToEnd(); var error = p.StandardError.ReadToEnd(); p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException(error);
        if (string.IsNullOrWhiteSpace(output)) return [];
        using var doc = JsonDocument.Parse(output);
        var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToArray() : [doc.RootElement];
        return arr.Select(x => new DiskInfo(x.GetProperty("Number").GetInt32(), Clean(x.GetProperty("Serial").GetString()), Clean(x.GetProperty("Model").GetString()), Clean(x.GetProperty("Bus").GetString()), x.GetProperty("SizeBytes").GetInt64(), Clean(x.GetProperty("Letters").GetString()))).ToList();
    }

    private static string Clean(string? s) => (s ?? "").Trim();
    private void BindDisks(ComboBox picker)
    {
        picker.ItemsSource = null;
        picker.ItemsSource = _disks;
        var preferred = _selectedDisk is null ? -1 : _disks.FindIndex(d => d.Number == _selectedDisk.Number && d.Serial == _selectedDisk.Serial);
        picker.SelectedIndex = preferred >= 0 ? preferred : (_disks.Count == 1 ? 0 : -1);
        if (picker.SelectedItem is DiskInfo initiallySelected)
        {
            _selectedDisk = initiallySelected;
            if (_serialId is not null && string.IsNullOrWhiteSpace(_serialId.Text)) _serialId.Text = initiallySelected.Serial;
        }
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is DiskInfo d)
            {
                _selectedDisk = d;
                if (_serialId is not null && string.IsNullOrWhiteSpace(_serialId.Text)) _serialId.Text = d.Serial;
            }
        };
    }

    private static void LoadComPorts(ComboBox combo)
    {
        combo.ItemsSource = SerialPort.GetPortNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        combo.SelectedItem = SerialPort.GetPortNames().FirstOrDefault(p => p.Equals("COM5", StringComparison.OrdinalIgnoreCase)) ?? combo.Items.Cast<string>().FirstOrDefault();
    }

    private void ToggleSerial(Button button)
    {
        try
        {
            if (_serial?.IsOpen == true) { _serial.DataReceived -= Serial_DataReceived; _serial.Close(); _serial.Dispose(); _serial = null; button.Content = "Conectar y escuchar"; SetStatus("Consola desconectada."); return; }
            var port = _comPicker?.SelectedItem?.ToString() ?? "COM5";
            _serial = new SerialPort(port, 115200, Parity.None, 8, StopBits.One) { NewLine = "\n", ReadTimeout = 1000, WriteTimeout = 1000, DtrEnable = false, RtsEnable = false };
            _serial.DataReceived += Serial_DataReceived;
            _serial.Open(); button.Content = "Desconectar"; SetStatus($"Escuchando {port} a 115200 8N1. Solo lectura.");
            AppendSerial($"\r\n[Conectado a {port} · 115200 8N1 · sin enviar datos]\r\n");
        }
        catch (Exception ex) { SetStatus("No se pudo abrir el puerto. Cierra PuTTY u otra terminal y vuelve a intentar. " + ex.Message); }
    }

    private void Serial_DataReceived(object? sender, SerialDataReceivedEventArgs e)
    {
        try { var text = _serial?.ReadExisting(); if (!string.IsNullOrEmpty(text)) Dispatcher.Invoke(() => AppendSerial(text)); }
        catch { }
    }
    private void AppendSerial(string text) { if (_serialLog is not null) { _serialLog.AppendText(text); _serialLog.ScrollToEnd(); } }

    private async Task CaptureInventoryAsync()
    {
        if (_serial?.IsOpen != true) { SetStatus("Conecta primero COM5."); return; }
        var command = "printf '\\n=== EDGE510 READ-ONLY INVENTORY ===\\n'; date -u; uname -a; (cat /etc/os-release 2>/dev/null); (lscpu 2>/dev/null); (free -h 2>/dev/null); (lsblk -o NAME,MODEL,SERIAL,SIZE,TYPE,FSTYPE,MOUNTPOINT 2>/dev/null); (lspci -nn 2>/dev/null); (dmidecode -t bios -t baseboard 2>/dev/null)";
        var confirm = MessageBox.Show("Confirma que ya has iniciado sesión en una consola autorizada y ves un prompt de shell.\n\nSe enviará un comando de inventario de solo lectura; no incluye contraseñas ni instrucciones de escritura.", "Capturar inventario", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (confirm != MessageBoxResult.OK) return;
        try
        {
            _serial?.WriteLine(command);
            SetStatus("Comando de consulta enviado. Esperando salida serial…");
            await Task.Delay(1500);
            var path = Path.Combine(AppFolder, $"consola-edge510-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            await File.WriteAllTextAsync(path, _serialLog?.Text ?? "");
            SetStatus("Registro serial guardado localmente: " + path);
        }
        catch (Exception ex) { SetStatus("No se pudo enviar/guardar el diagnóstico: " + ex.Message); }
    }

    private async Task DownloadAndVerifyAsync()
    {
        if (_mediaStatus is null) return;
        _imageVerified = false;
        _mediaStatus.Text = "Consultando el mirror oficial y comprobando la clave de firma…";
        SetStatus("Descargando desde pkg.opnsense.org. La imagen ocupa varios cientos de MB.");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            var readme = await http.GetStringAsync($"{OfficialMirror}/releases/mirror/README");
            var releaseMatch = Regex.Match(readme, @"latest stable release image for OPNsense is\s+(\d+(?:\.\d+)+)", RegexOptions.IgnoreCase);
            if (!releaseMatch.Success) throw new InvalidDataException("No pude determinar la versión estable desde el README oficial.");
            var version = releaseMatch.Groups[1].Value;
            if (version != PinnedVersion) throw new InvalidDataException($"La versión estable publicada es {version}, pero este ejecutable solo tiene fijada la clave pública de {PinnedVersion}. Detengo la descarga; actualiza la clave de confianza del proyecto y recompila.");
            var keyMatch = Regex.Match(readme, $@"The public key for version {Regex.Escape(version)} is:\s*(-----BEGIN PUBLIC KEY-----[\s\S]*?-----END PUBLIC KEY-----)", RegexOptions.IgnoreCase);
            if (!keyMatch.Success || NormalizePem(keyMatch.Groups[1].Value) != NormalizePem(PinnedPublicKey)) throw new CryptographicException("La clave publicada no coincide con la clave fijada dentro del programa.");

            var prefix = $"OPNsense-{version}-serial-amd64.img";
            var bzUrl = $"{OfficialMirror}/releases/{version}/{prefix}.bz2";
            var sigUrl = $"{OfficialMirror}/releases/{version}/{prefix}.sig";
            var bzPath = Path.Combine(AppFolder, prefix + ".bz2");
            var sigPath = Path.Combine(AppFolder, prefix + ".sig");
            var imgPath = Path.Combine(AppFolder, prefix);
            if (!File.Exists(imgPath) || !File.Exists(sigPath))
            {
            await DownloadToFile(http, bzUrl, bzPath, text => Dispatcher.Invoke(() => _mediaStatus.Text = text));
            await DownloadToFile(http, sigUrl, sigPath, _ => { });
            SetStatus("Descomprimiendo y comprobando firma digital…");
            await Task.Run(() => DecompressBzip2(bzPath, imgPath));
            }
            SetStatus("Verificando la firma de la imagen local…");
            var signature = await File.ReadAllBytesAsync(sigPath);
            var sigText = Encoding.ASCII.GetString(signature).Trim();
            if (Regex.IsMatch(sigText, @"^[A-Za-z0-9+/=\r\n]+$")) { try { signature = Convert.FromBase64String(sigText); } catch { } }
            using var rsa = RSA.Create(); rsa.ImportFromPem(PinnedPublicKey);
            await using var imageStream = File.OpenRead(imgPath);
            if (!rsa.VerifyData(imageStream, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException("La firma RSA no valida. No se permitirá grabar esta imagen.");
            imageStream.Position = 0;
            var imageHash = Convert.ToHexString(await SHA256.HashDataAsync(imageStream));
            SetStatus("Preparando la consola UART1 del Edge 510…");
            var configuredImage = await Task.Run(() => SerialImage.Prepare(imgPath, imageHash));
            _verifiedImageHash = SerialImage.PatchedHash;
            _imageFile = configuredImage; _imageSignatureFile = sigPath; _imageVerified = true;
            _mediaStatus.Text = $"PREPARADA\r\nVersión: {version}\r\nTipo: serial amd64, consola UART1\r\nOrigen: {OfficialMirror}\r\nFirma del original RSA-SHA256 válida\r\nAdaptación local UART1 verificada por SHA-256: {SerialImage.PatchedHash}\r\nArchivo: {configuredImage}";
            SetStatus("Imagen oficial descomprimida y firma verificada correctamente.");
            SaveMediaManifest(version, imageHash, bzUrl, sigUrl);
        }
        catch (Exception ex) { _mediaStatus.Text = "NO VERIFICADA\r\n" + ex.Message; SetStatus("Descarga detenida: " + ex.Message); }
    }

    private static string NormalizePem(string value) => Regex.Replace(value.Trim(), @"\s+", "");
    private static async Task DownloadToFile(HttpClient http, string url, string path, Action<string> progress)
    {
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "pkg.opnsense.org") throw new InvalidOperationException("Solo se permiten descargas HTTPS desde pkg.opnsense.org.");
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        var buffer = new byte[1024 * 1024]; long readTotal = 0; int read;
        while ((read = await source.ReadAsync(buffer)) > 0) { await destination.WriteAsync(buffer.AsMemory(0, read)); readTotal += read; if (total > 0) progress($"Descargando imagen oficial… {readTotal * 100 / total}% ({readTotal / 1024 / 1024} MB)"); }
        await destination.FlushAsync();
    }
    private static void DecompressBzip2(string sourcePath, string destinationPath)
    {
        using var input = File.OpenRead(sourcePath);
        using var bz = BZip2Stream.Create(input, CompressionMode.Decompress, decompressConcatenated: false, leaveOpen: false, tolerateTruncatedStream: false);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
        bz.CopyTo(output);
    }
    private void SaveMediaManifest(string version, string hash, string imageUrl, string signatureUrl)
    {
        var manifest = new { createdUtc = DateTime.UtcNow, author = "George Valdez", version, imageType = "serial-amd64", imageUrl, signatureUrl, originalSha256 = hash, originalSignatureVerified = true, customizedSha256 = SerialImage.PatchedHash, customization = "Consola UART1 0x2f8 para Edge 510", publicKeyVersion = PinnedVersion };
        File.WriteAllText(Path.Combine(AppFolder, "media-verificada.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task WriteUsbAsync()
    {
        if (!_imageVerified || _selectedDisk is null || !string.Equals(_targetSerial, _selectedDisk.Serial, StringComparison.OrdinalIgnoreCase)) { SetStatus("La imagen, identidad del USB o coincidencia de serie no es válida."); return; }
        var d = _selectedDisk;
        if (!d.Bus.Contains("USB", StringComparison.OrdinalIgnoreCase) || d.SizeBytes < 1024 * 1024 * 1024 || string.IsNullOrWhiteSpace(d.Serial)) { SetStatus("Bloqueado: el destino no parece un USB seguro o no tiene serie identificable."); return; }
        var latest = (await Task.Run(ReadDisks)).FirstOrDefault(x => x.Number == d.Number);
        if (latest is null || latest.Serial != d.Serial || latest.Model != d.Model || latest.SizeBytes != d.SizeBytes || !latest.Bus.Contains("USB", StringComparison.OrdinalIgnoreCase)) { SetStatus("La identidad del USB cambió desde el análisis. Vuelve al paso 1."); return; }
        var image = new FileInfo(_imageFile!);
        if (image.Length > d.SizeBytes) { SetStatus("El USB es más pequeño que la imagen."); return; }
        var phrase = $"BORRAR USB {d.Serial}";
        var first = MessageBox.Show($"Se borrará TODO el contenido de este dispositivo:\n\n{d.Display}\n\nSolo se escribe el disco USB. No se toca el Edge ni otros discos.\n\n¿Deseas continuar?", "Confirmar borrado del USB", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (first != MessageBoxResult.Yes) return;
        var typed = Microsoft.VisualBasic.Interaction.InputBox($"Para confirmar, escribe exactamente:\n\n{phrase}", "Confirmación final", "");
        if (!string.Equals(typed, phrase, StringComparison.Ordinal)) { SetStatus("Confirmación no coincide; no se escribió nada."); return; }
        if (!IsAdministrator())
        {
            MessageBox.Show("Cierra la app y vuelve a abrirla con clic derecho → Ejecutar como administrador para grabar el USB. El resto de pasos no requiere administrador.", "Se requieren permisos para el USB", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SetStatus("Bloqueando el USB y grabando la imagen… No retires la memoria.");
        try
        {
            var matches = (await Task.Run(ReadDisks)).Where(x => x.Serial == d.Serial).ToList();
            if (matches.Count != 1 || matches[0] != d)
                throw new IOException("La identidad del USB cambió. Repite el análisis.");
            await Task.Run(() => RawUsbWriter.WriteAndVerify(d.Number, d.Letters, _imageFile!, image.Length, _verifiedImageHash));
            SetStatus("USB grabado y verificado. Puedes expulsarlo desde Windows cuando termine la sincronización.");
            MessageBox.Show("La imagen se escribió y la lectura posterior coincide byte por byte.\n\nAhora puedes apagar el Edge, retirar el USB del PC e insertarlo en el appliance.", "USB listo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { SetStatus("La grabación no terminó correctamente. El USB puede haber quedado parcialmente escrito. " + ex.Message); }
    }

    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private void SetStatus(string value) { Dispatcher.Invoke(() => StatusText.Text = value); }
    private CardContainer NewCard() => new() { Background = (Brush)FindResource("Card"), CornerRadius = new CornerRadius(14), Padding = new Thickness(22), Margin = new Thickness(0, 0, 0, 15), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1) };
    private Border Card(string title, string body) { var c = NewCard(); AddTitle(c, title); AddText(c, body, true); return c; }
    private static void AddTitle(CardContainer panel, string title) => panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10), Foreground = new SolidColorBrush(Color.FromRgb(23, 35, 45)), TextWrapping = TextWrapping.Wrap });
    private static void AddText(CardContainer panel, string text, bool wrap, double size = 14, string color = "#495B64") => panel.Children.Add(new TextBlock { Text = text, FontSize = size, Foreground = (Brush)new BrushConverter().ConvertFromString(color)!, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, LineHeight = size * 1.55, Margin = new Thickness(0, 0, 0, 4) });
    private static void AddBullet(CardContainer panel, string text) => panel.Children.Add(new TextBlock { Text = "•  " + text, FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 22, Margin = new Thickness(0, 5, 0, 5), Foreground = new SolidColorBrush(Color.FromRgb(57, 73, 81)) });
    private Border Notice(string title, string body, string back, string fore)
    {
        var b = new Border { Background = (Brush)new BrushConverter().ConvertFromString(back)!, CornerRadius = new CornerRadius(12), Padding = new Thickness(17), Margin = new Thickness(0, 0, 0, 15) };
        var s = new StackPanel(); s.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, FontSize = 14, Foreground = (Brush)new BrushConverter().ConvertFromString(fore)! });
        s.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20, Margin = new Thickness(0, 5, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(59, 72, 78)) }); b.Child = s; return b;
    }
    private Button PrimaryButton(string text) => new() { Content = text, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 180 };
    private Button QuietButton(string text) => new() { Content = text, Style = (Style)FindResource("QuietButton"), HorizontalAlignment = HorizontalAlignment.Left };

    protected override void OnClosed(EventArgs e)
    {
        try { if (_serial is { IsOpen: true }) _serial.Close(); _serial?.Dispose(); } catch { }
        base.OnClosed(e);
    }

    private sealed record DiskInfo(int Number, string Serial, string Model, string Bus, long SizeBytes, string Letters)
    {
        public string Display => $"Disco {Number} · {Model} · {Bus} · {SizeBytes / 1073741824d:0.00} GiB · Serie {Serial} · Volúmenes {Letters}";
    }
}

internal static class RawUsbWriter
{
    private const uint GenericWrite = 0x40000000, GenericRead = 0x80000000;
    private const uint ShareRead = 1, ShareWrite = 2, OpenExisting = 3;
    private const uint FsctlLockVolume = 0x00090018, FsctlDismountVolume = 0x00090020;
    private const uint IoctlDiskUpdateProperties = 0x00070140;

    public static void WriteAndVerify(int number, string letters, string imagePath, long imageLength, string verifiedHash)
    {
        var locked = new List<SafeFileHandle>();
        try
        {
            foreach (var letter in letters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var vh = Native.CreateFile(@"\\.\" + letter + ":", 0, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (vh.IsInvalid) throw new IOException($"No se pudo abrir el volumen {letter}:.");
                if (!Native.DeviceIoControl(vh, FsctlLockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)) { vh.Dispose(); throw new IOException($"Windows no pudo bloquear {letter}:. Cierra el Explorador y aplicaciones que usen el USB."); }
                if (!Native.DeviceIoControl(vh, FsctlDismountVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)) { vh.Dispose(); throw new IOException($"No se pudo desmontar {letter}:. No se ha escrito la imagen."); }
                locked.Add(vh);
            }

            using var disk = Native.CreateFile(@"\\.\PhysicalDrive" + number, GenericRead | GenericWrite, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (disk.IsInvalid) throw new IOException("Windows no pudo abrir el disco físico. Ejecuta el asistente como administrador.");
            using var stream = new FileStream(disk, FileAccess.ReadWrite, 1024 * 1024, false);
            using var source = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            if (source.Length != imageLength) throw new IOException("La imagen cambió después de validarla.");
            if (Convert.ToHexString(SHA256.HashData(source)) != verifiedHash)
                throw new IOException("Cambió el contenido de la imagen firmada.");
            source.Position = 0;
            stream.Position = 0;
            source.CopyTo(stream, 1024 * 1024);
            stream.Flush(true);
            stream.Position = 0; source.Position = 0;
            using var expectedHash = SHA256.Create(); using var actualHash = SHA256.Create();
            var expected = expectedHash.ComputeHash(source);
            var actual = HashExactly(stream, imageLength, actualHash);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new IOException("La lectura de comprobación no coincide con la imagen.");
            Native.DeviceIoControl(disk, IoctlDiskUpdateProperties, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        finally { foreach (var h in locked) h.Dispose(); }
    }

    private static byte[] HashExactly(Stream stream, long length, HashAlgorithm algorithm)
    {
        var buffer = new byte[1024 * 1024]; long remaining = length;
        while (remaining > 0)
        {
            var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count <= 0) throw new EndOfStreamException("El USB terminó antes de completar la verificación.");
            algorithm.TransformBlock(buffer, 0, count, buffer, 0); remaining -= count;
        }
        algorithm.TransformFinalBlock([], 0, 0);
        return algorithm.Hash!;
    }

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
    }
}

internal sealed class CardContainer : Border
{
    private readonly StackPanel _content = new();
    public UIElementCollection Children => _content.Children;
    public CardContainer() => Child = _content;
}

