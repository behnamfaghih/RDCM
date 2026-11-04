namespace RDCM;

/// <summary>
/// One instance ("generation") of the native Remote Desktop control. A generation is never reused: once released it is
/// gone, and the session continues with a fresh one.
/// </summary>
public interface IRdpControlInstance : IDisposable
{
    int Generation { get; }

    /// <summary>Creates the native control and connects its events. Throws when it can't be created.</summary>
    void Initialize();

    /// <summary>Asks the control to disconnect if it is connected. Best effort: the control ignores it while it reconnects.</summary>
    void RequestDisconnectIfActive();

    /// <summary>Disconnects the control's event connection point, so nothing more is delivered from it.</summary>
    void DetachEvents();
}

/// <summary>
/// Holds the current native-control generation of one session. Callbacks carry the generation that raised them and are
/// delivered only while that generation is current, so a released control can't change the session. Releasing follows
/// the order that live testing showed ends the control's own reconnection at once (the same as closing a tab):
/// retire, request a disconnect, detach events, dispose. All members run on the UI thread that owns the controls.
/// </summary>
public sealed class RdpControlSlot<TInstance>(Func<int, TInstance> create, Action<string, Exception?> log)
    where TInstance : class, IRdpControlInstance
{
    private int _lastGeneration;

    public TInstance? Current { get; private set; }

    /// <summary>Callbacks dropped because their generation was no longer current (diagnostics and tests).</summary>
    public int StaleCallbacks { get; private set; }

    public bool IsCurrent(int generation) => Current is { } current && current.Generation == generation;

    /// <summary>Runs <paramref name="deliver"/> only if <paramref name="generation"/> is current.</summary>
    public bool Route(int generation, Action deliver)
    {
        if (!IsCurrent(generation))
        {
            StaleCallbacks++;
            return false;
        }
        deliver();
        return true;
    }

    /// <summary>Creates a generation when there is none. One attempt per call; a failed instance is released.</summary>
    public bool TryCreate()
    {
        if (Current is not null)
        {
            return true;
        }
        TInstance instance;
        try
        {
            instance = create(++_lastGeneration);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log($"Remote Desktop control generation {_lastGeneration} couldn't be created.", exception);
            return false;
        }
        // Current before Initialize: anything the control raises while it starts belongs to this generation.
        Current = instance;
        try
        {
            instance.Initialize();
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log($"Remote Desktop control generation {instance.Generation} couldn't be started.", exception);
            Release(requestDisconnect: false);
            return false;
        }
    }

    /// <summary>Releases the current generation and creates a fresh one. Returns false if no fresh one could be created.</summary>
    public bool Replace()
    {
        Release(requestDisconnect: true);
        return TryCreate();
    }

    /// <summary>
    /// Releases the current generation. It is retired first, so nothing it raises from then on is delivered; each later
    /// step runs even if an earlier one fails, and the instance is never offered again.
    /// </summary>
    public void Release(bool requestDisconnect)
    {
        if (Current is not { } instance)
        {
            return;
        }
        Current = null;
        if (requestDisconnect)
        {
            Attempt(instance.RequestDisconnectIfActive, instance.Generation, "requesting a disconnect");
        }
        Attempt(instance.DetachEvents, instance.Generation, "detaching events");
        Attempt(instance.Dispose, instance.Generation, "releasing");
    }

    private void Attempt(Action step, int generation, string description)
    {
        try
        {
            step();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log($"Remote Desktop control generation {generation}: {description} failed; the control isn't used again.", exception);
        }
    }
}
