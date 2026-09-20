namespace Ago.Chat.Domain;

/// <summary>
/// `25-173`: where the widget shows a visitor the channels this site has connected - one of two fixed
/// placements, the identical closed-choice shape <see cref="Position"/> already establishes for itself
/// (`adr/0028`) rather than a bool: a third placement is exactly the kind of value this item's own Out
/// of scope forbids, and an enum makes that a compile-time fact for every C# caller instead of a
/// convention nobody enforces.
///
/// <see cref="AboveComposer"/> is the first member (so it is also the CLR default,
/// <c>default(ChannelSwitcherPlacement)</c>) because it is `25-149`'s own pre-existing behaviour - a
/// site that never calls <see cref="Site.UpdateWidgetConfig"/> with this item's fields must keep
/// rendering exactly what it renders today, never the new placement by accident of enum ordering.
/// </summary>
public enum ChannelSwitcherPlacement
{
    AboveComposer,
    BelowLauncher,
}
