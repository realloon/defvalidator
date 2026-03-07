using MemoryPack;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace DefValidator.Core;

internal static class CacheFiles {
    public static string BuildPath(string prefix, string extension, IEnumerable<string> fingerprintLines) {
        var builder = new StringBuilder();
        foreach (var line in fingerprintLines) {
            builder.Append(line).Append('\n');
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return Path.Combine(GetCacheDirectory(), $"{prefix}-{hash}.{extension}");
    }

    public static bool TryReadMemoryPack<T>(string path, out T? value) {
        try {
            if (!File.Exists(path)) {
                value = default;
                return false;
            }

            value = MemoryPackSerializer.Deserialize<T>(File.ReadAllBytes(path));
            return value is not null;
        } catch {
            value = default;
            return false;
        }
    }

    public static void TryWriteMemoryPack<T>(string path, T value) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, MemoryPackSerializer.Serialize(value));
        } catch {
        }
    }

    public static bool TryReadXml(string path, out XDocument document) {
        try {
            if (!File.Exists(path)) {
                document = new XDocument(new XElement("Defs"));
                return false;
            }

            document = XDocument.Load(path, LoadOptions.None);
            return document.Root is not null;
        } catch {
            document = new XDocument(new XElement("Defs"));
            return false;
        }
    }

    public static void TryWriteXml(string path, XDocument document) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            document.Save(path, SaveOptions.DisableFormatting);
        } catch {
        }
    }

    private static string GetCacheDirectory() {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows()) {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "defvalidator");
        }

        if (OperatingSystem.IsMacOS()) {
            return Path.Combine(home, "Library", "Caches", "defvalidator");
        }

        return Path.Combine(home, ".cache", "defvalidator");
    }
}
