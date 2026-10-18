namespace EntLimit.Core;

/// <summary>Out-of-process opt-out latch. Its presence overrides saved settings.</summary>
public static class EmergencyStop
{
    public const string FileName = "emergency-stop.flag";
    public static bool IsActive(string root)
    {
        try { _ = File.GetAttributes(Path.Combine(root, FileName)); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        // An unreadable latch must not silently resume enforcement or recovery.
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
