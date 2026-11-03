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
/// <paramref name="AutoReconnectEnding"/> is true while a disconnect or reconnect waits for the control's own
/// reconnect attempts to end (closing the tab ends them at once).
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
    AutoReconnectProgress? AutoReconnect = null,
    bool AutoReconnectEnding = false);

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

    // While the control auto-reconnects it ignores Disconnect() (observed live), and the documented "stop" answer to its
    // OnAutoReconnecting event was ignored too. It ends when its attempts run out or it restores the session (which is
    // then disconnected); disposing it (closing the tab) ends it at once.
    // _nativeAutoReconnecting: the control is in its reconnect loop (set by its attempt events in any state, cleared
    // when it reports a disconnect or a restore, or when a safety net stops trusting it).
    private bool _nativeAutoReconnecting;
    private bool _autoReconnectStopSupported;
    private bool _disconnectWhenRestored;

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

    public bool NeedsPolling => State is RdpSessionState.Connecting or RdpSessionState.Connected
                                    or RdpSessionState.Disconnecting or RdpSessionState.Reconnecting
                                    or RdpSessionState.AutoReconnecting
                                || (State is RdpSessionState.Disconnected or RdpSessionState.Failed
                                    && (_lastNativeState != NativeNotConnected || _nativeActive));

    /// <summary>
    /// A reconnect is ready to start, or a session restored after the user ended it must be disconnected; the host
    /// should call <see cref="Poll"/> soon (outside native callbacks).
    /// </summary>
    public bool HasPendingWork => (State == RdpSessionState.Reconnecting && !_nativeActive) || _disconnectWhenRestored;

    /// <summary>
    /// The control is reconnecting and is stopped through its next attempt event (Disconnect() doesn't stop it).
    /// Its attempt events restart the phase timer, so "silent for the safety-net time" means it stopped reporting.
    /// </summary>
    private bool AwaitingAutoReconnectStop => _nativeAutoReconnecting && _autoReconnectStopSupported;

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
            case RdpSessionState.Disconnected or RdpSessionState.Failed when IsNativeIdle():
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
        if (State is RdpSessionState.Disposed or RdpSessionState.Created || !_client.IsReady)
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
        if (_disconnectWhenRestored)
        {
            // The control restored a session the user had already ended; end it now that we're outside its callback.
            _disconnectWhenRestored = false;
            if (native != NativeNotConnected)
            {
                RequestNativeDisconnect(now, force: true);
            }
        }
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
                    // Whatever the control is doing is no longer trusted to be a reconnect: release it actively. A later
                    // OnAutoReconnecting is answered "stop", and a late restore is disconnected.
                    _nativeAutoReconnecting = false;
                    if (native != NativeNotConnected)
                    {
                        RequestNativeDisconnect(now, force: true);
                    }
                }
                break;
            case RdpSessionState.Disconnecting or RdpSessionState.Reconnecting:
                if (native == NativeNotConnected && eventGraceOver)
                {
                    CompleteTeardown();
                }
                else if (AwaitingAutoReconnectStop)
                {
                    // Calling Disconnect() now is ignored; each attempt of the control is answered with "stop", and
                    // the control ends with a disconnect (or a restore, which is then disconnected).
                    if (now - _phaseStartedAt >= AutoReconnectSilenceTimeoutMs)
                    {
                        _log($"Session {Identity.SessionId}: Remote Desktop stopped reporting reconnect attempts before it could be stopped.", null);
                        Finish(RdpSessionState.Failed, RdpEndReason.ControlError,
                            "Remote Desktop didn't stop reconnecting",
                            "Close the tab to release the session.");
                        // No longer trusted to be reconnecting: release it with Disconnect() from now on.
                        _nativeAutoReconnecting = false;
                    }
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
                if (native != NativeNotConnected)
                {
                    // The native control is still active although the session has ended; release it. While it
                    // reconnects, its attempts are answered with "stop" instead, unless it has gone silent.
                    if (AwaitingAutoReconnectStop && now - _phaseStartedAt >= AutoReconnectSilenceTimeoutMs)
                    {
                        _nativeAutoReconnecting = false;
                    }
                    if (!AwaitingAutoReconnectStop)
                    {
                        RequestNativeDisconnect(now);
                    }
                }
                else if (eventGraceOver)
                {
                    _nativeActive = false;
                }
                break;
        }
        Publish();
    }

    public void OnNativeConnecting()
    {
        if (State == RdpSessionState.Disposed)
        {
            return;
        }
        _awaitingNativeStart = false;
        _nativeActive = true;
    }

    public void OnNativeDialogShown()
    {
        if (State == RdpSessionState.Disposed)
        {
            return;
        }
        NativeDialogOpen = true;
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
        if (State == RdpSessionState.Disposed)
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
    /// stop: per the documented contract it then ends the attempt and reports a disconnect. The control may keep
    /// reconnecting only while the session is still wanted. Live (mstscax 10.0.26100.8875) the answer reached the
    /// control but it kept reconnecting until its attempts ran out, so RDCM never depends on it: the session stays
    /// "disconnecting" until the control reports a disconnect, and a restore is disconnected at once.
    /// </summary>
    public bool OnNativeAutoReconnectDecision(int attempt)
    {
        if (State == RdpSessionState.Disposed)
        {
            return false;
        }
        _autoReconnectStopSupported = true;
        NoteNativeAutoReconnectAttempt();
        if (State is RdpSessionState.Connected or RdpSessionState.AutoReconnecting)
        {
            return true;
        }
        // The control may not honour it at once (it then keeps reporting attempts, and is asked again each time).
        _log($"Session {Identity.SessionId}: asked Remote Desktop to stop reconnecting at attempt {attempt}.", null);
        return false;
    }

    /// <summary>The control is in its reconnect loop. Outside a wanted session, each attempt restarts the safety-net timer.</summary>
    private void NoteNativeAutoReconnectAttempt()
    {
        _nativeActive = true;
        _nativeAutoReconnecting = true;
        if (State is RdpSessionState.Disconnecting or RdpSessionState.Reconnecting
            or RdpSessionState.Disconnected or RdpSessionState.Failed)
        {
            ResetPhase();
        }
    }

    /// <summary>
    /// The network dropped and the control is restoring the same session (OnAutoReconnecting2). Raised for each
    /// attempt. Shown as progress only while the session is wanted; otherwise the control is being stopped.
    /// </summary>
    public void OnNativeAutoReconnecting(int reason, bool networkAvailable, int attempt, int maxAttempts)
    {
        if (State is not (RdpSessionState.Connected or RdpSessionState.AutoReconnecting))
        {
            if (State is RdpSessionState.Disconnecting or RdpSessionState.Reconnecting
                or RdpSessionState.Disconnected or RdpSessionState.Failed)
            {
                NoteNativeAutoReconnectAttempt();
                if (State is RdpSessionState.Disconnected or RdpSessionState.Failed && !_autoReconnectStopSupported)
                {
                    // Without the stop answer (control versions that don't raise OnAutoReconnecting) this is all there is.
                    RequestNativeDisconnect(_clock(), force: true);
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
        if (State == RdpSessionState.Disposed)
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
                // Live, Disconnect() from inside this callback had no effect, so it is requested from Poll.
                _log($"Session {Identity.SessionId}: Remote Desktop restored a session that was ended; disconnecting it.", null);
                _nativeActive = true;
                _disconnectWhenRestored = true;
                if (State is RdpSessionState.Disconnecting or RdpSessionState.Reconnecting)
                {
                    ResetPhase();
                }
                break;
        }
    }

    public void OnNativeDisconnected(int reason)
    {
        if (State == RdpSessionState.Disposed || _awaitingNativeStart)
        {
            // While waiting for OnConnecting, a disconnect event belongs to the previous native connection.
            return;
        }

        _nativeActive = false;
        _nativeAutoReconnecting = false;
        _disconnectWhenRestored = false;
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
        if (State is RdpSessionState.Disposed or RdpSessionState.Disconnected or RdpSessionState.Failed)
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
        _disconnectWhenRestored = false;
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
            AwaitingAutoReconnectStop
                ? "Remote Desktop is still trying to restore the connection and ends only after its current attempts, which can take a few minutes. Close the tab to end it now."
                : null);
        ResetPhase();

        if (AwaitingAutoReconnectStop)
        {
            // Disconnect() is ignored while the control reconnects. Each attempt is answered "stop" (the documented way,
            // although the control tested live ignored it too); a restore is disconnected at once, and disposing the
            // control (closing the tab) ends the attempts immediately.
            _log($"Session {Identity.SessionId}: waiting for Remote Desktop's reconnect attempts to end.", null);
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
            current.AutoReconnect == PublishedAutoReconnect && current.AutoReconnectEnding == PublishedAutoReconnectEnding)
        {
            return;
        }
        _published = CreateSnapshot(++_sequence);
        Changed?.Invoke(_published);
    }

    private AutoReconnectProgress? PublishedAutoReconnect =>
        State == RdpSessionState.AutoReconnecting ? _autoReconnect : null;

    private bool PublishedAutoReconnectEnding =>
        State is RdpSessionState.Disconnecting or RdpSessionState.Reconnecting && AwaitingAutoReconnectStop;

    private RdpSessionSnapshot CreateSnapshot(long sequence) =>
        new(sequence, State, _endReason, _message, _detail, Identity, _failure, NativeDialogOpen, _serverAuthentication,
            PublishedAutoReconnect, PublishedAutoReconnectEnding);
}
