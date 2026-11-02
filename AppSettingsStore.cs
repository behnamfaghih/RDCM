using System.IO;
using System.Text.Json;

namespace RDCM;

public sealed class AppSettings
{
    public const int MaxRecentConnections = 10;
    public const int MaxCollapsedGroups = 200;
    public const double DefaultSidebarWidth = 318;

    public string Appearance { get; set; } = "System";
    public double SidebarWidth { get; set; } = DefaultSidebarWidth;
    public bool SidebarCollapsed { get; set; }
    public bool CompactConnectionList { get; set; }
    public WindowPlacementSettings? MainWindow { get; set; }

    /// <summary>Group names (case-insensitive) collapsed in the connection list. Groups are expanded by default.</summary>
    public List<string> CollapsedGroups { get; set; } = [];

    /// <summary>Profile ids only; no connection details are duplicated here.</summary>
    public List<Guid> FavoriteProfileIds { get; set; } = [];

    /// <summary>Most recent first. Profile ids and timestamps only; never credentials.</summary>
    public List<RecentConnection> RecentConnections { get; set; } = [];

    /// <summary>Repairs values from older, hand-edited, or partially corrupted settings files.</summary>
    public void Normalize()
    {
        Appearance = Appearance is "System" or "Light" or "Dark" ? Appearance : "System";
        SidebarWidth = double.IsFinite(SidebarWidth) ? Math.Clamp(SidebarWidth, 240, 440) : DefaultSidebarWidth;
        CollapsedGroups = (CollapsedGroups ?? [])
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCollapsedGroups)
            .ToList();
        FavoriteProfileIds = (FavoriteProfileIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        RecentConnections = RecentConnectionList.Normalize(RecentConnections);
        MainWindow = MainWindow is { } placement && placement.HasPlausibleSize ? placement : null;
    }

    /// <summary>Drops favourite/recent references to profiles that no longer exist.</summary>
    public void PruneProfileReferences(IReadOnlySet<Guid> existingProfileIds)
    {
        FavoriteProfileIds = FavoriteProfileIds.Where(existingProfileIds.Contains).ToList();
        RecentConnections = RecentConnections.Where(recent => existingProfileIds.Contains(recent.ProfileId)).ToList();
    }
}

public sealed class RecentConnection
{
    public Guid ProfileId { get; set; }
    public DateTimeOffset LastConnected { get; set; }
}

public static class RecentConnectionList
{
    /// <summary>Moves (or adds) the profile to the top, without duplicates, keeping at most <paramref name="limit"/> entries.</summary>
    public static List<RecentConnection> Record(
        IEnumerable<RecentConnection> recents,
        Guid profileId,
        DateTimeOffset connectedAt,
        int limit = AppSettings.MaxRecentConnections) =>
        recents
            .Where(recent => recent.ProfileId != profileId)
            .Prepend(new RecentConnection { ProfileId = profileId, LastConnected = connectedAt })
            .Take(limit)
            .ToList();

    public static List<RecentConnection> Normalize(
        IEnumerable<RecentConnection?>? recents,
        int limit = AppSettings.MaxRecentConnections) =>
        (recents ?? [])
            .OfType<RecentConnection>()
            .Where(recent => recent.ProfileId != Guid.Empty)
            .OrderByDescending(recent => recent.LastConnected)
            .DistinctBy(recent => recent.ProfileId)
            .Take(limit)
            .ToList();
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppSettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RDCM",
            "settings.json");
    }

    public string FilePath { get; }

    /// <summary>Where an unreadable settings file is copied before defaults replace it.</summary>
    public string InvalidCopyPath => $"{FilePath}.invalid";

    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))
                ?? throw new JsonException("The settings file must contain a JSON object.");
            settings.Normalize();
            return settings;
        }
        catch (JsonException)
        {
            TryPreserveInvalidFile();
            throw;
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("The settings directory could not be determined.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void TryPreserveInvalidFile()
    {
        try
        {
            File.Copy(FilePath, InvalidCopyPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("The unreadable settings file could not be preserved.", exception);
        }
    }
}
