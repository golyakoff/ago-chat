using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.UseCases.SuspendTenantAsOwner;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.LiftSuspensionAsOwner;

/// <summary>
/// `22-08`/`adr/0149` rule 1: "chat declining to renew, plus an immediate event that brings the effect
/// forward" - this handler is that immediate event. Unlike a self-expiry (nobody touches
/// <see cref="Domain.Site.SuspendedUntil"/> and it simply passes, at which point
/// <c>Ago.Chat.Worker.SuspensionLeaseRenewalJob</c> stops finding this site and stops renewing), an
/// explicit lift publishes <see langword="null"/> right away rather than leaving every module to
/// discover it only when its own copy of the lease next lapses, up to <see cref="SuspensionLeaseOptions.LeaseLength"/>
/// later - the whole point of "immediate" in the ADR's own words.
/// </summary>
public sealed class LiftSuspensionAsOwnerHandler(
    ISiteRepository sites,
    IUnitOfWork unitOfWork,
    ISiteSuspensionRecordRepository suspensionRecords,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock)
{
    public const int MaxReasonLength = SuspendTenantAsOwnerHandler.MaxReasonLength;

    public async Task<Result> HandleAsync(LiftSuspensionAsOwner command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return ConversationErrors.TenantSuspensionReasonRequired(
                "A reason is required to lift a suspension - state why this account is being unblocked.");
        }

        if (command.Reason.Trim().Length > MaxReasonLength)
        {
            return ConversationErrors.TenantSuspensionReasonRequired(
                $"A suspension reason cannot exceed {MaxReasonLength} characters.");
        }

        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(command.SiteId.Value);
        }

        var now = clock.UtcNow;

        if (site.SuspendedUntil is not { } currentUntil || currentUntil <= now)
        {
            return ConversationErrors.TenantNotSuspended(command.SiteId.Value);
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        site.LiftSuspension(now);
        site.ClearDomainEvents();

        // `null` - the immediate correction, not a lease instant. See this handler's own class remarks.
        outbox.Enqueue(TenantSuspensionChangedMapper.ToEnvelope(site.Id, null, now, idGenerator));

        await suspensionRecords.RecordAsync(
            new SiteSuspensionRecordToWrite(
                idGenerator.NewId(now), command.SiteId, "Lifted", command.LiftedBy, command.Reason.Trim(),
                SuspendedUntil: null, now),
            cancellationToken);

        await sites.SaveAsync(site, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return Result.Success();
    }
}
