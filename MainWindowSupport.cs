using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace RDCM;

public partial class MainWindow
{
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import RDCM profiles",
            Filter = "RDCM profile JSON (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        List<ConnectionProfile> imported;
        try
        {
            var size = new FileInfo(dialog.FileName).Length;
            if (size > ProfileStore.MaxImportBytes)
            {
                ChoiceDialog.ShowMessage(this, "Import failed",
                    $"The selected file is too large to be an RDCM profile export ({size / (1024.0 * 1024.0):0.#} MB). Choose an export smaller than {ProfileStore.MaxImportBytes / (1024 * 1024)} MB.",
                    ChoiceDialogKind.Warning, $"File: {dialog.FileName}");
                return;
            }
            imported = ProfileStore.Deserialize(File.ReadAllText(dialog.FileName));
        }
        catch (JsonException exception)
        {
            AppLog.Warning("Import file is not a valid profile export.", exception);
            ChoiceDialog.ShowMessage(this, "Import failed", "The selected file isn't a valid RDCM profile export.",
                ChoiceDialogKind.Warning, $"{exception.Message}{Environment.NewLine}File: {dialog.FileName}");
            return;
        }
        catch (IOException exception)
        {
            ShowError("Import failed", "The selected file couldn't be read.", exception, $"File: {dialog.FileName}");
            return;
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowError("Import failed", "Access to the selected file was denied.", exception, $"File: {dialog.FileName}");
            return;
        }

        var existingIds = Profiles.Select(profile => profile.Id).ToHashSet();
        var existingNames = Profiles.Select(profile => profile.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var accepted = new List<ConnectionProfile>();
        var skipped = 0;
        foreach (var profile in imported)
        {
            if (!IsUsableProfile(profile) || existingIds.Contains(profile.Id) ||
                existingNames.Contains(profile.Name.Trim()))
            {
                skipped++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(profile.Group))
            {
                profile.Group = "Ungrouped";
            }
            existingIds.Add(profile.Id);
            existingNames.Add(profile.Name.Trim());
            accepted.Add(profile);
        }

        if (accepted.Count == 0)
        {
            StatusText.Text = imported.Count == 0
                ? "The selected export contains no profiles."
                : $"Nothing imported; {skipped} invalid or duplicate profile(s) were skipped.";
            return;
        }

        foreach (var profile in accepted)
        {
            Profiles.Add(profile);
        }
        if (!SaveProfiles())
        {
            foreach (var profile in accepted)
            {
                Profiles.Remove(profile);
            }
            return;
        }

        RefreshProfileView();
        UpdateConnectionCount();
        StatusText.Text = $"Imported {accepted.Count} profile(s); skipped {skipped}.";
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export RDCM profiles",
            Filter = "RDCM profile JSON (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = "rdcm-profiles.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ProfileStore.Export(dialog.FileName, Profiles);
            StatusText.Text = $"Exported {Profiles.Count} profile(s) to {dialog.FileName}";
        }
        catch (IOException exception)
        {
            ShowError("Export failed", "The profiles couldn't be written to the selected location.", exception, $"File: {dialog.FileName}");
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowError("Export failed", "Access to the selected location was denied.", exception, $"File: {dialog.FileName}");
        }
    }

    private void AppearanceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || AppearanceBox.SelectedItem is not ComboBoxItem { Content: string appearance })
        {
            return;
        }
        _settings.Appearance = appearance;
        ThemeManager.Apply(appearance);
    }

    private void SystemParameters_StaticPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => ThemeManager.Apply(_settings.Appearance)));
    }
}
