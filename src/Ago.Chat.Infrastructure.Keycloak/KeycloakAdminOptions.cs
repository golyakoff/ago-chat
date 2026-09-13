using System.ComponentModel.DataAnnotations;

namespace Ago.Chat.Infrastructure.Keycloak;

/// <summary>
/// Bound from <c>Keycloak:Admin:*</c>, validated at startup.
///
/// <para><b><see cref="ClientSecret"/> is the new class of secret `13-01` named and this project had
/// so far avoided holding</b> (`adr/0058` argues why it is worth paying for). Two things bound its
/// blast radius, and both are properties of the realm rather than of this file: the service account
/// behind it holds exactly one role, `realm-management:manage-users` on the `ago-chat` realm, so it
/// can neither read the realm's configuration nor touch any other realm; and it is a realm client, not
/// the `master` realm's admin, which is what `apply-realm-settings.sh` uses from the node and what
/// this deliberately is not.</para>
///
/// <para>It is in `17-03`'s secret inventory. It has no default here and never will - a default would
/// be a committed credential.</para>
/// </summary>
public sealed class KeycloakAdminOptions
{
    public const string SectionName = "Keycloak:Admin";

    /// <summary>The Keycloak root, e.g. the in-cluster Service. No trailing slash is required - the
    /// client trims one.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The realm whose users are managed. Never `master`.</summary>
    [Required]
    public string Realm { get; set; } = "ago-chat";

    /// <summary>The confidential client with the service account. Separate from `ago-console`, which is
    /// public and must stay so.</summary>
    public string ClientId { get; set; } = "ago-demo-provisioner";

    /// <summary>Required only when demo minting is enabled - see
    /// <see cref="ServiceCollectionExtensions.AddKeycloakDemoIdentities"/> for why that is a validation
    /// delegate rather than <c>[Required]</c>.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>`25-73`: the public client id <c>OperatorInviteEmailProvisioner</c> names as the
    /// `client_id` on its own `execute-actions-email` call - Keycloak validates the `redirect_uri` it
    /// is also given against *this* client's own registered valid-redirect-uri patterns, so it must be
    /// the operator console's real client id, not the service-account client above. Defaulted to
    /// `"ago-console"` (matching this repository's own conventional naming for the console's OIDC
    /// client elsewhere) rather than left required - unlike <see cref="ClientSecret"/>, a wrong value
    /// here fails loudly and specifically (Keycloak refuses the call with a client-not-found/invalid
    /// redirect_uri error the console's own invite-list screen surfaces as a send failure), not
    /// silently, so this item's own worker could not verify the real value against a live realm and
    /// flags it for confirmation rather than guessing at a `[Required]` with no safe default.</summary>
    public string ConsoleClientId { get; set; } = "ago-console";

    /// <summary>How long before a cached access token's own expiry to stop using it. Covers clock skew
    /// and the flight time of the request the token is about to be spent on.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "00:05:00")]
    public TimeSpan TokenRefreshSkew { get; set; } = TimeSpan.FromSeconds(30);
}
