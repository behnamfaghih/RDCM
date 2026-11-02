using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using Point = System.Windows.Point;

namespace RDCM;

/// <summary>
/// Keyboard-first launcher for connections, open sessions, and commands. Focus stays in the search box;
/// the arrow keys move the selection and Enter runs it. The chosen item runs after the palette has closed,
/// so any dialog it opens is owned by the main window.
/// </summary>
public partial class CommandPalette : Window
{
    private const int PageSize = 8;
    private readonly IReadOnlyList<PaletteItem> _candidates;
    private readonly int _connectionCount;
    private bool _closing;

    public CommandPalette(IReadOnlyList<PaletteItem> candidates, int connectionCount, string initialText = "")
    {
        InitializeComponent();
        ThemeManager.TrackWindow(this);
        _candidates = candidates;
        _connectionCount = connectionCount;
        QueryBox.Text = initialText;
        QueryBox.CaretIndex = initialText.Length;
        QueryBox.TextChanged += (_, _) => UpdateResults();
        QueryBox.PreviewKeyDown += QueryBox_PreviewKeyDown;
        Deactivated += (_, _) => CloseOnce();
        Loaded += (_, _) => QueryBox.Focus();
        UpdateResults();
    }

    /// <summary>The item to run, available after the palette closes.</summary>
    public PaletteItem? ChosenItem { get; private set; }

    /// <summary>Anchors the palette near the top centre of the owner's client area, like other Windows launchers.</summary>
    public void PlaceOver(FrameworkElement anchor)
    {
        var source = PresentationSource.FromVisual(anchor);
        if (source?.CompositionTarget is null)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            return;
        }
        var topLeft = source.CompositionTarget.TransformFromDevice.Transform(anchor.PointToScreen(new Point(0, 0)));
        Width = Math.Min(Width, Math.Max(360, anchor.ActualWidth - 48));
        Left = topLeft.X + (anchor.ActualWidth - Width) / 2;
        Top = topLeft.Y + 8;
    }

    private void UpdateResults()
    {
        var results = CommandPaletteSearch.Filter(QueryBox.Text, _candidates);
        ResultsList.ItemsSource = results;
        ResultsList.SelectedIndex = results.Count > 0 ? 0 : -1;
        if (results.Count > 0)
        {
            ResultsList.ScrollIntoView(results[0]);
        }

        var text = QueryBox.Text.Trim();
        var commandsOnly = text.StartsWith(CommandPaletteSearch.CommandPrefix, StringComparison.Ordinal);
        NoResultsText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoResultsText.Text = commandsOnly ? "No matching commands." : $"No connections, sessions, or commands match “{text}”.";
        ResultCountText.Text = text.Length == 0
            ? $"Type to search {_connectionCount} connection{(_connectionCount == 1 ? "" : "s")}"
            : results.Count == CommandPaletteSearch.MaxResults
                ? $"Top {results.Count} results"
                : $"{results.Count} result{(results.Count == 1 ? "" : "s")}";
        // WPF does not raise live-region events by itself.
        UIElementAutomationPeer.FromElement(ResultCountText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void QueryBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var count = ResultsList.Items.Count;
        switch (e.Key)
        {
            case Key.Escape:
                CloseOnce();
                break;
            case Key.Enter:
                Run(ResultsList.SelectedItem as PaletteItem);
                break;
            case Key.Down when count > 0:
                MoveSelection(1);
                break;
            case Key.Up when count > 0:
                MoveSelection(-1);
                break;
            case Key.PageDown when count > 0:
                MoveSelection(PageSize);
                break;
            case Key.PageUp when count > 0:
                MoveSelection(-PageSize);
                break;
            case Key.P when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                // The shortcut that opened the palette also closes it.
                CloseOnce();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void MoveSelection(int delta)
    {
        var index = Math.Clamp(ResultsList.SelectedIndex + delta, 0, ResultsList.Items.Count - 1);
        ResultsList.SelectedIndex = index;
        ResultsList.ScrollIntoView(ResultsList.Items[index]);
    }

    private void ResultItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: PaletteItem item })
        {
            Run(item);
            e.Handled = true;
        }
    }

    private void Run(PaletteItem? item)
    {
        if (item is null)
        {
            return;
        }
        ChosenItem = item;
        CloseOnce();
    }

    private void CloseOnce()
    {
        if (_closing)
        {
            return;
        }
        _closing = true;
        // Closing from inside Deactivated or a key handler is deferred so WPF finishes the current input first.
        Dispatcher.BeginInvoke(Close);
    }
}
