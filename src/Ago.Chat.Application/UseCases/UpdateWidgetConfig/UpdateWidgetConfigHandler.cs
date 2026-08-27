using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.UseCases.GetWidgetConfig;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.UpdateWidgetConfig;

/// <summary>
/// `11-01`: the first real caller of `Site.UpdateWidgetConfig` - and, through it, the first real
/// producer of `SiteSettingsChanged` (`Ago.Chat.Contracts`), which has existed and been fully wired on
/// the consumer side (`SiteCacheInvalidationConsumer`) since `3-04` with nothing ever calling it.
///
/// Validates the hex format and the `Position` enum value here, in Application - not in
/// `Ago.Chat.Api`'s endpoint (`CreateAttachmentHandler`'s own precedent for content-type/size
/// validation) and not left for `Ago.Chat.Domain.WidgetConfig`'s constructor to be the only guard
/// (that constructor still throws defensively regardless of what called it - this handler is what
/// turns an expected bad input into a clean `Result` failure instead of an unhandled exception).
///
/// Injects `IOutboxWriter` directly rather than staging through `Infrastructure.Postgres.Pipeline` -
/// the same "plain, unbatched per-request handler, no shared multi-conversation transaction to
/// coordinate" shape `CloseConversationHandler`/`ConfirmAttachmentHandler` use (adr/0005: state change
/// and integration event, one transaction, one `SaveChangesAsync`) - an ordinary single-aggregate
/// write, not the wider multi-row transaction `10-02`'s registration handler needed.
///
/// `11-10`: also the first real caller of `Site.UpdateLocale`. One HTTP call, one command, but two
/// `Site` methods and therefore two domain events, both mapped to `SiteSettingsChanged` and both
/// enqueued in this same transaction - `adr/0005`'s "one transaction" is about the state change and
/// its own event committing together, not about a request producing exactly one event, and
/// `SiteCacheInvalidationConsumer` already treats a repeat invalidation of the same key as free
/// (`SiteOfflineAutoReplyUpdated`'s own precedent for two mappers converging on one contract).
/// `Site.UpdateLocale` has nothing to validate the way `WidgetConfig`'s constructor does (`Locale`'s
/// own remarks), so the only guard on this side is the `Enum.TryParse`/`Enum.IsDefined` check below,
/// mirroring `Position`'s.
/// </summary>
public sealed class UpdateWidgetConfigHandler(
    ISiteRepository sites,
    IPermissionChecker permissions,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<WidgetConfigDto>> HandleAsync(UpdateWidgetConfig command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to configure this site's widget.");
        }

        if (!Enum.TryParse<Position>(command.Position, ignoreCase: true, out var position)
            || !Enum.IsDefined(position))
        {
            return ConversationErrors.WidgetConfigInvalidPosition(
                $"'{command.Position}' is not a valid widget position - expected '{nameof(Position.BottomRight)}' or '{nameof(Position.BottomLeft)}'.");
        }

        if (!Enum.TryParse<Locale>(command.Locale, ignoreCase: true, out var locale) || !Enum.IsDefined(locale))
        {
            return ConversationErrors.WidgetConfigInvalidLocale(
                $"'{command.Locale}' is not a valid widget locale - expected '{nameof(Locale.En)}' or '{nameof(Locale.Ru)}'.");
        }

        WidgetConfig config;
        try
        {
            config = new WidgetConfig(command.PrimaryColorHex, position);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.WidgetConfigInvalidColor(ex.Message);
        }

        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(command.SiteId.Value);
        }

        var now = clock.UtcNow;
        site.UpdateWidgetConfig(config, now);
        site.UpdateLocale(locale, now);

        var widgetConfigChanged = site.DomainEvents.OfType<SiteWidgetConfigUpdated>().Single();
        var localeChanged = site.DomainEvents.OfType<SiteLocaleUpdated>().Single();
        outbox.Enqueue(SiteWidgetConfigUpdatedMapper.ToEnvelope(widgetConfigChanged, idGenerator));
        outbox.Enqueue(SiteLocaleUpdatedMapper.ToEnvelope(localeChanged, idGenerator));
        site.ClearDomainEvents();

        await sites.SaveAsync(site, cancellationToken);

        return new WidgetConfigDto(config.PrimaryColorHex, config.Position, locale);
    }
}
