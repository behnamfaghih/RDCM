using System.Windows;
using System.Windows.Automation;
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
        ApplyServerAuthentication(snapshot.ServerAuthentication);
        var indicator = SessionIndicator.For(snapshot.State);
        StateDot.Tag = indicator;
        StatusTextBlock.SetResourceReference(TextBlock.ForegroundProperty,
            snapshot.State == RdpSessionState.Failed ? "ErrorBrush" : "PrimaryText");
        ActivityBar.Visibility = indicator == SessionIndicator.Busy && SystemParameters.ClientAreaAnimation
            ? Visibility.Visible
            : Visibility.Collapsed;

        // The native surface stays up while the control auto-reconnects, so keyboard focus survives the interruption.
        var showSession = snapshot.State is RdpSessionState.Connected or RdpSessionState.AutoReconnecting;
        if (_host is not null)
        {
            if (!showSession)
            {
                // Keys must not keep going to a hidden (or released, or replaced) remote desktop window.
                MoveFocusOutOfSession(_host);
            }
            _host.Visibility = showSession ? Visibility.Visible : Visibility.Hidden;
        }
        StatePanel.Visibility = showSession ? Visibility.Collapsed : Visibility.Visible;
        if (!showSession)
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
                hint = snapshot.NativeDialogOpen ? "Close the Windows Security window to continue." : snapshot.Detail;
                canCancel = true;
                break;
            case RdpSessionState.Disconnecting:
                title = snapshot.Message;
                message = endpoint;
                hint = snapshot.NativeDialogOpen ? "Close the Windows Security window to finish." : snapshot.Detail;
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

    /// <summary>
    /// Where keyboard focus goes when the remote desktop is hidden while it has focus: the session's tab (its accessible
    /// name says which session and its state, and Tab and the arrow keys continue from there), or the release-keyboard
    /// button when the view isn't in a tab.
    /// </summary>
    internal UIElement FocusTargetWhenSessionHides =>
        System.Windows.LogicalTreeHelper.GetParent(this) is TabItem { IsEnabled: true } tab ? tab : AppControlsButton;

    private void MoveFocusOutOfSession(WindowsFormsHost host)
    {
        if (Window.GetWindow(this) is not { } window)
        {
            return;
        }
        var target = FocusTargetWhenSessionHides;
        if (window.IsActive && SessionHasKeyboardFocus(host) && target.Focus())
        {
            return;
        }
        // An inactive window restores its last focused element when it is activated again; that must not be the session.
        var scope = System.Windows.Input.FocusManager.GetFocusScope(target);
        if (System.Windows.Input.FocusManager.GetFocusedElement(scope) is not DependencyObject remembered ||
            ReferenceEquals(remembered, host) || (remembered is Visual visual && visual.IsDescendantOf(host)))
        {
            System.Windows.Input.FocusManager.SetFocusedElement(scope, target);
        }
    }

    /// <summary>
    /// The remote desktop (or anything else in the session host) has keyboard focus. Its input window can belong to another
    /// thread, which GetFocus doesn't report, so the foreground thread's focus is checked too; WPF itself then reports no
    /// focused element.
    /// </summary>
    private static bool SessionHasKeyboardFocus(WindowsFormsHost host)
    {
        if (host.IsKeyboardFocusWithin)
        {
            return true;
        }
        var root = host.Handle;
        if (root == IntPtr.Zero)
        {
            return false;
        }
        bool Inside(IntPtr window) => window != IntPtr.Zero && (window == root || IsChild(root, window));
        if (Inside(GetFocus()))
        {
            return true;
        }
        var info = new GuiThreadInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(0, ref info) && Inside(info.Focus);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public int CaretLeft;
        public int CaretTop;
        public int CaretRight;
        public int CaretBottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr window);

    // Never hidden while it applies: Remote Desktop won't warn if the server isn't the intended computer.
    private void ApplyServerAuthentication(ServerAuthenticationStatus? status)
    {
        var weakened = status?.IsWeakened == true;
        ServerIdentityChip.Visibility = weakened ? Visibility.Visible : Visibility.Collapsed;
        if (!weakened)
        {
            return;
        }
        var explanation = status!.Source == ServerAuthenticationSource.MachinePolicy
            ? "A policy on this PC (\"Configure server authentication for client\") lets Remote Desktop connect even when it can't verify the server's identity, so you won't be warned if this isn't the computer you intended. Ask your administrator if you need server authentication."
            : "Remote Desktop didn't apply RDCM's request to verify the server's identity, so you might not be warned if this isn't the computer you intended.";
        ServerIdentityChip.ToolTip = explanation;
        AutomationProperties.SetHelpText(ServerIdentityChip, explanation);
    }

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

    /// <summary>
    /// After a move to a monitor with a different scale, the session area keeps its size in device-independent units, so
    /// no SizeChanged follows; its physical size and DPI must be measured again here. The new DPI comes from the
    /// arguments: when a hidden tab is shown again, this runs before the session area's own DPI is updated.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateSessionAreaSize(newDpi);
    }

    private void UpdateSessionAreaSize(DpiScale? knownDpi = null)
    {
        if (_session is null || HostContainer.ActualWidth <= 0 || HostContainer.ActualHeight <= 0)
        {
            return;
        }
        var scale = knownDpi ?? VisualTreeHelper.GetDpi(HostContainer);
        var dpi = Dpi.IsPlausible((int)Math.Round(scale.PixelsPerInchX)) ? new Dpi((int)Math.Round(scale.PixelsPerInchX)) : Dpi.Default;
        _session.SetSessionArea(new SessionAreaMetrics(dpi.ToPhysical(HostContainer.ActualWidth), dpi.ToPhysical(HostContainer.ActualHeight), dpi));
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
        ServerIdentityText.Visibility = width < NarrowWidth ? Visibility.Collapsed : Visibility.Visible;
    }
}
