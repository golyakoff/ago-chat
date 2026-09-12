using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RemoveRequiredDocument;

/// <summary>
/// `24-16`: the other half of the platform owner's own write surface. Trims and forwards to
/// <see cref="IRequiredDocumentRepository.RemoveAsync"/> with no other decision to make - the same
/// "no behaviour to attach beyond it exists" judgement <see cref="AddRequiredDocument.AddRequiredDocumentHandler"/>'s
/// own remarks give for its sibling.
/// </summary>
public sealed class RemoveRequiredDocumentHandler(IRequiredDocumentRepository requiredDocuments)
{
    public async Task<Result<RemovedRequiredDocument>> HandleAsync(RemoveRequiredDocument command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.DocumentKey))
        {
            return RequiredDocumentErrors.Invalid("A document key cannot be empty.");
        }

        var trimmedKey = command.DocumentKey.Trim();
        var removed = await requiredDocuments.RemoveAsync(command.SubjectKind, trimmedKey, cancellationToken);
        return new RemovedRequiredDocument(command.SubjectKind, trimmedKey, WasRequired: removed);
    }
}
