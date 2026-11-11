using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.CSharp.RuntimeBinder;

namespace RDCM;

/// <summary>
/// One session tab's Remote Desktop host: a stable container (the child of the tab's WindowsFormsHost) that owns the
/// logical session and display preferences, and holds one native ActiveX control generation at a time. Connection
/// lifecycle decisions live in <see cref="RdpConnectionController"/>, which can ask for the native control to be
/// replaced (the only way to end its own reconnection at once); this class performs the replacement on the UI thread.
/// </summary>
public sealed class EmbeddedRdpControl : Panel
{
    private const string RdpClientClassId = "{8B918B82-7985-4C24-89DF-C33AD2BBFBCD}";
    private const int TransitionPollInterval = 250;
    private const int SteadyPollInterval = 1000;

    private readonly RdpConnectionController _controller;
    private readonly RdpControlSlot<NativeRdpControl> _slot;
    private readonly RemoteDisplayCoordinator _display;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly System.Windows.Forms.Timer _resizeTimer;
    private RdpSessionState _lastPublishedState = RdpSessionState.Created;
    private bool _pendingWorkScheduled;
    private int _lastStaleGenerationLogged;
    private bool _disposed;

    /// <summary>Raised (in order) whenever the session state or status text changes.</summary>
    public event Action<RdpSessionSnapshot>? SessionChanged;

    /// <summary>
    /// A display change failed and the requested settings went back to the ones the session uses. The flag says whether
    /// to tell the user: a failure is reported once until a display change succeeds again.
    /// </summary>
    public event Action<string, bool>? DisplayError;

    public RdpSessionSnapshot Snapshot => _controller.Snapshot;
    public bool HasActiveSession => _controller.HasActiveSession;

    /// <summary>True while the control shows its own sign-in dialog. The control must not be disposed until it closes.</summary>
    public bool IsNativeDialogOpen => _controller.NativeDialogOpen;
    public RemoteResolution RequestedResolution => _display.Resolution;
    public RemoteScale RequestedScale => _display.Scale;
    public RemoteResolution ActiveResolution => _display.ActiveResolution;
    public RemoteScale ActiveScale => _display.ActiveScale;

    /// <summary>Display updates sent to the connected session. Diagnostics only.</summary>
    public int DisplayUpdatesSent => _display.UpdatesSent;

    /// <summary>The desktop size the remote session last reported, from the current control. Diagnostics only.</summary>
    public (int Width, int Height)? RemoteDesktopSize { get; private set; }

    /// <summary>The current native control generation (0 when there is none). Diagnostics only.</summary>
    public int ControlGeneration => _slot.Current?.Generation ?? 0;

    private bool IsSessionConnected => _controller.State == RdpSessionState.Connected;

    public EmbeddedRdpControl(SessionIdentity identity)
    {
        _controller = new RdpConnectionController(identity, new NativeClient(this), log: AppLog.Warning);
        _controller.Changed += OnControllerChanged;
        _slot = new RdpControlSlot<NativeRdpControl>(generation => new NativeRdpControl(this, generation), LogControl);
        _pollTimer = new System.Windows.Forms.Timer { Interval = TransitionPollInterval };
        _pollTimer.Tick += (_, _) => Poll();
        _resizeTimer = new System.Windows.Forms.Timer { Interval = RemoteDisplayCoordinator.SettleDelayMilliseconds };
        _display = new RemoteDisplayCoordinator(
            new DisplayClient(this),
            () => !_disposed && IsSessionConnected,
            () =>
            {
                _resizeTimer.Stop();
                _resizeTimer.Start();
            },
            () => new SessionAreaMetrics(Width, Height, Dpi.IsPlausible(DeviceDpi) ? new Dpi(DeviceDpi) : Dpi.Default));
        _display.StatusChanged += message => _controller.UpdateConnectedMessage(message);
        _display.UpdateFailed += ReportDisplayError;
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            _display.OnSettleDelayElapsed();
        };
        Dock = DockStyle.Fill;
        BackColor = System.Drawing.Color.FromArgb(20, 21, 23);
        // The first generation's ActiveX control is created with its window (see EnsureHandle).
        _slot.TryCreate();
    }

    /// <summary>
    /// Physical size and DPI of the tab's session area, measured from WPF layout. Used for the fit-to-tab resolution
    /// (also while the native surface is hidden, for example during connection setup) and the session's display scale.
    /// </summary>
    public void SetSessionArea(SessionAreaMetrics area)
    {
        if (!_disposed)
        {
            _display.SetSessionArea(area);
        }
    }

    /// <summary>The session area last reported (or the control's own size before layout). Diagnostics only.</summary>
    public SessionAreaMetrics SessionArea => _display.Area;

    public void DisconnectSession()
    {
        if (!_disposed && _controller.Disconnect())
        {
            UpdatePolling();
            SchedulePendingWork();
        }
    }

    /// <summary>
    /// Creates the native window while the host is hidden. WinForms never creates handles for invisible
    /// controls, and the session surface stays hidden until the connection is established.
    /// </summary>
    public void EnsureHandle()
    {
        if (_disposed || IsDisposed)
        {
            return;
        }
        if (!IsHandleCreated)
        {
            _ = Handle;
        }
        if (_slot.Current is { IsHandleCreated: false } native)
        {
            _ = native.Handle;
        }
    }

    public void ReconnectSession() => ReconnectWithIdentity(null);

    public void ReconnectWithIdentity(SessionIdentity? identity)
    {
        if (!_disposed && _controller.Reconnect(identity))
        {
            UpdatePolling();
            SchedulePendingWork();
        }
    }

    /// <summary>Moves keyboard focus into the remote desktop.</summary>
    public bool FocusRemoteDesktop() => !_disposed && _slot.Current is { } native && native.Focus();

    /// <summary>Called when a generation's window exists; it starts once the current message has been handled.</summary>
    private void OnNativeHandleCreated(NativeRdpControl native)
    {
        BeginInvoke(new Action(() =>
        {
            if (_disposed || native.Started || !_slot.IsCurrent(native.Generation))
            {
                return;
            }
            native.Started = true;
            _controller.Start();
            UpdatePolling();
        }));
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // Null only while the base constructor runs.
        _display?.OnControlResized();
    }

    /// <summary>
    /// A control generation's window is created while this panel is hidden (the session surface stays hidden until the
    /// connection is established, and a replacement is created while the session is ending). AxHost then hides the
    /// ActiveX window (OLEIVERB_HIDE) and never shows it again when the panel appears, so the remote desktop would stay
    /// blank until something activated the control. Shows it without activating it, so keyboard focus doesn't move.
    /// </summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && !_disposed && _slot.Current is { IsHandleCreated: true } native &&
            (GetWindowLong(native.Handle, GwlStyle) & WsVisible) == 0)
        {
            ShowWindow(native.Handle, SwShowNoActivate);
        }
    }

    private const int GwlStyle = -16;
    private const int WsVisible = 0x10000000;
    private const int SwShowNoActivate = 8;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    /// <summary>The remote session reported its desktop size (it changed or the session started). Diagnostics only.</summary>
    private void OnRemoteDesktopSizeChanged(int generation, int width, int height)
    {
        if (_disposed || !_slot.IsCurrent(generation))
        {
            return;
        }
        RemoteDesktopSize = (width, height);
        AppLog.Information($"Session {_controller.Identity.SessionId}: the remote desktop reports {width} × {height}.");
    }

    /// <summary>Delivers a native event to the controller only if it came from the current control generation.</summary>
    private void DispatchNativeEvent(int generation, Action<RdpConnectionController> handler)
    {
        if (_disposed)
        {
            return;
        }
        if (!_slot.Route(generation, () => Deliver(handler)))
        {
            LogStaleEvent(generation);
            return;
        }
        UpdatePolling();
        SchedulePendingWork();
    }

    private void Deliver(Action<RdpConnectionController> handler)
    {
        try
        {
            handler(_controller);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Never let a managed exception escape into the ActiveX control.
            AppLog.Error("Handling a Remote Desktop event failed.", exception);
        }
    }

    /// <summary>
    /// Answers the control's OnAutoReconnecting question synchronously. A released generation, or any error, is told
    /// to stop.
    /// </summary>
    private bool ContinueAutoReconnect(int generation, int attempt)
    {
        if (_disposed || !_slot.IsCurrent(generation))
        {
            LogStaleEvent(generation);
            return false;
        }
        try
        {
            return _controller.OnNativeAutoReconnectDecision(attempt);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.Error("Deciding whether Remote Desktop may keep reconnecting failed; it is asked to stop.", exception);
            return false;
        }
        finally
        {
            UpdatePolling();
            SchedulePendingWork();
        }
    }

    private void LogStaleEvent(int generation)
    {
        if (_lastStaleGenerationLogged == generation)
        {
            return;
        }
        _lastStaleGenerationLogged = generation;
        AppLog.Warning($"Session {_controller.Identity.SessionId}: ignored an event from released Remote Desktop control generation {generation}.");
    }

    private void LogControl(string message, Exception? exception) =>
        AppLog.Warning($"Session {_controller.Identity.SessionId}: {message}", exception);

    /// <summary>
    /// Runs a pending replacement or reconnect outside native callbacks. Background priority: the accepted state (for
    /// example "Disconnecting…") is shown before a control is released.
    /// </summary>
    private void SchedulePendingWork()
    {
        if (_disposed || _pendingWorkScheduled || !_controller.HasPendingWork)
        {
            return;
        }
        _pendingWorkScheduled = true;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
            {
                _pendingWorkScheduled = false;
                Poll();
            }));
    }

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }
        _controller.Poll();
        UpdatePolling();
    }

    private void UpdatePolling()
    {
        if (_disposed || !_controller.NeedsPolling)
        {
            _pollTimer.Stop();
            return;
        }
        var interval = _controller.State is RdpSessionState.Connected
            or RdpSessionState.Disconnected or RdpSessionState.Failed
            ? SteadyPollInterval
            : TransitionPollInterval;
        if (_pollTimer.Interval != interval)
        {
            _pollTimer.Interval = interval;
        }
        _pollTimer.Start();
    }

    private void OnControllerChanged(RdpSessionSnapshot snapshot)
    {
        var enteredConnected = snapshot.State == RdpSessionState.Connected &&
                               _lastPublishedState != RdpSessionState.Connected;
        _lastPublishedState = snapshot.State;
        SessionChanged?.Invoke(snapshot);
        if (!enteredConnected)
        {
            return;
        }

        _display.OnConnected();
    }

    public bool SetResolution(RemoteResolution resolution) => _display.SetResolution(resolution);

    public bool SetScale(RemoteScale scale) => _display.SetScale(scale);

    private void ReportDisplayError(string message, Exception exception, bool notifyUser)
    {
        AppLog.Warning($"Display change failed: {message}", exception);
        _controller.UpdateConnectedMessage(message);
        // Always raised: the settings went back to the last confirmed ones, so the toolbar must show them again even
        // when the user isn't told again.
        DisplayError?.Invoke(message, notifyUser);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            // Same order as before control replacement existed (live-verified to end auto-reconnect at once): the
            // controller asks a connected control to disconnect, then events are detached and the control released.
            _controller.Shutdown();
            _disposed = true;
            _pollTimer.Dispose();
            _resizeTimer.Dispose();
            _controller.Changed -= OnControllerChanged;
            _slot.Release(requestDisconnect: false);
        }

        base.Dispose(disposing);
    }

    /// <summary>Releases the current control generation and creates a fresh, disconnected one (UI thread only).</summary>
    private bool ReplaceNativeControl()
    {
        if (_disposed)
        {
            return false;
        }
        var previous = _slot.Current?.Generation ?? 0;
        var replaced = _slot.Replace();
        AppLog.Warning($"Session {_controller.Identity.SessionId}: Remote Desktop control generation {previous} released; " +
                       (replaced ? $"generation {_slot.Current!.Generation} created." : "no new generation could be created."));
        return replaced;
    }

    /// <summary>
    /// One generation of the Remote Desktop ActiveX control. Its events carry its generation, so once it is released
    /// nothing it raises reaches the session. It is never reused.
    /// </summary>
    private sealed class NativeRdpControl(EmbeddedRdpControl owner, int generation)
        : AxHost(RdpClientClassId), IRdpControlInstance
    {
        private ConnectionPointCookie? _cookie;

        public int Generation { get; } = generation;

        /// <summary>The session may use it: its window exists and its start has been processed.</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Started { get; set; }

        public void Initialize()
        {
            Dock = DockStyle.Fill;
            BackColor = owner.BackColor;
            owner.Controls.Add(this);
            if (owner.IsHandleCreated)
            {
                // A replacement: create the ActiveX control now so the session has a usable control again.
                _ = Handle;
                if (GetOcx() is null)
                {
                    throw new InvalidOperationException("The Remote Desktop control was not created.");
                }
            }
        }

        public void RequestDisconnectIfActive()
        {
            if (!IsHandleCreated || GetOcx() is not { } ocx)
            {
                return;
            }
            dynamic client = ocx;
            if ((int)client.Connected != RdpConnectionController.NativeNotConnected)
            {
                client.Disconnect();
            }
        }

        public void DetachEvents()
        {
            _cookie?.Disconnect();
            _cookie = null;
        }

        protected override void CreateSink()
        {
            try
            {
                var ocx = GetOcx() ?? throw new InvalidOperationException("The Remote Desktop control was not created.");
                var generation = Generation;
                _cookie = new ConnectionPointCookie(ocx,
                    new RdpClientEventSink(
                        handler => owner.DispatchNativeEvent(generation, handler),
                        attempt => owner.ContinueAutoReconnect(generation, attempt),
                        (width, height) => owner.OnRemoteDesktopSizeChanged(generation, width, height)),
                    typeof(IMsTscAxEvents));
                owner._controller.NativeEventsAvailable = true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                AppLog.Warning("Remote Desktop events are unavailable; falling back to state polling.", exception);
                owner._controller.NativeEventsAvailable = false;
            }
        }

        protected override void DetachSink() => DetachEvents();

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            owner.OnNativeHandleCreated(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DetachEvents();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Sends display settings to the current control generation only; a released generation is never updated.
    /// </summary>
    private sealed class DisplayClient(EmbeddedRdpControl owner) : IRemoteDisplayClient
    {
        public void Apply(RemoteDisplaySettings settings)
        {
            try
            {
                dynamic client = (owner._disposed ? null : owner._slot.Current?.GetOcx())
                    ?? throw new RemoteDisplayUpdateException("The Remote Desktop control is unavailable.");
                client.UpdateSessionDisplaySettings(
                    (uint)settings.DesktopWidth,
                    (uint)settings.DesktopHeight,
                    settings.PhysicalWidthMillimeters,
                    settings.PhysicalHeightMillimeters,
                    0u,
                    settings.DesktopScaleFactor,
                    settings.DeviceScaleFactor);
                client.AdvancedSettings9.SmartSizing = true;
            }
            catch (COMException exception)
            {
                throw new RemoteDisplayUpdateException(
                    "The remote computer rejected the display change. It may not support dynamic resolution or scaling.", exception);
            }
            catch (InvalidComObjectException exception)
            {
                throw new RemoteDisplayUpdateException("The Remote Desktop control stopped responding.", exception);
            }
            catch (RuntimeBinderException exception)
            {
                throw new RemoteDisplayUpdateException(
                    "This version of Remote Desktop doesn't support changing display settings during a session.", exception);
            }
        }
    }

    /// <summary>Adapts the ActiveX automation interface; translates native failures to <see cref="RdpClientException"/>.</summary>
    private sealed class NativeClient(EmbeddedRdpControl owner) : IRdpNativeClient
    {
        public bool IsReady => !owner._disposed && owner._slot.Current is { Started: true, IsHandleCreated: true } native
                               && native.GetOcx() is not null;

        public bool ReplaceControl() => owner.ReplaceNativeControl();

        public int ReadConnectionState() =>
            Invoke(client => (int)client.Connected, "Couldn't read the connection state");

        public ServerAuthenticationStatus BeginConnect(SessionIdentity identity) => Invoke(client =>
        {
            client.Server = identity.Host.Trim().TrimStart('[').TrimEnd(']');
            client.UserName = identity.Username.Trim();
            // From the session area and preferences as they are now: a fresh control after a move uses the current
            // monitor's pixels and device scale factor, and the remote scaling chosen for this session.
            var display = owner._display.SettingsForConnect();
            client.DesktopWidth = display.DesktopWidth;
            client.DesktopHeight = display.DesktopHeight;
            try
            {
                RdpExtendedSettings.SetScaleFactors((object)client, display.DesktopScaleFactor, display.DeviceScaleFactor);
            }
            catch (Exception exception) when (exception is InvalidCastException or COMException or ArgumentException)
            {
                AppLog.Warning("The remote scaling couldn't be set before connecting; it is sent once the session is connected.", exception);
                owner._display.OnConnectScaleUnavailable();
            }
            AppLog.Information($"Connecting with a {display.DesktopWidth} × {display.DesktopHeight} remote desktop; " +
                               $"desktop scale {display.DesktopScaleFactor}%, device scale {display.DeviceScaleFactor}%.");
            client.AdvancedSettings9.RDPPort = identity.Port;
            client.AdvancedSettings9.EnableCredSspSupport = true;
            // The control returns S_FALSE (not an error) when a policy overrides this, so read back what applies.
            client.AdvancedSettings9.AuthenticationLevel = (uint)ServerAuthentication.RequestedMode;
            int? readBack;
            try
            {
                readBack = (int)(uint)client.AdvancedSettings9.AuthenticationLevel;
            }
            catch (Exception exception) when (exception is COMException or RuntimeBinderException or InvalidCastException)
            {
                AppLog.Warning("The server authentication level couldn't be read back.", exception);
                readBack = null;
            }
            client.AdvancedSettings9.SmartSizing = true;
            var status = ServerAuthentication.Evaluate(ServerAuthentication.RequestedMode, readBack, ServerAuthentication.ReadMachinePolicy());
            client.Connect();
            return status;
        }, "Remote Desktop couldn't start the connection");

        public void RequestDisconnect() => Invoke(client =>
        {
            client.Disconnect();
            return 0;
        }, "Remote Desktop rejected the disconnect request");

        public RdpDisconnectInfo DescribeDisconnect(int reason)
        {
            try
            {
                dynamic client = owner._slot.Current?.GetOcx() ?? throw new InvalidOperationException("Control unavailable.");
                var extended = (int)client.ExtendedDisconnectReason;
                return new RdpDisconnectInfo(extended, (string)client.GetErrorDescription((uint)reason, extended));
            }
            catch (Exception exception) when (IsNativeFailure(exception))
            {
                AppLog.Warning($"No description is available for disconnect reason {reason}.", exception);
                return new RdpDisconnectInfo(0, null);
            }
        }

        private T Invoke<T>(Func<dynamic, T> action, string message)
        {
            try
            {
                dynamic client = owner._slot.Current?.GetOcx()
                    ?? throw new InvalidOperationException("The Remote Desktop control is not available.");
                return action(client);
            }
            catch (Exception exception) when (IsNativeFailure(exception))
            {
                var code = exception is COMException com ? $" (error 0x{com.HResult:X8})" : "";
                throw new RdpClientException($"{message}{code}.", exception);
            }
        }

        private static bool IsNativeFailure(Exception exception) =>
            exception is COMException or InvalidComObjectException or RuntimeBinderException
                or InvalidOperationException or InvalidCastException;
    }
}
