using System.Runtime.InteropServices;

namespace RDCM;

/// <summary>
/// IMsRdpExtendedSettings (mstscax.dll): named connection properties. The methods are declared in type library order
/// (put_Property, then get_Property), which is what binds them; their names here don't matter.
/// </summary>
[ComImport]
[Guid("302D8188-0052-4807-806A-362B628F9AC5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsRdpExtendedSettings
{
    void SetProperty([MarshalAs(UnmanagedType.BStr)] string name, [In] ref object value);

    object GetProperty([MarshalAs(UnmanagedType.BStr)] string name);
}

/// <summary>Extended properties of the Remote Desktop control that RDCM sets.</summary>
public static class RdpExtendedSettings
{
    /// <summary>
    /// Sets the remote desktop scaling and the device scale factor (both VT_UI4). They can't be changed this way once a
    /// connection has started, so they are set before Connect; a connected session changes them by display updates.
    /// </summary>
    /// <exception cref="InvalidCastException">The control doesn't implement IMsRdpExtendedSettings.</exception>
    /// <exception cref="COMException">The control rejected a value.</exception>
    /// <exception cref="ArgumentException">The control rejected a value (E_INVALIDARG).</exception>
    public static void SetScaleFactors(object control, uint desktopScaleFactor, uint deviceScaleFactor)
    {
        var settings = (IMsRdpExtendedSettings)control;
        object desktop = desktopScaleFactor;
        object device = deviceScaleFactor;
        settings.SetProperty("DesktopScaleFactor", ref desktop);
        settings.SetProperty("DeviceScaleFactor", ref device);
    }

    public static uint GetUInt32(object control, string name) =>
        Convert.ToUInt32(((IMsRdpExtendedSettings)control).GetProperty(name), System.Globalization.CultureInfo.InvariantCulture);
}
