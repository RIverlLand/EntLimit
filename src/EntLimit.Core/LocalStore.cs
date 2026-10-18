using System.Text;
using System.Text.Json;

namespace EntLimit.Core;

public static class DataPaths
{
    public static string GetRoot()
    {
        var configured = Environment.GetEnvironmentVariable("ENT_LIMIT_DATA_DIR");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? File.Exists(Path.Combine(AppContext.BaseDirectory, "installed.flag"))
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EntLimit", "data")
                : Path.Combine(AppContext.BaseDirectory, "data")
            : configured);
    }
}

/// <summary>Local, single-writer JSON store. Corruption is surfaced; originals are never overwritten.</summary>
public sealed class LocalStore
{
    public string Root { get; }
    private readonly HashSet<string> recoveredDocuments = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public LocalStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public AppConfig LoadConfig()
    {
        return Load("config.json", () => new AppConfig(), ValidateConfig);
    }
    public void SaveConfig(AppConfig config)
    {
        ValidateConfig(config);
        Save("config.json", config);
    }
    public SessionState LoadSession()
    {
        return Load("session.json", () => new SessionState(), ValidateSession);
    }
    public void SaveSession(SessionState state)
    {
        ValidateSession(state);
        Save("session.json", state);
    }
    public List<AppCatalogEntry> LoadCatalog()
    {
        return Load("catalog.json", () => new List<AppCatalogEntry>(), ValidateCatalog);
    }
    public void SaveCatalog(List<AppCatalogEntry> catalog)
    {
        ValidateCatalog(catalog);
        Save("catalog.json", catalog);
    }

    private T Load<T>(string name, Func<T> defaults, Action<T> validate)
    {
        var path = Path.Combine(Root, name);
        if (!File.Exists(path)) return defaults();
        try
        {
            var document = ReadValidated(path, validate);
            recoveredDocuments.Remove(name);
            return document;
        }
        catch (InvalidDataException primaryError)
        {
            var backup = path + ".bak";
            if (!File.Exists(backup)) throw;
            T recovered;
            try { recovered = ReadValidated(backup, validate); }
            catch (InvalidDataException backupError)
            {
                throw new InvalidDataException($"{name} and its backup are invalid; both retained.",
                    new AggregateException(primaryError, backupError));
            }
            if (recoveredDocuments.Add(name))
                File.AppendAllText(Path.Combine(Root, "recovery.log"),
                    $"{DateTimeOffset.UtcNow:O} Recovered {name} from validated backup; primary retained until next save.{Environment.NewLine}");
            return recovered;
        }
    }

    private static T ReadValidated<T>(string path, Action<T> validate)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var document = JsonSerializer.Deserialize<T>(stream, JsonOptions)
                ?? throw new InvalidDataException("Document is empty; original retained.");
            validate(document);
            return document;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Document contains invalid JSON; original retained.", ex);
        }
    }

    private void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, name);
        // Refuse to silently replace a corrupt or incompatible existing document.
        if (File.Exists(path))
        {
            if (name == "config.json") _ = LoadConfig();
            else if (name == "session.json") _ = LoadSession();
            else if (name == "catalog.json") _ = LoadCatalog();
        }
        var temp = Path.Combine(Root, $".{name}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path) && recoveredDocuments.Contains(name))
            {
                // Copy evidence first; never rotate a corrupt primary into the known-good backup.
                var evidence = path + $".{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}.corrupt";
                File.Copy(path, evidence, overwrite: false);
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                recoveredDocuments.Remove(name);
            }
            else if (File.Exists(path)) File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void ValidateConfig(AppConfig config)
    {
        if (config is null || config.SchemaVersion != 1 || config.Apps is null || config.Domains is null)
            throw new InvalidDataException("Unsupported or incomplete configuration; original retained.");
        if (config.UnlockMinutes is < 90 or > 1440 || config.WarningMinutes < 0 || config.WarningMinutes >= config.UnlockMinutes)
            throw new InvalidDataException("Unlock duration must be 90–1440 minutes and warning shorter than duration.");
        if (config.Apps.Any(a => a is null || string.IsNullOrWhiteSpace(a.Id)
            || string.IsNullOrWhiteSpace(a.Name) || string.IsNullOrWhiteSpace(a.ExecutablePath)))
            throw new InvalidDataException("Managed applications need an ID, name and executable path.");
        if (config.Apps.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != config.Apps.Count)
            throw new InvalidDataException("Managed application IDs must be unique.");
        if (config.Domains.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Website domains must not be empty.");
    }

    private static void ValidateSession(SessionState state)
    {
        if (state is null || state.SchemaVersion != 1 || state.ActiveProcesses is null
            || state.DrainingProcesses is null || state.History is null)
            throw new InvalidDataException("Unsupported or incomplete session; original retained.");
        if (!Enum.IsDefined(state.Phase) || state.GrantedMinutes is < 90 or > 1440)
            throw new InvalidDataException("Invalid session state; original retained.");
        if (state.Phase == SessionPhase.Active && (state.StartedAtUtc is null || state.DeadlineUtc is null
            || state.DeadlineUtc < state.StartedAtUtc))
            throw new InvalidDataException("Active session has no valid timing information; original retained.");
        if (state.History.Any(x => x is null || string.IsNullOrWhiteSpace(x.AttemptId))
            || state.History.Select(x => x.AttemptId).Distinct().Count() != state.History.Count)
            throw new InvalidDataException("Learning completion IDs must be unique; original retained.");
        if (state.ActiveProcesses.Concat(state.DrainingProcesses).Any(x => x is null
            || x.Pid <= 0 || string.IsNullOrWhiteSpace(x.ExecutablePath)))
            throw new InvalidDataException("Invalid process identity; original retained.");
    }

    private static void ValidateCatalog(List<AppCatalogEntry> catalog)
    {
        if (catalog is null || catalog.Any(x => x is null || string.IsNullOrWhiteSpace(x.Name)
            || string.IsNullOrWhiteSpace(x.ExecutablePath)))
            throw new InvalidDataException("Invalid application catalog; original retained.");
    }
}
