using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

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
/// </summary>
public sealed class CreateOperatorInviteHandler(
    IOperatorInviteRepository invites,
    IRoleRepository roles,
    IPermissionChecker permissions,
    IOperatorInviteCodeGenerator codeGenerator,
    IOperatorInviteEmailProvisioner emailProvisioner,
    ISiteRepository sites,
    IRateLimiter rateLimiter,
    OperatorInviteOptions options,
    OperatorInviteCreationRateLimitOptions rateLimitOptions,
    IIdGenerator idGenerator,
    IClock clock)
{
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

        var roleId = await roles.GetIdByNameAsync(command.SiteId, command.RoleName, cancellationToken);
        if (roleId is null)
        {
            return ConversationErrors.OperatorInviteInvalidRole(
                $"Site {command.SiteId.Value} has no role named '{command.RoleName}'.");
        }

        var now = clock.UtcNow;
        var id = new OperatorInviteId(idGenerator.NewId(now));

        var code = codeGenerator.NewCode();
        var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(code));

        var invite = OperatorInvite.Generate(
            id, command.SiteId, roleId.Value, codeHash, email, command.RequestedBy, now, options.ValidFor);
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

        return new CreatedOperatorInvite(id.Value, code, invite.ExpiresAt, sendFailed);
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
