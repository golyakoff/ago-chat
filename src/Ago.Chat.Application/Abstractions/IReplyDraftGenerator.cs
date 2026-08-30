namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `19-01`: the outbound half of this item's own AI operator-reply-draft-assist - given a
/// conversation's own recent message history, asks an LLM provider for a suggested reply and hands the
/// text back for the composer to show, edited or discarded, never sent by anything other than the
/// operator's own explicit action (`adr/0078`'s kind 1, `18-03`'s trust boundary). Deliberately
/// provider-neutral in every member name and type - the same discipline `IYooKassaPaymentsClient`'s own
/// remarks state for ЮKassa, applied here to whichever LLM provider is chosen
/// (`Ago.Chat.Infrastructure.YandexGpt` is the only project that may know this is YandexGPT
/// specifically, what its request/response JSON shapes are, or how its auth works).
///
/// <para><b>Why Application may declare this port at all</b>: `clean-architecture.md`'s dependency
/// rule - Application knows a draft reply can be requested and what shape comes back, never which
/// HTTP API produces it. The alternative - calling an LLM SDK/HttpClient straight from
/// `GenerateReplyDraftHandler` - would make the use case untestable without a real network call and
/// would leak a vendor-specific request/response DTO into a layer that must not know one exists.</para>
/// </summary>
public interface IReplyDraftGenerator
{
    Task<ReplyDraftGenerationResult> GenerateDraftAsync(
        ReplyDraftGenerationRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// `19-01`'s own context-minimalism requirement, made a type rather than a convention: the only thing
/// this record can carry is the conversation's own recent messages, oldest first - no site name, no
/// tenant configuration, no other conversation's data, because there is no field here for any of that
/// to ride in on. <see cref="GenerateReplyDraftHandler"/> is the one place that builds this record, and
/// it builds it from exactly one conversation's own <c>IConversationReadStore</c> read.
/// </summary>
public sealed record ReplyDraftGenerationRequest(IReadOnlyList<ReplyDraftHistoryMessage> RecentMessages);

/// <summary>A single turn of the conversation's own history, stripped to what a reply-draft prompt
/// needs and nothing else - no message id, no attachment reference, no `14-06` structured payload
/// (`adr/0061`: AGO Chat must not understand a module's payload, and handing it to a third-party LLM
/// would be worse than not understanding it - it would be *exposing* it). <see cref="AuthorKind"/> is
/// this port's own two-value vocabulary, not <see cref="Domain.MessageAuthorKind"/> directly - the
/// third member of that enum (<c>System</c>, the offline auto-reply) is a real value a caller must
/// still be able to fold into one side or drop, and a Yandex-specific request DTO is exactly what this
/// type exists to keep out of Application, so the mapping decision belongs to
/// <see cref="Application.UseCases.GenerateReplyDraft.GenerateReplyDraftHandler"/>, not to a third enum
/// value here.</summary>
public sealed record ReplyDraftHistoryMessage(ReplyDraftAuthorKind AuthorKind, string Body);

public enum ReplyDraftAuthorKind
{
    Visitor,
    Operator,
}

/// <summary>
/// `resilience.md`'s "no fallback content" rule, applied honestly: there is nothing sensible to draft
/// in place of a real suggestion, so the only two outcomes are a real one or an explicit "not right
/// now" - never a placeholder string dressed up as a draft. No terminal/transient split at this level
/// the way <see cref="CreatePaymentResult"/> has one: unlike a payment, a malformed or refused LLM call
/// is not a distinct case an operator can act on differently than a timed-out one - both mean "no
/// suggestion is available right now", which is exactly what <see cref="Unavailable"/> already says.
/// The terminal/transient split still exists *inside* `Ago.Chat.Infrastructure.YandexGpt`, where it
/// decides what gets retried; it simply never needs to surface past
/// <c>Ago.Chat.Module.ReplyDraft.ResilientReplyDraftGenerator</c>, because there is no caller here who
/// could do anything with the distinction (`resilience.md`'s degrade-to-"suggestion unavailable" rule).
/// </summary>
public abstract record ReplyDraftGenerationResult
{
    private ReplyDraftGenerationResult()
    {
    }

    public sealed record Success(string DraftText) : ReplyDraftGenerationResult;

    public sealed record Unavailable(string Reason) : ReplyDraftGenerationResult;
}
