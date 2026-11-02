using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;

namespace RDCM;

/// <summary>
/// Content of a workspace tab: the session toolbar, the native RDP surface, and the state panel that
/// replaces the surface whenever the session is not connected. Presentation only; MainWindow wires actions.
/// </summary>
public partial class SessionView : System.Windows.Controls.UserControl
{
    private const double CompactWidth = 820;
    private const double NarrowWidth = 600;
    private EmbeddedRdpControl? _session;
    private WindowsFormsHost? _host;
    private RdpSessionSnapshot? _snapshot;
    private readonly System.Windows.Threading.DispatcherTimer _copyFeedbackTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public SessionView()
    {
        InitializeComponent();
        SizeChanged += (_, args) => UpdateDensity(args.NewSize.Width);
        HostContainer.SizeChanged += (_, _) => UpdateSessionAreaSize();
        _copyFeedbackTimer.Tick += (_, _) => ResetCopyButton();
    }

    public void AttachSession(EmbeddedRdpControl session, WindowsFormsHost host)
    {
        _session = session;
        _host = host;
        // Hidden (not Collapsed) keeps the host's layout slot, so the native control keeps its size.
        host.Visibility = Visibility.Hidden;
        host.Loaded += (_, _) => session.EnsureHandle();
        HostContainer.Child = host;
        UpdateSessionAreaSize();
    }

    public void Apply(RdpSessionSnapshot snapshot)
    {
        _snapshot = snapshot;
        var indicator = SessionIndicator.For(snapshot.State);
        StateDot.Tag = indicator;
        StatusTextBlock.SetResourceReference(TextBlock.ForegroundProperty,
            snapshot.State == RdpSessionState.Failed ? "ErrorBrush" : "PrimaryText");
        ActivityBar.Visibility = indicator == SessionIndicator.Busy && SystemParameters.ClientAreaAnimation
            ? Visibility.Visible
            : Visibility.Collapsed;

        var connected = snapshot.State == RdpSessionState.Connected;
        if (_host is not null)
        {
            _host.Visibility = connected ? Visibility.Visible : Visibility.Hidden;
        }
        StatePanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        if (!connected)
        {
            ShowStatePanel(snapshot);
        }
    }

    private void ShowStatePanel(RdpSessionSnapshot snapshot)
    {
        var name = snapshot.Identity.Name;
        var endpoint = string.IsNullOrWhiteSpace(snapshot.Identity.Username)
            ? snapshot.Identity.Address
            : $"{snapshot.Identity.Address} as {snapshot.Identity.Username}";
        string title;
        string message;
        string? hint = null;
        var failed = snapshot.State == RdpSessionState.Failed;
        var canCancel = false;
        var canRetry = false;
        var primaryLabel = "Reconnect";

        switch (snapshot.State)
        {
            case RdpSessionState.Created or RdpSessionState.Connecting:
                title = $"Connecting to {name}";
                message = endpoint;
                hint = snapshot.NativeDialogOpen
                    ? "Remote Desktop is waiting for you to sign in in the Windows Security window."
                    : "If Remote Desktop needs credentials, it asks in a separate sign-in window.";
                canCancel = true;
                break;
            case RdpSessionState.Reconnecting:
                title = $"Reconnecting to {name}";
                message = endpoint;
                hint = snapshot.NativeDialogOpen ? "Close the Windows Security window to continue." : null;
                canCancel = true;
                break;
            case RdpSessionState.Disconnecting:
                title = snapshot.Message;
                message = endpoint;
                hint = snapshot.NativeDialogOpen ? "Close the Windows Security window to finish." : null;
                break;
            case RdpSessionState.Failed:
                var category = snapshot.Failure?.Category
                    ?? (snapshot.EndReason == RdpEndReason.ControlError ? RdpFailureCategory.ClientError : RdpFailureCategory.Unknown);
                var guidance = RdpFailureClassifier.Describe(category, snapshot.EndReason, snapshot.Identity.Port);
                title = guidance.Title;
                message = snapshot.Detail ?? snapshot.Message;
                hint = snapshot.Failure is null ? null : guidance.Hint;
                canRetry = true;
                primaryLabel = "Try again";
                break;
            default:
                (title, message) = snapshot.EndReason switch
                {
                    RdpEndReason.ConnectionCancelled => ("Connection cancelled", $"The connection to {snapshot.Identity.Address} was cancelled."),
                    RdpEndReason.RemoteEnded => ("Session ended", snapshot.Detail ?? $"The remote session on {snapshot.Identity.Address} ended."),
                    _ => ("Disconnected", $"You disconnected from {snapshot.Identity.Address}.")
                };
                canRetry = true;
                break;
        }

        StateIcon.Text = failed ? "\uE783" : "\uE7F4";
        StateIcon.SetResourceReference(TextBlock.ForegroundProperty, failed ? "ErrorBrush" : "TertiaryText");
        StateTitle.Text = title;
        StateMessage.Text = message;
        StateHint.Text = hint ?? "";
        StateHint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
        PanelPrimaryButton.Content = primaryLabel;
        PanelPrimaryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        PanelCloseButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        PanelCancelButton.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
        StateActions.Visibility = canRetry || canCancel ? Visibility.Visible : Visibility.Collapsed;
        PanelCopyButton.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        ResetCopyButton();
    }

    /// <summary>Raised with a short message for the application status bar.</summary>
    public event Action<string>? StatusMessage;

    private void PanelCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            return;
        }
        var report = RdpDiagnosticReport.Build(_snapshot, DateTimeOffset.Now, AppVersion, WindowsVersion);
        try
        {
            System.Windows.Clipboard.SetText(report);
        }
        catch (System.Runtime.InteropServices.ExternalException exception)
        {
            AppLog.Warning("Copying diagnostic details failed.", exception);
            StatusMessage?.Invoke("Couldn't copy the details because another app is using the clipboard. Try again.");
            return;
        }
        PanelCopyButton.Content = "Copied";
        UiAssist.SetIcon(PanelCopyButton, "\uE73E");
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
        StatusMessage?.Invoke("Copied diagnostic details. Connection, computer, and user names were left out.");
    }

    private void ResetCopyButton()
    {
        _copyFeedbackTimer.Stop();
        PanelCopyButton.Content = "Copy details";
        UiAssist.SetIcon(PanelCopyButton, "\uE8C8");
    }

    private static string AppVersion =>
        typeof(SessionView).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private static string WindowsVersion =>
        $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})";

    private void UpdateSessionAreaSize()
    {
        if (_session is null || HostContainer.ActualWidth <= 0 || HostContainer.ActualHeight <= 0)
        {
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(HostContainer);
        _session.LayoutSize = new System.Drawing.Size(
            (int)Math.Round(HostContainer.ActualWidth * dpi.DpiScaleX),
            (int)Math.Round(HostContainer.ActualHeight * dpi.DpiScaleY));
    }

    // Collapses labels to icons so the single-row toolbar fits narrow workspaces.
    private void UpdateDensity(double width)
    {
        var compact = width < CompactWidth;
        var labelVisibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ResolutionLabel.Visibility = labelVisibility;
        ScaleLabel.Visibility = labelVisibility;
        ScalePicker.Margin = new Thickness(compact ? 6 : 0, 0, 0, 0);
        ReconnectButton.Content = compact ? null : "Reconnect";
        DisconnectButton.Content = compact ? null : "Disconnect";
        ProfileChangedText.Visibility = width < NarrowWidth ? Visibility.Collapsed : Visibility.Visible;
    }
}
