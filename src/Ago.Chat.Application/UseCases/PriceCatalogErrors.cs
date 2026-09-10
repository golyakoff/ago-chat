using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases;

/// <summary>Error codes for `25-43`'s own use cases - one small vocabulary, the same
/// one-file-per-feature-area shape <see cref="PublishedDocumentErrors"/> already establishes.</summary>
public static class PriceCatalogErrors
{
    /// <summary>`PublishPriceVersionHandler`'s own first check - the caller named a
    /// <see cref="Domain.PriceKey"/> <see cref="Domain.PricedResourceKeys"/> does not list.
    /// `25-43`'s own first decision, made mechanical: code registers a key by adding it to that
    /// registry; a caller of this command can only ever set a number for one that is already
    /// there.</summary>
    public static Error PriceKeyUnknown(string key) =>
        new("Billing.PriceKeyUnknown", $"'{key}' is not a registered price key - a developer must add it to PricedResourceKeys first.");

    /// <summary>A malformed key string, or a negative amount - the caller's own mistake to fix, the
    /// identical shape <c>Module.Invalid</c> already gives for an analogous case.</summary>
    public static Error PriceInvalid(string reason) => new("Billing.PriceInvalid", reason);

    /// <summary><see cref="Application.Abstractions.PriceCatalogConcurrencyConflictException"/>
    /// survived every retry <c>PublishPriceVersionHandler</c> allows itself - two publishes for the
    /// same key raced repeatedly. Genuinely conflict-shaped (`409`), the identical reasoning
    /// <see cref="PublishedDocumentErrors.PublishConflict"/> already gives for the identical
    /// shape.</summary>
    public static Error PricePublishConflict(string key) =>
        new("Billing.PricePublishConflict", $"Publishing a price for '{key}' conflicted with a concurrent publish; retry.");

    /// <summary>
    /// `25-43`'s own second decision, made mechanical at every real charge site: a key with no
    /// published version is "built, not yet for sale" - the ordinary state, never an error condition a
    /// caller must work around, but a real reason to refuse *this* charge cleanly. Every charge site
    /// (`CreateCheckoutSessionHandler`, `ProcessSubscriptionRenewalHandler`, `ChangeSubscriptionSeatsHandler`)
    /// returns this the moment <see cref="Application.Abstractions.IPriceCatalogRepository.FindCurrentAsync"/>
    /// answers <see langword="null"/> for a key it needs - never a crash, and never a charge for
    /// zero.
    /// </summary>
    public static Error PriceNotConfigured(string key) =>
        new("Billing.PriceNotConfigured", $"No price has been published yet for '{key}' - this resource is not for sale.");
}
