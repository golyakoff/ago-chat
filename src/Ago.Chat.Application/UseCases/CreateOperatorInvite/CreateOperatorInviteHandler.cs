using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.OperatorRoleSeats;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Application.UseCases.CreateOperatorInvite;

/// <summary>
/// `13-01`: `Permission.SiteManageOperators`'s first real write-path caller -
/// `docs/architecture/authorization.md` already noted "no handler anywhere uses it beyond the admin
/// console's read-only view" before this item. Generation is the simple half of this item (an ordinary
/// single-aggregate insert); the seat-limit enforcement this invite exists to gate lives entirely in
/// `RedeemOperatorInviteHandler`/`OperatorInviteRedemptionRepository` instead, at redemption time, per
/// this item's own Goal ("the entitlement check is enforced at its one real write path - operator
/// invite redemption - not bolted onto `10-02`'s registration flow").
///
/// <para><b>`25-73`: email becomes a required field, and Keycloak's own invite primitive delivers it.</b>
/// This handler now: validates the email's shape; checks the site's own five-a-day rate limit
/// (<see cref="IRateLimiter"/>, keyed per site - the same <see cref="Application.UseCases.RequestSiteExport.RequestSiteExportHandler"/>
/// shape); generates and saves the invite exactly as before; and calls
/// <see cref="IOperatorInviteEmailProvisioner"/> to create-or-find the invitee's Keycloak identity and
/// send the action-token email. A send failure at the SMTP layer is recorded on the invite itself
/// (<see cref="OperatorInvite.MarkSendFailed"/>) and saved again, rather than aborting the whole
/// request - see <see cref="OperatorInviteProvisionOutcome.SendFailed"/>'s own remarks for why "not
/// swallowed" (this item's own point 6) means "reaches the console attached to its own invite row", not
/// "fails the create call that produced it".</para>
///
/// <para><b>Ordering: permission, then rate limit, then Keycloak.</b> The same reasoning
/// <see cref="Application.UseCases.RequestSiteExport.RequestSiteExportHandler"/>'s own remarks give for
/// checking permission before spending a shared per-site budget - an operator with no
/// `SiteManageOperators` permission must never be able to spend a share of the site's own five-a-day
/// invite allowance finding that out. The rate limit runs before ever calling Keycloak so a caller who
/// has already exhausted today's budget costs this deployment no outbound Admin API call at all.</para>
///
/// <para><b>`25-90`: a second, independent email carrying the code as plain text.</b> Keycloak's own
/// `execute-actions-email` template has no parameter through which this codebase could hand it the
/// plaintext code, and the only way to give the template something to render - a Keycloak user
/// attribute - was rejected outright: the code must never sit in Keycloak's own storage
/// (`docs/backlog/25-90-*.md`'s own Scope). <see cref="SendInviteCodeFallbackEmailAsync"/> fires a
/// second, independent <see cref="INotificationMailSender"/> call instead, right after the Keycloak
/// send - deliberately best-effort and never allowed to fail this handler's own request: the Keycloak
/// email is the load-bearing one, this is a redundant second channel, and a failure here is logged and
/// swallowed the same way <c>InactivityWatchdogJob</c>/<c>DownloadThresholdWatchdogJob</c> already treat
/// this exact port's own failures (this handler's one caller-side fault boundary, since
/// <c>NotificationMailSender</c> itself only swallows a *permanent* SMTP refusal, not a
/// transient/connection-stage fault, which it throws).</para>
/// </summary>
public sealed class CreateOperatorInviteHandler(
    IOperatorInviteRepository invites,
    IRoleRepository roles,
    IPermissionChecker permissions,
    OperatorRoleSeatCapacity roleSeatCapacity,
    IOperatorInviteCodeGenerator codeGenerator,
    IOperatorInviteEmailProvisioner emailProvisioner,
    INotificationMailSender mailSender,
    ISiteRepository sites,
    IRateLimiter rateLimiter,
    OperatorInviteOptions options,
    OperatorInviteCreationRateLimitOptions rateLimitOptions,
    IIdGenerator idGenerator,
    IClock clock,
    ILogger<CreateOperatorInviteHandler> logger)
{
    /// <summary>`26-241`: the same bare literal `OperatorInviteRedemptionRepository`/`RoleSeatLimits`
    /// already declare their own copy of - no named-role catalogue exists yet for this codebase to reach
    /// for instead (those types' own remarks). Used only to pick which capacity-refusal error a full role
    /// maps to (Admin -> AdminLimitReached, everything else -> SeatLimitReached), the identical split
    /// `OperatorInviteRedemptionRepository` makes at redeem time.</summary>
    private const string AdminRoleName = "Admin";
    public async Task<Result<CreatedOperatorInvite>> HandleAsync(CreateOperatorInvite command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteManageOperators, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage operators for this site.");
        }

        var email = ValidateEmail(command.Email);
        if (email is null)
        {
            return ConversationErrors.OperatorInviteInvalidEmail("The invite email address is not valid.");
        }

        var limit = await rateLimiter.CheckAsync(
            new RateLimitKey($"operator-invite-create:site:{command.SiteId.Value}"),
            new RateLimitRule(rateLimitOptions.PerSiteCapacity, rateLimitOptions.PerSiteRefillPerSecond),
            cancellationToken);
        if (!limit.Allowed)
        {
            return ConversationErrors.OperatorInviteRateLimited(rateLimitOptions.PerSiteCapacity);
        }

        // `26-241`: resolve every requested role name to its id, refusing the whole invite if any name
        // is unknown. De-duplicated first (StringComparer.Ordinal - role names are the fixed literals
        // "Operator"/"Admin") so a caller repeating a role, or a single-role legacy caller, never sends
        // two identical role lines; the order is preserved so the per-role seat message below names the
        // role the caller listed first when more than one is full.
        var requestedRoleNames = command.RoleNames?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (requestedRoleNames.Length == 0)
        {
            return ConversationErrors.OperatorInviteInvalidRole(
                $"Site {command.SiteId.Value} operator invite must name at least one role.");
        }

        var resolvedRoles = new List<(string Name, Guid Id)>(requestedRoleNames.Length);
        foreach (var roleName in requestedRoleNames)
        {
            var roleId = await roles.GetIdByNameAsync(command.SiteId, roleName, cancellationToken);
            if (roleId is null)
            {
                return ConversationErrors.OperatorInviteInvalidRole(
                    $"Site {command.SiteId.Value} has no role named '{roleName}'.");
            }

            resolvedRoles.Add((roleName, roleId.Value));
        }

        // `26-241`: the create-time seat reservation - for EACH requested role, refuse the whole invite
        // unless there is a free seat counting current holders PLUS already-outstanding unredeemed
        // invites for that role (`OperatorRoleSeatCapacity.CheckForNewInviteAsync`). Runs after role
        // resolution and before persisting anything, so a refused invite leaves no row behind, and it
        // applies to single- and multi-role invites alike - a sent-but-unredeemed invite reserves its
        // seat so an admin cannot over-invite past the limit. The row-locked redeem-time check
        // (`OperatorInviteRedemptionRepository`) stays as defense-in-depth against a concurrent-send race
        // (this class's own remarks on why the create check is deliberately unlocked). The refusal names
        // which role has no slot through the same two 402 codes redemption already uses - "seat limit"
        // for the Operator role, "administrator limit" for Admin.
        foreach (var (roleName, roleId) in resolvedRoles)
        {
            var seatCheck = await roleSeatCapacity.CheckForNewInviteAsync(command.SiteId, roleName, roleId, cancellationToken);
            if (seatCheck.IsAtCapacity)
            {
                return roleName == AdminRoleName
                    ? ConversationErrors.OperatorInviteAdminLimitReached(seatCheck.Limit)
                    : ConversationErrors.OperatorInviteSeatLimitReached(seatCheck.Limit);
            }
        }

        var now = clock.UtcNow;
        var id = new OperatorInviteId(idGenerator.NewId(now));

        var code = codeGenerator.NewCode();
        var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(code));

        var invite = OperatorInvite.Generate(
            id, command.SiteId, [.. resolvedRoles.Select(role => role.Id)], codeHash, email, command.RequestedBy, now, options.ValidFor);
        await invites.SaveAsync(invite, cancellationToken);

        // `25-73`: the site's own configured Locale (`11-10`), not a separate language choice the admin
        // makes - this item's own point 2. `ISiteRepository.GetByIdAsync` is the existing, generic way
        // this codebase already loads a `Site` by id (`UpdateWidgetConfigHandler`'s own precedent) -
        // no new read port for one field nothing else in Application needs yet. Read after the invite
        // is already saved: a locale lookup failing must never lose an already-persisted invite, only
        // the language the email that follows it renders in. `Locale.En` (the default every
        // pre-`11-10` row reads back as) if the site cannot be found at all - unreachable in practice
        // (`OperatorInviteRedemptionRepository`'s own remarks on the identical foreign-key guarantee),
        // but a fallback default costs nothing here, unlike that method's own "thrown, not translated"
        // choice for a check a real write decision depends on.
        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        var locale = site?.Locale ?? Locale.En;

        var redirectUri = $"{options.ConsoleBaseUrl.TrimEnd('/')}/callback?inviteCode={Uri.EscapeDataString(code)}";
        var outcome = await emailProvisioner.ProvisionAndSendAsync(
            new OperatorInviteProvisionRequest(email, code, options.ValidFor, redirectUri, locale), cancellationToken);

        var sendFailed = outcome is OperatorInviteProvisionOutcome.SendFailed;
        if (outcome is OperatorInviteProvisionOutcome.SendFailed failed)
        {
            invite.MarkSendFailed(failed.SmtpErrorCode);
            await invites.SaveAsync(invite, cancellationToken);
        }

        // `25-90`: fired regardless of `outcome` above - this second channel is independent of the
        // Keycloak send, not a fallback triggered only when that one failed (both emails always go out;
        // whichever one a given inbox actually receives is what makes this a redundant channel at all).
        await SendInviteCodeFallbackEmailAsync(email, code, cancellationToken);

        return new CreatedOperatorInvite(id.Value, code, invite.ExpiresAt, sendFailed);
    }

    // `25-90`: this handler's own fault boundary for the second, independent invite-code email - see
    // this class's own doc comment for why a failure here is logged and swallowed rather than thrown or
    // folded into `OperatorInviteProvisionOutcome.SendFailed` above (that field is Keycloak's own send
    // status; this one is a different port entirely, with its own, deliberately silent failure mode).
    // `NotificationMailSender.SendAsync` itself only swallows a *permanent* SMTP refusal (its own doc
    // comment) - a transient/connection-stage fault is thrown, and this is the only place in this call
    // chain positioned to catch it without also swallowing a genuine Keycloak-side fault above.
    private async Task SendInviteCodeFallbackEmailAsync(string email, string code, CancellationToken cancellationToken)
    {
        try
        {
            var redeemUrl = $"{options.ConsoleBaseUrl.TrimEnd('/')}/redeem-invite";
            var (subject, body, htmlBody) = OperatorInviteCodeMailTemplate.Build(code, redeemUrl);
            await mailSender.SendAsync(new NotificationMailMessage(email, subject, body, htmlBody), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Failed to send the invite-code fallback email for an operator invite; Keycloak's own " +
                "action email is unaffected and this invite is still created.");
        }
    }

    // `System.Net.Mail.MailAddress`'s own constructor is the standard BCL shape-validator this codebase
    // has no existing helper for (a grep for one found none) - a private method here, not a new class,
    // matching CLAUDE.md's own "no premature generalisation" guidance: this is the only caller a real
    // email-shape check has anywhere in this codebase today.
    private static string? ValidateEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var trimmed = email.Trim();
        try
        {
            var parsed = new MailAddress(trimmed);
            return parsed.Address;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
