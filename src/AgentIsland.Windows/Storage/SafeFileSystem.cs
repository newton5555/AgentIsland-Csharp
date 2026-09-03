namespace AgentIsland.Windows.Storage;

/// Race-tolerant filesystem helpers shared by the Windows session and cost
/// readers. A provider can rotate or remove files while the monitor is
/// walking them; these helpers turn that into a partial pass, never a process
/// failure.
public static class SafeFileSystem
{
    public static List<string> EnumerateFiles(string root, string pattern)
    {
        var result = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try
        {
            using var walker = Directory.EnumerateFiles(root, pattern, options).GetEnumerator();
            while (true)
            {
                try
                {
                    if (!walker.MoveNext()) break;
                }
                catch
                {
                    // A directory vanished or became unreadable mid-walk.
                    break;
                }
                result.Add(walker.Current);
            }
        }
        catch
        {
        }
        return result;
    }

    public static List<string> EnumerateDirectories(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return new List<string>();
            return Directory.EnumerateDirectories(root).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static DateTimeOffset LastWriteTime(string path)
    {
        try
        {
            var utc = File.GetLastWriteTimeUtc(path);
            return utc.Year < 1700 ? DateTimeOffset.MinValue : new DateTimeOffset(utc);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }
}
