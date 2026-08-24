using System.Text.RegularExpressions;

namespace Ago.Chat.Domain;

/// <summary>
/// The widget's per-site appearance - `adr/0028`'s two fixed, validated fields (a primary color and a
/// launcher position), deliberately not arbitrary CSS or a free-text theme blob: the widget's Shadow
/// DOM isolation (`embeddable-widget` skill) exists to protect the widget *from* the host page, and a
/// config field that injects style rules the other direction - into a shadow tree the widget's own
/// script controls, sourced from a value a tenant operator supplies - is a real security-surface
/// question this item deliberately does not open (CSS can exfiltrate via attribute selectors, redress
/// content, or abuse <c>:has()</c>/animation timing even confined to a shadow root).
///
/// <see cref="PrimaryColorHex"/> is nullable - <see langword="null"/> means "use the widget's own
/// built-in default," so a freshly self-registered or pre-existing <see cref="Site"/> (<see cref="Default"/>,
/// below) never renders broken just because nobody has configured a color yet.
///
/// A <c>readonly record struct</c>, the same shape <see cref="MessageBody"/> already uses for a
/// validated primitive wrapper - value equality, no identity of its own, validated once at
/// construction so nothing downstream (`Site.UpdateWidgetConfig`, the EF mapping, a mapper building
/// the outbox envelope) has to re-check the hex format it already trusts.
/// </summary>
public readonly partial record struct WidgetConfig
{
    public string? PrimaryColorHex { get; }

    public Position Position { get; }

    public WidgetConfig(string? primaryColorHex, Position position)
    {
        if (primaryColorHex is not null && !HexColorPattern().IsMatch(primaryColorHex))
        {
            throw new ArgumentException(
                $"'{primaryColorHex}' is not a valid #RRGGBB hex color.", nameof(primaryColorHex));
        }

        PrimaryColorHex = primaryColorHex;
        Position = position;
    }

    /// <summary>What a <see cref="Site"/> has before anyone ever calls
    /// <see cref="Site.UpdateWidgetConfig"/> - no color override, launcher bottom-right, matching
    /// `Stage11AddSiteWidgetConfig`'s own column defaults so a row written before this field existed
    /// (or by `1-05`'s seed script, untouched) reads back exactly this value, not a null reference.
    /// </summary>
    public static readonly WidgetConfig Default = new(null, Position.BottomRight);

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColorPattern();
}
