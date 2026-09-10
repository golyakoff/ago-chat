using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.PublishPriceVersion;

/// <summary>
/// `25-43`: the one procedure by which a real charge's own price ever changes - not a code change and
/// a deploy, an authenticated call the platform owner makes once `ago-business` has decided the new
/// figure. Loads (or creates) the <see cref="PricedResource"/> for the given key, calls
/// <see cref="PricedResource.Publish"/> to mint the next version, and saves - retrying against a
/// freshly reloaded aggregate if <see cref="PriceCatalogConcurrencyConflictException"/> says another
/// publish for the same key won the race. The identical shape
/// `Ago.Chat.Application.UseCases.PublishDocumentVersion.PublishDocumentVersionHandler` already
/// establishes for `adr/0114`'s own mechanism - deliberately copied, not reinvented, since the design
/// problem (grandfathering) is identical.
///
/// <para><b>The one real difference from its document-shaped precedent: the key must already be
/// registered.</b> <see cref="Domain.PricedResourceKeys.IsKnown"/> is checked before anything else -
/// `25-43`'s own first decision ("the owner only ever sets or changes the Rouble figure for a key that
/// already exists") is enforced exactly here, the one place a caller-supplied key could otherwise
/// become a brand-new, code-never-registered priced thing. A <see cref="Document"/>'s own key has no
/// equivalent closed set to check against - any legally-shaped string is a legitimate new document -
/// which is exactly why this handler cannot simply reuse
/// <see cref="Domain.Document.Create"/>'s own "brand-new row on first publish" shape without this one
/// extra guard in front of it.</para>
/// </summary>
public sealed class PublishPriceVersionHandler(IPriceCatalogRepository prices, IIdGenerator idGenerator, IClock clock)
{
    // The identical bound PublishDocumentVersionHandler.MaxAttempts already uses, for the identical
    // reason: this row is written by exactly one caller in practice (the platform owner), so a real
    // race is already rare.
    private const int MaxAttempts = 5;

    public async Task<Result<PublishedPriceVersionDto>> HandleAsync(PublishPriceVersion command, CancellationToken cancellationToken)
    {
        PriceKey key;
        try
        {
            key = new PriceKey(command.Key);
        }
        catch (ArgumentException ex)
        {
            return PriceCatalogErrors.PriceInvalid(ex.Message);
        }

        if (!PricedResourceKeys.IsKnown(key))
        {
            return PriceCatalogErrors.PriceKeyUnknown(key.Value);
        }

        if (command.AmountRub < 0)
        {
            return PriceCatalogErrors.PriceInvalid("A published price cannot be negative.");
        }

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var now = clock.UtcNow;
            var resource = await prices.GetByKeyAsync(key, cancellationToken) ?? PricedResource.Create(new PricedResourceId(idGenerator.NewId(now)), key);

            var version = resource.Publish(new PublishedPriceVersionId(idGenerator.NewId(now)), command.AmountRub, now);

            try
            {
                await prices.SaveAsync(resource, cancellationToken);
            }
            catch (PriceCatalogConcurrencyConflictException)
            {
                continue;
            }

            return ToDto(version);
        }

        return PriceCatalogErrors.PricePublishConflict(key.Value);
    }

    private static PublishedPriceVersionDto ToDto(PublishedPriceVersion version) =>
        new(version.Key.Value, version.Version, version.Sequence, version.AmountRub, version.PublishedAt);
}

public sealed record PublishedPriceVersionDto(string Key, string Version, int Sequence, decimal AmountRub, DateTimeOffset PublishedAt);
