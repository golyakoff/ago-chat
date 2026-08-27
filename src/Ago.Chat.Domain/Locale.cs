namespace Ago.Chat.Domain;

/// <summary>
/// `11-10`: the language the widget renders in for one tenant - a closed set of two, not an
/// open-ended locale-negotiation scheme. This project has exactly two demo tenants needing exactly
/// two languages today (`golyakoff/ago-widget#22`'s own gap: `demo-shop1`/`demo-shop2` are
/// translated pages wrapping one English-only bundle); a third locale is a name and a string file
/// away whenever a real tenant needs one, and building that infrastructure now would be the premature
/// generalisation `CLAUDE.md` warns a platform layer against - except this sits in `Ago.Chat.Domain`,
/// a product layer, where the same caution still applies to inventing scope nobody asked for.
///
/// <see cref="En"/> is the first member (so it is also the CLR default, `default(Locale)`) for the
/// identical reason <see cref="Position.BottomRight"/> is first on <see cref="Position"/>: a freshly
/// self-registered or pre-existing <see cref="Site"/> that never called <see cref="Site.UpdateLocale"/>
/// must render in the language every widget has always spoken, never in `"Ru"` by accident of enum
/// ordering.
/// </summary>
public enum Locale
{
    En,
    Ru,
}
