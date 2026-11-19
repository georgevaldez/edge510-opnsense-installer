using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Renci.SshNet;

namespace GeorgeValdez.Edge510Installer;

internal sealed class InternalInstaller : IDisposable
{
    private readonly SshClient client;
    private readonly string host;
    private readonly string password;
    private readonly string folder;
    private readonly Action<string> progress;
    private byte[] hostKey = [];
    private string serial = "";
    private long size;
    private string backup = "";
    private string backupHash = "";
    private bool prepared;
    private string? gateway;
    private string? dns;
    private const string Disk = "da1";

    internal InternalInstaller(string host, string password, string folder, Action<string> progress, bool usePcNetwork)
    {
        this.host = host;
        this.password = password;
        this.folder = folder;
        this.progress = progress;
        if (usePcNetwork)
        {
            var target = IPAddress.Parse(host).GetAddressBytes();
            if (target.Length != 4) throw new IOException("La configuración de administración automática requiere IPv4.");
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetIPProperties())
                .Where(p => p.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                    a.Address.GetAddressBytes().Zip(target, (x, y) => (byte)(x ^ y))
                    .Zip(a.IPv4Mask.GetAddressBytes(), (x, mask) => x & mask).All(x => x == 0)))
                .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                .ToList();
            if (candidates.Count != 1) throw new IOException("No se pudo identificar una única red del PC para este Edge. Revisa la dirección o desmarca la configuración automática de gateway y DNS.");
            gateway = candidates[0].GatewayAddresses.First(g => g.Address.AddressFamily == AddressFamily.InterNetwork).Address.ToString();
            if (gateway == host) throw new IOException("El PC usa el propio Edge como gateway. Desmarca la opción de usar gateway y DNS del PC y vuelve a preparar.");
            dns = string.Join(",", candidates[0].DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)));
            if (dns.Length == 0) throw new IOException("La red seleccionada del PC no proporciona DNS IPv4.");
        }
        Directory.CreateDirectory(folder);
        client = new SshClient(host, "root", password);
        client.HostKeyReceived += (_, args) =>
        {
            args.CanTrust = hostKey.Length == 0 || args.HostKey.SequenceEqual(hostKey);
            if (args.CanTrust) hostKey = args.HostKey.ToArray();
        };
        client.Connect();
    }

    private string Run(string text, int timeoutSeconds = 120)
    {
        using var command = client.CreateCommand(text);
        command.CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        var result = command.Execute();
        if (command.ExitStatus != 0)
            throw new IOException(command.Error + result);
        return result.Trim();
    }

    internal string Prepare()
    {
        progress("Comprobando el Edge y el destino interno…");
        if (Run("kenv smbios.system.product") != "EDGE510" ||
            Run("kenv smbios.system.version") != "1" ||
            Run("kenv smbios.bios.version") != "VELOCLOUD-EDGE-01.00.00.05")
            throw new IOException("Esta conversión requiere el Edge 510 con firmware 01.00.00.05.");
        var interfaces = Run("ifconfig -l");
        if (!new[] { "igb0", "igb1", "igb2", "igb3" }.All(interfaces.Contains) ||
            long.Parse(Run("sysctl -n hw.realmem")) < 3000000000)
            throw new IOException("Se requieren las cuatro interfaces Intel y al menos 3 GB de memoria.");
        var mounts = Run("mount -p");
        if (!mounts.StartsWith("/dev/ufs/OPNsense_Install ", StringComparison.Ordinal) ||
            Regex.IsMatch(mounts, @"(?m)^/dev/da1") || mounts.Contains("\t/mnt"))
            throw new IOException("Arranca desde el USB y deja el disco interno desmontado.");
        var identity = Run("geom disk list da1");
        if (!identity.Contains("descr: Generic Ultra HS-COMBO"))
            throw new IOException("El modelo del disco interno requiere revisión.");
        serial = Regex.Match(identity, @"ident:s*(d+)").Groups[1].Value;
        size = long.Parse(Regex.Match(identity, @"Mediasize:s*(d+)").Groups[1].Value);
        if (serial.Length == 0 || size != 7650410496)
            throw new IOException("La capacidad o la serie del destino no coincide con el perfil compatible.");
        if (Run("geom disk list da0").Contains("ident: " + serial))
            throw new IOException("El destino coincide con el USB de arranque.");
        File.WriteAllText(Path.Combine(folder, "identidad-interna.txt"), identity + "\n" + mounts);
        backup = Path.Combine(folder, "EDGE510-fabrica-" + serial + ".img");
        if (File.Exists(backup) || File.Exists(backup + ".partial"))
            throw new IOException("Ya hay un respaldo en esta carpeta. Usa una carpeta nueva para conservarlo y evitar sobrescrituras.");
        if (new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace < size + 1073741824)
            throw new IOException("Faltan al menos 9 GB libres para el respaldo.");
        progress("Guardando la configuración del PIC y desactivando su watchdog…");
        DisableWatchdog();
        progress("Guardando una copia completa del disco interno…");
        using (var command = client.CreateCommand("dd if=/dev/da1 bs=1048576"))
        {
            command.CommandTimeout = TimeSpan.FromHours(2);
            var pending = command.BeginExecute();
            long total = 0;
            var buffer = new byte[1048576];
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var file = new FileStream(backup + ".partial", FileMode.CreateNew))
            {
                int count;
                long last = 0;
                while ((count = command.OutputStream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    file.Write(buffer, 0, count);
                    digest.AppendData(buffer, 0, count);
                    total += count;
                    if (total - last >= 134217728)
                    {
                        progress($"Respaldo del disco interno · {total * 100 / size}%");
                        last = total;
                    }
                }
                file.Flush(true);
            }
            command.EndExecute(pending);
            if (command.ExitStatus != 0 || total != size)
                throw new IOException("El respaldo quedó incompleto. El destino conserva sus datos.");
            backupHash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        }
        progress("Verificando el respaldo con una segunda lectura en el Edge…");
        if (Run("dd if=/dev/da1 bs=1048576 2>/tmp/gv-backup-read.log | sha256 -q", 1800) != backupHash)
            throw new IOException("Las dos lecturas del disco interno difieren.");
        File.Move(backup + ".partial", backup);
        File.WriteAllText(backup + ".sha256", backupHash + "  " + Path.GetFileName(backup));
        prepared = true;
        return $"Destino {Disk} · Generic Ultra HS-COMBO · serie {serial}\nCapacidad 7,125 GiB. Respaldo completo verificado en {backup}\nSSH SHA256:{Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=')}";
    }

    private SftpClient OpenSftp()
    {
        var ftp = new SftpClient(host, "root", password);
        ftp.HostKeyReceived += (_, args) => args.CanTrust = args.HostKey.SequenceEqual(hostKey);
        ftp.Connect();
        return ftp;
    }

    private static byte[] Payload(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(x => x.EndsWith("." + name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n"));
    }

    private void UploadPayload()
    {
        Run("mkdir -m 700 /tmp/gv-install");
        using var ftp = OpenSftp();
        foreach (var name in new[] { "04-edge510-pic", "install-finalize.sh", "admin-network.php" })
        {
            var bytes = Payload(name);
            using var stream = new MemoryStream(bytes);
            ftp.UploadFile(stream, "/tmp/gv-install/" + name, false);
            if (Run("sha256 -q /tmp/gv-install/" + name) != Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())
                throw new IOException("No coincide el archivo transferido al Edge.");
        }
        Run("test ! -e /usr/libexec/bsdinstall/local.post-configure");
        using var hook = new MemoryStream(Encoding.ASCII.GetBytes(
            "#!/bin/sh\nexec /bin/sh /tmp/gv-install/install-finalize.sh \"$1\" /tmp/gv-install " + serial + "\n"));
        ftp.UploadFile(hook, "/usr/libexec/bsdinstall/local.post-configure", false);
        Run("sh -n /tmp/gv-install/install-finalize.sh; sh -n /tmp/gv-install/04-edge510-pic");
    }

    private void DisableWatchdog()
    {
        Run("kldload -n ismt; kldload -n smb");
        var location = Run("sysctl -n dev.smb.1.%parent; sysctl -n dev.smbus.1.%parent");
        if (location.Replace("\r", "") != "smbus1\nismt0")
            throw new IOException("El bus del PIC no coincide con el perfil.");
        Run("smbmsg -f /dev/smb1 -s 0x4a -o 1 0");
        var first = Run("smbmsg -f /dev/smb1 -s 0x4a -i 1");
        var second = Run("smbmsg -f /dev/smb1 -s 0x4a -i 1");
        var picBackup = Path.Combine(folder, "pic-original.txt");
        if (!File.Exists(picBackup)) File.WriteAllText(picBackup, first + "\n" + second + "\n");
        if (!((first == "0xff" && second == "0xff") || (first == "0x00" && second == "0x00")))
            throw new IOException("El PIC tiene una configuración que requiere revisión.");
        for (int offset = 0; offset < 2; offset++)
        {
            Run($"smbmsg -f /dev/smb1 -s 0x48 -c {offset} -o 1 0");
            var ready = false;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                Thread.Sleep(50);
                if (Run("smbmsg -f /dev/smb1 -s 0x48 -i 1") == "0x00") { ready = true; break; }
            }
            if (!ready) throw new IOException("El PIC no confirmó la escritura.");
        }
        Run("smbmsg -f /dev/smb1 -s 0x4a -o 1 0");
        if (Run("smbmsg -f /dev/smb1 -s 0x4a -i 1") != "0x00" ||
            Run("smbmsg -f /dev/smb1 -s 0x4a -i 1") != "0x00")
            throw new IOException("La configuración del watchdog no coincide.");
        Run("smbmsg -f /dev/smb1 -s 0x44 -c 0 -o 1 0");
        if (Run("smbmsg -f /dev/smb1 -s 0x44 -c 0 -w -i 2") != "0x0000")
            throw new IOException("El watchdog sigue activo.");
    }

    internal void Install()
    {
        if (!prepared) throw new IOException("Completa primero el respaldo.");
        if (!Run("geom disk list da1").Contains("ident: " + serial))
            throw new IOException("Cambió el destino interno.");
        using (var file = File.OpenRead(backup))
            if (Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant() != backupHash)
                throw new IOException("El respaldo local cambió.");
        var marker = Path.Combine(folder, "instalacion-iniciada.txt");
        if (File.Exists(marker)) throw new IOException("Hay una instalación previa pendiente de revisar.");
        progress("Desactivando y verificando el watchdog del PIC…");
        DisableWatchdog();
        UploadPayload();
        if (gateway is not null && dns is not null)
        {
            progress("Guardando la puerta de enlace y los DNS de la red de administración…");
            Run($"/usr/local/bin/php /tmp/gv-install/admin-network.php {gateway} {dns}");
        }
        File.WriteAllText(marker, DateTimeOffset.Now + "\n" + serial);
        using var shell = client.CreateShellStream("xterm", 100, 35, 1000, 700, 65536);
        using var log = new StreamWriter(Path.Combine(folder, "instalacion-terminal.log")) { AutoFlush = true };
        string Wait(string text, int seconds = 120)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var output = new StringBuilder();
            while (timer.Elapsed.TotalSeconds < seconds)
            {
                if (shell.DataAvailable)
                {
                    var chunk = shell.Read(); log.Write(chunk); output.Append(chunk);
                    var clean = Regex.Replace(output.ToString(), @"\x1b\[[0-?]*[ -/]*[@-~]", "");
                    if (clean.Contains("Installation Error") || clean.Contains("Installation Abort") ||
                        clean.Contains("Partitioning error") || clean.Contains("Device busy"))
                        throw new IOException("El instalador informó un error. Revisa el registro antes de reintentar.");
                    if (clean.Contains(text))
                    {
                        Thread.Sleep(800);
                        if (shell.DataAvailable) { var rest = shell.Read(); log.Write(rest); output.Append(rest); }
                        return Regex.Replace(output.ToString(), @"\x1b\[[0-?]*[ -/]*[@-~]", "");
                    }
                }
                Thread.Sleep(100);
            }
            throw new IOException("El instalador quedó esperando en " + text + ". Conserva la consola y revisa el registro.");
        }
        void Send(string text) { shell.Write(text); Thread.Sleep(300); }
        Wait("Enter an option:"); Send("8\r"); Wait("root@OPNsense:");
        Send("env TERM=xterm BSDINSTALL_KEYMAP_DONE=1 bsdinstall opnsense; echo GV_INSTALL_EXIT=$status\r");
        Wait("Choose one of the following tasks"); Send("\x1bOB\r");
        var diskMenu = Wait("Please select a disk to continue");
        if (!diskMenu.Contains("da0") || !diskMenu.Contains("da1"))
            throw new IOException("El menú de discos cambió. Revisa la consola.");
        Send("\x1bOB\r");
        var confirmation = Wait("Last Chance!");
        if (!confirmation.Contains("da1") || Regex.IsMatch(confirmation, @"\bda0\b"))
            throw new IOException("La confirmación del destino es ambigua.");
        progress("Instalando OPNsense en la memoria interna…");
        Send("y");
        Wait("Final Configuration", 1800); Send("\x1bOB\r");
        Wait("Installation Complete", 180); Send("\x1bOB\r");
        Wait("GV_INSTALL_EXIT=42");
        Run("mount -o ro /dev/gpt/rootfs /mnt");
        try
        {
            Run("grep -F '/dev/gpt/rootfs' /mnt/etc/fstab");
            Run("grep -F 'comconsole_port=\"0x2f8\"' /mnt/boot/loader.conf.local");
            Run("test -x /mnt/usr/local/etc/rc.syshook.d/early/04-edge510-pic");
            if (Run("sha256 -q /mnt/usr/local/etc/rc.syshook.d/early/04-edge510-pic") !=
                Convert.ToHexString(SHA256.HashData(Payload("04-edge510-pic"))).ToLowerInvariant())
                throw new IOException("No coincide el servicio de estado instalado.");
            if (Run("sha256 -q /mnt/boot/gptboot") != Run("sha256 -q /boot/gptboot"))
                throw new IOException("No coincide el cargador instalado.");
        }
        finally { Run("umount /mnt"); }
        File.WriteAllText(Path.Combine(folder, "instalacion-completada.txt"), Run("gpart show da1"));
        progress("Instalación terminada. Apagando el Edge para retirar el USB…");
        using var halt = client.CreateCommand("shutdown -p now");
        halt.BeginExecute();
    }

    internal void VerifyInstalled()
    {
        client.Disconnect();
        client.Connect();
        var identity = Run("geom disk list da0; mount -p; kenv smbios.system.product; ifconfig -l");
        if (!identity.Contains("ident: " + serial) ||
            !Regex.IsMatch(identity, @"(?m)^/dev/(gpt/rootfs|ufs/[^\s]+|da0p3)\s+/\s+ufs\s") ||
            identity.Contains("/dev/ufs/OPNsense_Install") ||
            !new[] { "igb0", "igb1", "igb2", "igb3" }.All(identity.Contains))
            throw new IOException("Todavía no se confirmó el arranque desde la memoria interna con las cuatro interfaces.");
        var boot = Run("sysctl -n kern.boottime");
        long CpuTicks() => Run("sysctl -n kern.cp_time").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Sum(long.Parse);
        var previousTicks = CpuTicks();
        var picCheck = Run("/bin/sh /usr/local/etc/rc.syshook.d/early/04-edge510-pic --check");
        var picBus = Regex.Match(picCheck, @"Bus del PIC:\s*(/dev/smb\d+)").Groups[1].Value;
        if (!Regex.IsMatch(picBus, @"^/dev/smb\d+$"))
            throw new IOException("No se identificó el bus del PIC después del arranque interno.");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            var currentTicks = CpuTicks();
            if (currentTicks < previousTicks ||
                Run($"smbmsg -f {picBus} -s 0x44 -c 0 -w -i 2") != "0x0000")
                throw new IOException("Cambió el arranque o el watchdog volvió a activarse.");
            previousTicks = currentTicks;
            Run("curl --noproxy '*' -ksf --max-time 5 https://127.0.0.1/ -o /dev/null");
            progress($"Arranque interno verificado · {(int)timer.Elapsed.TotalSeconds} de 620 segundos");
            Thread.Sleep(20000);
        } while (timer.Elapsed.TotalSeconds < 620);
        File.WriteAllText(Path.Combine(folder, "arranque-interno-verificado.txt"),
            DateTimeOffset.Now + "\n" + identity + "\n" + boot + "\n" + Run("uptime"));
        if (gateway is not null)
            File.WriteAllText(Path.Combine(folder, "repositorio-verificado.txt"), Run("pkg-static update", 180));
        progress("OPNsense instalado y comprobado desde la memoria interna durante más de diez minutos.");
    }

    public void Dispose() => client.Dispose();
}
