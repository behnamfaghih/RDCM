using System.IO;

namespace RDCM;

/// <summary>
/// Retries file operations that fail only because another process briefly has the file open. Antivirus scanners and
/// the search indexer open newly written files; replacing such a file fails with "access denied" or a sharing
/// violation for a few milliseconds. Genuine failures (for example a read-only folder) still surface after the
/// last attempt, about 0.4 s later.
/// </summary>
public static class TransientFileRetry
{
    public const int Attempts = 6;

    public static void Run(Action operation, Action<int>? wait = null)
    {
        wait ??= static milliseconds => Thread.Sleep(milliseconds);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception exception) when (attempt < Attempts && IsTransient(exception))
            {
                wait(25 * attempt);
            }
        }
    }

    public static bool IsTransient(Exception exception) =>
        exception is UnauthorizedAccessException ||
        (exception is IOException and not FileNotFoundException and not DirectoryNotFoundException and not PathTooLongException);

    /// <summary>Deletes a leftover temporary file without hiding the error that caused it to be left behind.</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            AppLog.Warning("A temporary file could not be removed.", exception);
        }
    }
}
