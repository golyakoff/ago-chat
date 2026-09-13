using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SuspendTenantAsOwner;

/// <summary>
/// `22-08`/`adr/0166`: the platform owner's own enforcement freeze - never asks the calendar (or any
/// module) anything at write time, rule 8's answer restated for a third crossing after `22-05`/`22-07`:
/// this handler's whole job is making the freeze durable on chat's own side and telling every module
/// it changed. `RequirePlatformOwner` on the route that resolves this handler is the entire
/// access-control story - the identical single-gate shape every other owner surface in this codebase
/// already uses.
///
/// <para><b>Refuses a site that is already suspended, rather than silently re-suspending it.</b> A
/// caller who means "push the deadline further out" wants
/// <see cref="ExtendSuspensionAsOwner.ExtendSuspensionAsOwner"/>, not a second call here - collapsing
/// the two into one command that infers intent from current state would make an accidental double
/// click on "Suspend" silently reset a carefully chosen duration back down, which is exactly the kind
/// of surprise an act with this much weight must not produce.</para>
///
/// <para><b>One domain write, two outbox rows, one transaction - the identical shape
/// `UpdateContactVisibilityHandler` already establishes, widened by the audit record `23-72`'s
/// `RoleChangeRecordRepository` precedent adds.</b> <see cref="Site.Suspend"/> raises exactly one
/// domain event, mapped once (<see cref="TenantSuspensionChangedMapper"/> - the only cross-boundary
/// audience this fact has, <see cref="SiteSuspensionChanged"/>'s own remarks); the suspension record is
/// a second write joining the identical <see cref="IUnitOfWork"/>-scoped transaction
/// <c>ChangeOperatorRoleHandler</c> already uses for its own paired writes, not a second, separate
/// commit racing this one.</para>
///
/// <para><b>The published lease instant is <em>not</em> the owner's own <paramref name="until"/> - it
/// is "now plus <see cref="SuspensionLeaseOptions.LeaseLength"/>".</b> See
/// <see cref="Contracts.TenantSuspensionChanged"/>'s own remarks for why a module must never be told
/// the owner-facing duration: the lease bounds staleness, not "how long the owner wants this account
/// frozen for", and the two are independent numbers by `adr/0166`'s own design.</para>
///
/// <para><b>No upper bound on <see cref="SuspendTenantAsOwner.DurationMinutes"/>.</b> Unlike
/// <c>EnableModuleForSiteAsOwnerHandler.MaxGrantDuration</c> (a real ceiling this codebase's own
/// commercial grants need), a suspension's own length is entirely the platform owner's judgement about
/// a suspected violation - `CLAUDE.md` forbids inventing a ceiling nothing measures, and there is no
/// asymmetry here the way there is for a grant (a longer grant costs the product something; a longer
/// suspension costs the owner's own account, which is the account already suspected of a violation).
/// Only a lower bound (strictly positive) and an overflow guard apply.</para>
/// </summary>
public sealed class SuspendTenantAsOwnerHandler(
    ISiteRepository sites,
    IUnitOfWork unitOfWork,
    ISiteSuspensionRecordRepository suspensionRecords,
    IOutboxWriter outbox,
    SuspensionLeaseOptions leaseOptions,
    IIdGenerator idGenerator,
    IClock clock)
{
    /// <summary>The same free-text-justification bound every other reason field in this codebase
    /// reuses rather than inventing afresh - <c>RevokeModuleForSiteAsOwnerHandler.MaxReasonLength</c>'s
    /// own remarks state the precedent this restates.</summary>
    public const int MaxReasonLength = ConversationNote.MaxBodyLength;

    public async Task<Result<DateTimeOffset>> HandleAsync(SuspendTenantAsOwner command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return ConversationErrors.TenantSuspensionReasonRequired(
                "A reason is required to suspend an account - state why this account is being frozen.");
        }

        if (command.Reason.Trim().Length > MaxReasonLength)
        {
            return ConversationErrors.TenantSuspensionReasonRequired(
                $"A suspension reason cannot exceed {MaxReasonLength} characters.");
        }

        if (command.DurationMinutes <= 0)
        {
            return ConversationErrors.TenantSuspensionDurationInvalid(
                "A suspension's duration must be a positive number of minutes.");
        }

        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(command.SiteId.Value);
        }

        var now = clock.UtcNow;

        if (site.SuspendedUntil is { } currentUntil && currentUntil > now)
        {
            return ConversationErrors.TenantAlreadySuspended(command.SiteId.Value);
        }

        DateTimeOffset ownerUntil;
        try
        {
            ownerUntil = now.AddMinutes(command.DurationMinutes);
        }
        catch (ArgumentOutOfRangeException)
        {
            return ConversationErrors.TenantSuspensionDurationInvalid(
                "That duration is too large to represent as an instant in time.");
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        site.Suspend(ownerUntil, now);
        site.ClearDomainEvents();

        // `adr/0166`: the lease's own instant, computed independently of `ownerUntil` - see this
        // handler's own class remarks.
        var leaseUntil = now + leaseOptions.LeaseLength;
        outbox.Enqueue(TenantSuspensionChangedMapper.ToEnvelope(site.Id, leaseUntil, now, idGenerator));

        await suspensionRecords.RecordAsync(
            new SiteSuspensionRecordToWrite(
                idGenerator.NewId(now), command.SiteId, "Suspended", command.SuspendedBy, command.Reason.Trim(),
                ownerUntil, now),
            cancellationToken);

        await sites.SaveAsync(site, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return ownerUntil;
    }
}
