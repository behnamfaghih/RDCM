using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Control = System.Windows.Controls.Control;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace RDCM;

public partial class ProfileDialog : Window
{
    private readonly IReadOnlyCollection<ConnectionProfile> _existingProfiles;
    private readonly (Control Input, TextBlock Error)[] _fields;
    private string _initialValues = "";

    public ConnectionProfile Profile { get; private set; } = new();
    public bool ConnectAfterSave { get; private set; }

    /// <param name="profile">The profile to edit, or the template for a duplicate; null for a new profile.</param>
    /// <param name="duplicate">Creates a new profile prefilled from <paramref name="profile"/>.</param>
    public ProfileDialog(
        ConnectionProfile? profile = null,
        IReadOnlyCollection<ConnectionProfile>? existingProfiles = null,
        bool duplicate = false)
    {
        InitializeComponent();
        ThemeManager.TrackWindow(this);
        MonitorLayout.LimitHeightToMonitorWorkArea(this);
        _existingProfiles = existingProfiles ?? [];
        _fields =
        [
            (NameBox, NameError), (HostBox, HostError), (PortBox, PortError),
            (UsernameBox, UsernameError), (GroupBox, GroupError), (NotesBox, NotesError)
        ];

        GroupBox.ItemsSource = _existingProfiles
            .Select(existing => existing.Group?.Trim())
            .Where(group => !string.IsNullOrEmpty(group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        GroupBox.Text = "General";

        if (profile is not null)
        {
            if (duplicate)
            {
                Title = "Duplicate connection";
                HeadingText.Text = "Duplicate connection";
                SubheadingText.Text = "Creates a new connection with these settings. The original connection isn't changed.";
            }
            else
            {
                Title = "Edit connection";
                HeadingText.Text = "Edit connection";
                SubheadingText.Text = "Changes apply to new sessions. Open sessions keep their current endpoint until reconnected.";
            }
            Profile = new ConnectionProfile
            {
                Id = duplicate ? Guid.NewGuid() : profile.Id,
                Name = duplicate ? ProfileValidation.CopyName(profile.Name, _existingProfiles) : profile.Name,
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                Group = profile.Group,
                Notes = profile.Notes
            };
            NameBox.Text = Profile.Name;
            HostBox.Text = Profile.Host;
            PortBox.Text = Profile.Port.ToString();
            UsernameBox.Text = Profile.Username;
            GroupBox.Text = Profile.Group;
            NotesBox.Text = Profile.Notes;
        }

        foreach (var (input, error) in _fields)
        {
            input.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ClearError(input, error)));
        }
        PreviewKeyDown += ProfileDialog_PreviewKeyDown;
        Closing += ProfileDialog_Closing;
        Loaded += (_, _) =>
        {
            _initialValues = CurrentValues();
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private string CurrentValues() => string.Join("\u001F",
        NameBox.Text, HostBox.Text, PortBox.Text, UsernameBox.Text, GroupBox.Text, NotesBox.Text);

    /// <summary>Confirms before unsaved edits are thrown away by Cancel, Esc, or the title bar close button.</summary>
    private void ProfileDialog_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DialogResult == true || !IsLoaded || CurrentValues() == _initialValues)
        {
            return;
        }
        var choice = new ChoiceDialog(
            "Discard changes?",
            "This connection has changes that haven't been saved.",
            [
                new("Discard changes", "Close without saving changes"),
                new("Keep editing", "Return to the connection form", IsDefault: true, IsCancel: true)
            ]) { Owner = this };
        if (choice.ShowDialog() != true || choice.SelectedChoice != 0)
        {
            e.Cancel = true;
        }
    }

    private void ProfileDialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Save(connect: true);
            e.Handled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save(connect: false);

    private void SaveAndConnect_Click(object sender, RoutedEventArgs e) => Save(connect: true);

    private void Save(bool connect)
    {
        foreach (var (input, error) in _fields)
        {
            ClearError(input, error);
        }
        var name = NameBox.Text.Trim();
        var host = HostBox.Text.Trim();
        var group = GroupBox.Text.Trim();

        if (group.Length == 0)
        {
            group = "General";
        }
        var port = int.TryParse(PortBox.Text.Trim(), out var parsedPort) ? parsedPort : 0;
        var candidate = new ConnectionProfile
        {
            Id = Profile.Id,
            Name = name,
            Host = host,
            Port = port,
            Username = UsernameBox.Text.Trim(),
            Group = group,
            Notes = NotesBox.Text.Trim()
        };
        var issue = ProfileValidation.Validate(candidate, _existingProfiles);
        if (issue is not null)
        {
            var (control, error) = issue.Field switch
            {
                "host" => _fields[1],
                "port" => _fields[2],
                "username" => _fields[3],
                "group" => _fields[4],
                "notes" => _fields[5],
                _ => _fields[0]
            };
            ShowValidationError(issue.Message, control, error);
            return;
        }

        Profile = candidate;
        ConnectAfterSave = connect;
        DialogResult = true;
    }

    private static void ClearError(Control control, TextBlock errorText)
    {
        if (errorText.Text.Length == 0)
        {
            return;
        }
        errorText.Text = "";
        UiAssist.SetHasError(control, false);
        AutomationProperties.SetHelpText(control, control.Tag as string ?? "");
    }

    private static void ShowValidationError(string message, Control control, TextBlock errorText)
    {
        control.Tag ??= AutomationProperties.GetHelpText(control);
        errorText.Text = message;
        UiAssist.SetHasError(control, true);
        AutomationProperties.SetHelpText(control, message);
        control.Focus();
    }
}
