namespace EntLimit.Core;

/// <summary>Matches a registered executable or its validated descendants inside its installation.</summary>
public static class ApplicationProcessMatcher
{
    /// <param name="validatedAncestors">Monitor-provided live parent chain, checked for PID reuse and creation order.</param>
    public static bool Matches(ManagedApp app, ProcessIdentity candidate, IReadOnlyList<ProcessIdentity> validatedAncestors)
    {
        if (app is null || candidate is null || !app.Enabled) return false;
        string? main = Normalize(app.ExecutablePath), image = Normalize(candidate.ExecutablePath);
        if (main is null || image is null) return false;
        if (string.Equals(main, image, StringComparison.OrdinalIgnoreCase)) return true;

        string? installation = Path.GetDirectoryName(main);
        if (string.IsNullOrEmpty(installation)) return false;
        string root = Path.GetPathRoot(main) ?? "";
        // Registering an EXE at D:\ or a UNC share root cannot claim every descendant on that volume.
        if (string.Equals(installation.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return false;
        if (!image.StartsWith(installation.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;

        return validatedAncestors is not null && validatedAncestors.Any(ancestor => ancestor is not null
            && string.Equals(Normalize(ancestor.ExecutablePath), main, StringComparison.OrdinalIgnoreCase));
    }

    static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }
}
