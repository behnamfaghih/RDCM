namespace RDCM;

/// <summary>Why a session is no longer connected. Only the Connect/Lost/Control reasons are failures.</summary>
public enum RdpEndReason
{
    None,
    UserDisconnected,
    ConnectionCancelled,
    RemoteEnded,
    ConnectFailed,
    ConnectionLost,
    ControlError
}

/// <summary>
/// Immutable view of a session, published in order with an increasing sequence number.
/// <paramref name="NativeDialogOpen"/> is true while the control waits on its own sign-in dialog.
/// </summary>
public sealed record RdpSessionSnapshot(
    long Sequence,
    RdpSessionState State,
    RdpEndReason EndReason,
    string Message,
    string? Detail,
    SessionIdentity Identity,
    RdpFailureInfo? Failure = null,
    bool NativeDialogOpen = false,
    ServerAuthenticationStatus? ServerAuthentication = null,
    AutoReconnectProgress? AutoReconnect = null);

/// <summary>
/// Native auto-reconnect progress from IMsTscAxEvents::OnAutoReconnecting2. <see cref="Attempt"/> and
/// <see cref="MaxAttempts"/> are null when the control reports values outside the documented range.
/// </summary>
public sealed record AutoReconnectProgress(int? Attempt, int? MaxAttempts, bool NetworkAvailable, int DisconnectReason)
{
    public static AutoReconnectProgress From(int reason, bool networkAvailable, int attempt, int maxAttempts)
    {
        // MaxReconnectAttempts is documented as 0-200; anything else isn't shown as progress. Live, the control sends one
        // final notification numbered one past the maximum (6 of 5) before it gives up; that is still the last attempt.
        if (maxAttempts is >= 1 and <= 200 && attempt == maxAttempts + 1)
        {
            attempt = maxAttempts;
        }
        var valid = attempt >= 1 && maxAttempts is >= 1 and <= 200 && attempt <= maxAttempts;
        return new(valid ? attempt : null, valid ? maxAttempts : null, networkAvailable, reason);
    }

    public string Describe() => Attempt is { } attempt && MaxAttempts is { } max
        ? $"attempt {attempt} of {max}"
        : "attempting to reconnect";
}

public sealed record RdpDisconnectInfo(int ExtendedReason, string? Description);

/// <summary>Thin abstraction over the Remote Desktop ActiveX control so the lifecycle can be tested.</summary>
public interface IRdpNativeClient
{
    bool IsReady { get; }

    /// <summary>The control's Connected property: 0 = not connected, 1 = connected, 2 = connecting.</summary>
    int ReadConnectionState();

    /// <summary>Configures and starts a connection; returns the server authentication actually in effect.</summary>
    ServerAuthenticationStatus BeginConnect(SessionIdentity identity);
    void RequestDisconnect();
    RdpDisconnectInfo DescribeDisconnect(int reason);

    /// <summary>
    /// Releases the native control and puts a fresh, disconnected one in its place. This is the only way found to end the
    /// control's own reconnection at once: it ignores <see cref="RequestDisconnect"/> while it reconnects (observed live),
    /// while releasing it (as closing a tab does) ends it immediately. Nothing from the released control reaches the
    /// controller afterwards. Never called from inside a native callback, or while the control's sign-in window is open.
    /// Returns false when no fresh control could be created; the old one is released either way.
    /// </summary>
    bool ReplaceControl();
}

/// <summary>A native control call failed. The message is suitable for users; details are in the inner exception.</summary>
public sealed class RdpClientException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Owns the connection lifecycle of one embedded RDP session. Native events (when available) are the
/// primary signal; <see cref="Poll"/> is a watchdog that covers missed events and enforces teardown.
/// All members must be called on the UI thread that owns the ActiveX control.
/// </summary>
public sealed class RdpConnectionController
{
    public const int NativeNotConnected = 0;
    public const int NativeConnected = 1;
    public const int NativeConnecting = 2;

    // IMsTscAxEvents::OnDisconnected reasons that are not errors.
    private const int LocalDisconnect = 1;
    private const int RemoteDisconnectByUser = 2;
    private const int RemoteDisconnectByServer = 3;

    // Not in the documented reason list: the control reports it when the user clicks Cancel in the Windows Security
    // sign-in window (observed live with mstscax 10.0.26100; the control has no description for it).
    private const int SignInPromptCancelled = 0x1F07;

    public const long StartTimeoutMs = 15_000;
    public const long ConnectingTimeoutMs = 60_000;
    public const long EventGraceMs = 3_000;
    public const long DisconnectRetryMs = 2_000;
    public const long DisconnectTimeoutMs = 20_000;

    /// <summary>
    /// Safety net while the control auto-reconnects: each attempt event restarts it. Generous because attempts
    /// are documented at five-second intervals and a single attempt can wait for a TCP connect timeout.
    /// </summary>
    public const long AutoReconnectSilenceTimeoutMs = 120_000;

    private readonly IRdpNativeClient _client;
    private readonly Func<long> _clock;
    private readonly Action<string, Exception?> _log;
    private readonly RdpSessionModel _model;
    private RdpEndReason _endReason;
    private string _message = "Preparing connection…";
    private string? _detail;
    private RdpFailureInfo? _failure;
    private ServerAuthenticationStatus? _serverAuthentication;
    private AutoReconnectProgress? _autoReconnect;
    private long _sequence;
    private RdpSessionSnapshot _published;
    private SessionIdentity? _pendingIdentity;
    private bool _connectedThisAttempt;
    private bool _connectDeferred;
    private bool _awaitingNativeStart;
    private bool _nativeActive;
    private int _lastNativeState;
    private long _phaseStartedAt;
    private long _nativeIdleSince = -1;
    private long _lastDisconnectRequestAt = long.MinValue / 2;
    private bool _signInPromptShownThisAttempt;

    // While the control auto-reconnects it ignores Disconnect() and the documented "stop" answer to OnAutoReconnecting
    // (observed live); only releasing it ends that at once. _nativeAutoReconnecting: the control is in its reconnect
    // loop (set by its attempt events in any state, cleared when it reports a disconnect or a restore, or is replaced).
    // _replacementPending: the user's intent has been accepted and the control will be replaced (see Poll); until then
    // nothing it raises can change the session.
    private bool _nativeAutoReconnecting;
    private bool _replacementPending;

    // The last replacement left the session without a usable control; the next explicit Reconnect tries to create one.
    private bool _controlLost;

    public RdpConnectionController(
        SessionIdentity identity,
        IRdpNativeClient client,
        Func<long>? clock = null,
        Action<string, Exception?>? log = null)
    {
        _client = client;
        _clock = clock ?? (() => Environment.TickCount64);
        _log = log ?? ((_, _) => { });
        _model = new RdpSessionModel(identity);
        _published = CreateSnapshot(0);
    }

    public event Action<RdpSessionSnapshot>? Changed;

    /// <summary>Set by the host once the native event sink is connected.</summary>
    public bool NativeEventsAvailable { get; set; }

    public RdpSessionState State => _model.State;
    public SessionIdentity Identity => _model.Identity;
    public bool HasActiveSession => _model.HasActiveSession;
    public RdpSessionSnapshot Snapshot => _published;

    /// <summary>
    /// The control is showing its own modal dialog (the Windows Security sign-in prompt). The control must not be
    /// released while this is true: tearing it down then blocks inside the native control.
    /// </summary>
    public bool NativeDialogOpen { get; private set; }

    public bool NeedsPolling => _replacementPending
                                || State is RdpSessionState.Connecting or RdpSessionState.Connected
                                    or RdpSessionState.Disconnecting or RdpSessionState.Reconnecting
                                    or RdpSessionState.AutoReconnecting
                                || (State is RdpSessionState.Disconnected or RdpSessionState.Failed
                                    && (_lastNativeState != NativeNotConnected || _nativeActive));

    /// <summary>
    /// A control replacement or a reconnect is ready to run; the host should call <see cref="Poll"/> soon, outside
    /// native callbacks (after the UI has shown the accepted state).
    /// </summary>
    public bool HasPendingWork => ReplacementDue || (State == RdpSessionState.Reconnecting && !_nativeActive);

    /// <summary>Releasing the control while its sign-in window is open blocks inside the control, so that waits.</summary>
    private bool ReplacementDue => _replacementPending && !NativeDialogOpen;

    /// <summary>Events are not accepted from a control that is being replaced or after the session was disposed.</summary>
    private bool IgnoringNativeEvents => State == RdpSessionState.Disposed || _replacementPending;

    /// <summary>The native control is ready; starts the initial connection unless it was cancelled.</summary>
    public void Start()
    {
        if (State == RdpSessionState.Created)
        {
            BeginAttempt(null);
        }
        else if (State == RdpSessionState.Connecting && _connectDeferred && _client.IsReady)
        {
            _connectDeferred = false;
            InvokeNativeConnect();
        }
        Publish();
    }

    /// <summary>User-requested disconnect or cancellation. Returns false when there is nothing to disconnect.</summary>
    public bool Disconnect()
    {
        switch (State)
        {
            case RdpSessionState.Created:
            case RdpSessionState.Connecting when _connectDeferred:
                Finish(RdpSessionState.Disconnected, RdpEndReason.ConnectionCancelled, "Connection cancelled", null);
                break;
            case RdpSessionState.Connecting or RdpSessionState.Connected or RdpSessionState.Reconnecting
                or RdpSessionState.AutoReconnecting:
                BeginTeardown(null);
                break;
            default:
                return false;
        }
        Publish();
        return true;
    }

    /// <summary>Reconnects, optionally with an updated endpoint. Waits for the current native connection to end first.</summary>
    public bool Reconnect(SessionIdentity? identity = null)
    {
        if (State != RdpSessionState.Disposed && _controlLost && !_replacementPending && !TryRestoreControl())
        {
            Publish();
            return true;
        }
        switch (State)
        {
            case RdpSessionState.Disposed:
                return false;
            case RdpSessionState.Created:
                BeginAttempt(identity);
                break;
            case RdpSessionState.Connecting when _connectDeferred:
                _connectDeferred = false;
                BeginAttempt(identity);
                break;
            case RdpSessionState.Reconnecting:
                _pendingIdentity = identity ?? _pendingIdentity;
                break;
            case RdpSessionState.Disconnected or RdpSessionState.Failed when !_replacementPending && IsNativeIdle():
                BeginAttempt(identity);
                break;
            default:
                BeginTeardown(identity ?? Identity);
                break;
        }
        Publish();
        return true;
    }

    /// <summary>Updates the status line while connected (for example after a display change).</summary>
    public void UpdateConnectedMessage(string message)
    {
        if (State != RdpSessionState.Connected)
        {
            return;
        }
        _message = message;
        Publish();
    }

    public void Poll()
    {
        if (State is RdpSessionState.Disposed or RdpSessionState.Created)
        {
            return;
        }
        if (ReplacementDue)
        {
            ReplaceNativeControl();
            Publish();
            return;
        }
        if (_replacementPending)
        {
            // Waiting for the old control's sign-in window to close before releasing it; nothing it reports (its
            // Connected property reads 1 throughout its reconnect loop) may change the session meanwhile.
            Publish();
            return;
        }
        if (!_client.IsReady)
        {
            return;
        }

        var now = _clock();
        int native;
        try
        {
            native = _client.ReadConnectionState();
        }
        catch (RdpClientException exception)
        {
            _log("Could not read the Remote Desktop connection state.", exception);
            if (State is not (RdpSessionState.Disconnected or RdpSessionState.Failed))
            {
                Finish(RdpSessionState.Failed, RdpEndReason.ControlError, "Remote Desktop stopped responding",
                    "The Remote Desktop control is no longer available. Close the tab and connect again.");
            }
            _lastNativeState = NativeNotConnected;
            _nativeActive = false;
            Publish();
            return;
        }

        _lastNativeState = native;
        if (native == NativeNotConnected)
        {
            if (_nativeIdleSince < 0)
            {
                _nativeIdleSince = now;
            }
        }
        else
        {
            _nativeIdleSince = -1;
        }
        var idleFor = _nativeIdleSince < 0 ? 0 : now - _nativeIdleSince;
        var eventGraceOver = !NativeEventsAvailable || !_nativeActive || idleFor >= EventGraceMs;

        if (NativeDialogOpen)
        {
            // The control is waiting for the user in its own dialog and completes a requested disconnect once the dialog
            // closes, so nothing times out or is re-requested meanwhile. Timeouts restart when the dialog closes.
            if (State == RdpSessionState.Connecting && native == NativeConnected)
            {
                MarkConnected();
            }
            Publish();
            return;
        }

        switch (State)
        {
            case RdpSessionState.Connecting when _connectDeferred:
                break;
            case RdpSessionState.Connecting:
                if (native == NativeConnected)
                {
                    MarkConnected();
                }
                else if (native == NativeNotConnected && idleFor >= StartTimeoutMs)
                {
                    FailAttempt();
                }
                else if (native == NativeConnecting && !NativeEventsAvailable &&
                         now - _phaseStartedAt >= ConnectingTimeoutMs)
                {
                    FailAttempt();
                    RequestNativeDisconnect(now);
                }
                break;
            case RdpSessionState.Connected:
                if (native == NativeNotConnected && eventGraceOver)
                {
                    _nativeActive = false;
                    Finish(RdpSessionState.Disconnected, RdpEndReason.RemoteEnded, "The remote session ended", null);
                }
                break;
            case RdpSessionState.AutoReconnecting:
                // The control owns the retries and its events end this state; the connection property is not evidence
                // either way while it reconnects (live it stays 1 throughout). Only if the control goes silent does the
                // safety net decide, and it fails closed: a stuck control must not be shown as a working session.
                if (now - _phaseStartedAt >= AutoReconnectSilenceTimeoutMs)
                {
                    _log($"Session {Identity.SessionId}: no auto-reconnect progress for {AutoReconnectSilenceTimeoutMs / 1000} s.", null);
                    Finish(RdpSessionState.Failed, RdpEndReason.ConnectionLost, "Connection lost",
                        "Remote Desktop stopped reporting progress while reconnecting. Try again to start a new connection.",
                        new RdpFailureInfo(RdpFailureCategory.NetworkInterrupted));
                    // Whatever the control is doing is no longer trusted to be a reconnect: replace it (below).
                    RequestHardStop("it stopped reporting reconnect progress");
                }
                break;
            case RdpSessionState.Disconnecting or RdpSessionState.Reconnecting:
                if (NeedsHardStop)
                {
                    // The control kept reconnecting (or started to) after the session was ended.
                    RequestHardStop("it is reconnecting a session that was ended");
                }
                else if (native == NativeNotConnected && eventGraceOver)
                {
                    CompleteTeardown();
                }
                else if (native != NativeNotConnected && now - _phaseStartedAt >= DisconnectTimeoutMs)
                {
                    Finish(RdpSessionState.Failed, RdpEndReason.ControlError,
                        "The remote session didn't respond to the disconnect request",
                        "Close the tab to release the session.");
                }
                else if (native != NativeNotConnected)
                {
                    RequestNativeDisconnect(now);
                }
                break;
            case RdpSessionState.Disconnected or RdpSessionState.Failed:
                if (NeedsHardStop)
                {
                    RequestHardStop("it is reconnecting a session that has ended");
                }
                else if (native != NativeNotConnected)
                {
                    // The native control is still active although the session has ended; release it.
                    RequestNativeDisconnect(now);
                }
                else if (eventGraceOver)
                {
                    _nativeActive = false;
                }
                break;
        }
        if (ReplacementDue)
        {
            ReplaceNativeControl();
        }
        Publish();
    }

    public void OnNativeConnecting()
    {
        if (IgnoringNativeEvents)
        {
            return;
        }
        _awaitingNativeStart = false;
        _nativeActive = true;
    }

    // Tracked even for a control that is being replaced: releasing it while its sign-in window is open blocks inside
    // the control, so the replacement waits for the window to close.
    public void OnNativeDialogShown()
    {
        if (State == RdpSessionState.Disposed)
        {
            return;
        }
        NativeDialogOpen = true;
        if (_replacementPending)
        {
            Publish();
            return;
        }
        _signInPromptShownThisAttempt = true;
        _nativeActive = true;
        Publish();
    }

    public void OnNativeDialogClosed()
    {
        if (State == RdpSessionState.Disposed || !NativeDialogOpen)
        {
            return;
        }
        NativeDialogOpen = false;
        // The user may have kept the dialog open for a long time; timeouts count from now.
        ResetPhase();
        Publish();
    }

    public void OnNativeConnected()
    {
        if (IgnoringNativeEvents)
        {
            return;
        }
        _awaitingNativeStart = false;
        _nativeActive = true;
        switch (State)
        {
            case RdpSessionState.Connecting or RdpSessionState.AutoReconnecting:
                MarkConnected();
                break;
            case RdpSessionState.Disconnecting or RdpSessionState.Reconnecting
                or RdpSessionState.Disconnected or RdpSessionState.Failed:
                // Connected after a cancel or after the session ended: tear the stray connection down.
                RequestNativeDisconnect(_clock(), force: true);
                break;
        }
        Publish();
    }

    /// <summary>
    /// IMsTscAxEvents::OnAutoReconnecting, raised before each automatic attempt. Returns false to ask the control to
    /// stop. The control may keep reconnecting only while the session is still wanted. Live (mstscax 10.0.26100.8875)
    /// the control ignored the "stop" answer, so RDCM doesn't depend on it: an unwanted reconnection is ended by
    /// replacing the control.
    /// </summary>
    public bool OnNativeAutoReconnectDecision(int attempt)
    {
        if (IgnoringNativeEvents)
        {
            return false;
        }
        NoteNativeAutoReconnectAttempt();
        if (State is RdpSessionState.Connected or RdpSessionState.AutoReconnecting)
        {
            return true;
        }
        _log($"Session {Identity.SessionId}: asked Remote Desktop to stop reconnecting at attempt {attempt}.", null);
        if (!NativeDialogOpen && State != RdpSessionState.Created)
        {
            RequestHardStop("it is reconnecting a session that was ended");
        }
        return false;
    }

    private void NoteNativeAutoReconnectAttempt()
    {
        _nativeActive = true;
        _nativeAutoReconnecting = true;
    }

    /// <summary>
    /// The network dropped and the control is restoring the same session (OnAutoReconnecting2). Raised for each
    /// attempt. Shown as progress only while the session is wanted; otherwise the control is replaced.
    /// </summary>
    public void OnNativeAutoReconnecting(int reason, bool networkAvailable, int attempt, int maxAttempts)
    {
        if (IgnoringNativeEvents)
        {
            return;
        }
        if (State is not (RdpSessionState.Connected or RdpSessionState.AutoReconnecting))
        {
            // Includes Connecting: a reconnect loop still running from an earlier session would ignore the new attempt.
            if (State is RdpSessionState.Connecting or RdpSessionState.Disconnecting or RdpSessionState.Reconnecting
                or RdpSessionState.Disconnected or RdpSessionState.Failed)
            {
                NoteNativeAutoReconnectAttempt();
                if (!NativeDialogOpen)
                {
                    RequestHardStop("it is reconnecting a session that was ended");
                }
            }
            return;
        }
        _nativeActive = true;
        _nativeAutoReconnecting = true;
        _model.TransitionTo(RdpSessionState.AutoReconnecting);
        _autoReconnect = AutoReconnectProgress.From(reason, networkAvailable, attempt, maxAttempts);
        SetStatus(RdpEndReason.None, $"Connection interrupted · reconnecting ({_autoReconnect.Describe()})", null);
        // Each attempt restarts the safety-net timer.
        ResetPhase();
        Publish();
    }

    /// <summary>The control restored the session (OnAutoReconnected).</summary>
    public void OnNativeAutoReconnected()
    {
        if (IgnoringNativeEvents)
        {
            return;
        }
        _nativeAutoReconnecting = false;
        switch (State)
        {
            case RdpSessionState.AutoReconnecting:
                MarkConnected();
                Publish();
                break;
            case RdpSessionState.Disconnecting or RdpSessionState.Reconnecting
                or RdpSessionState.Disconnected or RdpSessionState.Failed:
                // An attempt already under way when the session was ended has succeeded; the user's choice wins.
                _log($"Session {Identity.SessionId}: Remote Desktop restored a session that was ended.", null);
                _nativeActive = true;
                RequestHardStop("it restored a session that was ended");
                break;
        }
    }

    public void OnNativeDisconnected(int reason)
    {
        if (State == RdpSessionState.Disposed)
        {
            return;
        }
        if (_replacementPending)
        {
            // The control being replaced has ended its connection; it can't still own a sign-in window.
            NativeDialogOpen = false;
            return;
        }
        if (_awaitingNativeStart)
        {
            // While waiting for OnConnecting, a disconnect event belongs to the previous native connection.
            return;
        }

        _nativeActive = false;
        _nativeAutoReconnecting = false;
        // The attempt that owned the sign-in dialog has ended. Live, the dialog-closed event always came first; this
        // also recovers if it is ever lost, so a tab can't be left waiting forever.
        NativeDialogOpen = false;
        switch (State)
        {
            case RdpSessionState.Connecting when reason == LocalDisconnect
                || (reason == SignInPromptCancelled && _signInPromptShownThisAttempt):
                // The user dismissed the Windows Security sign-in window (or RDCM cancelled while it was open).
                Finish(RdpSessionState.Disconnected, RdpEndReason.ConnectionCancelled, "Connection cancelled", null);
                break;
            case RdpSessionState.Connecting:
                FailWithReason(RdpEndReason.ConnectFailed, "Couldn't connect", reason);
                break;
            case RdpSessionState.Connected or RdpSessionState.AutoReconnecting when reason == LocalDisconnect:
                Finish(RdpSessionState.Disconnected, RdpEndReason.UserDisconnected, "Disconnected", null);
                break;
            case RdpSessionState.Connected or RdpSessionState.AutoReconnecting
                when reason is RemoteDisconnectByUser or RemoteDisconnectByServer:
                Finish(RdpSessionState.Disconnected, RdpEndReason.RemoteEnded, "The remote session ended",
                    DescribeRemoteEnd(reason));
                break;
            case RdpSessionState.Connected or RdpSessionState.AutoReconnecting:
                // From AutoReconnecting this is how the control reports that it gave up.
                FailWithReason(RdpEndReason.ConnectionLost, "Connection lost", reason);
                break;
            case RdpSessionState.Disconnecting:
                CompleteTeardown();
                break;
        }
        // Reconnecting continues in Poll so the next Connect is not issued from inside a native callback.
        Publish();
    }

    public void OnNativeFatalError(int errorCode)
    {
        if (IgnoringNativeEvents || State is RdpSessionState.Disconnected or RdpSessionState.Failed)
        {
            return;
        }
        _log($"Remote Desktop reported fatal error {errorCode}.", null);
        _nativeActive = false;
        Finish(RdpSessionState.Failed, RdpEndReason.ControlError, "Remote Desktop stopped unexpectedly",
            $"The Remote Desktop control reported an internal error (code {errorCode}). Close the tab and connect again.",
            new RdpFailureInfo(RdpFailureCategory.ClientError, ErrorCode: errorCode));
        Publish();
    }

    /// <summary>Ends the session for good. Later native events are ignored.</summary>
    public void Shutdown()
    {
        if (State == RdpSessionState.Disposed)
        {
            return;
        }
        _pendingIdentity = null;
        _replacementPending = false;
        try
        {
            if (_client.IsReady && _client.ReadConnectionState() != NativeNotConnected)
            {
                _client.RequestDisconnect();
            }
        }
        catch (RdpClientException exception)
        {
            _log("Disconnect during shutdown failed.", exception);
        }
        _model.TransitionTo(RdpSessionState.Disposed);
    }

    private void BeginAttempt(SessionIdentity? identity)
    {
        if (State is RdpSessionState.Reconnecting or RdpSessionState.Connecting)
        {
            _model.TransitionTo(RdpSessionState.Disconnected);
        }
        if (identity is not null)
        {
            _model.ReplaceIdentityWhenInactive(identity);
        }
        _model.TransitionTo(RdpSessionState.Connecting);
        _pendingIdentity = null;
        _connectedThisAttempt = false;
        SetStatus(RdpEndReason.None, $"Connecting to {Identity.Address}…", null);
        ResetPhase();
        // The previous attempt's server authentication no longer applies.
        _serverAuthentication = null;
        _autoReconnect = null;
        _signInPromptShownThisAttempt = false;
        _nativeAutoReconnecting = false;
        if (!_client.IsReady)
        {
            _connectDeferred = true;
            return;
        }
        InvokeNativeConnect();
    }

    private void InvokeNativeConnect()
    {
        _awaitingNativeStart = NativeEventsAvailable;
        try
        {
            _serverAuthentication = _client.BeginConnect(Identity);
            if (_serverAuthentication.IsWeakened)
            {
                _log($"Session {Identity.SessionId}: server authentication requested {_serverAuthentication.Requested}, " +
                     $"effective {_serverAuthentication.Effective?.ToString() ?? "unknown"} ({_serverAuthentication.Source}).", null);
            }
        }
        catch (RdpClientException exception)
        {
            _log($"Session {Identity.SessionId} could not start connecting.", exception);
            _awaitingNativeStart = false;
            if (State == RdpSessionState.Connecting)
            {
                Finish(RdpSessionState.Failed, RdpEndReason.ConnectFailed, "Couldn't start the connection", exception.Message,
                    new RdpFailureInfo(RdpFailureCategory.ClientError, ErrorCode: (exception.InnerException ?? exception).HResult));
            }
        }
    }

    private void BeginTeardown(SessionIdentity? reconnectIdentity)
    {
        var userRequested = reconnectIdentity is null;
        var wasAutoReconnecting = State == RdpSessionState.AutoReconnecting || _nativeAutoReconnecting;
        if (State is RdpSessionState.Connected or RdpSessionState.AutoReconnecting)
        {
            _connectedThisAttempt = true;
        }
        _pendingIdentity = reconnectIdentity;
        var target = userRequested ? RdpSessionState.Disconnecting : RdpSessionState.Reconnecting;
        if (State != target)
        {
            _model.TransitionTo(target);
        }
        SetStatus(RdpEndReason.None,
            !userRequested ? "Reconnecting…" : _connectedThisAttempt ? "Disconnecting…" : "Cancelling connection…",
            null);
        ResetPhase();

        if (_replacementPending)
        {
            // A replacement is already under way; it completes this teardown too.
            return;
        }
        if (NeedsHardStop)
        {
            RequestHardStop(userRequested ? "disconnect requested while it was reconnecting" : "reconnect requested while it was reconnecting");
            return;
        }
        // While auto-reconnecting the control is busy even if it reports "not connected", so it must be told to stop.
        if (!wasAutoReconnecting && IsNativeIdle())
        {
            // Nothing is running natively (for example a failed attempt not yet reported): never call Disconnect on an idle control.
            if (!(NativeEventsAvailable && _nativeActive))
            {
                CompleteTeardown();
            }
            return;
        }
        // The first request of a new teardown always goes out; Poll throttles the retries.
        RequestNativeDisconnect(_clock(), force: true);
    }

    private void CompleteTeardown()
    {
        _nativeActive = false;
        if (State == RdpSessionState.Reconnecting)
        {
            BeginAttempt(_pendingIdentity);
            return;
        }
        if (State != RdpSessionState.Disconnecting)
        {
            return;
        }
        var reason = _connectedThisAttempt ? RdpEndReason.UserDisconnected : RdpEndReason.ConnectionCancelled;
        Finish(RdpSessionState.Disconnected, reason,
            reason == RdpEndReason.UserDisconnected ? "Disconnected" : "Connection cancelled", null);
    }

    /// <summary>
    /// The control is doing something the session no longer wants (reconnecting, or a session it restored) and ignores
    /// Disconnect() while it reconnects. Not while its sign-in window is open: releasing it then blocks inside the control.
    /// </summary>
    private bool NeedsHardStop => _nativeAutoReconnecting && !NativeDialogOpen;

    /// <summary>
    /// Accepts that the current control must be replaced. From now on nothing it raises can change the session; the
    /// replacement itself runs from <see cref="Poll"/>, outside native callbacks.
    /// </summary>
    private void RequestHardStop(string why)
    {
        if (_replacementPending)
        {
            return;
        }
        _replacementPending = true;
        _log($"Session {Identity.SessionId}: replacing the Remote Desktop control ({why}).", null);
    }

    private void ReplaceNativeControl()
    {
        _replacementPending = false;
        var replaced = TryReplaceControl();
        // Whatever the released control was doing went with it.
        _nativeActive = false;
        _nativeAutoReconnecting = false;
        NativeDialogOpen = false;
        _awaitingNativeStart = false;
        _lastNativeState = NativeNotConnected;
        _nativeIdleSince = -1;
        _controlLost = !replaced;
        if (!replaced)
        {
            FailControlUnavailable();
            return;
        }
        if (State == RdpSessionState.Connecting)
        {
            // The connect went to the released control: start the attempt again on the fresh one (deferred until the
            // host reports it started).
            BeginAttempt(null);
            return;
        }
        // Disconnecting ends as Disconnected; Reconnecting starts the new attempt on the fresh control (deferred until
        // the host reports it started). A session that had already ended keeps the result it reported.
        CompleteTeardown();
    }

    /// <summary>One attempt to create a control after a replacement left none; used for an explicit Reconnect.</summary>
    private bool TryRestoreControl()
    {
        _controlLost = !TryReplaceControl();
        if (_controlLost)
        {
            FailControlUnavailable();
        }
        return !_controlLost;
    }

    private bool TryReplaceControl()
    {
        try
        {
            if (_client.ReplaceControl())
            {
                _log($"Session {Identity.SessionId}: Remote Desktop control replaced.", null);
                return true;
            }
            _log($"Session {Identity.SessionId}: no Remote Desktop control could be created after the replacement.", null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log($"Session {Identity.SessionId}: replacing the Remote Desktop control failed.", exception);
        }
        return false;
    }

    private void FailControlUnavailable() =>
        Finish(RdpSessionState.Failed, RdpEndReason.ControlError, "Remote Desktop couldn't be restarted in this tab",
            "The previous connection was ended. Close the tab, then connect again.",
            new RdpFailureInfo(RdpFailureCategory.ClientError));

    private void MarkConnected()
    {
        _model.TransitionTo(RdpSessionState.Connected);
        _connectedThisAttempt = true;
        _nativeActive = true;
        _autoReconnect = null;
        SetStatus(RdpEndReason.None, "Connected", null);
        ResetPhase();
    }

    private void FailAttempt() =>
        Finish(RdpSessionState.Failed, RdpEndReason.ConnectFailed, "Connection not established",
            "Check the host name, network connection, and credentials, then try again.");

    private void FailWithReason(RdpEndReason endReason, string summary, int reason)
    {
        var info = _client.DescribeDisconnect(reason);
        // Diagnostics identify the session, never the server or user.
        _log($"Session {Identity.SessionId} ended with reason {reason} (extended {info.ExtendedReason}).", null);
        var description = string.IsNullOrWhiteSpace(info.Description) || info.Description.Contains("internal error", StringComparison.OrdinalIgnoreCase)
            ? $"Remote Desktop reported error code {reason}."
            : info.Description.Trim();
        var firstSentence = FirstSentence(description);
        var failure = new RdpFailureInfo(
            RdpFailureClassifier.Classify(reason, info.ExtendedReason), reason, info.ExtendedReason);
        if (State == RdpSessionState.AutoReconnecting && _autoReconnect is { Attempt: { } attempts })
        {
            description += $" Remote Desktop tried to reconnect {attempts} time{(attempts == 1 ? "" : "s")}.";
        }
        Finish(RdpSessionState.Failed, endReason, $"{summary}: {firstSentence}", description, failure);
    }

    private static string FirstSentence(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        var end = line.IndexOf(". ", StringComparison.Ordinal);
        return (end > 0 ? line[..(end + 1)] : line).TrimEnd(':');
    }

    private string? DescribeRemoteEnd(int reason) => _client.DescribeDisconnect(reason).ExtendedReason switch
    {
        1 => "The session was disconnected by another program or an administrator.",
        2 => "You were signed out by another program or an administrator.",
        3 => "The session was disconnected because it was idle.",
        4 => "The session ended because signing in took too long.",
        5 => "Another connection to this session replaced this one.",
        11 => "The session was disconnected from the remote computer.",
        12 => "You signed out of the remote session.",
        _ => null
    };

    private void Finish(RdpSessionState state, RdpEndReason reason, string message, string? detail,
        RdpFailureInfo? failure = null)
    {
        if (State == RdpSessionState.Created)
        {
            // Created can only leave via Connecting or Disconnecting.
            _model.TransitionTo(state == RdpSessionState.Failed ? RdpSessionState.Connecting : RdpSessionState.Disconnecting);
        }
        _model.TransitionTo(state);
        _pendingIdentity = null;
        _connectDeferred = false;
        _awaitingNativeStart = false;
        SetStatus(reason, message, detail, failure);
        ResetPhase();
    }

    private bool IsNativeIdle()
    {
        if (!_client.IsReady)
        {
            return !_nativeActive;
        }
        try
        {
            _lastNativeState = _client.ReadConnectionState();
        }
        catch (RdpClientException exception)
        {
            _log("Could not read the Remote Desktop connection state.", exception);
            return false;
        }
        return _lastNativeState == NativeNotConnected;
    }

    private void RequestNativeDisconnect(long now, bool force = false)
    {
        if (!force && now - _lastDisconnectRequestAt < DisconnectRetryMs)
        {
            return;
        }
        _lastDisconnectRequestAt = now;
        try
        {
            _client.RequestDisconnect();
        }
        catch (RdpClientException exception)
        {
            // Expected while the control is between phases; the watchdog retries until it is idle.
            _log("Remote Desktop rejected a disconnect request; it will be retried.", exception);
        }
    }

    private void ResetPhase()
    {
        _phaseStartedAt = _clock();
        _nativeIdleSince = -1;
    }

    private void SetStatus(RdpEndReason reason, string message, string? detail, RdpFailureInfo? failure = null)
    {
        _endReason = reason;
        _message = message;
        _detail = detail;
        _failure = failure;
    }

    private void Publish()
    {
        var current = _published;
        if (current.State == State && current.EndReason == _endReason && current.Message == _message &&
            current.Detail == _detail && current.Identity == Identity && current.Failure == _failure &&
            current.NativeDialogOpen == NativeDialogOpen && current.ServerAuthentication == _serverAuthentication &&
            current.AutoReconnect == PublishedAutoReconnect)
        {
            return;
        }
        _published = CreateSnapshot(++_sequence);
        Changed?.Invoke(_published);
    }

    private AutoReconnectProgress? PublishedAutoReconnect =>
        State == RdpSessionState.AutoReconnecting ? _autoReconnect : null;

    private RdpSessionSnapshot CreateSnapshot(long sequence) =>
        new(sequence, State, _endReason, _message, _detail, Identity, _failure, NativeDialogOpen, _serverAuthentication,
            PublishedAutoReconnect);
}
