namespace RDCM;

public enum RdpSessionState
{
    Created,
    Connecting,
    Connected,
    Disconnecting,
    Disconnected,
    Reconnecting,
    Failed,
    Disposed
}

public sealed class RdpSessionModel(SessionIdentity identity)
{
    private static readonly IReadOnlyDictionary<RdpSessionState, RdpSessionState[]> AllowedTransitions =
        new Dictionary<RdpSessionState, RdpSessionState[]>
        {
            [RdpSessionState.Created] = [RdpSessionState.Connecting, RdpSessionState.Disconnecting, RdpSessionState.Disposed],
            [RdpSessionState.Connecting] = [RdpSessionState.Connected, RdpSessionState.Disconnecting, RdpSessionState.Disconnected, RdpSessionState.Reconnecting, RdpSessionState.Failed, RdpSessionState.Disposed],
            [RdpSessionState.Connected] = [RdpSessionState.Disconnecting, RdpSessionState.Disconnected, RdpSessionState.Reconnecting, RdpSessionState.Failed, RdpSessionState.Disposed],
            [RdpSessionState.Disconnecting] = [RdpSessionState.Disconnected, RdpSessionState.Reconnecting, RdpSessionState.Failed, RdpSessionState.Disposed],
            [RdpSessionState.Disconnected] = [RdpSessionState.Connecting, RdpSessionState.Reconnecting, RdpSessionState.Disconnecting, RdpSessionState.Disposed],
            [RdpSessionState.Reconnecting] = [RdpSessionState.Disconnecting, RdpSessionState.Connecting, RdpSessionState.Failed, RdpSessionState.Disconnected, RdpSessionState.Disposed],
            [RdpSessionState.Failed] = [RdpSessionState.Connecting, RdpSessionState.Disconnecting, RdpSessionState.Reconnecting, RdpSessionState.Disconnected, RdpSessionState.Disposed],
            [RdpSessionState.Disposed] = []
        };

    public SessionIdentity Identity { get; private set; } = identity;
    public RdpSessionState State { get; private set; } = RdpSessionState.Created;

    public bool HasActiveSession => State is RdpSessionState.Connecting or RdpSessionState.Connected
        or RdpSessionState.Disconnecting or RdpSessionState.Reconnecting;

    public void TransitionTo(RdpSessionState state)
    {
        if (State == state)
        {
            return;
        }

        if (!AllowedTransitions[State].Contains(state))
        {
            throw new InvalidOperationException($"Invalid RDP session transition: {State} -> {state}.");
        }
        State = state;
    }

    public void ReplaceIdentityWhenInactive(SessionIdentity identity)
    {
        if (State is not (RdpSessionState.Created or RdpSessionState.Disconnected or RdpSessionState.Failed))
        {
            throw new InvalidOperationException("Session identity can only change while the remote session is inactive.");
        }
        Identity = identity with { SessionId = Identity.SessionId };
    }
}
