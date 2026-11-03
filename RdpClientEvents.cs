using System.Runtime.InteropServices;

namespace RDCM;

/// <summary>
/// Subset of the Remote Desktop ActiveX event dispinterface (IMsTscAxEvents in mstscax.dll).
/// DISPIDs match the installed type library; unlisted events are ignored by the COM runtime.
/// </summary>
[ComImport]
[Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsTscAxEvents
{
    [DispId(1)] void OnConnecting();
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int discReason);
    [DispId(10)] void OnFatalError(int errorCode);
    // OnAutoReconnecting(disconnectReason, attemptCount, [out] pArcContinueStatus). Observed live (mstscax 10.0.26100):
    // the control passes these arguments in declaration order instead of the reversed order IDispatch uses, so the
    // [out] status binds to the first parameter and the reason to the last. Both ends are bound by reference; see the sink.
    [DispId(17)] void OnAutoReconnecting(ref int first, int attemptCount, ref int last);
    [DispId(18)] void OnAuthenticationWarningDisplayed();
    [DispId(19)] void OnAuthenticationWarningDismissed();
    [DispId(33)] void OnAutoReconnected();
    [DispId(34)] void OnAutoReconnecting2(int disconnectReason, bool networkAvailable, int attemptCount, int maxAttemptCount);
}

/// <summary>
/// Forwards native events to the owning control. Exceptions never propagate back into COM.
/// <paramref name="continueAutoReconnect"/> answers, for an attempt number, whether the control may keep reconnecting.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class RdpClientEventSink(Action<Action<RdpConnectionController>> dispatch, Func<int, bool> continueAutoReconnect)
    : IMsTscAxEvents
{
    // AutoReconnectContinueState.autoReconnectContinueStop
    public const int AutoReconnectContinueStop = 1;

    public void OnConnecting() => dispatch(controller => controller.OnNativeConnecting());
    public void OnConnected() => dispatch(controller => controller.OnNativeConnected());
    public void OnLoginComplete() { }
    public void OnDisconnected(int discReason) => dispatch(controller => controller.OnNativeDisconnected(discReason));
    public void OnFatalError(int errorCode) => dispatch(controller => controller.OnNativeFatalError(errorCode));

    // Raised around the control's own modal dialogs. With this control version they bracket the Windows Security
    // credential prompt, which runs outside RDCM while the control waits for it.
    public void OnAuthenticationWarningDisplayed() => dispatch(controller => controller.OnNativeDialogShown());
    public void OnAuthenticationWarningDismissed() => dispatch(controller => controller.OnNativeDialogClosed());

    // Native auto-reconnect (Windows 8+). OnAutoReconnecting2 reports progress. OnAutoReconnecting is asked before each
    // attempt; answering "stop" is the documented way to end auto-reconnect (Disconnect() is ignored while the control
    // reconnects). Live, this control version ignored the answer as well, so RDCM doesn't rely on it; it is still given.
    // The default ("continue automatically") is left untouched.
    // "Stop" is written to both possible positions of the [out] status: the CLR copies a value back only into an
    // argument passed by reference, so the by-value disconnect reason in the other position is never changed.
    public void OnAutoReconnecting(ref int first, int attemptCount, ref int last)
    {
        if (!continueAutoReconnect(attemptCount))
        {
            first = AutoReconnectContinueStop;
            last = AutoReconnectContinueStop;
        }
    }

    public void OnAutoReconnecting2(int disconnectReason, bool networkAvailable, int attemptCount, int maxAttemptCount) =>
        dispatch(controller => controller.OnNativeAutoReconnecting(disconnectReason, networkAvailable, attemptCount, maxAttemptCount));
    public void OnAutoReconnected() => dispatch(controller => controller.OnNativeAutoReconnected());
}
