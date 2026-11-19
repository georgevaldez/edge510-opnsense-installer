using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace GeorgeValdez.Edge510Installer;

internal static class SerialImage
{
    internal const string OriginalHash = "CD141E9A4C550E4E3A41CB15103A20DA33FAA4C0FA03980EDFF5D77FFAA13804";
    internal const string PatchedHash = "2551A66E5E7A5965B5D3CA7F85D6844A8120C4C9118FF96735662322893EB577";

    internal static string Prepare(string source, string sourceHash)
    {
        if (sourceHash != OriginalHash)
            throw new CryptographicException("La adaptación UART1 requiere la imagen Serial 26.7 validada para este perfil.");
        var destination = Path.Combine(Path.GetDirectoryName(source)!, "OPNsense-26.7-Edge510-UART1.img");
        File.Copy(source, destination, true);
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(x => x.EndsWith(".uart1-patch.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var document = JsonDocument.Parse(stream);
        using var file = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        foreach (var patch in document.RootElement.EnumerateArray())
        {
            var original = Convert.FromHexString(patch.GetProperty("original").GetString()!);
            var replacement = Convert.FromHexString(patch.GetProperty("replacement").GetString()!);
            if (original.Length != replacement.Length) throw new IOException("Parche UART1 inválido.");
            file.Position = patch.GetProperty("offset").GetInt64();
            var current = new byte[original.Length];
            file.ReadExactly(current);
            if (!current.SequenceEqual(original)) throw new IOException("El contenido del cargador no coincide.");
            file.Position -= current.Length;
            file.Write(replacement);
        }
        file.Flush(true);
        file.Position = 0;
        if (Convert.ToHexString(SHA256.HashData(file)) != PatchedHash)
            throw new CryptographicException("No coincide el SHA-256 de la adaptación UART1.");
        return destination;
    }
}
