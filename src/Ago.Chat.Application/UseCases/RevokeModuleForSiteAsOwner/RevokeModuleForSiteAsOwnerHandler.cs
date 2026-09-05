using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RevokeModuleForSiteAsOwner;

/// <summary>
/// `22-17`: the platform owner's own revoke - proves the grant is a real entitlement rather than a
/// one-way door. Identical module-first ordering to <c>RevokeModuleForSiteHandler</c> (revoke on the
/// module deployment before deleting Chat's own row, so a call in the gap cannot still succeed), and
/// the identical reason a separate class exists rather than a nullable-<see cref="OperatorId"/> branch
/// on that handler: see <see cref="EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwnerHandler"/>'s
/// own remarks, which apply here unchanged. <see cref="IPermissionChecker"/> is never called -
/// <c>RequirePlatformOwner</c> on the route that resolves this handler is the entire access-control
/// story, the same single-gate shape every other owner surface in this codebase already uses.
///
/// <para><b>`23-13`: revokes a grant the owner made exactly as before - no new ceremony - but refuses
/// a tenant's own self-service purchase (<see cref="EnabledModule.GrantedByOwner"/> <see langword="false"/>)
/// unless the caller states <see cref="RevokeModuleForSiteAsOwner.Force"/> and a real
/// <see cref="RevokeModuleForSiteAsOwner.Reason"/>.</b> That is the asymmetry `flows.md` 5.3 names
/// ("undoing something without seeing it was not yours") made mechanical rather than left to whoever is
/// running the runbook to remember. A narrower rule - owners can only revoke what owners granted - would
/// leave the support scenario this item's own brief opens with (a tenant's own broken registration)
/// without a remedy an owner can apply at all; a wider one - no distinction, ever - is exactly what
/// `22-17` shipped and what this item exists to correct.</para>
///
/// <para><b>The force/reason check runs first, before the module or the module-registration gateway is
/// ever touched</b> - the same "reject the caller's input before touching another system" ordering
/// <see cref="EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwnerHandler"/>'s own <c>ExpiresAt</c>
/// bound check already follows, and deliberately unconditional on
/// <see cref="EnabledModule.GrantedByOwner"/>: a caller who sets <see cref="RevokeModuleForSiteAsOwner.Force"/>
/// with a blank reason is refused before this handler has even loaded the row to find out whether
/// forcing was going to be necessary. The alternative - checking the reason only once
/// <see cref="EnabledModule.GrantedByOwner"/> is known to be <see langword="false"/> - would make the
/// identical request body succeed or fail depending on a fact the caller cannot see from the request,
/// which is a worse shape for a runbook operator to reason about than "state a reason whenever you set
/// the flag, full stop".</para>
///
/// <para><b>The override record is written only when the override was actually exercised</b> - force
/// set *and* the row being revoked was a self-service purchase. Setting force against an owner's own
/// grant is accepted (no new ceremony on that path) but writes nothing: nothing was overridden, so
/// there is nothing to attest to - <see cref="Application.Abstractions.IModuleRevokeOverrideRepository"/>'s
/// own remarks state this for its whole reason to exist.</para>
/// </summary>
public sealed class RevokeModuleForSiteAsOwnerHandler(
    IEnabledModuleRepository modules, IModuleRegistrationGateway registrationGateway,
    IModuleRevokeOverrideRepository overrides, IClock clock, IIdGenerator idGenerator)
{
    /// <summary>An implementer's-call safety rail against an unbounded free-text reason, the same
    /// "not a measured number, only a mistake-catcher" posture
    /// <see cref="EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwnerHandler.MaxGrantDuration"/>'s own
    /// remarks state for its bound (`CLAUDE.md`: "do not invent numbers... a typical production
    /// figure"). <see cref="Domain.ConversationNote.MaxBodyLength"/> is this codebase's own precedent
    /// for "how long is a free-text justification allowed to run" - reused here rather than invented
    /// afresh, since a revoke reason is the same shape of thing (a person typing an explanation) as a
    /// conversation note.</summary>
    public const int MaxReasonLength = ConversationNote.MaxBodyLength;

    public async Task<Result> HandleAsync(RevokeModuleForSiteAsOwner command, CancellationToken cancellationToken)
    {
        // `23-13`'s own "decide, don't default": checked first and unconditionally on Force, before
        // anything else this handler does - see this type's own remarks for why the check does not
        // wait to learn whether GrantedByOwner made forcing necessary.
        if (command.Force)
        {
            if (string.IsNullOrWhiteSpace(command.Reason))
            {
                return ConversationErrors.ModuleRevokeReasonRequired(
                    "A reason is required whenever force is set - state why this override is being used.");
            }

            if (command.Reason.Trim().Length > MaxReasonLength)
            {
                return ConversationErrors.ModuleRevokeReasonRequired(
                    $"A revoke reason cannot exceed {MaxReasonLength} characters.");
            }
        }

        ModuleKey moduleKey;
        ModuleProvisioningSecret provisioningSecret;
        try
        {
            moduleKey = new ModuleKey(command.ModuleKey);
            provisioningSecret = new ModuleProvisioningSecret(command.ProvisioningSecret);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.ModuleInvalid(ex.Message);
        }

        var existing = await modules.GetAsync(command.SiteId, moduleKey, cancellationToken);
        if (existing is null)
        {
            return ConversationErrors.ModuleNotEnabled();
        }

        var overridingAPurchase = !existing.GrantedByOwner;
        if (overridingAPurchase && !command.Force)
        {
            // A refusal a person acting on a support ticket can act on: what this row is, and what to
            // do about it - never merely naming the rule that tripped (`23-13`'s own brief).
            return ConversationErrors.ModuleRevokePurchaseRequiresForce(
                $"Module '{moduleKey.Value}' on this site was purchased by the tenant, not granted by an owner. " +
                "Revoking it requires force=true and a reason describing why.");
        }

        try
        {
            await registrationGateway.RevokeAsync(
                new ModuleRegistrationTarget(moduleKey, command.SiteId, existing.EntryPoint), provisioningSecret,
                cancellationToken);
        }
        catch (ModuleUnreachableException ex)
        {
            return ConversationErrors.ModuleRegistrationFailed(ex.Message);
        }

        await modules.DeleteAsync(existing.Id, cancellationToken);

        if (overridingAPurchase && command.Force)
        {
            var now = clock.UtcNow;
            await overrides.RecordAsync(
                idGenerator.NewId(now), command.SiteId, moduleKey.Value, command.RevokedBy, command.Reason!.Trim(),
                now, cancellationToken);
        }

        return Result.Success();
    }
}
