using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Rectangle = System.Drawing.Rectangle;

namespace RDCM;

/// <summary>Normal (restored) window bounds in screen pixels plus the maximized flag. Never stores minimized.</summary>
public sealed class WindowPlacementSettings
{
    private const int MinimumUsableSize = 200;
    private const int MaximumPlausibleSize = 32_000;
    private const int MinimumVisibleTitleWidth = 120;
    private const int TitleBarHeight = 32;

    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }

    internal bool HasPlausibleSize =>
        Width is >= MinimumUsableSize and <= MaximumPlausibleSize &&
        Height is >= MinimumUsableSize and <= MaximumPlausibleSize &&
        Math.Abs((long)Left) <= MaximumPlausibleSize && Math.Abs((long)Top) <= MaximumPlausibleSize;

    /// <summary>
    /// Returns bounds that are fully inside one current monitor work area, or null when the saved
    /// window would not be reachable (for example after a monitor was disconnected).
    /// </summary>
    public static WindowPlacementSettings? Validate(WindowPlacementSettings? saved, IReadOnlyList<Rectangle> workAreas)
    {
        if (saved is null || !saved.HasPlausibleSize || workAreas.Count == 0)
        {
            return null;
        }

        var bounds = new Rectangle(saved.Left, saved.Top, saved.Width, saved.Height);
        var titleBar = new Rectangle(bounds.Left, bounds.Top, bounds.Width, TitleBarHeight);
        var target = workAreas
            .Select(area => (Area: area, TitleOverlap: Rectangle.Intersect(area, titleBar), Overlap: Rectangle.Intersect(area, bounds)))
            .Where(candidate => candidate.TitleOverlap.Width >= Math.Min(MinimumVisibleTitleWidth, bounds.Width) &&
                                candidate.TitleOverlap.Height > 0)
            .OrderByDescending(candidate => (long)candidate.Overlap.Width * candidate.Overlap.Height)
            .Select(candidate => (Rectangle?)candidate.Area)
            .FirstOrDefault();
        if (target is not { } area)
        {
            return null;
        }

        var width = Math.Min(bounds.Width, area.Width);
        var height = Math.Min(bounds.Height, area.Height);
        return new WindowPlacementSettings
        {
            Width = width,
            Height = height,
            Left = Math.Clamp(bounds.Left, area.Left, area.Right - width),
            Top = Math.Clamp(bounds.Top, area.Top, area.Bottom - height),
            Maximized = saved.Maximized
        };
    }
}

/// <summary>Reads and applies window placement through Win32 so maximized windows keep their restore bounds.</summary>
public static class WindowPlacementService
{
    private const int SwShowNormal = 1;
    private const int SwShowMinimized = 2;
    private const int SwShowMaximized = 3;
    private const int WpfRestoreToMaximized = 2;
    private const uint MonitorDefaultToNearest = 2;

    public static IReadOnlyList<Rectangle> GetWorkAreas() =>
        System.Windows.Forms.Screen.AllScreens.Select(screen => screen.WorkingArea).ToList();

    public static WindowPlacementSettings? Capture(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (handle == IntPtr.Zero || !GetWindowPlacement(handle, ref placement))
        {
            return null;
        }

        var normal = WorkspaceToScreen(placement.NormalPosition);
        return new WindowPlacementSettings
        {
            Left = normal.Left,
            Top = normal.Top,
            Width = normal.Right - normal.Left,
            Height = normal.Bottom - normal.Top,
            Maximized = placement.ShowCommand == SwShowMaximized ||
                        (placement.ShowCommand == SwShowMinimized && (placement.Flags & WpfRestoreToMaximized) != 0)
        };
    }

    /// <summary>Call from SourceInitialized, before the window is first shown.</summary>
    public static void Apply(Window window, WindowPlacementSettings settings)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (handle == IntPtr.Zero || !GetWindowPlacement(handle, ref placement))
        {
            return;
        }

        placement.Flags = 0;
        placement.ShowCommand = settings.Maximized ? SwShowMaximized : SwShowNormal;
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
