using System.IO;
using System.Text.Json;

namespace RDCM;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Largest file accepted for import (about 40,000 profiles), so a wrong file can't stall the UI.</summary>
    public const long MaxImportBytes = 10 * 1024 * 1024;

    public string FilePath { get; }
    public string BackupPath => $"{FilePath}.bak";
    public bool RecoveredFromBackup { get; private set; }
    public string? PreservedCorruptPath { get; private set; }

    public ProfileStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RDCM",
            "connections.json");
    }

    public List<ConnectionProfile> Load()
    {
        RecoveredFromBackup = false;
        PreservedCorruptPath = null;
        if (!File.Exists(FilePath))
        {
            return [];
        }

        try
        {
            return Deserialize(File.ReadAllText(FilePath));
        }
        catch (JsonException primaryException)
        {
            if (!File.Exists(BackupPath))
            {
                throw;
            }

            try
            {
                var recovered = Deserialize(File.ReadAllText(BackupPath));
                RecoveredFromBackup = true;
                PreservedCorruptPath = FilePath;
                return recovered;
            }
            catch (JsonException backupException)
            {
                throw new JsonException(
                    $"The profile file and its recovery backup are invalid. The original file was preserved. " +
                    $"Profile file error: {primaryException.Message} Backup error: {backupException.Message}",
                    primaryException);
            }
        }
    }

    public void Save(IEnumerable<ConnectionProfile> profiles)
    {
        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("The profile storage directory could not be determined.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(profiles, JsonOptions));
            if (File.Exists(FilePath) && !RecoveredFromBackup)
            {
                File.Copy(FilePath, BackupPath, overwrite: true);
            }
            else if (File.Exists(FilePath) && RecoveredFromBackup)
            {
                var preservedPath = $"{FilePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json";
                File.Copy(FilePath, preservedPath, overwrite: false);
                PreservedCorruptPath = preservedPath;
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
            RecoveredFromBackup = false;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static List<ConnectionProfile> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<ConnectionProfile>>(json)
        ?? throw new JsonException("The connection profile file must contain a JSON array.");

    public static void Export(string path, IEnumerable<ConnectionProfile> profiles) =>
        File.WriteAllText(path, JsonSerializer.Serialize(profiles, JsonOptions));
}
