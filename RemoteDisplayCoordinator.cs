namespace RDCM;

/// <summary>Display values for a Remote Desktop session (IMsRdpClient9.UpdateSessionDisplaySettings).</summary>
/// <param name="DesktopWidth">Remote desktop width in pixels.</param>
/// <param name="DesktopHeight">Remote desktop height in pixels.</param>
/// <param name="PhysicalWidthMillimeters">Physical width of the area showing the session.</param>
/// <param name="PhysicalHeightMillimeters">Physical height of the area showing the session.</param>
/// <param name="DesktopScaleFactor">Remote desktop scaling in percent (the user's "DPI scale" choice).</param>
/// <param name="DeviceScaleFactor">Device scale factor in percent: 100, 140, or 180.</param>
public readonly record struct RemoteDisplaySettings(
    int DesktopWidth,
    int DesktopHeight,
    uint PhysicalWidthMillimeters,
    uint PhysicalHeightMillimeters,
    uint DesktopScaleFactor,
    uint DeviceScaleFactor);

/// <summary>How a session's resolution and display metadata follow its tab and monitor.</summary>
public static class RemoteDisplayPlan
{
    // The documented limits for the remote desktop size (IMsTscAx.DesktopWidth/DesktopHeight on Windows 8 and later,
    // and the RDP display update protocol). A larger minimum would make the desktop bigger than a small tab, which
    // the control then clips or scrolls instead of scaling.
    private const int MinimumDesktopSize = 200;
    private const int MaximumDesktopSize = 8192;
    private static readonly uint[] DeviceScaleFactors = [100, 140, 180];

    /// <summary>
    /// The remote desktop size. Fit to tab uses the session area's physical pixels, so the remote desktop is shown
    /// pixel for pixel at the DPI of the monitor the window is on. Rounded down to a multiple of 4 pixels: dynamic
    /// display updates need an even width.
    /// </summary>
    public static (int Width, int Height) DesktopSize(RemoteResolution resolution, SessionAreaMetrics area)
    {
        if (!resolution.IsFitToTab)
        {
            return (resolution.Width, resolution.Height);
        }

        static int RoundToSupportedSize(int size) =>
            Math.Clamp(size - size % 4, MinimumDesktopSize, MaximumDesktopSize);

        return (RoundToSupportedSize(area.Width), RoundToSupportedSize(area.Height));
    }

    public static uint PhysicalMillimeters(int pixels, Dpi dpi) =>
        (uint)Math.Clamp(Math.Round(pixels * 25.4 / dpi.Value), 10, 10_000);

    /// <summary>The documented device scale factor (100, 140, or 180) nearest to the monitor's scaling.</summary>
    public static uint DeviceScaleFactor(Dpi dpi) =>
        DeviceScaleFactors.OrderBy(candidate => Math.Abs(candidate - dpi.Value * 100d / Dpi.DefaultValue)).First();

    public static RemoteDisplaySettings For(RemoteResolution resolution, RemoteScale scale, SessionAreaMetrics area)
    {
        var (width, height) = DesktopSize(resolution, area);
        return new RemoteDisplaySettings(
            width,
            height,
            PhysicalMillimeters(area.Width, area.Dpi),
            PhysicalMillimeters(area.Height, area.Dpi),
            scale.Percent,
            DeviceScaleFactor(area.Dpi));
    }
}

/// <summary>A display update the Remote Desktop control couldn't apply; the message is shown to the user.</summary>
public sealed class RemoteDisplayUpdateException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Sends display settings to the session's current Remote Desktop control.</summary>
public interface IRemoteDisplayClient
{
    /// <exception cref="RemoteDisplayUpdateException">The control is unavailable or rejected the update.</exception>
    void Apply(RemoteDisplaySettings settings);
}

/// <summary>
/// Owns a session's display preferences (resolution, remote scaling) and keeps the remote desktop in step with the
/// session area. Changes of size or monitor DPI are coalesced: each one restarts a short settle delay, and only when it
/// elapses is the remote session updated, once, and only if the fit-to-tab desktop size actually changed. Moving to a
/// monitor with a different scale therefore never reconnects the session, and a fixed resolution is never renegotiated
/// because of a move.
/// </summary>
public sealed class RemoteDisplayCoordinator
{
    /// <summary>How long the session area must stay unchanged before the remote desktop is resized.</summary>
    public const int SettleDelayMilliseconds = 350;

    private readonly IRemoteDisplayClient _client;
    private readonly Func<bool> _isConnected;
    private readonly Action _restartSettleDelay;
    private readonly Func<SessionAreaMetrics> _fallbackArea;
    private SessionAreaMetrics? _area;
    private (int Width, int Height)? _sentDesktopSize;
    private bool _failureReported;

    public RemoteDisplayCoordinator(
        IRemoteDisplayClient client,
        Func<bool> isConnected,
        Action restartSettleDelay,
        Func<SessionAreaMetrics> fallbackArea)
    {
        _client = client;
        _isConnected = isConnected;
        _restartSettleDelay = restartSettleDelay;
        _fallbackArea = fallbackArea;
    }

    /// <summary>The status text to show for a connected session (for example "Connected · 1920 × 1080 · 100%").</summary>
    public event Action<string>? StatusChanged;

    /// <summary>An update failed: the user-facing message, the cause, and whether the user should be told now.</summary>
    public event Action<string, Exception, bool>? UpdateFailed;

    /// <summary>The preferences chosen in the session toolbar; while disconnected, the next connection uses them.</summary>
    public RemoteResolution Resolution { get; private set; } = RemoteDisplayOptions.Resolutions[0];
    public RemoteScale Scale { get; private set; } = RemoteDisplayOptions.Scales[0];

    /// <summary>The settings the session uses: from the last connection or the last accepted display update.</summary>
    public RemoteResolution ActiveResolution { get; private set; } = RemoteDisplayOptions.Resolutions[0];
    public RemoteScale ActiveScale { get; private set; } = RemoteDisplayOptions.Scales[0];

    /// <summary>Display updates sent to a connected session (diagnostics and tests).</summary>
    public int UpdatesSent { get; private set; }

    /// <summary>Physical size and DPI of the session area, measured from layout; falls back to the control's own size.</summary>
    public SessionAreaMetrics Area => _area ?? _fallbackArea();

    public void SetSessionArea(SessionAreaMetrics area)
    {
        if (_area == area)
        {
            return;
        }
        _area = area;
        RequestFitCheck();
    }

    /// <summary>The control itself was resized.</summary>
    public void OnControlResized() => RequestFitCheck();

    /// <summary>
    /// The settings to connect with: the current preferences and the session area as it is now, so a new control uses
    /// the monitor the window is on. The remote scaling and device scale factor can only be set before connecting, so
    /// a connection never needs a display update to match them.
    /// </summary>
    public RemoteDisplaySettings SettingsForConnect()
    {
        var settings = RemoteDisplayPlan.For(Resolution, Scale, Area);
        _sentDesktopSize = (settings.DesktopWidth, settings.DesktopHeight);
        ActiveResolution = Resolution;
        ActiveScale = Scale;
        return settings;
    }

    /// <summary>The control couldn't take the remote scaling before connecting: it connects at 100%.</summary>
    public void OnConnectScaleUnavailable() => ActiveScale = RemoteDisplayOptions.Scales[0];

    /// <summary>The session has just connected.</summary>
    public void OnConnected()
    {
        // Changed while connecting (for example during sign-in), or the scaling couldn't be set before connecting.
        if (ActiveResolution != Resolution || ActiveScale != Scale)
        {
            TryApply();
            return;
        }
        StatusChanged?.Invoke(Describe());
        // The window may have been resized or moved to another monitor while the user was signing in.
        RequestFitCheck();
    }

    public void OnSettleDelayElapsed()
    {
        if (_isConnected() && Resolution.IsFitToTab && RemoteDisplayPlan.DesktopSize(Resolution, Area) != _sentDesktopSize)
        {
            TryApply();
        }
    }

    public bool SetResolution(RemoteResolution resolution) => Change(resolution, Scale);

    public bool SetScale(RemoteScale scale) => Change(Resolution, scale);

    private bool Change(RemoteResolution resolution, RemoteScale scale)
    {
        if (Resolution == resolution && Scale == scale)
        {
            return true;
        }
        Resolution = resolution;
        Scale = scale;
        return !_isConnected() || TryApply();
    }

    private void RequestFitCheck()
    {
        if (_isConnected() && Resolution.IsFitToTab)
        {
            _restartSettleDelay();
        }
    }

    private bool TryApply()
    {
        var area = Area;
        var settings = RemoteDisplayPlan.For(Resolution, Scale, area);
        try
        {
            _client.Apply(settings);
        }
        catch (RemoteDisplayUpdateException exception)
        {
            Resolution = ActiveResolution;
            Scale = ActiveScale;
            var notify = !_failureReported;
            _failureReported = true;
            UpdateFailed?.Invoke(exception.Message, exception.InnerException ?? exception, notify);
            return false;
        }

        UpdatesSent++;
        _sentDesktopSize = (settings.DesktopWidth, settings.DesktopHeight);
        _failureReported = false;
        ActiveResolution = Resolution;
        ActiveScale = Scale;
        AppLog.Information(
            $"Remote display update {UpdatesSent}: {settings.DesktopWidth} × {settings.DesktopHeight} for a {area.Width} × {area.Height} area at {area.Dpi}; " +
            $"desktop scale {settings.DesktopScaleFactor}%, device scale {settings.DeviceScaleFactor}%.");
        StatusChanged?.Invoke(Describe());
        return true;
    }

    private string Describe()
    {
        var (width, height) = RemoteDisplayPlan.DesktopSize(Resolution, Area);
        return $"Connected · {width} × {height} · {Scale.Percent}%";
    }
}
