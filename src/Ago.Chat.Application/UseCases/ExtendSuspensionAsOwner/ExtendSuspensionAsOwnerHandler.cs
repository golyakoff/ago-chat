using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.UseCases.SuspendTenantAsOwner;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ExtendSuspensionAsOwner;

/// <summary>
/// `22-08`: pushes an already-suspended site's <see cref="Site.SuspendedUntil"/> further out - the
/// console list screen's own "extend" action. See
/// <see cref="SuspendTenantAsOwnerHandler"/>'s own class remarks for the shared
/// shape (one domain write, one audit record, one lease republish, one transaction) this handler
/// mirrors exactly; the only real difference is which current state is required (already suspended,
/// not "not yet suspended") and what the new instant is computed from (the *current*
/// <see cref="Site.SuspendedUntil"/>, not "now" - <see cref="ExtendSuspensionAsOwner"/>'s own
/// remarks).
/// </summary>
public sealed class ExtendSuspensionAsOwnerHandler(
    ISiteRepository sites,
    IUnitOfWork unitOfWork,
    ISiteSuspensionRecordRepository suspensionRecords,
    IOutboxWriter outbox,
    SuspensionLeaseOptions leaseOptions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public const int MaxReasonLength = SuspendTenantAsOwnerHandler.MaxReasonLength;

    public async Task<Result<DateTimeOffset>> HandleAsync(ExtendSuspensionAsOwner command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return ConversationErrors.TenantSuspensionReasonRequired(
                "A reason is required to extend a suspension - state why this account stays frozen.");
        }

        if (command.Reason.Trim().Length > MaxReasonLength)
        {
            return ConversationErrors.TenantSuspensionReasonRequired(
                $"A suspension reason cannot exceed {MaxReasonLength} characters.");
        }

        if (command.AdditionalMinutes <= 0)
        {
            return ConversationErrors.TenantSuspensionDurationInvalid(
                "An extension must add a positive number of minutes.");
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

        DateTimeOffset extendedUntil;
        try
        {
            extendedUntil = currentUntil.AddMinutes(command.AdditionalMinutes);
        }
        catch (ArgumentOutOfRangeException)
        {
            return ConversationErrors.TenantSuspensionDurationInvalid(
                "That extension is too large to represent as an instant in time.");
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        site.Suspend(extendedUntil, now);
        site.ClearDomainEvents();

        var leaseUntil = now + leaseOptions.LeaseLength;
        outbox.Enqueue(TenantSuspensionChangedMapper.ToEnvelope(site.Id, leaseUntil, now, idGenerator));

        await suspensionRecords.RecordAsync(
            new SiteSuspensionRecordToWrite(
                idGenerator.NewId(now), command.SiteId, "Extended", command.ExtendedBy, command.Reason.Trim(),
                extendedUntil, now),
            cancellationToken);

        await sites.SaveAsync(site, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return extendedUntil;
    }
}
