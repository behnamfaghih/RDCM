using System.Windows;
using Brush = System.Windows.Media.Brush;

namespace RDCM;

/// <summary>
/// Attached properties used by the shared control templates in Themes/Controls.xaml so that
/// a single template can serve every button, input, and status variant.
/// </summary>
public static class UiAssist
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(UiAssist), new FrameworkPropertyMetadata(null));

    public static string? GetIcon(DependencyObject element) => (string?)element.GetValue(IconProperty);
    public static void SetIcon(DependencyObject element, string? value) => element.SetValue(IconProperty, value);

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.RegisterAttached(
        "IconSize", typeof(double), typeof(UiAssist), new FrameworkPropertyMetadata(14d));

    public static double GetIconSize(DependencyObject element) => (double)element.GetValue(IconSizeProperty);
    public static void SetIconSize(DependencyObject element, double value) => element.SetValue(IconSizeProperty, value);

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(UiAssist), new FrameworkPropertyMetadata(null));

    public static string? GetPlaceholder(DependencyObject element) => (string?)element.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject element, string? value) => element.SetValue(PlaceholderProperty, value);

    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.RegisterAttached(
        "HasError", typeof(bool), typeof(UiAssist), new FrameworkPropertyMetadata(false));

    public static bool GetHasError(DependencyObject element) => (bool)element.GetValue(HasErrorProperty);
    public static void SetHasError(DependencyObject element, bool value) => element.SetValue(HasErrorProperty, value);

    public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
        "HoverBackground", typeof(Brush), typeof(UiAssist), new FrameworkPropertyMetadata(null));

    public static Brush? GetHoverBackground(DependencyObject element) => (Brush?)element.GetValue(HoverBackgroundProperty);
    public static void SetHoverBackground(DependencyObject element, Brush? value) => element.SetValue(HoverBackgroundProperty, value);

    public static readonly DependencyProperty PressedBackgroundProperty = DependencyProperty.RegisterAttached(
        "PressedBackground", typeof(Brush), typeof(UiAssist), new FrameworkPropertyMetadata(null));

    public static Brush? GetPressedBackground(DependencyObject element) => (Brush?)element.GetValue(PressedBackgroundProperty);
    public static void SetPressedBackground(DependencyObject element, Brush? value) => element.SetValue(PressedBackgroundProperty, value);
}
