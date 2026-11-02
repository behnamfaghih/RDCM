using System.Diagnostics;

namespace RDCM;

/// <summary>
/// Technical diagnostics (debugger / DebugView output). User-facing messages stay concise;
/// exception details and HRESULTs are written here instead.
/// </summary>
public static class AppLog
{
    public static void Warning(string message, Exception? exception = null) =>
        Trace.TraceWarning(Format(message, exception));

    public static void Error(string message, Exception? exception = null) =>
        Trace.TraceError(Format(message, exception));

    private static string Format(string message, Exception? exception) =>
        exception is null ? $"RDCM: {message}" : $"RDCM: {message}{Environment.NewLine}{exception}";
}
