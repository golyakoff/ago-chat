namespace Ago.Chat.Domain;

/// <summary>
/// `23-64`: the closed set the backlog item's own Scope fixes - "15, 30, 45, 60, 90, 120 seconds,
/// default 30. A select, not a free number - the author fixed the list, and a closed set is one
/// fewer thing to validate." A real enum, the same reasoning <see cref="Position"/>/<see cref="Locale"/>
/// already establish for their own closed sets: a bare `int` column would still need exactly this
/// same six-value check at every boundary that touches it, and an enum makes an out-of-range value a
/// compile-time impossibility for any C# caller instead of a runtime check nobody can forget.
///
/// <para>Member values equal the delay itself in whole seconds - unlike <see cref="Position"/>
/// (`"bottom-right"`) or <see cref="Locale"/> (`"en"`), whose wire/storage spelling deliberately
/// differs from the C# member name, a delay has no more natural spelling than the number of seconds
/// it already is. `System.Text.Json`'s default enum handling serialises the underlying `int`, so the
/// wire carries `30`, not `"Seconds30"` - the console's own `AutoOpenDelaySeconds` type is a plain
/// numeric union (`15 | 30 | ...`) for exactly this reason, not a second PascalCase-string convention
/// to parse.</para>
/// </summary>
public enum AutoOpenDelay
{
    Seconds15 = 15,
    Seconds30 = 30,
    Seconds45 = 45,
    Seconds60 = 60,
    Seconds90 = 90,
    Seconds120 = 120,
}
