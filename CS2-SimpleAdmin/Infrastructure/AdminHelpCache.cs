namespace CS2_SimpleAdmin.Infrastructure;

/// <summary>
/// Managed copy of <c>admin_help.txt</c> for css_adminhelp. The game thread only reads the published array; the file
/// is read, cut to <see cref="MaxLines"/> × <see cref="MaxLineChars"/> and colour-tag-processed on a background
/// worker, once at startup and again (in the background) whenever the file's timestamp changed since the last read.
/// </summary>
internal sealed class AdminHelpCache
{
    public const int MaxLines = 60;
    public const int MaxLineChars = 240;

    private sealed record Snapshot(string[] Lines, int OmittedLines, DateTime FileStamp);

    private volatile Snapshot? _snapshot;
    private int _refreshing;

    public bool IsLoaded => _snapshot != null;
    public string[] Lines => _snapshot?.Lines ?? [];
    public int OmittedLines => _snapshot?.OmittedLines ?? 0;

    /// <summary>
    /// Reads the file (blocking I/O: call from a background worker only) and publishes it if it changed or nothing is
    /// loaded yet. <paramref name="decorate"/> turns a raw line into its display form (colour tags).
    /// Returns false when a refresh was already running or the file is unreadable (the old copy stays published).
    /// </summary>
    public bool Refresh(string path, Func<string, string> decorate)
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return false;
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (_snapshot is { } current && current.FileStamp == stamp) return true;

            var lines = new List<string>(Math.Min(MaxLines, 64));
            var total = 0;
            foreach (var raw in File.ReadLines(path))
            {
                total++;
                if (lines.Count >= MaxLines) continue; // keep counting only
                var text = raw.Length > MaxLineChars ? raw[..MaxLineChars] : raw;
                lines.Add(string.IsNullOrWhiteSpace(text) ? " " : decorate(text));
            }

            _snapshot = new Snapshot(lines.ToArray(), total - lines.Count, stamp);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RateLimitedLog.Warning("adminhelp.read", $"Unable to read admin_help.txt: {ex.Message}");
            return false;
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }
}
