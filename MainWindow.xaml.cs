using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Forms.Integration;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Ellipse = System.Windows.Shapes.Ellipse;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using TextTrimming = System.Windows.TextTrimming;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace RDCM;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const double SidebarMinWidth = 240;
    private const double SidebarMaxWidth = 440;

    private readonly ProfileStore _store;
    private readonly AppSettingsStore _settingsStore;
    private readonly ListCollectionView _profileView;
    private readonly List<StartupNotice> _startupNotices = [];
    private AppSettings _settings = new();

    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];
    public ObservableCollection<WorkspaceTab> Tabs { get; } = [];
    public ICollectionView ProfileView => _profileView;

    /// <summary>Session indicator per profile id, consumed by the connection list.</summary>
    public IReadOnlyDictionary<Guid, string> SessionIndicators { get; private set; } =
        new Dictionary<Guid, string>();

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow() : this(new ProfileStore(), new AppSettingsStore())
    {
    }

    /// <summary>Uses the given stores (for example isolated test data instead of the user's profile folder).</summary>
    public MainWindow(ProfileStore store, AppSettingsStore settingsStore)
    {
        _store = store;
        _settingsStore = settingsStore;
        InitializeComponent();
        ThemeManager.TrackWindow(this);
        LoadSettings();
        RestoreWindowPlacement();
        ProfileColumn.Width = new GridLength(_settings.SidebarWidth);
        if (_settings.SidebarCollapsed)
        {
            SetSidebarCollapsed(true);
        }
        AppearanceBox.SelectedIndex = _settings.Appearance switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0
        };
        ThemeManager.Apply(_settings.Appearance);
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        InitializePreferences();
        DataContext = this;
        _profileView = (ListCollectionView)CollectionViewSource.GetDefaultView(Profiles);
        _profileView.Filter = FilterProfile;
        _profileView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ConnectionProfile.Group)));
        LoadProfiles();
        UpdateRecentRanks();
        UpdateWorkspace();
        UpdateSelectionCommands();
        RegisterCommands();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        WorkspaceTabs.GotKeyboardFocus += WorkspaceTabs_GotKeyboardFocus;
        Loaded += (_, _) => Dispatcher.BeginInvoke(ShowStartupNotices, System.Windows.Threading.DispatcherPriority.ContextIdle);
        // WPF does not raise live-region events by itself; announce status changes explicitly.
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .AddValueChanged(StatusText, AnnounceStatus);
    }

    private void AnnounceStatus(object? sender, EventArgs e) =>
        System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(StatusText)?
            .RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);

    private sealed record StartupNotice(string Title, string Message, ChoiceDialogKind Kind, string? Details);

    // Problems found while loading are shown once the window is visible, so dialogs have an owner.
    private void ShowStartupNotices()
    {
        var notices = _startupNotices.ToList();
        _startupNotices.Clear();
        foreach (var notice in notices)
        {
            ChoiceDialog.ShowMessage(this, notice.Title, notice.Message, notice.Kind, notice.Details);
        }
    }

    private void ShowError(string title, string message, Exception exception, string? context = null)
    {
        AppLog.Error($"{title}. {context}", exception);
        ChoiceDialog.ShowMessage(this, title, message, ChoiceDialogKind.Error,
            context is null ? exception.Message : $"{exception.Message}{Environment.NewLine}{context}");
    }

    private void LoadSettings()
    {
        try
        {
            _settings = _settingsStore.Load();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("Settings could not be loaded; defaults are used.", exception);
            _startupNotices.Add(new StartupNotice(
                "Preferences couldn't be loaded",
                "RDCM is using default preferences. Your connections are not affected.",
                ChoiceDialogKind.Warning,
                exception is JsonException
                    ? $"The settings file is not valid. A copy was kept at {_settingsStore.InvalidCopyPath}."
                    : exception.Message));
        }
    }

    private void LoadProfiles()
    {
        try
        {
            var savedProfiles = _store.Load();
            var validProfiles = savedProfiles.Where(IsUsableProfile).ToList();
            var duplicateIds = validProfiles.GroupBy(profile => profile.Id)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            validProfiles.RemoveAll(profile => duplicateIds.Contains(profile.Id));
            foreach (var profile in validProfiles.Where(profile => string.IsNullOrWhiteSpace(profile.Group)))
            {
                profile.Group = "Ungrouped";
            }

            foreach (var profile in validProfiles.OrderBy(p => p.Group).ThenBy(p => p.Name))
            {
                Profiles.Add(profile);
            }
            _profilesLoaded = true;

            var invalidCount = savedProfiles.Count - validProfiles.Count;
            if (invalidCount > 0)
            {
                _startupNotices.Add(new StartupNotice(
                    "Some connections were skipped",
                    $"{invalidCount} saved connection{(invalidCount == 1 ? " is" : "s are")} invalid and {(invalidCount == 1 ? "was" : "were")} not loaded.",
                    ChoiceDialogKind.Warning,
                    $"File: {_store.FilePath}{Environment.NewLine}Saving changes in RDCM removes skipped entries from this file. To repair them, edit the file before making changes."));
            }
            if (_store.RecoveredFromBackup)
            {
                StatusText.Text = "Connections were recovered from the backup file.";
                _startupNotices.Add(new StartupNotice(
                    "Connections recovered from backup",
                    "The connections file was damaged, so RDCM loaded your connections from its backup copy.",
                    ChoiceDialogKind.Information,
                    $"Damaged file: {_store.PreservedCorruptPath}{Environment.NewLine}It is kept as a separate copy when connections are next saved."));
            }
        }
        catch (JsonException exception)
        {
            AppLog.Error("The connections file could not be parsed.", exception);
            _startupNotices.Add(new StartupNotice(
                "Connections couldn't be loaded",
                "The connections file is damaged and no usable backup was found. RDCM started with an empty connection list.",
                ChoiceDialogKind.Error,
                $"File: {_store.FilePath}{Environment.NewLine}{exception.Message}"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("The connections file could not be read.", exception);
            _startupNotices.Add(new StartupNotice(
                "Connections couldn't be loaded",
                exception is UnauthorizedAccessException
                    ? "Access to the connections file was denied. RDCM started with an empty connection list."
                    : "The connections file couldn't be read. RDCM started with an empty connection list.",
                ChoiceDialogKind.Error,
                $"File: {_store.FilePath}{Environment.NewLine}{exception.Message}"));
        }

        UpdateConnectionCount();
    }

    private static bool IsUsableProfile(ConnectionProfile? profile) =>
        profile is not null && ProfileValidation.Validate(profile, []) is null;

    private bool FilterProfile(object item)
    {
        if (item is not ConnectionProfile profile)
        {
            return false;
        }
        if ((_listMode == ConnectionListMode.Favorites && !_favoriteIds.Contains(profile.Id)) ||
            (_listMode == ConnectionListMode.Recent && !_recentRanks.ContainsKey(profile.Id)))
        {
            return false;
        }

        return ConnectionSearch.Matches(_searchQuery, profile);
    }

    private SearchQuery _searchQuery = SearchQuery.Empty;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProfileDialog(existingProfiles: Profiles.ToList()) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Profiles.Add(dialog.Profile);
        if (!SaveProfiles())
        {
            Profiles.Remove(dialog.Profile);
            return;
        }

        _profileView.Refresh();
        ConnectionsList.SelectedItem = dialog.Profile;
        UpdateConnectionCount();
        if (dialog.ConnectAfterSave)
        {
            OpenTab(dialog.Profile);
        }
        else
        {
            StatusText.Text = $"Saved {dialog.Profile.Name}";
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionsList.SelectedItem is not ConnectionProfile current)
        {
            return;
        }

        var matchingTabs = Tabs.Where(tab => tab.Identity.ProfileId == current.Id).ToList();
        var activeTabs = matchingTabs.Where(tab => tab.Session.HasActiveSession).ToList();
        var dialog = new ProfileDialog(current, Profiles.ToList()) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var reconnect = dialog.ConnectAfterSave;
        if (activeTabs.Count > 0 && !dialog.ConnectAfterSave)
        {
            var choice = new ChoiceDialog(
                "Profile changed",
                $"The saved profile “{current.Name}” has an active session. Keep the current session on {activeTabs[0].Identity.Address}, reconnect it using the updated profile, or cancel the change?",
                [
                    new("Save; keep session", "Save profile changes and keep the current session unchanged", true),
                    new("Save & reconnect", "Save changes and reconnect using updated profile settings"),
                    new("Cancel", "Cancel profile changes")
                ],
                ChoiceDialogKind.Information) { Owner = this };
            if (choice.ShowDialog() != true || choice.SelectedChoice == 2)
            {
                return;
            }
            reconnect = choice.SelectedChoice == 1;
        }

        var oldValues = CopyProfile(current);
        CopyProfile(dialog.Profile, current);
        if (!SaveProfiles())
        {
            CopyProfile(oldValues, current);
            return;
        }

        _profileView.Refresh();
        if (reconnect)
        {
            var reconnectTargets = activeTabs.Count > 0 ? activeTabs : matchingTabs;
            if (reconnectTargets.Count == 0)
            {
                OpenTab(current);
            }
            foreach (var tab in reconnectTargets)
            {
                var identity = SessionIdentity.Capture(current);
                tab.ProfileChanged = false;
                tab.Session.ReconnectWithIdentity(identity);
            }
        }
        else if (activeTabs.Count > 0)
        {
            foreach (var tab in activeTabs)
            {
                tab.ProfileChanged = true;
            }
        }
        UpdateConnectionCount();
        UpdateRecentRanks();
        StatusText.Text = activeTabs.Count > 0 && !reconnect
            ? $"Saved {current.Name}. Profile changed; reconnect to apply."
            : $"Updated {current.Name}";
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionsList.SelectedItem is not ConnectionProfile profile)
        {
            return;
        }

        var matchingTabs = Tabs.Where(tab => tab.Identity.ProfileId == profile.Id).ToList();
        var activeTabs = matchingTabs.Where(tab => tab.Session.HasActiveSession).ToList();
        var removeAndDisconnect = false;
        if (activeTabs.Count > 0)
        {
            var choice = new ChoiceDialog(
                "Remove connection profile",
                $"“{profile.Name}” has {activeTabs.Count} active session(s). Removing the saved profile does not have to end those sessions.",
                [
                    new("Remove; keep sessions", "Remove saved profile and keep active sessions open", true),
                    new("Remove & disconnect", "Remove profile and disconnect affected sessions"),
                    new("Cancel", "Cancel profile removal")
                ]) { Owner = this };
            if (choice.ShowDialog() != true || choice.SelectedChoice == 2)
            {
                return;
            }
            removeAndDisconnect = choice.SelectedChoice == 1;
        }
        else
        {
            var confirm = new ChoiceDialog(
                "Remove connection profile",
                $"Remove the saved profile “{profile.Name}”? No active session uses this profile.",
                [
                    new("Remove", "Remove the saved connection profile", true),
                    new("Cancel", "Keep the saved connection profile")
                ]) { Owner = this };
            if (confirm.ShowDialog() != true || confirm.SelectedChoice != 0)
            {
                return;
            }
        }

        var index = Profiles.IndexOf(profile);
        Profiles.Remove(profile);
        if (!SaveProfiles())
        {
            Profiles.Insert(Math.Clamp(index, 0, Profiles.Count), profile);
            return;
        }

        if (removeAndDisconnect)
        {
            foreach (var tab in matchingTabs)
            {
                CloseTab(tab, confirm: false);
            }
        }

        _profileView.Refresh();
        RemoveProfileReferences(profile.Id);
        UpdateConnectionCount();
        UpdateWorkspace();
        StatusText.Text = $"Removed {profile.Name}";
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionsList.SelectedItem is ConnectionProfile profile)
        {
            OpenTab(profile);
        }
    }

    private void ConnectionsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left &&
            e.OriginalSource is DependencyObject source &&
            !IsWithinButton(source) &&
            ItemsControl.ContainerFromElement(ConnectionsList, source) is ListBoxItem &&
            ConnectionsList.SelectedItem is ConnectionProfile profile)
        {
            OpenTab(profile);
        }
    }

    private static bool IsWithinButton(DependencyObject? element)
    {
        for (; element is not null and not ListBoxItem; element = System.Windows.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }
        }
        return false;
    }

    private void ConnectionsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None &&
            e.OriginalSource is ListBoxItem &&
            ConnectionsList.SelectedItem is ConnectionProfile profile)
        {
            OpenTab(profile);
            e.Handled = true;
        }
    }

    private void OpenTab(ConnectionProfile profile)
    {
        var existing = Tabs.FirstOrDefault(tab => tab.Identity.ProfileId == profile.Id);
        if (existing is not null)
        {
            WorkspaceTabs.SelectedItem = existing.Container;
            return;
        }

        var identity = SessionIdentity.Capture(profile);
        var session = new EmbeddedRdpControl(identity);
        var windowsFormsHost = new SessionHost
        {
            Child = session,
            Focusable = true
        };
        var view = new SessionView();
        view.AttachSession(session, windowsFormsHost);
        var tab = new WorkspaceTab(identity, session) { View = view, Host = windowsFormsHost };
        session.SessionChanged += snapshot => Dispatcher.BeginInvoke(new Action(() => ApplySnapshot(tab, snapshot)));

        tab.Container = new TabItem
        {
            Header = CreateTabHeader(tab, identity),
            Content = view,
            Tag = tab,
            ToolTip = WorkspaceTab.Describe(identity)
        };
        tab.Container.MouseUp += (_, args) =>
        {
            if (args.ChangedButton == MouseButton.Middle)
            {
                CloseTab(tab, confirm: true);
                args.Handled = true;
            }
        };
        AutomationProperties.SetName(tab.Container, $"{identity.Name} session tab, {identity.Address}, {tab.Status}");
        tab.Container.ContextMenu = CreateSessionContextMenu(tab);

        view.StatusTextBlock.SetBinding(TextBlock.TextProperty, new Binding(nameof(WorkspaceTab.Status)) { Source = tab });
        AutomationProperties.SetHelpText(view.StatusTextBlock, $"{identity.Name} session status");

        view.ReconnectButton.Tag = tab;
        view.ReconnectButton.Click += ReconnectTab_Click;
        AutomationProperties.SetName(view.ReconnectButton, $"Reconnect {identity.Name} session");
        tab.ReconnectButton = view.ReconnectButton;

        view.DisconnectButton.Tag = tab;
        view.DisconnectButton.Click += DisconnectTab_Click;
        AutomationProperties.SetName(view.DisconnectButton, $"Disconnect {identity.Name} session");
        tab.DisconnectButton = view.DisconnectButton;

        view.AppControlsButton.Tag = tab;
        view.AppControlsButton.Click += FocusApplication_Click;
        AutomationProperties.SetName(view.AppControlsButton,
            $"Return focus to RDCM controls from {identity.Name} remote desktop");
        tab.AppControlsButton = view.AppControlsButton;

        view.PanelPrimaryButton.Tag = tab;
        view.PanelPrimaryButton.Click += ReconnectTab_Click;
        AutomationProperties.SetName(view.PanelPrimaryButton, $"Reconnect {identity.Name} session");
        view.PanelCancelButton.Tag = tab;
        view.PanelCancelButton.Click += DisconnectTab_Click;
        AutomationProperties.SetName(view.PanelCancelButton, $"Cancel connecting to {identity.Name}");
        view.PanelCloseButton.Tag = tab;
        view.PanelCloseButton.Click += CloseTab_Click;
        AutomationProperties.SetName(view.PanelCloseButton, $"Close {identity.Name} session tab");
        view.StatusMessage += message => StatusText.Text = message;

        var resolutionPicker = view.ResolutionPicker;
        resolutionPicker.ItemsSource = RemoteDisplayOptions.Resolutions;
        resolutionPicker.DisplayMemberPath = nameof(RemoteResolution.Label);
        resolutionPicker.SelectedItem = RemoteDisplayOptions.Resolutions[0];
        resolutionPicker.Tag = tab;
        AutomationProperties.SetName(resolutionPicker, $"Resolution for {identity.Name} remote session");
        resolutionPicker.SelectionChanged += ResolutionPicker_SelectionChanged;
        tab.ResolutionPicker = resolutionPicker;

        var scalePicker = view.ScalePicker;
        scalePicker.ItemsSource = RemoteDisplayOptions.Scales;
        scalePicker.DisplayMemberPath = nameof(RemoteScale.Label);
        scalePicker.SelectedItem = RemoteDisplayOptions.Scales[0];
        scalePicker.Tag = tab;
        AutomationProperties.SetName(scalePicker, $"DPI scale for {identity.Name} remote session");
        scalePicker.SelectionChanged += ScalePicker_SelectionChanged;
        tab.ScalePicker = scalePicker;

        session.DisplayError += (message, notify) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (notify)
            {
                StatusText.Text = $"{tab.Identity.Name}: {message}";
            }
            resolutionPicker.SelectedItem = session.RequestedResolution;
            scalePicker.SelectedItem = session.RequestedScale;
        }));

        tab.ProfileChangedLabel = view.ProfileChangedChip;

        Tabs.Add(tab);
        WorkspaceTabs.Items.Add(tab.Container);
        WorkspaceTabs.SelectedItem = tab.Container;
        UpdateWorkspace();
        ApplySnapshot(tab, session.Snapshot);
    }

    /// <summary>Applies a session snapshot to the tab. Snapshots arrive in order; older ones are ignored.</summary>
    private void ApplySnapshot(WorkspaceTab tab, RdpSessionSnapshot snapshot)
    {
        if (!Tabs.Contains(tab) || (tab.Snapshot is { } current && snapshot.Sequence < current.Sequence))
        {
            return;
        }

        var previousState = tab.Snapshot?.State;
        var previousServerAuthentication = tab.Snapshot?.ServerAuthentication;
        tab.Snapshot = snapshot;
        if (snapshot.Identity != tab.Identity)
        {
            tab.UpdateIdentity(snapshot.Identity);
            tab.Container.ContextMenu = CreateSessionContextMenu(tab);
        }
        tab.Status = snapshot.Message;
        tab.View?.Apply(snapshot);
        UpdateSessionControls(tab);
        UpdateSessionIndicators();
        if (tab.PendingClose && !snapshot.NativeDialogOpen && !tab.Session.IsNativeDialogOpen && !tab.Session.HasActiveSession)
        {
            // Deferred close: the sign-in dialog has closed and the session has ended, so releasing the control is safe.
            Dispatcher.BeginInvoke(() =>
            {
                if (Tabs.Contains(tab))
                {
                    tab.PendingClose = false;
                    CloseTab(tab, confirm: false);
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        if (_closeAfterNativeDialogs && snapshot.NativeDialogOpen)
        {
            // Exit is waiting for sign-in windows; a session that opens a new one is ended as the user already chose.
            tab.Session.DisconnectSession();
        }
        if (_closeAfterNativeDialogs && !Tabs.Any(item => item.Session.IsNativeDialogOpen))
        {
            _closeAfterNativeDialogs = false;
            _allowClose = true;
            Dispatcher.BeginInvoke(Close, System.Windows.Threading.DispatcherPriority.Background);
        }
        if (snapshot.State == RdpSessionState.Connected && previousState is not (RdpSessionState.Connected or RdpSessionState.AutoReconnecting))
        {
            // A session restored by auto-reconnect isn't a new connection.
            RecordRecentConnection(snapshot.Identity.ProfileId);
        }
        if (WorkspaceTabs.SelectedItem == tab.Container)
        {
            StatusText.Text = $"{tab.Identity.Name} ({tab.Identity.Address}): {snapshot.Message}";
        }
        else if (previousState != snapshot.State &&
                 (snapshot.State is RdpSessionState.Failed or RdpSessionState.AutoReconnecting ||
                  (snapshot.State == RdpSessionState.Disconnected && snapshot.EndReason == RdpEndReason.RemoteEnded)))
        {
            // Without this, a failure or interruption in a background tab is only visible as a small dot in its header.
            StatusText.Text = $"{tab.Identity.Name} (background tab): {snapshot.Message}";
        }
        if (snapshot.ServerAuthentication is { IsWeakened: true } weakened && weakened != previousServerAuthentication)
        {
            StatusText.Text = weakened.Source == ServerAuthenticationSource.MachinePolicy
                ? $"{tab.Identity.Name}: server identity isn't verified because a policy on this PC turns that check off"
                : $"{tab.Identity.Name}: server identity isn't verified";
        }
    }

    private StackPanel CreateTabHeader(WorkspaceTab tab, SessionIdentity identity)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        tab.HeaderDot = new Ellipse
        {
            Style = (Style)FindResource("StateDot"),
            Width = 7,
            Height = 7,
            Margin = new Thickness(0, 0, 8, 0)
        };
        header.Children.Add(tab.HeaderDot);

        tab.HeaderLabel = new TextBlock
        {
            Text = identity.Name,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        header.Children.Add(tab.HeaderLabel);

        var closeButton = new Button
        {
            Style = (Style)FindResource("TabCloseButton"),
            Margin = new Thickness(8, 0, 0, 0),
            Tag = tab,
            ToolTip = "Close tab (Ctrl+W)"
        };
        AutomationProperties.SetName(closeButton, $"Close {identity.Name} session tab");
        closeButton.Click += CloseTab_Click;
        header.Children.Add(closeButton);
        return header;
    }

    private void ResolutionPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { Tag: WorkspaceTab tab, SelectedItem: RemoteResolution resolution })
        {
            if (!tab.Session.SetResolution(resolution))
            {
                ((ComboBox)sender).SelectedItem = tab.Session.RequestedResolution;
            }
        }
    }

    private void ScalePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { Tag: WorkspaceTab tab, SelectedItem: RemoteScale scale })
        {
            if (!tab.Session.SetScale(scale))
            {
                ((ComboBox)sender).SelectedItem = tab.Session.RequestedScale;
            }
        }
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WorkspaceTab tab })
        {
            CloseTab(tab, confirm: true);
        }
    }

    private void CloseTab(WorkspaceTab tab, bool confirm)
    {
        if (tab.PendingClose)
        {
            StatusText.Text = $"{tab.Identity.Name} closes when the Windows Security window closes";
            return;
        }
        if (confirm && tab.Session.HasActiveSession)
        {
            var choice = new ChoiceDialog(
                "Close remote session",
                $"Closing “{tab.Identity.Name}” disconnects {tab.Identity.Address}.",
                [
                    new("Disconnect & close", "Disconnect and close this remote session tab"),
                    new("Cancel", "Keep this remote session tab open", true)
                ]) { Owner = this };
            if (choice.ShowDialog() != true || choice.SelectedChoice != 0)
            {
                return;
            }
        }

        if (tab.Session.IsNativeDialogOpen)
        {
            // Releasing the control while its Windows Security dialog is open blocks inside the control.
            // Cancel instead; ApplySnapshot finishes closing once the dialog has closed and the session has ended.
            tab.PendingClose = true;
            tab.Session.DisconnectSession();
            UpdateSessionControls(tab);
            StatusText.Text = $"{tab.Identity.Name} closes when the Windows Security window closes";
            return;
        }

        tab.Session.Dispose();
        tab.Host?.Dispose();
        Tabs.Remove(tab);
        WorkspaceTabs.Items.Remove(tab.Container);
        UpdateWorkspace();
        StatusText.Text = $"Closed {tab.Identity.Name} session";
    }

    private void ReconnectTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WorkspaceTab tab })
        {
            tab.Session.ReconnectSession();
        }
    }

    private void DisconnectTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WorkspaceTab tab })
        {
            tab.Session.DisconnectSession();
        }
    }

    private System.Windows.Controls.ContextMenu CreateSessionContextMenu(WorkspaceTab tab)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        var focus = CreateMenuItem("Focus remote desktop", $"Focus {tab.Identity.Name} remote desktop",
            "\uE7F4", null, (_, _) => tab.Session.FocusRemoteDesktop());
        var reconnect = CreateMenuItem("Reconnect", $"Reconnect {tab.Identity.Name} session",
            "\uE72C", null, (_, _) => tab.Session.ReconnectSession());
        var disconnect = CreateMenuItem("Disconnect", $"Disconnect {tab.Identity.Name} session",
            "\uE73B", null, (_, _) => tab.Session.DisconnectSession());
        menu.Items.Add(focus);
        menu.Items.Add(reconnect);
        menu.Items.Add(disconnect);
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Close", $"Close {tab.Identity.Name} session tab",
            "\uE711", "Ctrl+W", (_, _) => CloseTab(tab, confirm: true)));
        menu.Opened += (_, _) =>
        {
            var state = tab.Snapshot?.State ?? RdpSessionState.Created;
            focus.IsEnabled = state == RdpSessionState.Connected;
            reconnect.IsEnabled = CanReconnect(state) && !tab.PendingClose;
            disconnect.IsEnabled = CanDisconnect(state);
        };
        return menu;
    }

    private static bool CanReconnect(RdpSessionState state) =>
        state is RdpSessionState.Connected or RdpSessionState.Disconnected or RdpSessionState.Failed;

    private static bool CanDisconnect(RdpSessionState state) =>
        state is RdpSessionState.Created or RdpSessionState.Connecting or RdpSessionState.Connected
            or RdpSessionState.Reconnecting or RdpSessionState.AutoReconnecting;

    private static System.Windows.Controls.MenuItem CreateMenuItem(
        string text,
        string automationName,
        string icon,
        string? gesture,
        RoutedEventHandler action)
    {
        var item = new System.Windows.Controls.MenuItem { Header = text, InputGestureText = gesture ?? "" };
        UiAssist.SetIcon(item, icon);
        AutomationProperties.SetName(item, automationName);
        item.Click += action;
        return item;
    }

    private void FocusApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Control control)
        {
            control.Focus();
        }
    }

    private static void UpdateSessionControls(WorkspaceTab tab)
    {
        if (tab.ReconnectButton is null)
        {
            return;
        }

        var state = tab.Snapshot?.State ?? RdpSessionState.Created;
        if (tab.HeaderDot is not null)
        {
            tab.HeaderDot.Tag = SessionIndicator.For(state);
        }
        tab.ReconnectButton.IsEnabled = CanReconnect(state) && !tab.PendingClose;
        if (tab.DisconnectButton is not null)
        {
            tab.DisconnectButton.IsEnabled = CanDisconnect(state);
        }
        var canSetDisplay = state is RdpSessionState.Created or RdpSessionState.Connecting
            or RdpSessionState.Connected;
        if (tab.ResolutionPicker is not null)
        {
            tab.ResolutionPicker.IsEnabled = canSetDisplay;
        }
        if (tab.ScalePicker is not null)
        {
            tab.ScalePicker.IsEnabled = canSetDisplay;
        }
    }

    private void UpdateSessionIndicators()
    {
        SessionIndicators = Tabs
            .GroupBy(tab => tab.Identity.ProfileId)
            .ToDictionary(group => group.Key,
                group => SessionIndicator.Combine(group.Select(tab => tab.Snapshot?.State ?? RdpSessionState.Created)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SessionIndicators)));

        var connected = Tabs.Count(tab => tab.Snapshot?.State == RdpSessionState.Connected);
        SessionSummaryText.Text = Tabs.Count == 0
            ? "No open sessions"
            : $"{Tabs.Count} session{(Tabs.Count == 1 ? "" : "s")} · {connected} connected";
    }

    /// <summary>
    /// WPF falls back to the tab control itself when the focused control becomes unavailable, for example the Disconnect
    /// button after it is pressed. The selected session tab is the useful place: its accessible name says which session
    /// it is and its state, and Tab and the arrow keys continue from there.
    /// </summary>
    private void WorkspaceTabs_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!ReferenceEquals(e.NewFocus, WorkspaceTabs))
        {
            return;
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(Keyboard.FocusedElement, WorkspaceTabs) && WorkspaceTabs.SelectedItem is TabItem { IsEnabled: true } item)
            {
                item.Focus();
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender == WorkspaceTabs && WorkspaceTabs.SelectedItem is TabItem { Tag: WorkspaceTab tab } item)
        {
            StatusText.Text = $"{tab.Identity.Name}: {tab.Status}";
            item.BringIntoView();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshProfileView();

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || ConnectionsList.Items.Count == 0)
        {
            return;
        }

        ConnectionsList.SelectedItem ??= ConnectionsList.Items[0];
        FocusSelectedConnection();
        e.Handled = true;
    }

    /// <summary>
    /// Focuses the selected row. The list virtualizes rows, so an off-screen selection is scrolled into view
    /// first; focusing the list itself instead would make Enter and Down act on the wrong item.
    /// </summary>
    private void FocusSelectedConnection(bool retry = true)
    {
        var selected = ConnectionsList.SelectedItem;
        if (selected is null)
        {
            return;
        }
        ConnectionsList.ScrollIntoView(selected);
        ConnectionsList.UpdateLayout();
        if (ConnectionsList.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item)
        {
            item.Focus();
        }
        else if (retry)
        {
            Dispatcher.BeginInvoke(() => FocusSelectedConnection(retry: false), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void ConnectionsList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSelectionCommands();

    private void UpdateSelectionCommands()
    {
        var selected = ConnectionsList.SelectedItem as ConnectionProfile;
        var enabled = selected is not null;
        EditButton.IsEnabled = enabled;
        RemoveButton.IsEnabled = enabled;
        ConnectButton.IsEnabled = enabled;
        if (ConnectionsList.ContextMenu is not null)
        {
            foreach (var item in ConnectionsList.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>()
                         .Where(item => item.Tag as string == "selection"))
            {
                item.IsEnabled = enabled;
            }
        }
        var isFavorite = selected is not null && _favoriteIds.Contains(selected.Id);
        FavoriteMenuItem.Header = isFavorite ? "Remove from favourites" : "Add to favourites";
        UiAssist.SetIcon(FavoriteMenuItem, isFavorite ? "\uE735" : "\uE734");
        AutomationProperties.SetName(FavoriteMenuItem,
            isFavorite ? "Remove selected profile from favourites" : "Add selected profile to favourites");
    }

    private void ConnectionsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var item = ItemsControl.ContainerFromElement(ConnectionsList, source) as ListBoxItem;
        if (item is not null)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void UpdateConnectionCount() => RefreshProfileView();

    private void UpdateWorkspace()
    {
        EmptyWorkspace.Visibility = Tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateProfileEmptyState();
        UpdateSessionIndicators();
    }

    private void RefreshProfileView()
    {
        _searchQuery = SearchQuery.Parse(SearchBox.Text);
        _profileView.Refresh();
        // The view's count is the filtered count, so the profiles are filtered only once per refresh.
        var filtered = _profileView.Count;
        var narrowed = !_searchQuery.IsEmpty || _listMode != ConnectionListMode.All;
        ConnectionCountText.Text = narrowed ? $"{filtered} of {Profiles.Count}" : $"{Profiles.Count}";
        AutomationProperties.SetName(ConnectionCountText, narrowed
            ? $"Showing {filtered} of {Profiles.Count} profiles"
            : $"{Profiles.Count} saved profile{(Profiles.Count == 1 ? "" : "s")}");
        SearchClearButton.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateProfileEmptyState();
    }

    private void UpdateProfileEmptyState()
    {
        if (Profiles.Count == 0)
        {
            ShowProfileEmptyState("\uE7F4", "No connections yet",
                "Create a connection profile to start your remote desktop workspace.", "New connection", accent: true);
            return;
        }

        if (_profileView.Count > 0)
        {
            ProfileEmptyState.Visibility = Visibility.Collapsed;
            return;
        }

        if (!_searchQuery.IsEmpty)
        {
            ShowProfileEmptyState("\uE721", "No matching connections",
                "Try another name, host, user, group, or note.", "Clear search", accent: false);
        }
        else if (_listMode == ConnectionListMode.Favorites)
        {
            ShowProfileEmptyState("\uE734", "No favourites yet",
                "Right-click a connection and choose Add to favourites, or press Ctrl+D.", "Show all", accent: false);
        }
        else
        {
            ShowProfileEmptyState("\uE81C", "No recent connections",
                "Connections you open successfully appear here.", "Show all", accent: false);
        }
    }

    private void ShowProfileEmptyState(string icon, string title, string message, string action, bool accent)
    {
        ProfileEmptyIcon.Text = icon;
        ProfileEmptyTitle.Text = title;
        ProfileEmptyMessage.Text = message;
        ProfileEmptyAction.Content = action;
        if (accent)
        {
            ProfileEmptyAction.Style = (Style)FindResource("AccentButton");
        }
        else
        {
            ProfileEmptyAction.ClearValue(StyleProperty);
        }
        ProfileEmptyState.Visibility = Visibility.Visible;
    }

    private void ProfileEmptyAction_Click(object sender, RoutedEventArgs e)
    {
        if (Profiles.Count == 0)
        {
            Add_Click(sender, e);
        }
        else if (SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            SearchBox.Focus();
        }
        else
        {
            SetListMode(ConnectionListMode.All);
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) =>
        SetSidebarCollapsed(ProfileSidebar.Visibility == Visibility.Visible);

    private void SetSidebarCollapsed(bool collapsed)
    {
        if (collapsed)
        {
            if (ProfileColumn.ActualWidth > 0)
            {
                _settings.SidebarWidth = Math.Clamp(ProfileColumn.ActualWidth, SidebarMinWidth, SidebarMaxWidth);
            }
            var focusWasInSidebar = ProfileSidebar.IsKeyboardFocusWithin;
            _settings.SidebarCollapsed = true;
            ProfileColumn.MinWidth = 0;
            ProfileColumn.Width = new GridLength(0);
            ProfileSidebar.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
            if (focusWasInSidebar)
            {
                SidebarToggleButton.Focus();
            }
        }
        else
        {
            _settings.SidebarCollapsed = false;
            ProfileColumn.MinWidth = SidebarMinWidth;
            ProfileColumn.MaxWidth = SidebarMaxWidth;
            ProfileColumn.Width = new GridLength(_settings.SidebarWidth);
            ProfileSidebar.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;
        }

        var action = collapsed ? "Show" : "Hide";
        SidebarToggleButton.ToolTip = $"{action} connections (Ctrl+B)";
        AutomationProperties.SetName(SidebarToggleButton, $"{action} connections sidebar");
    }

    private bool SaveProfiles()
    {
        try
        {
            _store.Save(Profiles);
            return true;
        }
        catch (IOException exception)
        {
            ShowError("Couldn't save connections",
                "Your change wasn't saved because the connections file couldn't be written. Check that the file isn't open elsewhere and that the disk has space.",
                exception, $"File: {_store.FilePath}");
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowError("Couldn't save connections",
                "Your change wasn't saved because access to the connections file was denied.",
                exception, $"File: {_store.FilePath}");
        }

        return false;
    }

    private static ConnectionProfile CopyProfile(ConnectionProfile source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Host = source.Host,
        Port = source.Port,
        Username = source.Username,
        Group = source.Group,
        Notes = source.Notes
    };

    private static void CopyProfile(ConnectionProfile source, ConnectionProfile target)
    {
        target.Name = source.Name;
        target.Host = source.Host;
        target.Port = source.Port;
        target.Username = source.Username;
        target.Group = source.Group;
        target.Notes = source.Notes;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // Each closing pass decides afresh whether to wait for sign-in windows.
        _closeAfterNativeDialogs = false;
        if (!_allowClose && Tabs.Any(tab => tab.Session.HasActiveSession))
        {
            var activeCount = Tabs.Count(tab => tab.Session.HasActiveSession);
            var choice = new ChoiceDialog(
                "Remote sessions are still active",
                $"Disconnect {activeCount} active remote session(s) and exit RDCM?",
                [
                    new("Disconnect all & exit", "Disconnect all active remote sessions and exit", false),
                    new("Cancel", "Cancel exit and keep sessions open", true)
                ]) { Owner = this };
            if (choice.ShowDialog() != true || choice.SelectedChoice != 0)
            {
                e.Cancel = true;
                return;
            }
        }

        if (ProfileColumn.ActualWidth > 0)
        {
            _settings.SidebarWidth = Math.Clamp(ProfileColumn.ActualWidth, SidebarMinWidth, SidebarMaxWidth);
        }
        _settings.MainWindow = WindowPlacementService.Capture(this) ?? _settings.MainWindow;
        if (!TrySaveSettings(out var error) && error is not null)
        {
            ShowError("Preferences weren't saved",
                "Your window layout, favourites, and other preferences couldn't be saved. Your connections are not affected.",
                error, $"File: {_settingsStore.FilePath}");
        }

        // Checked last, after any dialog above: a connecting session may have opened its sign-in window meanwhile.
        if (Tabs.Any(tab => tab.Session.IsNativeDialogOpen))
        {
            // Releasing a control while its Windows Security dialog is open blocks inside the control. The user has
            // chosen to disconnect everything, so end every session and close once every dialog has gone (see ApplySnapshot).
            e.Cancel = true;
            _closeAfterNativeDialogs = true;
            foreach (var tab in Tabs.Where(tab => tab.Session.HasActiveSession))
            {
                tab.Session.DisconnectSession();
            }
            StatusText.Text = "RDCM closes when the Windows Security window closes";
            return;
        }

        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .RemoveValueChanged(StatusText, AnnounceStatus);
        _allowClose = true;
        foreach (var tab in Tabs.ToList())
        {
            tab.Session.Dispose();
            tab.Host?.Dispose();
        }
    }

    private bool _allowClose;
    private bool _closeAfterNativeDialogs;
}

public sealed class WorkspaceTab(SessionIdentity identity, EmbeddedRdpControl session) : INotifyPropertyChanged
{
    private string _status = "Connecting...";
    private SessionIdentity _identity = identity;
    private bool _profileChanged;

    public EmbeddedRdpControl Session { get; } = session;
    public SessionView? View { get; init; }
    public WindowsFormsHost? Host { get; init; }

    /// <summary>Most recent session snapshot applied to the UI.</summary>
    public RdpSessionSnapshot? Snapshot { get; set; }

    /// <summary>The user closed the tab while the control's sign-in dialog was open; it closes once that dialog has closed.</summary>
    public bool PendingClose { get; set; }
    public TabItem Container { get; set; } = new();
    public TextBlock HeaderLabel { get; set; } = new();
    public Ellipse? HeaderDot { get; set; }
    public Button? ReconnectButton { get; set; }
    public Button? DisconnectButton { get; set; }
    public Button? AppControlsButton { get; set; }
    public ComboBox? ResolutionPicker { get; set; }
    public ComboBox? ScalePicker { get; set; }
    public FrameworkElement? ProfileChangedLabel { get; set; }

    public SessionIdentity Identity
    {
        get => _identity;
        private set
        {
            _identity = value;
            HeaderLabel.Text = value.Name;
            Container.ToolTip = Describe(value);
            AutomationProperties.SetName(Container, $"{value.Name} session tab, {value.Address}, {Status}");
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Identity)));
        }
    }

    public static string Describe(SessionIdentity identity) =>
        string.Join(" · ", new[] { identity.Address, identity.Username, identity.Group }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    public void UpdateIdentity(SessionIdentity identity) => Identity = identity;

    public bool ProfileChanged
    {
        get => _profileChanged;
        set
        {
            if (_profileChanged == value)
            {
                return;
            }
            _profileChanged = value;
            if (ProfileChangedLabel is not null)
            {
                ProfileChangedLabel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProfileChanged)));
            if (value)
            {
                Status = $"{Status} · Profile changed; reconnect to apply";
            }
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            AutomationProperties.SetName(Container, $"{Identity.Name} session tab, {Identity.Address}, {value}");
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
