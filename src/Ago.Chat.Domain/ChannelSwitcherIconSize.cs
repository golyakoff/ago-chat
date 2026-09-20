namespace Ago.Chat.Domain;

/// <summary>
/// `25-173`: the closed set of three circle sizes the `BelowLauncher`
/// <see cref="ChannelSwitcherPlacement"/> renders at - a real enum, the same reasoning
/// <see cref="Position"/>/<see cref="AutoOpenDelay"/> already establish for their own closed sets: a
/// fourth size or a free pixel value is exactly what this item's own Out of scope forbids, and an enum
/// makes an out-of-range value a compile-time impossibility for any C# caller rather than a runtime
/// check nobody can forget.
///
/// <see cref="Medium"/> is the author's own stated default (the backlog item's Scope table), but
/// deliberately **not** the first member - unlike <see cref="Position.BottomRight"/>/
/// <see cref="ChannelSwitcherPlacement.AboveComposer"/>, whose first-member-is-default shape matters
/// because a bare `default(T)` must read as today's real behaviour, this field is always present but
/// only ever meaningful when <see cref="ChannelSwitcherPlacement"/> is <see cref="ChannelSwitcherPlacement.BelowLauncher"/>
/// (<see cref="WidgetConfig.ChannelSwitcherIconSize"/>'s own remarks) - <see cref="WidgetConfig.Default"/>
/// states the default explicitly rather than relying on enum-member order to say it silently.
///
/// <para>Ordered <see cref="Large"/>/<see cref="Medium"/>/<see cref="Small"/> - largest to smallest,
/// matching the backlog's own table order and the order `ago-console`'s Select renders its three
/// options in, not diameter or any other sortable property.</para>
/// </summary>
public enum ChannelSwitcherIconSize
{
    Large,
    Medium,
    Small,
}
