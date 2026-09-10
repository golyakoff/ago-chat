using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

/// <summary>`25-43`: one published, immutable version of one <see cref="PricedResource"/> - the
/// identical role <see cref="PublishedDocumentVersionId"/> plays for
/// <see cref="PublishedDocumentVersion"/>.</summary>
public readonly record struct PublishedPriceVersionId(Guid Value) : IStronglyTypedId;
