using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace RDCM;

public sealed record DialogChoice(string Label, string AutomationName, bool IsDefault = false, bool IsCancel = false);

public enum ChoiceDialogKind
{
    Warning,
    Information,
    Error
}

/// <summary>
/// Themed confirmation and message dialog. Choices are rendered left to right in the order given
/// (primary first, Cancel last), following Windows dialog conventions.
/// </summary>
public sealed class ChoiceDialog : Window
{
    public int SelectedChoice { get; private set; } = -1;

    public ChoiceDialog(
        string title,
        string message,
        IReadOnlyList<DialogChoice> choices,
        ChoiceDialogKind kind = ChoiceDialogKind.Warning,
        string? details = null)
    {
        Title = title;
        Style = (Style)Application.Current.FindResource("AppWindowStyle");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ThemeManager.TrackWindow(this);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var body = new Grid { Margin = new Thickness(24, 22, 24, 24) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var (glyph, brush) = kind switch
        {
            ChoiceDialogKind.Information => ("\uE946", "AccentBrush"),
            ChoiceDialogKind.Error => ("\uE783", "ErrorBrush"),
            _ => ("\uE7BA", "WarningBrush")
        };
        var icon = new Glyph
        {
            Text = glyph,
            Style = (Style)Application.Current.FindResource("IconGlyph"),
            FontSize = 22,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 14, 0)
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, brush);
        body.Children.Add(icon);

        var text = new StackPanel();
        Grid.SetColumn(text, 1);
        text.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.FindResource("SubtitleText"),
            TextWrapping = TextWrapping.Wrap
        });
        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            LineHeight = 19
        };
        AutomationProperties.SetName(messageText, message);
        text.Children.Add(messageText);
        if (!string.IsNullOrWhiteSpace(details))
        {
            text.Children.Add(CreateDetails(details));
        }
        body.Children.Add(text);
        root.Children.Add(body);

        var footer = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 14, 24, 14)
        };
        footer.SetResourceReference(Border.BackgroundProperty, "HeaderBackground");
        footer.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        Grid.SetRow(footer, 1);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Button? defaultButton = null;
        for (var index = 0; index < choices.Count; index++)
        {
            var choiceIndex = index;
            var choice = choices[index];
            var isCancel = choice.IsCancel || choice.Label == "Cancel";
            var button = new Button
            {
                Content = choice.Label,
                MinWidth = 96,
                Margin = new Thickness(index == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = choice.IsDefault,
                IsCancel = isCancel
            };
            if (choice.IsDefault && (!isCancel || choices.Count == 1))
            {
                button.Style = (Style)Application.Current.FindResource("AccentButton");
            }
            if (choice.IsDefault)
            {
                defaultButton = button;
            }
            AutomationProperties.SetName(button, choice.AutomationName);
            button.Click += (_, _) =>
            {
                SelectedChoice = choiceIndex;
                DialogResult = true;
            };
            buttons.Children.Add(button);
        }
        footer.Child = buttons;
        root.Children.Add(footer);
        Content = root;

        Loaded += (_, _) => defaultButton?.Focus();
    }

    /// <summary>Shows a single-button message. The owner is used only when it is already visible.</summary>
    public static void ShowMessage(
        Window? owner,
        string title,
        string message,
        ChoiceDialogKind kind,
        string? details = null)
    {
        var dialog = new ChoiceDialog(title, message,
            [new DialogChoice("OK", "Close this message", IsDefault: true, IsCancel: true)], kind, details);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        dialog.ShowDialog();
    }

    private static StackPanel CreateDetails(string details)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var detailsBox = new TextBox
        {
            Text = details,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            MaxHeight = 140,
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = (double)Application.Current.FindResource("FontSizeCaption"),
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(detailsBox, "Technical details");
        var toggle = new Button
        {
            Content = "Show details",
            Style = (Style)Application.Current.FindResource("SubtleButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-8, 0, 0, 0)
        };
        UiAssist.SetIcon(toggle, "\uE70D");
        UiAssist.SetIconSize(toggle, 10);
        toggle.Click += (_, _) =>
        {
            var show = detailsBox.Visibility != Visibility.Visible;
            detailsBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = show ? "Hide details" : "Show details";
            UiAssist.SetIcon(toggle, show ? "\uE70E" : "\uE70D");
        };
        panel.Children.Add(toggle);
        panel.Children.Add(detailsBox);
        return panel;
    }
}
