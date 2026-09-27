using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.CreateOperatorInvite;

/// <summary><paramref name="RoleNames"/> is the set of site-local role names the invitee will hold once
/// redeemed (one or more of `"Operator"`/`"Admin"`) - names, not `roles.id`s, because the caller (an
/// admin filling in a console form, or a curl call per this item's own "no UI" scope) has no reason to
/// know the ids `5-08`'s seed transaction happened to generate for either row on this particular site.
///
/// <para>`26-241`: a <em>set</em>, not a single role - an admin can now invite a colleague to more than
/// one role at once (Operator + Admin in one invite, redeemed into both). A one-element list is exactly
/// the pre-`26-241` single-role behaviour, so the endpoint's own request DTO keeps accepting the legacy
/// single `RoleName` and maps it to a one-element list here (`OperatorInviteEndpoints`' own remarks) -
/// the wire contract stays backward-compatible while the command carries the additive shape.</para>
///
/// <para>`25-73`: <paramref name="Email"/> is now required - refused by
/// <see cref="CreateOperatorInviteHandler"/>, not merely hidden by the console's own form, since
/// Keycloak's own admin-created-user + `execute-actions-email` primitive is what actually delivers
/// this invite now, and that primitive needs an address to create a user for.</para></summary>
public sealed record CreateOperatorInvite(OperatorId RequestedBy, SiteId SiteId, IReadOnlyList<string> RoleNames, string Email);

/// <summary><see cref="Code"/> is the plaintext value, present in this response only - the same
/// "shown exactly once" shape `RegisterWebhookEndpointHandler`'s own `RegisteredWebhookEndpoint`
/// establishes for a different bearer secret in this codebase.
///
/// <para>`25-73`: <see cref="SendFailed"/> - <see langword="true"/> when Keycloak's own realm relay
/// failed to deliver this invite's email at the SMTP layer (never thrown for that case - this item's
/// own point 6, "not swallowed"). The invite still exists either way; the console's own invite-list
/// screen is where the failure's own SMTP error code is shown, not this creation response, so an admin
/// who is not looking at that screen right now still gets an honest "sent" flag on the response their
/// own click produced.</para></summary>
public sealed record CreatedOperatorInvite(Guid OperatorInviteId, string Code, DateTimeOffset ExpiresAt, bool SendFailed);
