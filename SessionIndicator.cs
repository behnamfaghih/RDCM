using System.Globalization;
using System.Windows.Data;

namespace RDCM;

/// <summary>
/// Maps RDP session states to the small set of visual indicators used across the UI
/// (connection list dots, tab header dots, and the session toolbar).
/// </summary>
public static class SessionIndicator
{
    public const string None = "None";
    public const string Connected = "Connected";
    public const string Busy = "Busy";
    public const string Failed = "Failed";
    public const string Idle = "Idle";

    public static string For(RdpSessionState state) => state switch
    {
        RdpSessionState.Connected => Connected,
        RdpSessionState.Created or RdpSessionState.Connecting or RdpSessionState.Reconnecting
            or RdpSessionState.Disconnecting or RdpSessionState.AutoReconnecting => Busy,
        RdpSessionState.Failed => Failed,
        _ => Idle
    };

    public static string Describe(string indicator) => indicator switch
    {
        Connected => "Session connected",
        Busy => "Session connecting",
        Failed => "Session failed",
        Idle => "Session disconnected",
        _ => ""
    };

    private static int Priority(string indicator) => indicator switch
    {
        Connected => 4,
        Busy => 3,
        Failed => 2,
        Idle => 1,
        _ => 0
    };

    public static string Combine(IEnumerable<RdpSessionState> states) =>
        states.Select(For).OrderByDescending(Priority).FirstOrDefault() ?? None;
}

/// <summary>Looks up a profile's session indicator. Values: [0] profile id, [1] indicator map.</summary>
public sealed class ProfileSessionIndicatorConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [Guid id, IReadOnlyDictionary<Guid, string> map, ..] && map.TryGetValue(id, out var value)
            ? value
            : SessionIndicator.None;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
