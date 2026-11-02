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
    bool NativeDialogOpen = false);

public sealed record RdpDisconnectInfo(int ExtendedReason, string? Description);

/// <summary>Thin abstraction over the Remote Desktop ActiveX control so the lifecycle can be tested.</summary>
public interface IRdpNativeClient
{
    bool IsReady { get; }

    /// <summary>The control's Connected property: 0 = not connected, 1 = connected, 2 = connecting.</summary>
    int ReadConnectionState();

    void BeginConnect(SessionIdentity identity);
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

    public const long StartTimeoutMs = 15_000;
    public const long ConnectingTimeoutMs = 60_000;
    public const long EventGraceMs = 3_000;
    public const long DisconnectRetryMs = 2_000;
    public const long DisconnectTimeoutMs = 20_000;

    private readonly IRdpNativeClient _client;
    private readonly Func<long> _clock;
    private readonly Action<string, Exception?> _log;
    private readonly RdpSessionModel _model;
    private RdpEndReason _endReason;
    private string _message = "Preparing connection…";
    private string? _detail;
    private RdpFailureInfo? _failure;
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
                                || (State is RdpSessionState.Disconnected or RdpSessionState.Failed
                                    && (_lastNativeState != NativeNotConnected || _nativeActive));

    /// <summary>A reconnect is ready to start; the host should call <see cref="Poll"/> soon (outside native callbacks).</summary>
    public bool HasPendingWork => State == RdpSessionState.Reconnecting && !_nativeActive;

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
            case RdpSessionState.Connecting or RdpSessionState.Connected or RdpSessionState.Reconnecting:
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
            case RdpSessionState.Disconnecting or RdpSessionState.Reconnecting:
                if (native == NativeNotConnected && eventGraceOver)
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
                if (native != NativeNotConnected)
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
            case RdpSessionState.Connecting:
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

    public void OnNativeDisconnected(int reason)
    {
        if (State == RdpSessionState.Disposed || _awaitingNativeStart)
        {
            // While waiting for OnConnecting, a disconnect event belongs to the previous native connection.
            return;
        }

        _nativeActive = false;
        // The attempt that owned the sign-in dialog has ended. Live, the dialog-closed event always came first; this
        // also recovers if it is ever lost, so a tab can't be left waiting forever.
        NativeDialogOpen = false;
        switch (State)
        {
            case RdpSessionState.Connecting when reason == LocalDisconnect:
                // Typically the user dismissed the Remote Desktop credential prompt.
                Finish(RdpSessionState.Disconnected, RdpEndReason.ConnectionCancelled, "Connection cancelled", null);
                break;
            case RdpSessionState.Connecting:
                FailWithReason(RdpEndReason.ConnectFailed, "Couldn't connect", reason);
                break;
            case RdpSessionState.Connected when reason == LocalDisconnect:
                Finish(RdpSessionState.Disconnected, RdpEndReason.UserDisconnected, "Disconnected", null);
                break;
            case RdpSessionState.Connected when reason is RemoteDisconnectByUser or RemoteDisconnectByServer:
                Finish(RdpSessionState.Disconnected, RdpEndReason.RemoteEnded, "The remote session ended",
                    DescribeRemoteEnd(reason));
                break;
            case RdpSessionState.Connected:
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
            _client.BeginConnect(Identity);
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
        if (State == RdpSessionState.Connected)
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

        if (IsNativeIdle())
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
            current.NativeDialogOpen == NativeDialogOpen)
        {
            return;
        }
        _published = CreateSnapshot(++_sequence);
        Changed?.Invoke(_published);
    }

    private RdpSessionSnapshot CreateSnapshot(long sequence) =>
        new(sequence, State, _endReason, _message, _detail, Identity, _failure, NativeDialogOpen);
}
