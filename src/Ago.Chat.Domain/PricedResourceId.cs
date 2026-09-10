using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

/// <summary>`25-43`: one row per <see cref="PriceKey"/> - the aggregate root that owns
/// <see cref="PricedResource.LastSequence"/> and, through it, the ordering of every
/// <see cref="PublishedPriceVersion"/> published under that key. The identical role
/// <see cref="DocumentId"/> plays for <see cref="Document"/> - see <see cref="PricedResource"/>'s own
/// remarks for why this mirrors that shape deliberately.</summary>
public readonly record struct PricedResourceId(Guid Value) : IStronglyTypedId;
