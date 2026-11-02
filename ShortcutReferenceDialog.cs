using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace RDCM;

/// <summary>Keyboard shortcut reference generated from the command registry, grouped by category.</summary>
public sealed class ShortcutReferenceDialog : Window
{
    public ShortcutReferenceDialog(IEnumerable<AppCommand> commands)
    {
        Title = "Keyboard shortcuts";
        Style = (Style)Application.Current.FindResource("AppWindowStyle");
        Width = 520;
        Height = 600;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        ThemeManager.TrackWindow(this);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var list = new StackPanel { Margin = new Thickness(24, 18, 24, 20) };
        list.Children.Add(new TextBlock
        {
            Text = "Keyboard shortcuts",
            Style = (Style)Application.Current.FindResource("SubtitleText")
        });
        list.Children.Add(new TextBlock
        {
            Text = "Shortcuts work while RDCM has keyboard focus. While you type in a remote desktop, keys go to the remote computer.",
            Style = (Style)Application.Current.FindResource("CaptionText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 4)
        });

        foreach (var category in commands
                     .Where(command => command.ShowInReference && command.DisplayShortcut is not null)
                     .GroupBy(command => command.Category))
        {
            var header = new TextBlock
            {
                Text = category.Key,
                Style = (Style)Application.Current.FindResource("BodyStrongText"),
                Margin = new Thickness(0, 14, 0, 4)
            };
            AutomationProperties.SetHeadingLevel(header, AutomationHeadingLevel.Level2);
            list.Children.Add(header);
            foreach (var command in category)
            {
                list.Children.Add(CreateRow(command.Title, command.DisplayShortcut!));
            }
        }

        var scroll = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false
        };
        root.Children.Add(scroll);

        var footer = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 14, 24, 14)
        };
        footer.SetResourceReference(Border.BackgroundProperty, "HeaderBackground");
        footer.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        Grid.SetRow(footer, 1);
        var close = new Button
        {
            Content = "Close",
            MinWidth = 96,
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.FindResource("AccentButton")
        };
        AutomationProperties.SetName(close, "Close keyboard shortcuts");
        close.Click += (_, _) => Close();
        footer.Child = close;
        root.Children.Add(footer);
        Content = root;
        Loaded += (_, _) => close.Focus();
    }

    private static Grid CreateRow(string title, string shortcut)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(label);
        var keys = new DecorativePanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
        Grid.SetColumn(keys, 1);
        // Alternatives such as "Enter or Ctrl+Enter" are separated by "or"; each key gets its own keycap.
        foreach (var part in shortcut.Split(" or "))
        {
            if (keys.Children.Count > 0)
            {
                keys.Children.Add(new TextBlock
                {
                    Text = "or",
                    Style = (Style)Application.Current.FindResource("CaptionText"),
                    Margin = new Thickness(2, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            foreach (var key in part.Split('+'))
            {
                keys.Children.Add(new Border
                {
                    Style = (Style)Application.Current.FindResource("Keycap"),
                    Child = new TextBlock { Text = key }
                });
            }
        }
        // Screen readers announce each row as one phrase, for example "Command palette: Ctrl+Shift+P".
        AutomationProperties.SetName(label, $"{title}: {shortcut}");
        row.Children.Add(keys);
        return row;
    }

    /// <summary>Visual-only keycaps; the row label already carries the shortcut for assistive technology.</summary>
    private sealed class DecorativePanel : StackPanel
    {
        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
            new DecorativePeer(this);

        private sealed class DecorativePeer(FrameworkElement owner)
            : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner)
        {
            protected override List<System.Windows.Automation.Peers.AutomationPeer>? GetChildrenCore() => null;
            protected override bool IsControlElementCore() => false;
            protected override bool IsContentElementCore() => false;
        }
    }
}
