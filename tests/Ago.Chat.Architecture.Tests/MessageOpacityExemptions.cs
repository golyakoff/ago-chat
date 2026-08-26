namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// Places <see cref="MessageOpacityRule"/> flags that are not boundary crossings, each with the
/// reason it is not - the same shape <see cref="TenantScopeExemptions"/> uses, and for the same
/// reason: a rule with no escape hatch gets weakened, and a rule whose escape hatch takes no argument
/// gets used.
///
/// <para><b>One entry, and it is the collision this rule always knew it had.</b> `14-06` added
/// structured message content without a single word of another product's vocabulary reaching any
/// assembly; the one thing the scan flags predates it by two stages and is a different concept
/// wearing the same English word.</para>
///
/// <para><b>What legitimately belongs here:</b> an English word that collides with a product concept
/// in an unrelated sense - an operator's capacity "slot" - once it reaches an identifier or a literal
/// rather than staying in a comment. What does <i>not</i> belong here is a field, a DTO member or a
/// branch that genuinely names what is inside a payload. That is the violation, not a false positive,
/// and the remedy is to delete the code rather than to list it.</para>
/// </summary>
internal static class MessageOpacityExemptions
{
    /// <summary>Keyed by the exact description <see cref="MessageOpacityTests"/> builds
    /// (<c>Assembly: Type -&gt; what names 'word'</c>), so an exemption cannot silently widen to
    /// cover a second violation that happens to look similar.</summary>
    public static readonly IReadOnlyDictionary<string, string> ByViolation =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Ago.Chat.Contracts: Ago.Chat.Contracts.ChatMetrics -> string literal in '.cctor' names 'slot'"] =
                "`6-10`'s metric description, and a genuinely different concept: an operator's capacity slot is "
                + "one simultaneous conversation they may hold, decremented by CloseConversationHandler. It has "
                + "nothing to do with a bookable interval, it predates `14-06` by two stages, and it lives in a "
                + "human-readable OpenTelemetry description rather than in a branch. Exempted rather than "
                + "reworded, because renaming a shipped metric's description to satisfy a test would be the test "
                + "changing the product.",
        };

    public static bool IsExempt(string violation) => ByViolation.ContainsKey(violation);
}
