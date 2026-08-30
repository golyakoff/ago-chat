using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `19-02`: a second, differently-shaped consumer of the same underlying LLM provider `19-01`
/// established - not a reuse of <see cref="IReplyDraftGenerator"/> itself. That port's request/response
/// shape (a conversation's own history in, one free-text draft string out) has no way to carry this
/// item's own load-bearing constraint - "pick zero or more of *this site's own existing* tags, never
/// invent one" - as anything the type system enforces; the closest it could do is a string the caller
/// would have to parse and validate by convention, which is exactly the kind of "hope the prompt was
/// followed" shape this item's own Done-when explicitly does not accept ("never a tag the LLM invents").
/// <see cref="CategorizationCandidateTag"/>/<see cref="CategorizationResult.Success"/> instead make "the
/// answer is a subset of the given candidates" a type both the caller and every implementation must
/// honour, closed vocabulary in, closed vocabulary out.
///
/// <para><b>The alternative considered and rejected</b>: extending <see cref="IReplyDraftGenerator"/>
/// with a second method, or a discriminated request type covering both shapes. Rejected because the two
/// features do not share a caller, a resilience budget, or a failure mode an operator waits on
/// (`19-01`'s draft blocks one operator's own composer; this item's own job runs unattended, `adr/0078`'s
/// kind 2's "a periodic batch job, not real-time classification") - the same "one port per genuinely
/// different use case" reasoning `IYooKassaPaymentsClient` and this codebase's other narrow, single-
/// purpose ports already follow, rather than one wide interface every caller has to partially ignore.
/// </para>
///
/// <para><b>Why Application may declare this port at all</b>: the identical dependency-rule argument
/// <see cref="IReplyDraftGenerator"/>'s own remarks give - Application knows a conversation can be
/// classified into a subset of a given candidate list and what shape comes back, never which HTTP API
/// produces it or how it phrases the prompt. <c>Ago.Chat.Infrastructure.YandexGpt</c> is still the only
/// project that may know this is YandexGPT specifically.</para>
/// </summary>
public interface IConversationCategorizer
{
    Task<CategorizationResult> CategorizeAsync(CategorizationRequest request, CancellationToken cancellationToken);
}

/// <summary>Everything a categorization prompt is allowed to see: one conversation's own recent
/// history, and the fixed list of tags it is allowed to choose among - never a site name, another
/// conversation's data, or anything else, the same context-minimalism discipline
/// <see cref="ReplyDraftGenerationRequest"/>'s own remarks state for the reply-draft port.</summary>
public sealed record CategorizationRequest(
    IReadOnlyList<CategorizationHistoryMessage> RecentMessages,
    IReadOnlyList<CategorizationCandidateTag> CandidateTags);

/// <summary>A single turn of the conversation's own history - the identical shape
/// <see cref="ReplyDraftHistoryMessage"/> already establishes, its own two-value
/// <see cref="CategorizationAuthorKind"/> rather than <see cref="Domain.MessageAuthorKind"/> directly,
/// for the identical reason that type's own remarks give (the mapping decision belongs to
/// <c>CategorizeConversationHandler</c>, not to a third enum value on this port).</summary>
public sealed record CategorizationHistoryMessage(CategorizationAuthorKind AuthorKind, string Body);

public enum CategorizationAuthorKind
{
    Visitor,
    Operator,
}

/// <summary>One tag this site's own vocabulary already has - <see cref="TagId"/> carried alongside
/// <see cref="Name"/> because the prompt needs the human-readable name and the caller needs the id back;
/// a real <see cref="Domain.TagId"/> rather than a bare <see cref="Guid"/>, since Application already
/// legally depends on Domain (the dependency rule this codebase's own clean-architecture.md states) and
/// a strongly-typed id here is what makes "the response can only name one of these" a compile-time
/// question instead of a runtime string-matching one.</summary>
public sealed record CategorizationCandidateTag(TagId TagId, string Name);

/// <summary>
/// The identical "no fallback content, no terminal/transient split above this port" shape
/// <see cref="ReplyDraftGenerationResult"/>'s own remarks state, applied here: a malformed or refused LLM
/// call is not a distinct case `CategorizeConversationHandler` could act on differently than a timed-out
/// one, so both mean "no categorization available this cycle" - <see cref="Unavailable"/>. The one
/// addition <see cref="Success"/> makes over its reply-draft counterpart: an empty
/// <see cref="Success.TagIds"/> list is itself a real, valid answer ("this conversation matches none of
/// the site's tags"), never converted to <see cref="Unavailable"/> - the scope's own "zero or more"
/// wording is a type this result honours directly, not a special case layered on top.
/// </summary>
public abstract record CategorizationResult
{
    private CategorizationResult()
    {
    }

    /// <summary><see cref="TagIds"/> is every candidate id the provider (and this port's own
    /// implementation) judged applicable - not guaranteed by the type system alone to be a subset of
    /// what was offered (an implementation could misbehave), which is exactly why
    /// <c>CategorizeConversationHandler</c> re-validates every id against the candidate set it sent
    /// before writing anything, the second half of this item's own "never invent a tag" defence in
    /// depth (`YandexGptConversationCategorizerClient`'s own remarks are the first half).</summary>
    public sealed record Success(IReadOnlyList<TagId> TagIds) : CategorizationResult;

    public sealed record Unavailable(string Reason) : CategorizationResult;
}
