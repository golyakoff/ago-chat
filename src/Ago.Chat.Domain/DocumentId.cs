using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

/// <summary>`24-02`: one row per document key (<c>"privacy-policy"</c>, <c>"operator-terms"</c>, ...) -
/// the aggregate root that owns <see cref="Document.LastSequence"/> and, through it, the ordering of
/// every <see cref="PublishedDocumentVersion"/> published under that key. See <see cref="Document"/>'s
/// own remarks for why this exists as a second table rather than folding a counter onto
/// <see cref="PublishedDocumentVersion"/> itself.</summary>
public readonly record struct DocumentId(Guid Value) : IStronglyTypedId;
