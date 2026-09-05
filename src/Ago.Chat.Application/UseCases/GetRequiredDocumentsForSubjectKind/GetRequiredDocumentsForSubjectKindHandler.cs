using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.UseCases.GetRequiredDocumentsForSubjectKind;

/// <summary>
/// `24-03`. Joins <see cref="IRequiredDocumentRepository.GetRequiredDocumentKeysAsync"/> (which keys
/// are required) against <see cref="IDocumentRepository.FindCurrentAsync"/> (what each currently says)
/// - two small, already-existing reads, not a new table needed to answer this. Never fails - the same
/// "no <c>Result&lt;T&gt;</c> wrapper, because there is no failure case" shape
/// <see cref="Ago.Chat.Application.UseCases.GetAcceptancesForSubject.GetAcceptancesForSubjectHandler"/>
/// already established: an unknown or unconfigured
/// <see cref="GetRequiredDocumentsForSubjectKind.SubjectKind"/> simply returns an empty list, the
/// honest answer to "what must this kind of subject accept" when the answer is "nothing today"
/// (`RegisterSiteHandler`'s own remarks on why that is a real, considered outcome rather than a gap).
/// </summary>
public sealed class GetRequiredDocumentsForSubjectKindHandler(IRequiredDocumentRepository requiredDocuments, IDocumentRepository documents)
{
    public async Task<IReadOnlyList<RequiredDocumentSummary>> HandleAsync(
        GetRequiredDocumentsForSubjectKind query, CancellationToken cancellationToken)
    {
        var keys = await requiredDocuments.GetRequiredDocumentKeysAsync(query.SubjectKind, cancellationToken);
        var summaries = new List<RequiredDocumentSummary>(keys.Count);
        foreach (var documentKey in keys)
        {
            var current = await documents.FindCurrentAsync(documentKey, cancellationToken);
            summaries.Add(new RequiredDocumentSummary(documentKey, current?.Version, current?.Title, current?.PublishedAt));
        }

        return summaries;
    }
}
