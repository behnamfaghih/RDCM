namespace RDCM;

public sealed record RemoteResolution(string Label, int Width, int Height)
{
    public bool IsFitToTab => Width == 0 && Height == 0;
}

public sealed record RemoteScale(string Label, uint Percent);

public static class RemoteDisplayOptions
{
    public static IReadOnlyList<RemoteResolution> Resolutions { get; } =
    [
        new("Fit to tab", 0, 0),
        new("1024 × 768", 1024, 768),
        new("1280 × 720", 1280, 720),
        new("1366 × 768", 1366, 768),
        new("1600 × 900", 1600, 900),
        new("1920 × 1080", 1920, 1080),
        new("2560 × 1440", 2560, 1440)
    ];

    public static IReadOnlyList<RemoteScale> Scales { get; } =
    [
        new("100%", 100),
        new("125%", 125),
        new("150%", 150),
        new("175%", 175),
        new("200%", 200),
        new("250%", 250)
    ];
}
