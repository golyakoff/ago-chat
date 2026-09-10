using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-43`: <see cref="IPriceCatalogRepository.SaveAsync"/>'s own technology-agnostic signal that a
/// <see cref="PricedResource"/> row changed underneath it before this save committed - the identical
/// "translated at the port boundary so Application never sees EF's own exception type" shape
/// <see cref="DocumentConcurrencyConflictException"/> already established for `24-02`. A handler that
/// wants to retry (`PublishPriceVersionHandler`) catches this, never
/// <c>Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException</c>.
/// </summary>
public sealed class PriceCatalogConcurrencyConflictException(PricedResourceId pricedResourceId)
    : Exception($"Priced resource {pricedResourceId.Value} was modified concurrently before it could be saved.")
{
    public PricedResourceId PricedResourceId { get; } = pricedResourceId;
}
