using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Media;

namespace RDCM;

/// <summary>Decides whether a key event belongs to a remote session rather than to RDCM's shortcuts.</summary>
public static class SessionKeyRouting
{
    /// <summary>
    /// True when the key event's source is the session host or inside it. Live testing showed that keys typed into a
    /// connected session are handled by the Remote Desktop control's own input thread and never reach RDCM's window;
    /// this guard covers any key WPF does route from the host.
    /// </summary>
    public static bool IsFromSessionHost(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is WindowsFormsHost)
            {
                return true;
            }
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }
}
