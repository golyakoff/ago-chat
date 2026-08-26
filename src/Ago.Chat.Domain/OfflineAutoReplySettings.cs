namespace Ago.Chat.Domain;

/// <summary>
/// `14-04`: a site's whole offline auto-reply configuration - the toggle, the fallback text, and the
/// ordered keyword rules that can override it.
///
/// <para><b>Why there is a fallback and not only rules.</b> The item's Goal is "a visitor gets an
/// automatic reply instead of silence". A rule list alone answers only the messages somebody
/// anticipated, and answers the rest with the silence the feature exists to remove - so
/// <see cref="FallbackReply"/> is the reply, and <see cref="Rules"/> are refinements of it. That is
/// also why <see cref="FallbackReply"/> is required whenever <see cref="Enabled"/> is set: an enabled
/// configuration with nothing to say is a setting that looks on and does nothing, which is worse than
/// off.</para>
///
/// <para><b>What this type does not decide.</b> Whether to send at all - that is
/// <c>SendOfflineAutoReplyHandler</c>'s, and it depends on live facts (is anybody online, is this
/// conversation already assigned) that no configuration value can answer. <see cref="Match"/> answers
/// only "given this text, what would the script say", deliberately without consulting
/// <see cref="Enabled"/>, so that the gate lives in exactly one place and a test can aim at it.</para>
///
/// <para>A <c>sealed record</c> rather than a <c>readonly record struct</c> like its
/// <see cref="WidgetConfig"/> sibling: it holds a collection, so it is neither small nor cheap to
/// copy, and it is cached (<c>SiteConfigDto</c>) - which means it is serialised, and a reference type
/// with one public constructor is what <c>System.Text.Json</c> round-trips predictably.</para>
/// </summary>
public sealed record OfflineAutoReplySettings
{
    /// <summary>A bound, not a product requirement - it exists so one tenant cannot turn a cached,
    /// per-message-evaluated configuration value into an unbounded one. Revisit when a real tenant
    /// hits it.</summary>
    public const int MaxRules = 20;

    public bool Enabled { get; }

    public string FallbackReply { get; }

    public IReadOnlyList<OfflineAutoReplyRule> Rules { get; }

    public OfflineAutoReplySettings(bool enabled, string fallbackReply, IReadOnlyList<OfflineAutoReplyRule> rules)
    {
        ArgumentNullException.ThrowIfNull(fallbackReply);
        ArgumentNullException.ThrowIfNull(rules);

        if (rules.Count > MaxRules)
        {
            throw new ArgumentException(
                $"A site cannot have more than {MaxRules} auto-reply rules.", nameof(rules));
        }

        if (enabled && string.IsNullOrWhiteSpace(fallbackReply))
        {
            throw new ArgumentException(
                "An enabled offline auto-reply needs a fallback reply.", nameof(fallbackReply));
        }

        if (fallbackReply.Length > OfflineAutoReplyRule.MaxReplyLength)
        {
            throw new ArgumentException(
                $"Auto-reply text cannot exceed {OfflineAutoReplyRule.MaxReplyLength} characters.",
                nameof(fallbackReply));
        }

        Enabled = enabled;
        FallbackReply = fallbackReply;
        Rules = [.. rules];
    }

    /// <summary>What a <see cref="Site"/> has before anyone ever calls
    /// <see cref="Site.UpdateOfflineAutoReply"/> - off, with nothing to say. `14-04`'s "off by
    /// default, tenant-toggleable... not a silent behaviour change to existing tenants' widgets",
    /// expressed as the value every pre-existing row reads back as.</summary>
    public static readonly OfflineAutoReplySettings Disabled = new(false, string.Empty, []);

    /// <summary>
    /// The first rule whose keyword appears in <paramref name="body"/>, else the fallback, else
    /// <see langword="null"/> when there is nothing configured to say.
    ///
    /// <para>First match wins, in the order the tenant listed them - so a more specific rule is put
    /// above a broader one, which is a rule an operator can act on. "Longest keyword wins" was the
    /// alternative and is worse: it makes the outcome depend on a property of the text nobody sees in
    /// the editor.</para>
    /// </summary>
    public string? Match(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        foreach (var rule in Rules)
        {
            if (rule.Matches(body))
            {
                return rule.Reply;
            }
        }

        return string.IsNullOrWhiteSpace(FallbackReply) ? null : FallbackReply;
    }
}
