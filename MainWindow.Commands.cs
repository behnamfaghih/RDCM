using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Media;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using PasswordBox = System.Windows.Controls.PasswordBox;
using TextBox = System.Windows.Controls.TextBox;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace RDCM;

/// <summary>Command registry wiring: keyboard routing, the command palette, and the shortcut reference.</summary>
public partial class MainWindow
{
    private const string ConnectionsCategory = "Connections";
    private const string SessionsCategory = "Sessions";
    private const string ViewCategory = "View";
    private const string HelpCategory = "Help";

    private readonly CommandRegistry _commands = new();
    private CommandPalette? _palette;

    private ConnectionProfile? SelectedProfile => ConnectionsList.SelectedItem as ConnectionProfile;

    private WorkspaceTab? CurrentTab => WorkspaceTabs.SelectedItem is TabItem { Tag: WorkspaceTab tab } ? tab : null;

    private void RegisterCommands()
    {
        // Connections
        _commands.Add(new AppCommand
        {
            Id = "connection.new", Title = "New connection…", Category = ConnectionsCategory, Icon = "\uE710",
            Shortcut = new(Key.N, ModifierKeys.Control), Keywords = "add create profile",
            Execute = () => Add_Click(this, new RoutedEventArgs())
        });
        _commands.Add(new AppCommand
        {
            Id = "connection.connect", Title = "Connect to selected connection", Category = ConnectionsCategory,
            Icon = "\uE768", Shortcut = new(Key.Enter, ModifierKeys.Control), ShortcutText = "Enter or Ctrl+Enter",
            ShowInPalette = false, CanExecute = () => SelectedProfile is not null,
            Execute = () => Connect_Click(this, new RoutedEventArgs())
        });
        _commands.Add(new AppCommand
        {
            Id = "connection.edit", Title = "Edit selected connection…", Category = ConnectionsCategory, Icon = "\uE70F",
            Shortcut = new(Key.F2), Keywords = "change rename properties",
            CanExecute = () => SelectedProfile is not null, Execute = () => Edit_Click(this, new RoutedEventArgs())
        });
        _commands.Add(new AppCommand
        {
            Id = "connection.duplicate", Title = "Duplicate selected connection…", Category = ConnectionsCategory,
            Icon = "\uE8C8", Keywords = "copy clone", CanExecute = () => SelectedProfile is not null,
            Execute = DuplicateSelected
        });
        _commands.Add(new AppCommand
        {
            Id = "connection.favorite", Title = "Add or remove selected connection from favourites",
            Category = ConnectionsCategory, Icon = "\uE734", Shortcut = new(Key.D, ModifierKeys.Control),
            Keywords = "star pin favorite", CanExecute = () => SelectedProfile is not null,
            Execute = () => ToggleFavorite(SelectedProfile!)
        });
        _commands.Add(new AppCommand
        {
            Id = "connection.remove", Title = "Remove selected connection…", Category = ConnectionsCategory,
            Icon = "\uE74D", Shortcut = new(Key.Delete), AllowInTextInput = false, Keywords = "delete",
            CanExecute = () => SelectedProfile is not null, Execute = () => Remove_Click(this, new RoutedEventArgs())
        });
        _commands.Add(new AppCommand
        {
            Id = "connection.import", Title = "Import connections…", Category = ConnectionsCategory, Icon = "\uE8B5",
            Keywords = "json load", Execute = () => Import_Click(this, new RoutedEventArgs())
        });
        _commands.Add(new AppCommand
        {
            Id = "connection.export", Title = "Export connections…", Category = ConnectionsCategory, Icon = "\uEDE1",
            Keywords = "json save backup", Execute = () => Export_Click(this, new RoutedEventArgs())
        });
        _commands.Add(new AppCommand
        {
            Id = "connection.clearsearch", Title = "Clear connection search", Category = ConnectionsCategory,
            ShortcutText = "Esc", ShowInPalette = false
        });

        // Sessions
        _commands.Add(new AppCommand
        {
            Id = "session.next", Title = "Next session tab", Category = SessionsCategory, Icon = "\uE76C",
            Shortcut = new(Key.Tab, ModifierKeys.Control), CanExecute = () => Tabs.Count > 1,
            Execute = () => SelectTabByOffset(1)
        });
        _commands.Add(new AppCommand
        {
            Id = "session.previous", Title = "Previous session tab", Category = SessionsCategory, Icon = "\uE76B",
            Shortcut = new(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift), CanExecute = () => Tabs.Count > 1,
            Execute = () => SelectTabByOffset(-1)
        });
        _commands.Add(new AppCommand
        {
            Id = "session.goto", Title = "Go to session tab 1 to 9", Category = SessionsCategory,
            ShortcutText = "Ctrl+1…9", ShowInPalette = false
        });
        for (var number = 1; number <= 9; number++)
        {
            var index = number - 1;
            _commands.Add(new AppCommand
            {
                Id = $"session.goto.{number}", Title = $"Go to session tab {number}", Category = SessionsCategory,
                Shortcut = new(Key.D0 + number, ModifierKeys.Control), ShowInPalette = false, ShowInReference = false,
                CanExecute = () => index < Tabs.Count,
                Execute = () => WorkspaceTabs.SelectedItem = Tabs[index].Container
            });
        }
        _commands.Add(new AppCommand
        {
            Id = "session.reconnect", Title = "Reconnect current session", Category = SessionsCategory, Icon = "\uE72C",
            Keywords = "retry refresh", CanExecute = () => CurrentTab is { PendingClose: false } tab && CanReconnect(tab.Snapshot?.State ?? RdpSessionState.Created),
            Execute = () => CurrentTab?.Session.ReconnectSession()
        });
        _commands.Add(new AppCommand
        {
            Id = "session.disconnect", Title = "Disconnect current session", Category = SessionsCategory, Icon = "\uE73B",
            Keywords = "cancel stop", CanExecute = () => CurrentTab is { } tab && CanDisconnect(tab.Snapshot?.State ?? RdpSessionState.Created),
            Execute = () => CurrentTab?.Session.DisconnectSession()
        });
        _commands.Add(new AppCommand
        {
            Id = "session.close", Title = "Close current session tab", Category = SessionsCategory, Icon = "\uE711",
            Shortcut = new(Key.W, ModifierKeys.Control), CanExecute = () => CurrentTab is not null,
            Execute = () => CloseTab(CurrentTab!, confirm: true)
        });

        // View
        _commands.Add(new AppCommand
        {
            Id = "view.palette", Title = "Command palette", Category = ViewCategory, Icon = "\uE721",
            Shortcut = new(Key.P, ModifierKeys.Control | ModifierKeys.Shift), ShowInPalette = false,
            Execute = () => ShowCommandPalette()
        });
        _commands.Add(new AppCommand
        {
            Id = "view.search", Title = "Search connections", Category = ViewCategory, Icon = "\uE721",
            Shortcut = new(Key.F, ModifierKeys.Control), Keywords = "find filter", Execute = FocusSearch
        });
        _commands.Add(new AppCommand
        {
            Id = "view.sidebar", Title = "Show or hide connections sidebar", Category = ViewCategory, Icon = "\uE90C",
            Shortcut = new(Key.B, ModifierKeys.Control), Keywords = "panel toggle",
            Execute = () => SetSidebarCollapsed(ProfileSidebar.Visibility == Visibility.Visible)
        });
        _commands.Add(new AppCommand
        {
            Id = "view.all", Title = "Show all connections", Category = ViewCategory, Icon = "\uE8FD",
            Keywords = "filter list", Execute = () => ShowListMode(ConnectionListMode.All)
        });
        _commands.Add(new AppCommand
        {
            Id = "view.favorites", Title = "Show favourite connections", Category = ViewCategory, Icon = "\uE734",
            Keywords = "filter favorites starred", Execute = () => ShowListMode(ConnectionListMode.Favorites)
        });
        _commands.Add(new AppCommand
        {
            Id = "view.recent", Title = "Show recent connections", Category = ViewCategory, Icon = "\uE81C",
            Keywords = "filter history", Execute = () => ShowListMode(ConnectionListMode.Recent)
        });
        _commands.Add(new AppCommand
        {
            Id = "view.compact", Title = "Turn compact connection list on or off", Category = ViewCategory,
            Icon = "\uE8FD", Keywords = "density dense", Execute = () => CompactListToggle.IsChecked = !IsCompactList
        });
        string[] appearances = ["System", "Light", "Dark"];
        for (var index = 0; index < appearances.Length; index++)
        {
            var appearanceIndex = index;
            _commands.Add(new AppCommand
            {
                Id = $"view.appearance.{appearances[index].ToLowerInvariant()}",
                Title = $"Appearance: {appearances[index]}", Category = ViewCategory, Icon = "\uE790",
                Keywords = "theme colour color mode",
                CanExecute = () => AppearanceBox.SelectedIndex != appearanceIndex,
                Execute = () => AppearanceBox.SelectedIndex = appearanceIndex
            });
        }

        // Help
        _commands.Add(new AppCommand
        {
            Id = "help.shortcuts", Title = "Keyboard shortcuts", Category = HelpCategory, Icon = "\uE765",
            Shortcut = new(Key.F1), Keywords = "keys help reference hotkeys", Execute = ShowShortcutReference
        });
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Keys typed into a remote desktop belong to the remote computer, never to RDCM's shortcuts.
        // Keys in an open dropdown or menu (a popup) belong to that popup: Esc closes it, Delete doesn't remove.
        var source = e.OriginalSource as DependencyObject;
        if (IsInsideSessionHost(source) || IsInsidePopup(source))
        {
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (SearchBox.Text.Length > 0)
            {
                SearchBox.Clear();
                e.Handled = true;
            }
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Enter && e.OriginalSource is TextBox { AcceptsReturn: true })
        {
            return;
        }
        var inTextInput = e.OriginalSource is TextBoxBase or PasswordBox;
        if (_commands.FindByShortcut(key, Keyboard.Modifiers, inTextInput) is not { } command)
        {
            return;
        }
        command.Execute();
        e.Handled = true;
    }

    private static bool IsInsideSessionHost(DependencyObject? element) => SessionKeyRouting.IsFromSessionHost(element);

    private bool IsInsidePopup(DependencyObject? element)
    {
        if (element is Visual visual && PresentationSource.FromVisual(visual)?.RootVisual is { } root &&
            !ReferenceEquals(root, this))
        {
            return true;
        }
        for (; element is not null; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is System.Windows.Controls.ComboBox { IsDropDownOpen: true })
            {
                return true;
            }
        }
        return false;
    }

    private void SelectTabByOffset(int offset)
    {
        var current = WorkspaceTabs.SelectedIndex;
        WorkspaceTabs.SelectedIndex = (current + offset + Tabs.Count) % Tabs.Count;
    }

    private void FocusSearch()
    {
        if (ProfileSidebar.Visibility != Visibility.Visible)
        {
            SetSidebarCollapsed(false);
            UpdateLayout();
        }
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void ShowListMode(ConnectionListMode mode)
    {
        if (ProfileSidebar.Visibility != Visibility.Visible)
        {
            SetSidebarCollapsed(false);
        }
        SetListMode(mode);
    }

    private void DuplicateSelected()
    {
        if (SelectedProfile is not { } source)
        {
            return;
        }
        var dialog = new ProfileDialog(source, Profiles.ToList(), duplicate: true) { Owner = this };
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
        RefreshProfileView();
        ConnectionsList.SelectedItem = dialog.Profile;
        ConnectionsList.ScrollIntoView(dialog.Profile);
        if (dialog.ConnectAfterSave)
        {
            OpenTab(dialog.Profile);
        }
        else
        {
            StatusText.Text = $"Saved {dialog.Profile.Name}";
        }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e) => DuplicateSelected();

    private void ShortcutReference_Click(object sender, RoutedEventArgs e) => ShowShortcutReference();

    private void ShowShortcutReference()
    {
        new ShortcutReferenceDialog(_commands.Commands) { Owner = this }.ShowDialog();
    }

    private void CommandPalette_Click(object sender, RoutedEventArgs e) => ShowCommandPalette();

    private void ShowCommandPalette(string initialText = "")
    {
        if (_palette is not null)
        {
            _palette.Activate();
            return;
        }

        var palette = new CommandPalette(BuildPaletteItems(), Profiles.Count, initialText) { Owner = this };
        palette.PlaceOver((FrameworkElement)Content);
        palette.Closed += (_, _) =>
        {
            _palette = null;
            if (palette.ChosenItem is { } item)
            {
                // Run after the palette is gone so dialogs are owned by, and focus returns to, the main window.
                Dispatcher.BeginInvoke(item.Execute, System.Windows.Threading.DispatcherPriority.Input);
            }
        };
        _palette = palette;
        palette.Show();
    }

    private List<PaletteItem> BuildPaletteItems()
    {
        var items = new List<PaletteItem>();
        foreach (var tab in Tabs)
        {
            var identity = tab.Identity;
            var state = tab.Snapshot?.State ?? RdpSessionState.Created;
            items.Add(new PaletteItem(PaletteItemKind.Session, identity.Name, identity.Address, "\uE8A7",
                $"Open session · {DescribeState(state)}", null,
                () =>
                {
                    if (Tabs.Contains(tab))
                    {
                        WorkspaceTabs.SelectedItem = tab.Container;
                    }
                },
                [identity.Address, identity.Username, identity.Group], IsSuggested: true));
        }

        var open = Tabs.Select(tab => tab.Identity.ProfileId).ToHashSet();
        var recent = RecentProfiles.Select(profile => profile.Id).ToHashSet();
        var ordered = RecentProfiles
            .Concat(Profiles.Where(profile => _favoriteIds.Contains(profile.Id)))
            .Concat(Profiles)
            .DistinctBy(profile => profile.Id)
            .Where(profile => !open.Contains(profile.Id));
        foreach (var profile in ordered)
        {
            var label = recent.Contains(profile.Id) ? "Recent · Connect"
                : _favoriteIds.Contains(profile.Id) ? "Favourite · Connect"
                : "Connect";
            var subtitle = string.IsNullOrWhiteSpace(profile.Username)
                ? $"{profile.Address} · {profile.Group}"
                : $"{profile.Address} · {profile.Username} · {profile.Group}";
            var target = profile;
            items.Add(new PaletteItem(PaletteItemKind.Connection, profile.Name, subtitle, "\uE7F4", label, null,
                () =>
                {
                    if (Profiles.Contains(target))
                    {
                        ConnectionsList.SelectedItem = target;
                        OpenTab(target);
                    }
                },
                [profile.Address, profile.Username, profile.Group, profile.Notes],
                IsSuggested: recent.Contains(profile.Id) || _favoriteIds.Contains(profile.Id)));
        }

        foreach (var command in _commands.Commands.Where(command => command.ShowInPalette && command.CanExecute()))
        {
            var target = command;
            items.Add(new PaletteItem(PaletteItemKind.Command, command.Title, null, command.Icon, command.Category,
                command.DisplayShortcut,
                () =>
                {
                    if (target.CanExecute())
                    {
                        target.Execute();
                    }
                },
                [command.Category, command.Keywords], IsSuggested: true));
        }
        return items;
    }

    private static string DescribeState(RdpSessionState state) => state switch
    {
        RdpSessionState.Created or RdpSessionState.Connecting => "Connecting",
        RdpSessionState.Connected => "Connected",
        RdpSessionState.Reconnecting => "Reconnecting",
        RdpSessionState.Disconnecting => "Disconnecting",
        RdpSessionState.Failed => "Failed",
        _ => "Disconnected"
    };
}
