using System.Runtime.InteropServices;
using Rectangle = System.Drawing.Rectangle;

namespace RDCM;

/// <summary>
/// Dots per inch of a monitor or window; 96 is 100% scaling. RDCM is per-monitor DPI aware (V2), so every pixel value
/// in the application is a physical pixel at the DPI of the monitor it belongs to, and WPF sizes are device-independent
/// units (1/96 inch). Convert between them only with the DPI of the window or monitor concerned, never a fixed factor.
/// </summary>
public readonly record struct Dpi
{
    public const int DefaultValue = 96;
    private const int MinimumValue = 48;
    private const int MaximumValue = 960;

    public Dpi(int value)
    {
        if (!IsPlausible(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Not a plausible DPI value.");
        }
        Value = value;
    }

    public int Value { get; }

    public static Dpi Default => new(DefaultValue);

    /// <summary>Scaling percentage shown by Windows (for example 175 for 168 DPI).</summary>
    public int Percent => (int)Math.Round(Value * 100d / DefaultValue);

    public static bool IsPlausible(int value) => value is >= MinimumValue and <= MaximumValue;

    /// <summary>Physical pixels for a length in device-independent units.</summary>
    public int ToPhysical(double deviceIndependentUnits) =>
        (int)Math.Round(deviceIndependentUnits * Value / DefaultValue, MidpointRounding.AwayFromZero);

    /// <summary>Device-independent units for a length in physical pixels.</summary>
    public double ToLogical(int pixels) => pixels * (double)DefaultValue / Value;

    /// <summary>The pixel length at <paramref name="target"/> that has the same apparent (logical) size.</summary>
    public int Rescale(int pixels, Dpi target) =>
        (int)Math.Round(pixels * (double)target.Value / Value, MidpointRounding.AwayFromZero);

    public override string ToString() => $"{Value} DPI ({Percent}%)";
}

/// <summary>
/// The physical pixel size of a session's remote desktop area and the DPI it is displayed at, measured from WPF layout.
/// </summary>
public readonly record struct SessionAreaMetrics(int Width, int Height, Dpi Dpi);

/// <summary>A monitor's bounds and work area in physical screen pixels, and its effective DPI.</summary>
public sealed record MonitorDescription(Rectangle Bounds, Rectangle WorkArea, Dpi Dpi, bool IsPrimary);

/// <summary>Reads the current monitor layout. All coordinates are physical pixels (the process is per-monitor aware).</summary>
public static class MonitorLayout
{
    public static IReadOnlyList<MonitorDescription> GetMonitors()
    {
        var monitors = new List<MonitorDescription>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var dpi = GetDpiForMonitor(monitor, MonitorDpiTypeEffective, out var dpiX, out _) == 0 && Dpi.IsPlausible((int)dpiX)
                    ? new Dpi((int)dpiX)
                    : GetSystemDpi();
                monitors.Add(new MonitorDescription(info.Monitor.ToRectangle(), info.Work.ToRectangle(), dpi, (info.Flags & MonitorInfoPrimary) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    /// <summary>The system DPI: the primary monitor's DPI when the user signed in (what system-aware programs use).</summary>
    public static Dpi GetSystemDpi()
    {
        var value = (int)GetDpiForSystem();
        return Dpi.IsPlausible(value) ? new Dpi(value) : Dpi.Default;
    }

    /// <summary>The DPI of a window (the DPI of the monitor it is on), or null if the window is gone.</summary>
    public static Dpi? GetWindowDpi(IntPtr hwnd)
    {
        var value = hwnd == IntPtr.Zero ? 0 : (int)GetDpiForWindow(hwnd);
        return Dpi.IsPlausible(value) ? new Dpi(value) : null;
    }

    /// <summary>The work area of the monitor a window is on (nearest monitor if it is off-screen).</summary>
    public static (Rectangle WorkArea, Dpi Dpi)? GetWorkAreaForWindow(IntPtr hwnd)
    {
        var monitor = hwnd == IntPtr.Zero ? IntPtr.Zero : MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return null;
        }
        return (info.Work.ToRectangle(), GetWindowDpi(hwnd) ?? GetSystemDpi());
    }

    private const int MonitorDpiTypeEffective = 0;
    private const uint MonitorInfoPrimary = 1;
    private const uint MonitorDefaultToNearest = 2;

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr clip, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
