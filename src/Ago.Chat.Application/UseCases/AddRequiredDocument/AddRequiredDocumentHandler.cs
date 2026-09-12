using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.AddRequiredDocument;

/// <summary>
/// `24-16`: the one write this whole item exists to add. No domain aggregate sits behind this call for
/// the identical reason `RequiredDocumentRecord`'s own remarks give: a row's entire fact is "this pair
/// is required," nothing more to validate beyond shape, so a plain handler over the port is the whole
/// mechanism - the same "no behaviour to attach to a row beyond it exists" judgement, restated as code
/// rather than merely as that persistence record's own comment.
/// </summary>
public sealed class AddRequiredDocumentHandler(IRequiredDocumentRepository requiredDocuments)
{
    public async Task<Result<AddedRequiredDocument>> HandleAsync(AddRequiredDocument command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.DocumentKey))
        {
            return RequiredDocumentErrors.Invalid("A document key cannot be empty.");
        }

        var trimmedKey = command.DocumentKey.Trim();
        if (trimmedKey.Length > Document.MaxDocumentKeyLength)
        {
            return RequiredDocumentErrors.Invalid($"A document key cannot exceed {Document.MaxDocumentKeyLength} characters.");
        }

        var added = await requiredDocuments.AddAsync(command.SubjectKind, trimmedKey, cancellationToken);
        return new AddedRequiredDocument(command.SubjectKind, trimmedKey, AlreadyRequired: !added);
    }
}
