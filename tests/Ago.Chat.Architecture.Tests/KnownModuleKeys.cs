namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `20-07`/`adr/0065` §8's "no module runtime... wired statically": there is no registry of module
/// keys this build discovers at runtime, so guard 2 (<see cref="ModuleKeyLiteralRule"/>) cannot derive
/// its own allow-list from one either. This is that allow-list, maintained by hand.
///
/// <para><b>Must be updated by hand when a second module exists.</b> Calendar is the sole
/// implementation today (`adr/0065` §8); the day a second real module is wired statically, its key
/// belongs here too, or guard 2 stops catching a literal of it. This is the deliberate cost of "no
/// module runtime" - the same cost `adr/0065`'s own Consequences section names for
/// <see cref="MessageOpacityRule"/>'s two curated word lists: "three tests that fail for reasons a
/// reader has to understand before they can fix them."</para>
/// </summary>
internal static class KnownModuleKeys
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "calendar",
    };
}
