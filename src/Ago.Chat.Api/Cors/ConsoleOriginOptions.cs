using System.ComponentModel.DataAnnotations;

namespace Ago.Chat.Api.Cors;

/// <summary>
/// `5-18`: the origins the **operator console** is served from.
///
/// <para><b>Why this exists at all.</b> A hub connection's `Origin` header is the only enforcement
/// point a WebSocket has - there is no CORS preflight to lean on (`HubOriginValidator`'s own
/// remarks). `5-01` built that check once and pointed both hubs at the *tenant's* `AllowedOrigins`,
/// which is correct for `VisitorHub` and wrong for `OperatorHub`: that list answers "which pages may
/// embed this tenant's widget", and an operator does not connect from a tenant's page. They connect
/// from the console, which is one first-party origin for the whole deployment and has nothing to do
/// with any tenant.</para>
///
/// <para>Conflating the two had a live consequence, not a theoretical one: a tenant whose
/// `AllowedOrigins` did not happen to include the console could never be operated at all, because
/// every operator's connection was aborted immediately after the SignalR handshake. See `5-18`.</para>
///
/// <para><b>Required, and validated at startup.</b> An unset list means every operator connection is
/// refused, which is exactly the silent, product-breaking failure this item exists to remove - so a
/// host with no console origin configured refuses to start instead. The same shape `8-08`'s schema
/// guard and `KeycloakAdminOptions` already use: fail at boot, loudly, rather than at the first
/// operator's first connection, quietly.</para>
/// </summary>
public sealed class ConsoleOriginOptions
{
    public const string SectionName = "Console";

    /// <summary>
    /// Exact origin strings (`scheme://host[:port]`, no trailing slash) - compared with the `Origin`
    /// header verbatim, the same way <c>Site.AllowedOrigins</c> already is. More than one because the
    /// local loop and a deployment differ, and because a console served from a second hostname is an
    /// ordinary thing to want.
    /// </summary>
    [MinLength(1, ErrorMessage = "Console:AllowedOrigins must list at least one origin - the operator console cannot connect otherwise (5-18).")]
    public string[] AllowedOrigins { get; set; } = [];
}
