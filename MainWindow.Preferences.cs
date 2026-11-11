using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Button = System.Windows.Controls.Button;

namespace RDCM;

public enum ConnectionListMode
{
    All,
    Favorites,
    Recent
}

/// <summary>Favourites, recent connections, list density, group expansion, and window placement.</summary>
public partial class MainWindow
{
    private const int RecentProfilesShownInWorkspace = 5;

    private readonly HashSet<string> _collapsedGroups = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<Guid> _favoriteIds = [];
    private Dictionary<Guid, int> _recentRanks = [];
    private ConnectionListMode _listMode = ConnectionListMode.All;
    private bool _isCompactList;
    private bool _profilesLoaded;
    private bool _preferencesReady;

    /// <summary>Favourite profile ids; replaced (not mutated) on change so bindings refresh.</summary>
    public IReadOnlySet<Guid> FavoriteIds => _favoriteIds;

    public bool IsCompactList
    {
        get => _isCompactList;
        private set
        {
            if (_isCompactList == value)
            {
                return;
            }
            _isCompactList = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCompactList)));
        }
    }

    public IReadOnlyList<ConnectionProfile> RecentProfiles { get; private set; } = [];

    private void InitializePreferences()
    {
        _favoriteIds = [.. _settings.FavoriteProfileIds];
        _collapsedGroups.UnionWith(_settings.CollapsedGroups);
        ((GroupExpansionConverter)FindResource("GroupExpansionConverter")).CollapsedGroups = _collapsedGroups;
        UpdateRecentRanks();
        IsCompactList = _settings.CompactConnectionList;
        CompactListToggle.IsChecked = IsCompactList;
        _preferencesReady = true;
    }

    private void RestoreWindowPlacement()
    {
        var placement = WindowPlacementSettings.Validate(_settings.MainWindow, MonitorLayout.GetMonitors(), MonitorLayout.GetSystemDpi());
        if (placement is null)
        {
            return;
        }
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) => WindowPlacementService.Apply(this, placement);
    }

    /// <summary>Saves preferences immediately. Failures are reported in the status bar, not as dialogs.</summary>
    private void SavePreferences()
    {
        if (!TrySaveSettings(out var error))
        {
            StatusText.Text = "Preferences couldn't be saved. RDCM will try again when it closes.";
            AppLog.Warning("Saving preferences failed.", error);
        }
    }

    private bool TrySaveSettings(out Exception? error)
    {
        SyncSettingsFromState();
        try
        {
            _settingsStore.Save(_settings);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception;
            return false;
        }
    }

    private void SyncSettingsFromState()
    {
        if (_profilesLoaded)
        {
            var existingIds = Profiles.Select(profile => profile.Id).ToHashSet();
            if (_favoriteIds.Any(id => !existingIds.Contains(id)))
            {
                _favoriteIds = _favoriteIds.Where(existingIds.Contains).ToHashSet();
            }
            var existingGroups = Profiles.Select(profile => profile.Group).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _collapsedGroups.IntersectWith(existingGroups);
            _settings.PruneProfileReferences(existingIds);
        }
        _settings.FavoriteProfileIds = [.. _favoriteIds];
        _settings.CollapsedGroups = [.. _collapsedGroups.Order(StringComparer.OrdinalIgnoreCase)];
        _settings.CompactConnectionList = IsCompactList;
    }

    private void ToggleFavorite(ConnectionProfile profile)
    {
        var favorites = new HashSet<Guid>(_favoriteIds);
        var added = favorites.Add(profile.Id);
        if (!added)
        {
            favorites.Remove(profile.Id);
        }
        _favoriteIds = favorites;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavoriteIds)));
        SavePreferences();
        if (_listMode == ConnectionListMode.Favorites)
        {
            RefreshProfileView();
        }
        UpdateSelectionCommands();
        StatusText.Text = added ? $"Added {profile.Name} to favourites" : $"Removed {profile.Name} from favourites";
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionsList.SelectedItem is ConnectionProfile profile)
        {
            ToggleFavorite(profile);
        }
    }

    private void RowFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ConnectionProfile profile })
        {
            ToggleFavorite(profile);
            e.Handled = true;
        }
    }

    /// <summary>Called when a session actually reaches Connected; failed attempts are never recorded.</summary>
    private void RecordRecentConnection(Guid profileId)
    {
        if (Profiles.All(profile => profile.Id != profileId))
        {
            return;
        }
        _settings.RecentConnections = RecentConnectionList.Record(_settings.RecentConnections, profileId, DateTimeOffset.Now);
        UpdateRecentRanks();
        SavePreferences();
        if (_listMode == ConnectionListMode.Recent)
        {
            ApplyListMode();
        }
    }

    private void RemoveProfileReferences(Guid profileId)
    {
        if (_favoriteIds.Contains(profileId))
        {
            _favoriteIds = _favoriteIds.Where(id => id != profileId).ToHashSet();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavoriteIds)));
        }
        _settings.RecentConnections = _settings.RecentConnections.Where(recent => recent.ProfileId != profileId).ToList();
        UpdateRecentRanks();
        SavePreferences();
    }

    private void UpdateRecentRanks()
    {
        _recentRanks = _settings.RecentConnections
            .Select((recent, index) => (recent.ProfileId, index))
            .ToDictionary(item => item.ProfileId, item => item.index);
        RecentProfiles = _settings.RecentConnections
            .Select(recent => Profiles.FirstOrDefault(profile => profile.Id == recent.ProfileId))
            .OfType<ConnectionProfile>()
            .Take(RecentProfilesShownInWorkspace)
            .ToList();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecentProfiles)));
        if (RecentConnectionsPanel is not null)
        {
            RecentConnectionsPanel.Visibility = RecentProfiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void RecentConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ConnectionProfile profile })
        {
            ConnectionsList.SelectedItem = profile;
            OpenTab(profile);
        }
    }

    private void ConnectionFilter_Checked(object sender, RoutedEventArgs e)
    {
        var mode = sender == ShowFavoritesButton ? ConnectionListMode.Favorites
            : sender == ShowRecentButton ? ConnectionListMode.Recent
            : ConnectionListMode.All;
        if (mode == _listMode || _profileView is null)
        {
            return;
        }
        _listMode = mode;
        ApplyListMode();
    }

    private void SetListMode(ConnectionListMode mode)
    {
        (mode switch
        {
            ConnectionListMode.Favorites => ShowFavoritesButton,
            ConnectionListMode.Recent => ShowRecentButton,
            _ => ShowAllButton
        }).IsChecked = true;
    }

    /// <summary>Grouped by group for All/Favourites; ungrouped, most recent first, for Recent.</summary>
    private void ApplyListMode()
    {
        using (_profileView.DeferRefresh())
        {
            _profileView.GroupDescriptions.Clear();
            if (_listMode == ConnectionListMode.Recent)
            {
                _profileView.CustomSort = new RecentOrderComparer(_recentRanks);
            }
            else
            {
                _profileView.CustomSort = null;
                _profileView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ConnectionProfile.Group)));
            }
        }
        RefreshProfileView();
    }

    private void CompactListToggle_Changed(object sender, RoutedEventArgs e)
    {
        IsCompactList = CompactListToggle.IsChecked == true;
        if (_preferencesReady)
        {
            SavePreferences();
        }
    }

    private void ConnectionGroup_ExpansionChanged(object sender, RoutedEventArgs e)
    {
        if (!_preferencesReady || e.OriginalSource is not Expander { DataContext: CollectionViewGroup { Name: string name } } expander)
        {
            return;
        }
        var changed = expander.IsExpanded ? _collapsedGroups.Remove(name) : _collapsedGroups.Add(name);
        if (changed)
        {
            SavePreferences();
        }
    }

    private sealed class RecentOrderComparer(IReadOnlyDictionary<Guid, int> ranks) : IComparer
    {
        public int Compare(object? x, object? y) => Rank(x).CompareTo(Rank(y));

        private int Rank(object? item) =>
            item is ConnectionProfile profile && ranks.TryGetValue(profile.Id, out var rank) ? rank : int.MaxValue;
    }
}
