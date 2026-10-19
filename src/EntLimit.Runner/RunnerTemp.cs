using EntLimit.Core;

namespace EntLimit.Runner;

/// <summary>Creates a writable scratch directory for short-lived runner processes.</summary>
public static class RunnerTemp
{
    public static string CreateDirectory(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        foreach (var root in CandidateRoots())
        {
            try
            {
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, prefix + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(path);
                var probe = Path.Combine(path, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                // Try the next candidate. A low-integrity host may not be able to write the user's %TEMP%.
            }
        }
        throw new IOException("No writable temporary directory is available for the runner.");
    }

    private static IEnumerable<string> CandidateRoots()
    {
        string? dataRoot = null;
        try { dataRoot = DataPaths.GetRoot(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        if (!string.IsNullOrWhiteSpace(dataRoot))
            yield return Path.Combine(dataRoot, "tmp");

        string? temp = null;
        try { temp = Path.GetTempPath(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!string.IsNullOrWhiteSpace(temp))
            yield return temp;
    }
}
