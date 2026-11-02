using System.IO;
using System.Windows;

namespace RDCM;

public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _instanceGuard;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var dataDirectory = Path.GetDirectoryName(new ProfileStore().FilePath)!;
        _instanceGuard = new SingleInstanceGuard(SingleInstanceGuard.NameFor(dataDirectory));
        if (!_instanceGuard.IsFirstInstance)
        {
            if (!SingleInstanceGuard.ActivateExistingInstance())
            {
                ChoiceDialog.ShowMessage(null, "RDCM is already running",
                    "Another RDCM window is open for your account. Switch to it from the taskbar; only one window can manage your connections at a time.",
                    ChoiceDialogKind.Information);
            }
            Shutdown();
            return;
        }

        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceGuard?.Dispose();
        base.OnExit(e);
    }
}
