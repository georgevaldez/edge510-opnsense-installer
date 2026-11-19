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
    private bool mediaBusy;
    private List<DiskInfo> _disks = [];
    private DiskInfo? _selectedDisk;
    private string? _imageFile;
    private string? _imageSignatureFile;
    private bool _imageVerified;
    private string _verifiedImageHash = "";
    private SerialPort? _serial;
    private string _targetSerial = "";
    private ComboBox? _comPicker;
    private TextBox? _serialLog;
    private TextBox? _mediaStatus;
    private readonly StringBuilder serialBuffer = new();
    private bool ConnectionsReady => _serial?.IsOpen == true && _selectedDisk is not null;
    private UIElement[]? installationPage;

    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (internalBusy || mediaBusy)
            {
                e.Cancel = true;
                SetStatus("Espera a que termine la operación en curso.");
            }
            else internalInstaller?.Dispose();
        };
        BuildVersion.Text = "Versión " + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "");
        Directory.CreateDirectory(AppFolder);
        ShowPage(0);
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && int.TryParse(Convert.ToString(b.Tag), out var page)) ShowPage(page);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowPage(Math.Max(0, _page - 1));
    private void Next_Click(object sender, RoutedEventArgs e) => ShowPage(Math.Min(3, _page + 1));

    private void ShowPage(int page)
    {
        if (internalBusy || mediaBusy) return;
        if (page > 0 && !ConnectionsReady)
        {
            SetStatus("Primero conecta la consola y selecciona el USB en el paso 1.");
            return;
        }
        _page = page;
        _serialLog = null;
        var titles = new[] { "Conecta la consola y el USB", "Prepara OPNsense", "Crea el USB de instalación", "Instala en tu Edge" };
        Eyebrow.Text = $"PASO {page + 1} DE 4";
        PageTitle.Text = titles[page];
        BackButton.IsEnabled = page > 0;
        NextButton.Visibility = page < 3 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = page == 2 ? "Ir al Edge" : "Continuar";
        NextButton.IsEnabled = page != 0 || ConnectionsReady;
        var nav = new[] { Nav1, Nav2, Nav3, Nav4 };
        var dots = new[] { Dot1, Dot2, Dot3, Dot4 };
        for (var i = 0; i < nav.Length; i++)
        {
            nav[i].IsEnabled = i == 0 || ConnectionsReady;
            nav[i].Foreground = i == page ? Brushes.White : new SolidColorBrush(Color.FromRgb(199, 208, 211));
            nav[i].Background = i == page ? new SolidColorBrush(Color.FromRgb(38, 63, 70)) : Brushes.Transparent;
            dots[i].Background = i == page ? new SolidColorBrush(Color.FromRgb(11, 130, 116)) : new SolidColorBrush(Color.FromRgb(52, 69, 76));
        }
        PageHost.Children.Clear();
        switch (page)
        {
            case 0: BuildStartPage(); break;
            case 1: BuildImagePage(); break;
            case 2: BuildUsbPage(); break;
            case 3:
                if (installationPage is null)
                {
                    BuildConversionPage();
                    installationPage = PageHost.Children.Cast<UIElement>().ToArray();
                }
                else foreach (var element in installationPage) PageHost.Children.Add(element);
                break;
        }
        SetStatus(page switch
        {
            0 => ConnectionsReady ? "Consola conectada y USB seleccionado. Puedes continuar." : "Conecta la consola antes de elegir el USB.",
            1 => _imageVerified ? "OPNsense está listo para grabar." : "Descarga y comprueba la imagen.",
            2 => "Revisa la unidad antes de grabar.",
            _ => "Empieza por conectar y respaldar tu Edge."
        });
    }

    private void BuildStartPage()
    {
        PageHost.Children.Add(BuildConnectionCard());
        var card = NewCard();
        AddTitle(card, "2. Elige el USB del PC");
        AddText(card, "Selecciona la memoria que usarás para instalar OPNsense.", true);
        var selected = new TextBlock
        {
            Text = _selectedDisk?.Label ?? "Ninguna unidad seleccionada",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16,
            Margin = new Thickness(0, 8, 0, 12)
        };
        card.Children.Add(selected);
        var choose = PrimaryButton("Seleccionar USB…");
        choose.IsEnabled = _serial?.IsOpen == true;
        choose.Click += async (_, _) =>
        {
            choose.IsEnabled = false;
            try
            {
                await SelectUsbAsync();
                selected.Text = _selectedDisk?.Label ?? "Ninguna unidad seleccionada";
                NextButton.IsEnabled = ConnectionsReady;
                foreach (var nav in new[] { Nav2, Nav3, Nav4 }) nav.IsEnabled = ConnectionsReady;
            }
            finally { choose.IsEnabled = true; }
        };
        card.Children.Add(choose);
        PageHost.Children.Add(card);
    }

    private CardContainer BuildConnectionCard()
    {
        var card = NewCard();
        AddTitle(card, "1. Conecta la consola del Edge");
        AddText(card, "Conecta el cable serial al PC y al Edge. No necesitas apagarlo.", true);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        _comPicker = new ComboBox { Width = 110, MinHeight = 38, Margin = new Thickness(0, 0, 10, 0) };
        LoadComPorts(_comPicker);
        if (_serial?.IsOpen == true) _comPicker.SelectedItem = _serial.PortName;
        _comPicker.IsEnabled = _serial?.IsOpen != true;
        row.Children.Add(_comPicker);
        var connect = PrimaryButton(_serial?.IsOpen == true ? "Desconectar" : "Conectar consola");
        connect.MinWidth = 150;
        connect.Click += (_, _) =>
        {
            ToggleSerial(connect);
            var result = StatusText.Text;
            ShowPage(0);
            SetStatus(result);
        };
        row.Children.Add(connect);
        var refresh = QuietButton("Actualizar");
        refresh.Margin = new Thickness(10, 0, 0, 0);
        refresh.IsEnabled = _serial?.IsOpen != true;
        refresh.Click += (_, _) => LoadComPorts(_comPicker);
        row.Children.Add(refresh);
        card.Children.Add(row);
        AddText(card, _serial?.IsOpen == true ? $"Conectada a {_serial.PortName} · 115200 8N1" : "Selecciona el puerto COM del cable serial · 115200 8N1", true, 13);
        var panel = new StackPanel();
        _serialLog = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 120,
            FontFamily = new FontFamily("Consolas"), FontSize = 12, Margin = new Thickness(0, 10, 0, 10),
            Text = serialBuffer.ToString()
        };
        panel.Children.Add(_serialLog);
        var capture = QuietButton("Capturar diagnóstico");
        capture.Click += async (_, _) => await CaptureInventoryAsync();
        panel.Children.Add(capture);
        panel.Children.Add(Details("Ayuda de acceso", "Linux de fábrica: root / VeloHelloXXX. XXX son los últimos tres caracteres de la serie inferior, salvo que la contraseña haya cambiado. USB de OPNsense: root / opnsense. Abrir el puerto confirma que está disponible; la salida permite comprobar que el Edge responde."));
        card.Children.Add(new Expander { Header = "Ver consola y ayuda", Content = panel, Margin = new Thickness(0, 6, 0, 0) });
        return card;
    }

    private void BuildImagePage()
    {
        var card = NewCard();
        AddTitle(card, "Descarga la imagen oficial");
        AddText(card, "El asistente comprueba la firma y prepara la consola del Edge.", true);
        var download = PrimaryButton(_imageVerified ? "Comprobar de nuevo" : "Descargar OPNsense");
        download.Margin = new Thickness(0, 16, 0, 16);
        download.Click += async (_, _) => await RunMediaAsync(DownloadAndVerifyAsync);
        card.Children.Add(download);
        _mediaStatus = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 90, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(12), FontSize = 13,
            Text = _imageVerified ? "Imagen comprobada y lista para el USB." : "La descarga todavía no ha comenzado."
        };
        card.Children.Add(_mediaStatus);
        PageHost.Children.Add(card);
        PageHost.Children.Add(Details("Detalles de la imagen", "OPNsense Serial amd64. La firma del original se comprueba con la clave de publicación. La copia para Edge 510 usa la consola UART1 a 115200 y se verifica por SHA-256."));
    }

    private void BuildUsbPage()
    {
        var card = NewCard();
        AddTitle(card, "Revisa la unidad seleccionada");
        AddText(card, _selectedDisk?.Label ?? "Primero selecciona tu USB en el paso 1.", true);
        AddText(card, "Al continuar se borrará todo el contenido de este USB.", true, 14, "#9B3B35");
        var write = PrimaryButton("Grabar y verificar USB");
        write.Margin = new Thickness(0, 18, 0, 0);
        write.IsEnabled = _imageVerified && _selectedDisk is not null && string.Equals(_targetSerial, _selectedDisk.Serial, StringComparison.OrdinalIgnoreCase);
        write.Click += async (_, _) => await RunMediaAsync(WriteUsbAsync);
        card.Children.Add(write);
        PageHost.Children.Add(card);
        PageHost.Children.Add(Details("Después de grabar", "Cuando termine la verificación, apaga el Edge, coloca el USB y enciéndelo con la consola conectada. Continúa con la instalación interna."));
    }

    private bool internalBusy;
    private InternalInstaller? internalInstaller;

    private void BuildConversionPage()
    {
        var card = NewCard();
        card.Padding = new Thickness(16);
        AddTitle(card, "Del USB a la memoria interna");
        AddText(card, "Arranca el Edge con el USB y conecta la LAN al PC.", true);
        var fields = new Grid { Margin = new Thickness(0, 6, 0, 8) };
        fields.ColumnDefinitions.Add(new ColumnDefinition());
        fields.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var right = new StackPanel();
        Grid.SetColumn(right, 1);
        fields.Children.Add(left);
        fields.Children.Add(right);
        var address = new TextBox { Text = "192.168.1.1", MinHeight = 32, Margin = new Thickness(0, 5, 0, 8) };
        left.Children.Add(new TextBlock { Text = "IP del Edge" });
        left.Children.Add(address);
        var password = new PasswordBox { Password = "opnsense", MinHeight = 32, Margin = new Thickness(0, 5, 0, 8) };
        right.Children.Add(new TextBlock { Text = "Contraseña de root" });
        right.Children.Add(password);
        card.Children.Add(fields);
        var pcNetwork = new CheckBox
        {
            Content = new TextBlock { Text = "Usar la red del PC y conservar el DHCP de la LAN", TextWrapping = TextWrapping.Wrap, FontSize = 13 },
            IsChecked = true,
            Margin = new Thickness(0, 5, 0, 10)
        };
        card.Children.Add(pcNetwork);
        var report = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 76, Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 8), Visibility = Visibility.Collapsed };
        var prepare = PrimaryButton("1. Respaldar");
        var install = PrimaryButton("2. Instalar");
        var verify = PrimaryButton("3. Verificar");
        verify.IsEnabled = false;
        install.IsEnabled = false;
        var confirmation = new CheckBox
        {
            Content = new TextBlock { Text = "Autorizo borrar el disco interno indicado en el respaldo.", TextWrapping = TextWrapping.Wrap, FontSize = 13 },
            Margin = new Thickness(0, 10, 0, 10)
        };
        var ready = false;
        confirmation.Checked += (_, _) => install.IsEnabled = ready && !internalBusy;
        confirmation.Unchecked += (_, _) => install.IsEnabled = false;
        prepare.Click += async (_, _) =>
        {
            if (internalBusy) return;
            if (_serial?.IsOpen != true) { SetStatus("Reconecta la consola en el paso 1."); return; }
            if (!System.Net.IPAddress.TryParse(address.Text.Trim(), out _))
            {
                SetStatus("Introduce una dirección IP válida.");
                return;
            }
            report.Visibility = Visibility.Visible;
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
                report.Text = "Instalación terminada. Cuando el Edge se apague, retira el USB y enciéndelo. Después pulsa 3. Verificar.";
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
                report.Text = "Listo: arranque interno estable durante más de diez minutos, red y web disponibles, watchdog desactivado.";
            }
            catch (Exception ex)
            {
                report.Text = "Verificación pendiente. " + ex.Message;
                verify.IsEnabled = true;
            }
            finally { internalBusy = false; }
        };
        card.Children.Add(report);
        card.Children.Add(confirmation);
        var actions = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 8) };
        foreach (var action in new[] { prepare, install, verify })
        {
            action.MinWidth = 0;
            action.FontSize = 13;
            action.Padding = new Thickness(8, 11, 8, 11);
            action.HorizontalAlignment = HorizontalAlignment.Stretch;
            action.Margin = new Thickness(0, 0, 8, 0);
            actions.Children.Add(action);
        }
        card.Children.Add(actions);
        AddText(card, "Respaldar → autorizar → instalar → retirar USB → verificar", true, 12);
        PageHost.Children.Add(card);
        PageHost.Children.Add(Details("Qué significa la luz",
            "Durante la preparación de OPNsense, azul, violeta y ámbar en respiración. Verde cuando responde la web y hay enlace LAN. Ámbar cuando falta enlace LAN. Rojo cuando falla la web o se detecta un problema con el PIC. Antes de iniciar OPNsense, la luz depende del firmware."));
    }

    private async Task SelectUsbAsync()
    {
        SetStatus("Buscando unidades USB…");
        try
        {
            _disks = await Task.Run(ReadDisks);
            var picker = new Window
            {
                Owner = this, Title = "Seleccionar unidad USB", Width = 580, Height = 420,
                MinWidth = 480, MinHeight = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brushes.White, ShowInTaskbar = false
            };
            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new TextBlock { Text = _disks.Count == 0 ? "Conecta una memoria USB y pulsa Actualizar." : "Elige la memoria que vas a usar.", Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
            grid.Children.Add(heading);
            var list = new ListBox { ItemsSource = _disks, DisplayMemberPath = "Label", Padding = new Thickness(6) };
            Grid.SetRow(list, 1);
            grid.Children.Add(list);
            var detail = new TextBlock { Margin = new Thickness(0, 12, 0, 12), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(detail, 2);
            grid.Children.Add(detail);
            var footer = new DockPanel();
            Grid.SetRow(footer, 3);
            var refresh = QuietButton("Actualizar");
            refresh.Click += async (_, _) =>
            {
                refresh.IsEnabled = false;
                try
                {
                    _disks = await Task.Run(ReadDisks);
                    list.ItemsSource = _disks;
                    heading.Text = _disks.Count == 0 ? "No se encontró una unidad USB disponible." : "Elige la memoria que vas a usar.";
                }
                catch (Exception ex) { heading.Text = ex.Message; }
                finally { refresh.IsEnabled = true; }
            };
            DockPanel.SetDock(refresh, Dock.Left);
            footer.Children.Add(refresh);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = QuietButton("Cancelar");
            cancel.Margin = new Thickness(12, 0, 10, 0);
            cancel.Click += (_, _) => picker.DialogResult = false;
            var select = PrimaryButton("Seleccionar");
            select.MinWidth = 110;
            select.IsEnabled = false;
            select.Click += (_, _) => picker.DialogResult = true;
            buttons.Children.Add(cancel);
            buttons.Children.Add(select);
            footer.Children.Add(buttons);
            grid.Children.Add(footer);
            list.SelectionChanged += (_, _) =>
            {
                select.IsEnabled = list.SelectedItem is DiskInfo d && !string.IsNullOrWhiteSpace(d.Serial);
                detail.Text = list.SelectedItem is DiskInfo selected
                    ? (string.IsNullOrWhiteSpace(selected.Serial) ? "Windows no pudo leer la serie de esta unidad." : "Serie " + selected.Serial)
                    : "";
            };
            picker.Content = grid;
            if (picker.ShowDialog() == true && list.SelectedItem is DiskInfo disk)
            {
                _selectedDisk = disk;
                _targetSerial = disk.Serial;
                SetStatus("USB seleccionado. Continúa con OPNsense.");
            }
            else SetStatus(_selectedDisk is null ? "Selecciona un USB para continuar." : "USB seleccionado.");
        }
        catch (Exception ex) { SetStatus("No se pudo buscar el USB. " + ex.Message); }
    }

    private static List<DiskInfo> ReadDisks()
    {
        const string command = "$ErrorActionPreference='Stop'; Get-Disk | Where-Object { $_.BusType -eq 'USB' -and $_.Size -gt 0 -and -not $_.IsBoot -and -not $_.IsSystem -and -not $_.IsReadOnly -and -not $_.IsOffline } | ForEach-Object { $d=$_; $v=@(Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue | Get-Volume -ErrorAction SilentlyContinue | Where-Object DriveLetter | ForEach-Object { $_.DriveLetter }); [pscustomobject]@{ Number=$d.Number; Serial=([string]$d.SerialNumber).Trim(); Model=$d.FriendlyName; Bus=$d.BusType.ToString(); SizeBytes=[long]$d.Size; Letters=($v -join ',') } } | ConvertTo-Json -Compress";
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
        try { var text = _serial?.ReadExisting(); if (!string.IsNullOrEmpty(text)) Dispatcher.BeginInvoke(() => AppendSerial(text)); }
        catch { }
    }
    private void AppendSerial(string text) { serialBuffer.Append(text); if (serialBuffer.Length > 200000) serialBuffer.Remove(0, serialBuffer.Length - 150000); if (_serialLog is not null) { _serialLog.AppendText(text); _serialLog.ScrollToEnd(); } }

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
            await File.WriteAllTextAsync(path, serialBuffer.ToString());
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
            _mediaStatus.Text = $"OPNsense {version} está listo para grabar.\r\nFirma oficial válida y consola del Edge preparada.";
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

    private async Task RunMediaAsync(Func<Task> operation)
    {
        if (mediaBusy || internalBusy) return;
        if (!ConnectionsReady) { SetStatus("Primero conecta la consola y selecciona el USB en el paso 1."); return; }
        mediaBusy = true;
        PageHost.IsEnabled = false;
        foreach (var button in new[] { Nav1, Nav2, Nav3, Nav4, BackButton, NextButton }) button.IsEnabled = false;
        try { await operation(); }
        finally
        {
            mediaBusy = false;
            PageHost.IsEnabled = true;
            foreach (var button in new[] { Nav1, Nav2, Nav3, Nav4, NextButton }) button.IsEnabled = true;
            BackButton.IsEnabled = _page > 0;
            NextButton.IsEnabled = _page != 0 || ConnectionsReady;
            foreach (var nav in new[] { Nav2, Nav3, Nav4 }) nav.IsEnabled = ConnectionsReady;
        }
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
    private static Expander Details(string title, string text) => new()
    {
        Header = title,
        Margin = new Thickness(0, 8, 0, 14),
        Content = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 21,
            Foreground = new SolidColorBrush(Color.FromRgb(73, 91, 100)),
            Margin = new Thickness(0, 12, 0, 0)
        }
    };
    private Button PrimaryButton(string text) => new() { Content = text, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 180 };
    private Button QuietButton(string text) => new() { Content = text, Style = (Style)FindResource("QuietButton"), HorizontalAlignment = HorizontalAlignment.Left };

    protected override void OnClosed(EventArgs e)
    {
        try { if (_serial is { IsOpen: true }) _serial.Close(); _serial?.Dispose(); } catch { }
        base.OnClosed(e);
    }

    private sealed record DiskInfo(int Number, string Serial, string Model, string Bus, long SizeBytes, string Letters)
    {
        public string Label => $"{(string.IsNullOrWhiteSpace(Letters) ? "Sin letra" : Letters + ":")} · {Model} · {SizeBytes / 1073741824d:0.0} GiB";
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

