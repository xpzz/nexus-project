using System.Text.RegularExpressions;

namespace Nexus.Web.Setup;

/// <summary>Last lines of the newest log, with secrets removed. Logs are written without secrets, and this redacts again before anything reaches a page.</summary>
public static partial class LogTail
{
    [GeneratedRegex(@"(?i)\b(password|pwd|secret|token|authorization|api[-_ ]?key|client[_-]?secret|connection[-_ ]?string|private[-_ ]?key)\b(\s*[=:]\s*)(""[^""]*""|'[^']*'|[^;\s,]+)")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"(?i)bearer\s+[A-Za-z0-9\-\._~\+/]+=*")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();

    public static string Redact(string line)
    {
        var text = Jwt().Replace(line, "***jwt***");
        text = Bearer().Replace(text, "Bearer ***");
        return KeyValue().Replace(text, "$1$2***");
    }

    public static IReadOnlyList<string> Read(string directory, int lines = 80)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var file = new DirectoryInfo(directory).GetFiles("*.log").Concat(new DirectoryInfo(directory).GetFiles("*.txt")).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (file is null)
        {
            return [];
        }

        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var buffer = new Queue<string>(lines);
        while (reader.ReadLine() is { } line)
        {
            if (buffer.Count == lines)
            {
                buffer.Dequeue();
            }

            buffer.Enqueue(Redact(line.Length > 600 ? line[..600] + "…" : line));
        }

        return buffer.ToList();
    }
}
