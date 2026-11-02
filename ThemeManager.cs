using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;
using SystemColors = System.Windows.SystemColors;

namespace RDCM;

public static class ThemeManager
{
    private const int DwmUseImmersiveDarkModeLegacy = 19;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;
    private const int DwmColorDefault = unchecked((int)0xFFFFFFFF);

    private static Dictionary<string, Color> _palette = DarkPalette();
    private static bool _isDark = true;

    public static void Apply(string appearance)
    {
        var app = System.Windows.Application.Current;
        _isDark = !SystemParameters.HighContrast && appearance switch
        {
            "Light" => false,
            "Dark" => true,
            _ => !IsWindowsLightTheme()
        };
        _palette = SystemParameters.HighContrast
            ? HighContrastPalette()
            : _isDark ? DarkPalette() : LightPalette();

        foreach (var (key, color) in _palette)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            app.Resources[key] = brush;
        }

        foreach (Window window in app.Windows)
        {
            ApplyWindowChrome(window);
        }
    }

    /// <summary>
    /// Matches the native title bar to the active palette (dark mode on Windows 10 20H1+,
    /// caption colour on Windows 11). Unsupported attributes are ignored by DWM.
    /// </summary>
    public static void ApplyWindowChrome(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var dark = _isDark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeLegacy, ref dark, sizeof(int));
        }

        var caption = DwmColorDefault;
        var text = DwmColorDefault;
        if (!SystemParameters.HighContrast)
        {
            caption = ToColorRef(_palette["HeaderBackground"]);
            text = ToColorRef(_palette["PrimaryText"]);
        }
        DwmSetWindowAttribute(handle, DwmCaptionColor, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, DwmTextColor, ref text, sizeof(int));
    }

    /// <summary>Attaches title bar theming to a window before it is first shown.</summary>
    public static void TrackWindow(Window window) =>
        window.SourceInitialized += (_, _) => ApplyWindowChrome(window);

    private static int ToColorRef(Color color) => color.R | color.G << 8 | color.B << 16;

    private static Dictionary<string, Color> DarkPalette() => new()
    {
        ["WindowBackground"] = Rgb(0x1A1B1E),
        ["HeaderBackground"] = Rgb(0x222327),
        ["PanelBackground"] = Rgb(0x222327),
        ["SurfaceBackground"] = Rgb(0x141517),
        ["ControlBackground"] = Rgb(0x2B2D31),
        ["ControlHover"] = Rgb(0x34363B),
        ["ControlPressed"] = Rgb(0x26282C),
        ["InputFocusBackground"] = Rgb(0x1E1F23),
        ["BorderBrushColor"] = Rgb(0x3D4046),
        ["StrongBorderBrush"] = Rgb(0x5A5E66),
        ["DividerBrush"] = Rgb(0x313338),
        ["PanelHover"] = Rgb(0x2C2E33),
        ["SelectedBackground"] = Rgb(0x2D3B50),
        ["SelectedText"] = Rgb(0xF1F3F5),
        ["PrimaryText"] = Rgb(0xE9EBEE),
        ["SecondaryText"] = Rgb(0xAEB3BB),
        ["TertiaryText"] = Rgb(0x8E949D),
        ["DisabledText"] = Rgb(0x6B7079),
        ["AccentBrush"] = Rgb(0x5AA2FF),
        ["AccentText"] = Rgb(0x8CC0FF),
        ["AccentButtonBackground"] = Rgb(0x2E6BD1),
        ["AccentButtonHover"] = Rgb(0x3A78DE),
        ["AccentButtonPressed"] = Rgb(0x285FBC),
        ["AccentButtonBorder"] = Rgb(0x3A78DE),
        ["AccentForeground"] = Rgb(0xFFFFFF),
        ["SuccessBrush"] = Rgb(0x4CC38A),
        ["WarningBrush"] = Rgb(0xE5AE4A),
        ["FavoriteBrush"] = Rgb(0xF2C14E),
        ["ErrorBrush"] = Rgb(0xF07A74),
        ["FocusBrush"] = Rgb(0xFFFFFF),
        ["PopupBackground"] = Rgb(0x2A2C30),
        ["PopupBorder"] = Rgb(0x45484F),
        ["ScrollThumb"] = Rgb(0x5A5E66),
        ["ScrollThumbHover"] = Rgb(0x7A7F88),
        ["IconBackground"] = Rgb(0x26364C)
    };

    private static Dictionary<string, Color> LightPalette() => new()
    {
        ["WindowBackground"] = Rgb(0xFFFFFF),
        ["HeaderBackground"] = Rgb(0xF3F4F6),
        ["PanelBackground"] = Rgb(0xF3F4F6),
        ["SurfaceBackground"] = Rgb(0xE6E7EA),
        ["ControlBackground"] = Rgb(0xFFFFFF),
        ["ControlHover"] = Rgb(0xF5F6F8),
        ["ControlPressed"] = Rgb(0xEBECEF),
        ["InputFocusBackground"] = Rgb(0xFFFFFF),
        ["BorderBrushColor"] = Rgb(0xC9CDD3),
        ["StrongBorderBrush"] = Rgb(0x9DA2AA),
        ["DividerBrush"] = Rgb(0xDFE1E5),
        ["PanelHover"] = Rgb(0xE6E8EC),
        ["SelectedBackground"] = Rgb(0xDAE6F7),
        ["SelectedText"] = Rgb(0x14161A),
        ["PrimaryText"] = Rgb(0x1B1D21),
        ["SecondaryText"] = Rgb(0x555B65),
        ["TertiaryText"] = Rgb(0x656B75),
        ["DisabledText"] = Rgb(0x9EA3AB),
        ["AccentBrush"] = Rgb(0x005FB8),
        ["AccentText"] = Rgb(0x004E99),
        ["AccentButtonBackground"] = Rgb(0x005FB8),
        ["AccentButtonHover"] = Rgb(0x1A6EC2),
        ["AccentButtonPressed"] = Rgb(0x004F9A),
        ["AccentButtonBorder"] = Rgb(0x00539F),
        ["AccentForeground"] = Rgb(0xFFFFFF),
        ["SuccessBrush"] = Rgb(0x0E7A3E),
        ["WarningBrush"] = Rgb(0x985A00),
        ["FavoriteBrush"] = Rgb(0xB07800),
        ["ErrorBrush"] = Rgb(0xC42B1C),
        ["FocusBrush"] = Rgb(0x1B1D21),
        ["PopupBackground"] = Rgb(0xFFFFFF),
        ["PopupBorder"] = Rgb(0xC9CDD3),
        ["ScrollThumb"] = Rgb(0xA3A8B0),
        ["ScrollThumbHover"] = Rgb(0x81868F),
        ["IconBackground"] = Rgb(0xDAE6F7)
    };

    private static Dictionary<string, Color> HighContrastPalette() => new()
    {
        ["WindowBackground"] = SystemColors.WindowColor,
        ["HeaderBackground"] = SystemColors.WindowColor,
        ["PanelBackground"] = SystemColors.WindowColor,
        ["SurfaceBackground"] = SystemColors.WindowColor,
        ["ControlBackground"] = SystemColors.ControlColor,
        ["ControlHover"] = SystemColors.ControlColor,
        ["ControlPressed"] = SystemColors.ControlColor,
        ["InputFocusBackground"] = SystemColors.WindowColor,
        ["BorderBrushColor"] = SystemColors.WindowTextColor,
        ["StrongBorderBrush"] = SystemColors.HighlightColor,
        ["DividerBrush"] = SystemColors.WindowTextColor,
        ["PanelHover"] = SystemColors.ControlColor,
        ["SelectedBackground"] = SystemColors.HighlightColor,
        ["SelectedText"] = SystemColors.HighlightTextColor,
        ["PrimaryText"] = SystemColors.WindowTextColor,
        ["SecondaryText"] = SystemColors.WindowTextColor,
        ["TertiaryText"] = SystemColors.WindowTextColor,
        ["DisabledText"] = SystemColors.GrayTextColor,
        ["AccentBrush"] = SystemColors.HighlightColor,
        ["AccentText"] = SystemColors.HotTrackColor,
        ["AccentButtonBackground"] = SystemColors.HighlightColor,
        ["AccentButtonHover"] = SystemColors.HighlightColor,
        ["AccentButtonPressed"] = SystemColors.HighlightColor,
        ["AccentButtonBorder"] = SystemColors.WindowTextColor,
        ["AccentForeground"] = SystemColors.HighlightTextColor,
        ["SuccessBrush"] = SystemColors.WindowTextColor,
        ["WarningBrush"] = SystemColors.WindowTextColor,
        ["FavoriteBrush"] = SystemColors.WindowTextColor,
        ["ErrorBrush"] = SystemColors.WindowTextColor,
        ["FocusBrush"] = SystemColors.WindowTextColor,
        ["PopupBackground"] = SystemColors.WindowColor,
        ["PopupBorder"] = SystemColors.WindowTextColor,
        ["ScrollThumb"] = SystemColors.WindowTextColor,
        ["ScrollThumbHover"] = SystemColors.HighlightColor,
        ["IconBackground"] = SystemColors.ControlColor
    };

    private static Color Rgb(int value) =>
        Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);

    private static bool IsWindowsLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
