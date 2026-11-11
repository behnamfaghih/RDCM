using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Media;

namespace RDCM;

/// <summary>
/// The WindowsFormsHost of a session tab. WindowsFormsHost converts its layout size to pixels by truncation, so at some
/// scale factors the hosted session panel ends a pixel short of the host window (2560 px at 175% is 1462.857 units,
/// which becomes 2559 px), leaving a strip beside the remote desktop. This host rounds to the nearest pixel instead,
/// using the DPI of the monitor the window is on.
/// </summary>
public sealed class SessionHost : WindowsFormsHost
{
    protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        if (Child is { } child && Visibility != Visibility.Collapsed)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rounded = new System.Drawing.Size(
                (int)Math.Round(finalSize.Width * dpi.DpiScaleX, MidpointRounding.AwayFromZero),
                (int)Math.Round(finalSize.Height * dpi.DpiScaleY, MidpointRounding.AwayFromZero));
            // Only a rounding difference is corrected; anything larger is WindowsFormsHost's own decision.
            if (child.Size != rounded && Math.Abs(child.Width - rounded.Width) <= 1 && Math.Abs(child.Height - rounded.Height) <= 1)
            {
                child.Size = rounded;
            }
        }
        return arranged;
    }
}
