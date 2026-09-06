using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetConsentRequirement;

/// <summary>
/// `24-05`. Never fails on "nothing required" - the same "no `Result&lt;T&gt;` wrapper reason,
/// restated here with one" shape <see cref="GetRequiredDocumentsForSubjectKind.GetRequiredDocumentsForSubjectKindHandler"/>
/// already establishes, except this read genuinely can fail (a conversation id from another visitor,
/// or one that does not exist), so this handler does return a <see cref="Result{T}"/> - only the
/// "required or not" question inside a successful result is never itself a failure.
///
/// <para><b>Any version of the visitor's own acceptance counts, not only the current one.</b> `adr/0114`
/// deliberately left "does a new version invalidate an existing acceptance" as an open, lawyer-owned
/// question (its own Consequences). Until that question is answered, this handler takes the reading
/// that does not silently re-demand consent nobody asked to withdraw: a visitor who accepted v1 still
/// reads as having accepted once v2 replaces it. If the eventual legal answer is "no, re-acceptance is
/// required on a material change," that is this handler's one line to revisit, not a rebuild.</para>
/// </summary>
public sealed class GetConsentRequirementHandler(
    IConversationRepository conversations, ISiteRepository sites, IDocumentRepository documents, IAcceptanceRepository acceptances)
{
    public async Task<Result<ConsentRequirement>> HandleAsync(GetConsentRequirement query, CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetByIdAsync(query.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(query.ConversationId.Value);
        }

        if (conversation.VisitorId != query.RequestedBy)
        {
            return ConversationErrors.Forbidden("This visitor is not a participant of this conversation.");
        }

        var site = await sites.GetByIdAsync(conversation.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(conversation.SiteId.Value);
        }

        if (!site.WidgetConfig.RequireContactConsent)
        {
            return new ConsentRequirement(false, null, false, null, false);
        }

        var accepted = await acceptances.GetForSubjectAsync(AcceptanceSubjectKind.Visitor, query.RequestedBy.Value, cancellationToken);
        var acceptedKeys = accepted.Select(a => a.DocumentKey).ToHashSet(StringComparer.Ordinal);

        var (contactSummary, contactAccepted) =
            await ResolveAsync(conversation.SiteId, VisitorConsentPurpose.Contact, acceptedKeys, cancellationToken);
        var (marketingSummary, marketingAccepted) =
            await ResolveAsync(conversation.SiteId, VisitorConsentPurpose.Marketing, acceptedKeys, cancellationToken);

        return new ConsentRequirement(true, contactSummary, contactAccepted, marketingSummary, marketingAccepted);
    }

    private async Task<(ConsentDocumentSummary? Summary, bool Accepted)> ResolveAsync(
        SiteId siteId, VisitorConsentPurpose purpose, IReadOnlySet<string> acceptedKeys, CancellationToken cancellationToken)
    {
        var documentKey = SiteConsentDocumentKey.For(siteId, purpose);
        var current = await documents.FindCurrentAsync(documentKey, cancellationToken);
        var summary = new ConsentDocumentSummary(documentKey, current?.Version, current?.Title, current?.Body, current?.PublishedAt);
        return (summary, acceptedKeys.Contains(documentKey));
    }
}
