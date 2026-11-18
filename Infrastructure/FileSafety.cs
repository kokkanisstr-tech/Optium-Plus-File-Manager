using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OptiumPlusFileManager.Infrastructure;

public static partial class FileSafety
{
    [GeneratedRegex("-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|\\bAKIA[0-9A-Z]{16}\\b|\\bgh[pousr]_[A-Za-z0-9]{20,}\\b|\\bsk-[A-Za-z0-9_-]{20,}\\b|(?im)^\\s*(password|passwd|api[_-]?key|secret|connectionstring)\\s*[:=]\\s*\\S+", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPatterns();

    public static string SanitizeFileName(string fileName)
    {
        var leaf = Path.GetFileName(fileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(leaf.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray())
            .Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..")
            return "unnamed-file";
        var stem = Path.GetFileNameWithoutExtension(safe).TrimEnd(' ', '.');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(stem, "^(COM|LPT)[1-9]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            safe = "_" + safe;
        return safe.Length > 180 ? safe[..180] : safe;
    }

    public static bool ContainsSecrets(string fileName, string text) =>
        Path.GetFileName(fileName).Equals(".env", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(fileName).StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
        SecretPatterns().IsMatch(text);

    public static bool IsWithin(string candidatePath, string rootPath)
    {
        var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasReparsePoint(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var current = fullPath;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        return true;
                }
                catch (IOException) { return true; }
                catch (UnauthorizedAccessException) { return true; }
            }
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }
        return false;
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task<bool> WaitUntilReadyAsync(string path, CancellationToken cancellationToken = default)
    {
        long previousLength = -1;
        DateTime previousWrite = DateTime.MinValue;
        var stableChecks = 0;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path)) return false;
            try
            {
                var info = new FileInfo(path);
                var length = info.Length;
                var lastWrite = info.LastWriteTimeUtc;
                stableChecks = length == previousLength && lastWrite == previousWrite ? stableChecks + 1 : 0;
                previousLength = length;
                previousWrite = lastWrite;
                if (stableChecks >= 2)
                {
                    await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    return true;
                }
            }
            catch (IOException) { stableChecks = 0; }
            catch (UnauthorizedAccessException) { stableChecks = 0; }
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
        return false;
    }
}
