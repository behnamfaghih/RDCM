using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace RDCM;

/// <summary>
/// Decorative icon-font glyph. It has no automation peer so screen readers do not announce
/// private-use icon characters; meaning is conveyed by adjacent text or automation names.
/// </summary>
public sealed class Glyph : TextBlock
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
