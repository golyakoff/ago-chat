namespace Ago.Chat.Domain;

/// <summary>
/// `14-04`: one scripted keyword rule - "if the visitor's message contains this word, answer with
/// this text."
///
/// <para><b>Why a flat keyword list and not a decision tree.</b> The backlog item leaves the rule
/// shape to implementation and asks for it to stay cheap and predictable. A substring match over an
/// ordered list is a rule an operator can hold in their head and a reviewer can verify by reading it:
/// there is exactly one way it can fire, and its outcome depends on nothing but the message text and
/// the order of the list. A decision tree would need state per conversation (which node are we at),
/// which is a second thing to persist, invalidate and reason about under redelivery - for a v1 whose
/// entire purpose is "say something rather than nothing while the shop is closed."</para>
///
/// <para><b>Substring, ordinal, case-insensitive.</b> Not a regex: a tenant-supplied regular
/// expression evaluated on the server is a denial-of-service surface (catastrophic backtracking) for a
/// feature that gains nothing from one. Not culture-aware: a culture-sensitive comparison makes the
/// match depend on the server's locale, which is exactly the kind of invisible per-node disagreement
/// <c>CLAUDE.md</c> rule 11 rejects for timestamps, for the same reason.</para>
///
/// <para><b>A <c>sealed record</c>, not a <c>readonly record struct</c> like its
/// <see cref="WidgetConfig"/> neighbour - and the difference is load-bearing, not stylistic.</b> This
/// value is cached: it goes into Redis as JSON and comes back out
/// (<c>SiteConfigDto</c>). <c>System.Text.Json</c> will not use a struct's parameterised constructor
/// unless it is annotated - a struct always has an implicit parameterless one, so the deserialiser
/// takes that and then cannot set get-only properties, and every rule comes back with
/// <see langword="null"/> fields. It fails only on a cache <em>hit</em>, which is what makes it worth
/// this paragraph: the first read of a site works, and the second one throws. A record *class* has
/// exactly one constructor and is unambiguous, which is the same reason
/// <see cref="MessageAction"/> is one. Annotating the struct with <c>[JsonConstructor]</c> was the
/// alternative and was rejected: <see cref="Ago.Chat.Domain"/> does not reference a serializer, and a
/// serialisation attribute here would be the first crack in that.</para>
///
/// <para>Validated once at construction, so nothing downstream (the EF converter, the cached DTO, the
/// matcher) re-checks what it already trusts.</para>
/// </summary>
public sealed record OfflineAutoReplyRule
{
    public const int MaxKeywordLength = 64;

    /// <summary>Deliberately far below <see cref="MessageBody.MaxLength"/>: this text becomes a
    /// message body, so it must fit one, and a scripted reply that runs to thousands of characters is
    /// a product problem before it is a storage one.</summary>
    public const int MaxReplyLength = 1000;

    public string Keyword { get; }

    public string Reply { get; }

    public OfflineAutoReplyRule(string keyword, string reply)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            throw new ArgumentException("Auto-reply keyword cannot be empty.", nameof(keyword));
        }

        var trimmedKeyword = keyword.Trim();
        if (trimmedKeyword.Length > MaxKeywordLength)
        {
            throw new ArgumentException(
                $"Auto-reply keyword cannot exceed {MaxKeywordLength} characters.", nameof(keyword));
        }

        if (string.IsNullOrWhiteSpace(reply))
        {
            throw new ArgumentException("Auto-reply text cannot be empty.", nameof(reply));
        }

        if (reply.Length > MaxReplyLength)
        {
            throw new ArgumentException(
                $"Auto-reply text cannot exceed {MaxReplyLength} characters.", nameof(reply));
        }

        Keyword = trimmedKeyword;
        Reply = reply;
    }

    public bool Matches(string body) =>
        body.Contains(Keyword, StringComparison.OrdinalIgnoreCase);
}
