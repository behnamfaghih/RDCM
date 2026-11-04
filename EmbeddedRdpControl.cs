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
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly System.Windows.Forms.Timer _resizeTimer;
    private RemoteResolution _resolution = RemoteDisplayOptions.Resolutions[0];
    private RemoteScale _scale = RemoteDisplayOptions.Scales[0];
    private RemoteResolution _activeResolution = RemoteDisplayOptions.Resolutions[0];
    private RemoteScale _activeScale = RemoteDisplayOptions.Scales[0];
    private RdpSessionState _lastPublishedState = RdpSessionState.Created;
    private System.Drawing.Size? _layoutSize;
    private (int Width, int Height) _requestedDesktopSize;
    private bool _hasReportedDisplayError;
    private bool _displaySettingsChanged;
    private bool _pendingWorkScheduled;
    private int _lastStaleGenerationLogged;
    private bool _disposed;

    /// <summary>Raised (in order) whenever the session state or status text changes.</summary>
    public event Action<RdpSessionSnapshot>? SessionChanged;
    public event Action<string>? DisplayError;

    public RdpSessionSnapshot Snapshot => _controller.Snapshot;
    public bool HasActiveSession => _controller.HasActiveSession;

    /// <summary>True while the control shows its own sign-in dialog. The control must not be disposed until it closes.</summary>
    public bool IsNativeDialogOpen => _controller.NativeDialogOpen;
    public RemoteResolution RequestedResolution => _resolution;
    public RemoteScale RequestedScale => _scale;
    public RemoteResolution ActiveResolution => _activeResolution;
    public RemoteScale ActiveScale => _activeScale;

    /// <summary>The current native control generation (0 when there is none). Diagnostics only.</summary>
    public int ControlGeneration => _slot.Current?.Generation ?? 0;

    private bool IsSessionConnected => _controller.State == RdpSessionState.Connected;

    public EmbeddedRdpControl(SessionIdentity identity)
    {
        _controller = new RdpConnectionController(identity, new NativeClient(this), log: AppLog.Warning);
        _controller.Changed += OnControllerChanged;
        _slot = new RdpControlSlot<NativeRdpControl>(generation => new NativeRdpControl(this, generation), LogControl);
        Dock = DockStyle.Fill;
        BackColor = System.Drawing.Color.FromArgb(20, 21, 23);
        _pollTimer = new System.Windows.Forms.Timer { Interval = TransitionPollInterval };
        _pollTimer.Tick += (_, _) => Poll();
        _resizeTimer = new System.Windows.Forms.Timer { Interval = 350 };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            if (IsSessionConnected && _resolution.IsFitToTab && GetRequestedResolution() != _requestedDesktopSize)
            {
                ApplyDisplaySettings();
            }
        };
        // The first generation's ActiveX control is created with its window (see EnsureHandle).
        _slot.TryCreate();
    }

    /// <summary>
    /// Size of the tab's session area in device pixels. Used for fit-to-tab resolution while the
    /// native surface is hidden (for example during connection setup).
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public System.Drawing.Size? LayoutSize
    {
        get => _layoutSize;
        set
        {
            if (_layoutSize == value)
            {
                return;
            }
            _layoutSize = value;
            ScheduleFitToTabUpdate();
        }
    }

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
        ScheduleFitToTabUpdate();
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

    private void ScheduleFitToTabUpdate()
    {
        if (!_disposed && IsSessionConnected && _resolution.IsFitToTab)
        {
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }
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

        if (_displaySettingsChanged)
        {
            ApplyDisplaySettings();
        }
        else
        {
            _activeResolution = _resolution;
            _activeScale = _scale;
            _controller.UpdateConnectedMessage(DescribeConnectedDisplay());
        }
    }

    public bool SetResolution(RemoteResolution resolution)
    {
        if (_resolution == resolution)
        {
            return true;
        }

        var previous = _resolution;
        _resolution = resolution;
        _displaySettingsChanged = true;
        if (IsSessionConnected && !ApplyDisplaySettings())
        {
            _resolution = previous;
            return false;
        }
        return true;
    }

    public bool SetScale(RemoteScale scale)
    {
        if (_scale == scale)
        {
            return true;
        }

        var previous = _scale;
        _scale = scale;
        _displaySettingsChanged = true;
        if (IsSessionConnected && !ApplyDisplaySettings())
        {
            _scale = previous;
            return false;
        }
        return true;
    }

    private (int Width, int Height) GetRequestedResolution()
    {
        if (!_resolution.IsFitToTab)
        {
            return (_resolution.Width, _resolution.Height);
        }

        static int RoundToSupportedSize(int size, int minimum) =>
            Math.Clamp(size - size % 4, minimum, 8192);

        var size = GetSessionAreaSize();
        return (RoundToSupportedSize(size.Width, 640), RoundToSupportedSize(size.Height, 480));
    }

    private System.Drawing.Size GetSessionAreaSize() =>
        _layoutSize is { Width: > 0, Height: > 0 } layout ? layout : new System.Drawing.Size(Width, Height);

    private string DescribeConnectedDisplay()
    {
        var (width, height) = GetRequestedResolution();
        return $"Connected · {width} × {height} · {_scale.Percent}%";
    }

    private bool ApplyDisplaySettings()
    {
        try
        {
            dynamic client = _slot.Current?.GetOcx()
                ?? throw new InvalidOperationException("The Remote Desktop control is unavailable.");
            var (width, height) = GetRequestedResolution();
            var area = GetSessionAreaSize();
            var physicalWidth = GetPhysicalSizeMillimeters(area.Width);
            var physicalHeight = GetPhysicalSizeMillimeters(area.Height);

            client.UpdateSessionDisplaySettings(
                (uint)width,
                (uint)height,
                physicalWidth,
                physicalHeight,
                0u,
                _scale.Percent,
                GetDeviceScaleFactor());
            client.AdvancedSettings9.SmartSizing = true;
            _requestedDesktopSize = (width, height);
            _displaySettingsChanged = false;
            _hasReportedDisplayError = false;
            _activeResolution = _resolution;
            _activeScale = _scale;
            _controller.UpdateConnectedMessage(DescribeConnectedDisplay());
            return true;
        }
        catch (COMException exception)
        {
            RestoreActiveDisplaySettings();
            ReportDisplayError(
                "The remote computer rejected the display change. It may not support dynamic resolution or scaling.",
                exception);
        }
        catch (InvalidComObjectException exception)
        {
            RestoreActiveDisplaySettings();
            ReportDisplayError("The Remote Desktop control stopped responding.", exception);
        }
        catch (RuntimeBinderException exception)
        {
            RestoreActiveDisplaySettings();
            ReportDisplayError("This version of Remote Desktop doesn't support changing display settings during a session.", exception);
        }
        catch (InvalidOperationException exception)
        {
            RestoreActiveDisplaySettings();
            ReportDisplayError(exception.Message, exception);
        }
        return false;
    }

    private void RestoreActiveDisplaySettings()
    {
        _resolution = _activeResolution;
        _scale = _activeScale;
        _displaySettingsChanged = false;
    }

    private uint GetPhysicalSizeMillimeters(int sizeInPixels)
    {
        var dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        var millimeters = (uint)Math.Clamp(
            Math.Round(sizeInPixels * 25.4 / dpi),
            10,
            10_000);
        return millimeters;
    }

    private uint GetDeviceScaleFactor()
    {
        var scale = DeviceDpi * 100d / 96;
        return (uint)new[] { 100, 140, 180 }
            .OrderBy(candidate => Math.Abs(candidate - scale))
            .First();
    }

    private void ReportDisplayError(string message, Exception exception)
    {
        AppLog.Warning($"Display change failed: {message}", exception);
        _controller.UpdateConnectedMessage(message);
        if (_hasReportedDisplayError)
        {
            return;
        }

        _hasReportedDisplayError = true;
        DisplayError?.Invoke(message);
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
                        attempt => owner.ContinueAutoReconnect(generation, attempt)),
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
            var (width, height) = owner.GetRequestedResolution();
            client.DesktopWidth = width;
            client.DesktopHeight = height;
            owner._requestedDesktopSize = (width, height);
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
