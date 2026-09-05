using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Api.Owner;

/// <summary>
/// `24-12`: writes one <c>access_records</c> row for a platform-owner surface, called from the
/// endpoint delegate itself rather than from the handler it wraps - see
/// <see cref="AccessRecordActorKind"/>'s own remarks for why. Every owner endpoint in this codebase
/// (`OwnerSitesEndpoints`, `OwnerModuleEndpoints`, `OwnerChannelIdentityEndpoints`) shares this one
/// method rather than re-reading the `sub` claim three times, so the "which claim names the actor" and
/// "only after a real success" decisions live in exactly one place - the same reason
/// <see cref="PlatformOwnerRealmRole"/> exists at all, applied to an audit fact instead of an
/// authorization one.
///
/// <para><b>Reads <c>sub</c>, never anything RBAC-shaped.</b> The platform owner has no
/// <c>operators</c> row and no domain identifier `Ago.Chat.Application` could name them by
/// (`adr/0032`) - the Keycloak subject claim is the only stable "who" this deployment has for this
/// actor, the identical claim `MeEndpoints`/`SitesEndpoints`/`OperatorInviteEndpoints` already read for
/// their own, unrelated reasons.</para>
/// </summary>
internal static class OwnerAccessRecorder
{
    public static async Task RecordAsync(
        HttpContext httpContext,
        IAccessRecordRepository accessRecords,
        IClock clock,
        IIdGenerator idGenerator,
        AccessRecordKind accessKind,
        SiteId? siteId,
        AccessRecordResourceKind? resourceKind,
        Guid? resourceId,
        CancellationToken cancellationToken)
    {
        var subject = httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (subject is null)
        {
            // Cannot happen through the real pipeline - RequirePlatformOwner already required an
            // authenticated Keycloak token, which always carries `sub` - but a defensive no-op is the
            // honest answer to "the claim this method depends on is somehow absent" rather than
            // throwing out of an endpoint delegate for a fact this method did not itself decide.
            return;
        }

        var now = clock.UtcNow;
        await accessRecords.RecordAsync(
            new AccessRecordToWrite(
                idGenerator.NewId(now), now, accessKind, siteId, AccessRecordActorKind.PlatformOwner, subject,
                resourceKind, resourceId),
            cancellationToken);
    }
}
