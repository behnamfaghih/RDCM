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
    [DispId(18)] void OnAuthenticationWarningDisplayed();
    [DispId(19)] void OnAuthenticationWarningDismissed();
}

/// <summary>Forwards native events to the owning control. Exceptions never propagate back into COM.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class RdpClientEventSink(Action<Action<RdpConnectionController>> dispatch) : IMsTscAxEvents
{
    public void OnConnecting() => dispatch(controller => controller.OnNativeConnecting());
    public void OnConnected() => dispatch(controller => controller.OnNativeConnected());
    public void OnLoginComplete() { }
    public void OnDisconnected(int discReason) => dispatch(controller => controller.OnNativeDisconnected(discReason));
    public void OnFatalError(int errorCode) => dispatch(controller => controller.OnNativeFatalError(errorCode));

    // Raised around the control's own modal dialogs. With this control version they bracket the Windows Security
    // credential prompt, which runs outside RDCM while the control waits for it.
    public void OnAuthenticationWarningDisplayed() => dispatch(controller => controller.OnNativeDialogShown());
    public void OnAuthenticationWarningDismissed() => dispatch(controller => controller.OnNativeDialogClosed());
}
