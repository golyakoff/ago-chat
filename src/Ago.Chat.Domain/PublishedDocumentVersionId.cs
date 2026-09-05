using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct PublishedDocumentVersionId(Guid Value) : IStronglyTypedId;
