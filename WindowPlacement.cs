using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Rectangle = System.Drawing.Rectangle;

namespace RDCM;

/// <summary>
/// Normal (restored) window bounds plus the maximized flag. Never stores minimized.
/// <para>
/// <see cref="Format"/> 2 (RDCM 1.1 and later): physical screen pixels, with <see cref="Dpi"/> the window's DPI when it
/// was saved. Format 0 (RDCM 1.0, which was system DPI aware): coordinates in the system-DPI space Windows showed that
/// version, where each monitor appears scaled by system DPI / monitor DPI. Both load; format 2 is always saved.
/// </para>
/// </summary>
public sealed class WindowPlacementSettings
{
    public const int CurrentFormat = 2;
    private const int MinimumUsableSize = 200;
    private const int MaximumPlausibleSize = 32_000;
    // Reachability margins in device-independent units, converted at the monitor's DPI.
    private const int MinimumVisibleTitleWidth = 120;
    private const int TitleBarHeight = 32;

    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }

    /// <summary>Coordinate format; see the class remarks. 0 when the file was written by RDCM 1.0.</summary>
    public int Format { get; set; }

    /// <summary>DPI of the window when it was saved (format 2); 0 when unknown.</summary>
    public int Dpi { get; set; }

    internal bool HasPlausibleSize =>
        Width is >= MinimumUsableSize and <= MaximumPlausibleSize &&
        Height is >= MinimumUsableSize and <= MaximumPlausibleSize &&
        Math.Abs((long)Left) <= MaximumPlausibleSize && Math.Abs((long)Top) <= MaximumPlausibleSize;

    /// <summary>
    /// Returns physical-pixel bounds (format 2) that are fully inside one current monitor work area, or null when the
    /// saved window would not be reachable (for example after a monitor was disconnected). If the monitor's scaling
    /// differs from when the window was saved, the window keeps its apparent size.
    /// </summary>
    public static WindowPlacementSettings? Validate(
        WindowPlacementSettings? saved, IReadOnlyList<MonitorDescription> monitors, RDCM.Dpi systemDpi)
    {
        if (saved is null || !saved.HasPlausibleSize || monitors.Count == 0)
        {
            return null;
        }

        var (bounds, savedDpi) = saved.Format >= CurrentFormat && RDCM.Dpi.IsPlausible(saved.Dpi)
            ? (new Rectangle(saved.Left, saved.Top, saved.Width, saved.Height), new RDCM.Dpi(saved.Dpi))
            : FromSystemDpiCoordinates(saved, monitors, systemDpi);

        var target = monitors
            .Select(monitor =>
            {
                var titleBar = new Rectangle(bounds.Left, bounds.Top, bounds.Width, monitor.Dpi.ToPhysical(TitleBarHeight));
                return (Monitor: monitor, TitleOverlap: Rectangle.Intersect(monitor.WorkArea, titleBar),
                    Overlap: Rectangle.Intersect(monitor.WorkArea, bounds));
            })
            .Where(candidate => candidate.TitleOverlap.Width >= Math.Min(candidate.Monitor.Dpi.ToPhysical(MinimumVisibleTitleWidth), bounds.Width) &&
                                candidate.TitleOverlap.Height > 0)
            .OrderByDescending(candidate => (long)candidate.Overlap.Width * candidate.Overlap.Height)
            .Select(candidate => candidate.Monitor)
            .FirstOrDefault();
        if (target is null)
        {
            return null;
        }

        var area = target.WorkArea;
        var width = savedDpi == target.Dpi ? bounds.Width : savedDpi.Rescale(bounds.Width, target.Dpi);
        var height = savedDpi == target.Dpi ? bounds.Height : savedDpi.Rescale(bounds.Height, target.Dpi);
        width = Math.Min(width, area.Width);
        height = Math.Min(height, area.Height);
        return new WindowPlacementSettings
        {
            Width = width,
            Height = height,
            Left = Math.Clamp(bounds.Left, area.Left, area.Right - width),
            Top = Math.Clamp(bounds.Top, area.Top, area.Bottom - height),
            Maximized = saved.Maximized,
            Format = CurrentFormat,
            Dpi = target.Dpi.Value
        };
    }

    /// <summary>
    /// Converts bounds saved by the system-DPI-aware RDCM 1.0 to physical pixels. Windows showed that version every
    /// monitor scaled about the screen origin by system DPI / monitor DPI, so the monitor whose scaled bounds contain
    /// most of the window identifies the factor. Bounds that match no monitor are returned unchanged (and are then
    /// rejected as unreachable unless they happen to be on screen).
    /// </summary>
    internal static (Rectangle Bounds, RDCM.Dpi Dpi) FromSystemDpiCoordinates(
        WindowPlacementSettings saved, IReadOnlyList<MonitorDescription> monitors, RDCM.Dpi systemDpi)
    {
        var legacy = new Rectangle(saved.Left, saved.Top, saved.Width, saved.Height);
        var match = monitors
            .Select(monitor => (Monitor: monitor, Overlap: Rectangle.Intersect(Scale(monitor.Bounds, systemDpi, monitor.Dpi), legacy)))
            .Where(candidate => !candidate.Overlap.IsEmpty)
            .OrderByDescending(candidate => (long)candidate.Overlap.Width * candidate.Overlap.Height)
            .Select(candidate => candidate.Monitor)
            .FirstOrDefault();
        return match is null ? (legacy, systemDpi) : (Scale(legacy, systemDpi, match.Dpi), match.Dpi);
    }

    private static Rectangle Scale(Rectangle rectangle, RDCM.Dpi from, RDCM.Dpi to) => Rectangle.FromLTRB(
        from.Rescale(rectangle.Left, to), from.Rescale(rectangle.Top, to),
        from.Rescale(rectangle.Right, to), from.Rescale(rectangle.Bottom, to));

    /// <summary>
    /// Builds the setting from a window's Win32 placement. A minimized window is saved as normal, or as maximized if it
    /// will restore to maximized, so RDCM never starts minimized.
    /// </summary>
    internal static WindowPlacementSettings FromNative(int showCommand, int flags, Rectangle normalBounds, RDCM.Dpi dpi) => new()
    {
        Left = normalBounds.Left,
        Top = normalBounds.Top,
        Width = normalBounds.Width,
        Height = normalBounds.Height,
        Maximized = showCommand == ShowMaximized || (showCommand == ShowMinimized && (flags & RestoreToMaximized) != 0),
        Format = CurrentFormat,
        Dpi = dpi.Value
    };

    internal const int ShowMinimized = 2;
    internal const int ShowMaximized = 3;
    internal const int RestoreToMaximized = 2;
}

/// <summary>Reads and applies window placement through Win32 so maximized windows keep their restore bounds.</summary>
public static class WindowPlacementService
{
    private const int SwShowNormal = 1;
    private const uint MonitorDefaultToNearest = 2;

    public static WindowPlacementSettings? Capture(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (handle == IntPtr.Zero || !GetWindowPlacement(handle, ref placement) || MonitorLayout.GetWindowDpi(handle) is not { } dpi)
        {
            return null;
        }

        var normal = WorkspaceToScreen(placement.NormalPosition);
        return WindowPlacementSettings.FromNative(placement.ShowCommand, placement.Flags,
            Rectangle.FromLTRB(normal.Left, normal.Top, normal.Right, normal.Bottom), dpi);
    }

    /// <summary>
    /// Call from SourceInitialized, before the window is first shown, with bounds from
    /// <see cref="WindowPlacementSettings.Validate"/>. Windows applies them in physical pixels on the target monitor,
    /// and WPF then renders the window at that monitor's DPI.
    /// </summary>
    public static void Apply(Window window, WindowPlacementSettings settings)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (handle == IntPtr.Zero || !GetWindowPlacement(handle, ref placement))
        {
            return;
        }

        placement.Flags = 0;
        placement.ShowCommand = settings.Maximized ? WindowPlacementSettings.ShowMaximized : SwShowNormal;
        placement.NormalPosition = ScreenToWorkspace(new NativeRect
        {
            Left = settings.Left,
            Top = settings.Top,
            Right = settings.Left + settings.Width,
            Bottom = settings.Top + settings.Height
        });
        if (!SetWindowPlacement(handle, ref placement))
        {
            AppLog.Warning($"Restoring the window position failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    // WINDOWPLACEMENT uses workspace coordinates: screen coordinates offset by the taskbar on the window's monitor.
    private static NativeRect WorkspaceToScreen(NativeRect rect) => Offset(rect, GetWorkspaceOffset(rect), 1);

    private static NativeRect ScreenToWorkspace(NativeRect rect) => Offset(rect, GetWorkspaceOffset(rect), -1);

    private static (int X, int Y) GetWorkspaceOffset(NativeRect rect)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var monitor = MonitorFromRect(ref rect, MonitorDefaultToNearest);
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)
            ? (info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top)
            : (0, 0);
    }

    private static NativeRect Offset(NativeRect rect, (int X, int Y) offset, int direction) => new()
    {
        Left = rect.Left + offset.X * direction,
        Top = rect.Top + offset.Y * direction,
        Right = rect.Right + offset.X * direction,
        Bottom = rect.Bottom + offset.Y * direction
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
