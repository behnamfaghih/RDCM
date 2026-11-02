using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RDCM;

/// <summary>
/// Allows one RDCM per data folder in a Windows session. A second instance would keep its own copy of the
/// connections and preferences, and whichever closed last would silently overwrite the other's changes.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex? _mutex;
    private bool _disposed;

    public SingleInstanceGuard(string name)
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
            IsFirstInstance = createdNew;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // The mutex exists but belongs to an instance running with different rights (for example elevated).
            IsFirstInstance = false;
        }
    }

    public bool IsFirstInstance { get; }

    /// <summary>A session-local mutex name derived from the data folder, so separate data folders don't collide.</summary>
    public static string NameFor(string dataDirectory)
    {
        var normalized = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return $@"Local\RDCM.{hash}";
    }

    /// <summary>Brings the other instance's window to the front. Returns false when no window was found.</summary>
    public static bool ActivateExistingInstance()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id == current.Id || process.SessionId != current.SessionId)
                {
                    continue;
                }
                var window = process.MainWindowHandle;
                if (window == IntPtr.Zero)
                {
                    continue;
                }
                if (IsIconic(window))
                {
                    ShowWindow(window, ShowWindowRestore);
                }
                // A process started by the user may hand the foreground to another window.
                return SetForegroundWindow(window);
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (IsFirstInstance)
        {
            _mutex?.ReleaseMutex();
        }
        _mutex?.Dispose();
    }

    private const int ShowWindowRestore = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
