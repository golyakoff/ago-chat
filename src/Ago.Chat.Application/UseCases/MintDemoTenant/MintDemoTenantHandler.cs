using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.MintDemoTenant;

/// <summary>
/// `8-07`/`adr/0058`: mints one throwaway tenant - a `Site`, its two roles, an `Operator`, and the
/// Keycloak user that operator resolves to - and hands the caller credentials that work for about a
/// day.
///
/// <para><b>It reuses `10-02`'s bootstrap rather than writing a second one.</b>
/// <see cref="ISiteRegistrationRepository.TryRegisterAsync"/> is the same transaction a real
/// registration goes through, so a demo tenant is structurally an ordinary tenant. That is the whole
/// reason the demo demonstrates anything: a special-cased tenant would prove that special cases work.
/// What differs is exactly one column, <see cref="Site.DemoExpiresAt"/>.</para>
///
/// <para><b>Two guards, not one, and they defend different things.</b> The per-IP limiter bounds one
/// caller's rate. The total cap bounds every caller's effect - a thousand callers each politely minting
/// one tenant passes every rate limit ever written, and is the shape an actual abuse of this endpoint
/// takes. `8-07` requires both, and the cap is read from the database inside the request that acts on
/// it, never from a cache (CLAUDE.md rule 8: a cached count is a cap that can be exceeded by exactly
/// the traffic it exists to stop).</para>
///
/// <para><b>Keycloak first, then Postgres, then a compensating delete - and the order was decided by
/// measurement, not preference.</b> This handler was first written the other way round: generate the
/// subject id here, write the operator row, then create the identity with that id. The half-failure
/// would then have been self-healing - a tenant nobody can log into, removed by the expiry sweeper
/// within a day - rather than an orphaned identity-provider user nothing knows about. Keycloak does not
/// allow it, and does not say so: `POST /admin/realms/{realm}/users` answers <b>201 Created and assigns
/// a different id</b> when the body carries one (`DemoTenantLifecycleTests.KeycloakSilentlyIgnoresACallerChosenUserId`).
/// Shipping that ordering would have written operator rows pointing at identities that do not exist,
/// with nothing anywhere reporting a problem.
///
/// <para>So the identity is created first, its assigned id becomes
/// <c>Operator.ExternalSubjectId</c>, and a failed registration deletes the user it just made. What
/// that leaves is one genuinely uncovered window: a crash between the two writes orphans a Keycloak
/// user with no site, which nothing expires. It is recorded in `adr/0058` rather than hidden, it is
/// bounded by this endpoint's own cap and rate limit, and the compensation covers every failure that is
/// not a process death.</para></para>
///
/// </summary>
public sealed class MintDemoTenantHandler(
    IDemoTenantRepository demoTenants,
    ISiteRegistrationRepository registrations,
    IDemoIdentityProvisioner identities,
    IDemoCredentialGenerator credentials,
    IRateLimiter rateLimiter,
    DemoTenantOptions options,
    DemoTenantRateLimitOptions rateLimitOptions,
    IIdGenerator idGenerator,
    IClock clock)
{
    // `10-02`'s own fixed sets, restated for the same reason RegisterSiteHandler restates them: the
    // seed script and that handler already each hold a copy, so a shared constant would still have to
    // match two independent restatements. A demo operator gets both roles - the point of the demo is
    // to show the console, and half a console is a worse demonstration than none.
    // `18-04`: ConversationNoteWrite/ConversationTag join the Operator set here too, restated for the
    // same reason `RegisterSiteHandler`'s own array is restated rather than shared - found missing
    // 2026-08-29 while landing that item; see that class's own remarks for why this is Operator-level.
    private static readonly string[] OperatorRolePermissions =
        [
            Permission.ConversationRead.Value, Permission.ConversationSend.Value, Permission.ConversationAssign.Value,
            Permission.ConversationNoteWrite.Value, Permission.ConversationTag.Value,
            // `22-05`/`adr/0093`: the calendar day-to-day actions join the Operator set here -
            // the same split calendar/Role.cs itself drew (v1 gave one "Operator" role every
            // permission including configuration; this account-side model already splits
            // Operator/Admin by day-to-day-vs-configuration everywhere else, so the split
            // continues rather than being re-derived: booking actions and lead-card edits are
            // ordinary operator work, calendar:configure joins Admin below instead.
            Permission.BookingConfirm.Value, Permission.BookingReject.Value, Permission.BookingCancel.Value,
            Permission.BookingMarkNoShow.Value, Permission.CustomerRead.Value, Permission.CustomerEdit.Value,
        ];

    // `16-02`: SiteErase/ConversationErase join the Admin set here too, the same restatement this
    // class's own remarks already accept - a demo operator gets the full Admin capability set, "half a
    // console is a worse demonstration than none" applied to erasure as much as anything else.
    // `16-03`: SiteExport joins them, same reasoning.
    private static readonly string[] AdminRolePermissions =
        [
            Permission.SiteConfigure.Value, Permission.SiteManageOperators.Value, Permission.AttachmentDelete.Value,
            Permission.SiteErase.Value, Permission.ConversationErase.Value, Permission.SiteExport.Value,
            Permission.ConversationExport.Value,
            // `22-05`/`adr/0093`: calendar:configure joins the Admin set - the configuration-shaped
            // action, the same category SiteConfigure already occupies here.
            Permission.CalendarConfigure.Value,
        ];

    public async Task<Result<MintedDemoTenant>> HandleAsync(
        MintDemoTenant command, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            // Not a 404 dressed as a feature flag: a deployment that has not turned this on genuinely
            // has no such capability, and saying so is more useful than pretending the route is absent.
            return DemoTenantErrors.Disabled();
        }

        // Rate limit before any database work - the same "a bad caller still costs them a token, never
        // costs us a query" ordering RegisterSiteHandler and CreateAttachmentHandler already use.
        var ipLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"demo-mint:ip:{command.RequestIp}"),
            new RateLimitRule(rateLimitOptions.PerIpCapacity, rateLimitOptions.PerIpRefillPerSecond),
            cancellationToken);
        if (!ipLimit.Allowed)
        {
            return DemoTenantErrors.RateLimited(ipLimit.RetryAfter);
        }

        var now = clock.UtcNow;

        var live = await demoTenants.CountLiveAsync(now, cancellationToken);
        if (live >= options.MaxLiveTenants)
        {
            // Deliberately tells the caller to come back rather than hiding the reason. The cap is a
            // property of the demo, not a fault, and a viewer who is turned away should know it is
            // temporary. It is also the honest signal that somebody is hammering this endpoint.
            return DemoTenantErrors.CapacityReached(options.MaxLiveTenants);
        }

        var expiresAt = now + options.Lifetime;

        var publicKey = $"demo_{idGenerator.NewId(now):N}";
        var siteId = new SiteId(idGenerator.NewId(now));
        // Recognisably temporary in the one field `12-03`'s owner view already renders, so the owner
        // view needs no change to stop a demo tenant reading like a real customer (`8-07`'s Scope).
        // The expiry is in the name as well as the column because a name is what a human reads.
        var siteName = $"Demo tenant — expires {expiresAt:yyyy-MM-dd HH:mm} UTC";

        // A string, because that is what `Operator.ExternalSubjectId` holds (`adr/0022`) and what
        // Keycloak accepts as a caller-chosen user id. Generated through IIdGenerator like every other
        // id in this handler - never Guid.NewGuid() (CLAUDE.md rule 2).
        // Keycloak assigns the id, so the identity has to exist before the operator row that names it -
        // see this class's own remarks for the measurement that forced this order.
        // Random, not derived from the id: a UUIDv7's leading hex is its timestamp, so a username cut
        // from it repeats for every mint in the same minute (IDemoCredentialGenerator's own remarks).
        var username = $"demo-{credentials.NewUsernameSuffix()}";
        var password = credentials.NewPassword();

        var identity = await identities.CreateAsync(username, password, cancellationToken);
        if (identity.IsFailure)
        {
            return Result<MintedDemoTenant>.Failure(identity.Error!.Value);
        }

        var externalSubjectId = identity.Value;
        var operatorId = new OperatorId(idGenerator.NewId(now));

        var site = new Site(
            siteId, publicKey, [options.VisitorOrigin], siteName, createdAt: now, demoExpiresAt: expiresAt);
        // `23-02`: no `displayName`/`email` - this identity was minted, not authenticated, so there is
        // no token and no claims to copy. Left empty rather than inventing one from `MintedDemoTenant`'s
        // own `Username` (`MintDemoTenant`'s own doc comment: "no name, no email and no identity").
        var operatorEntity = new Operator(
            operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: externalSubjectId);

        var registered = await registrations.TryRegisterAsync(
            new SiteRegistration(
                site,
                operatorEntity,
                new RoleSeed(idGenerator.NewId(now), "Operator", OperatorRolePermissions),
                new RoleSeed(idGenerator.NewId(now), "Admin", AdminRolePermissions)),
            cancellationToken);
        if (!registered)
        {
            // The compensation. Without it, every failed registration leaves a Keycloak user that no
            // site points at and no sweeper will ever find - the sweeper works from `sites`, so an
            // identity with no site is invisible to it forever.
            await identities.DeleteAsync(externalSubjectId, cancellationToken);
            return DemoTenantErrors.Unavailable();
        }

        return new MintedDemoTenant(
            username, password, siteName, publicKey,
            VisitorUrl: $"{options.VisitorOrigin.TrimEnd('/')}/?site={publicKey}",
            expiresAt);
    }
}
