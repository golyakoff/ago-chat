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

    /// <summary>How long before a cached access token's own expiry to stop using it. Covers clock skew
    /// and the flight time of the request the token is about to be spent on.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "00:05:00")]
    public TimeSpan TokenRefreshSkew { get; set; } = TimeSpan.FromSeconds(30);
}
