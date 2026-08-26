using Microsoft.IdentityModel.Tokens;

namespace Ago.Chat.Api.Auth;

/// <summary>
/// `17-03`/`adr/0067`: the seam between "which key signs" and "which keys are accepted", which are
/// one thing in a single-key system and must be two things in a rotatable one.
///
/// <para>An interface rather than a concrete class read straight out of <c>IConfiguration</c> for one
/// reason that is not style: <see cref="ValidationKeys"/> is answered <b>per validation</b>, not once
/// at startup. A retired key leaves the set when its drain window closes, and it does so without a
/// restart - so this cannot be a <c>TokenValidationParameters.IssuerSigningKey</c> captured while the
/// host was starting, which is exactly what it was before this item. `Program.cs` wires it as
/// <c>IssuerSigningKeyResolver</c>, a delegate the JWT handler calls on every token.</para>
///
/// <para>The second reason is testability, and it is the one that made the difference in practice:
/// the behaviour worth proving is "a token minted under the previous key still validates, and one
/// minted under a drained key does not", which is a statement about time passing. Behind this
/// interface that is a fake clock and three lines; against a class that reads configuration and the
/// ambient clock it is not testable at all.</para>
///
/// <para><b>Why this is not an Application port.</b> `clean-architecture.md`'s rule is that an
/// external resource sits behind a port declared in <c>Application/Abstractions</c>. This is not
/// that: no use case issues or validates a token, the types in this signature come from
/// <c>Microsoft.IdentityModel.Tokens</c>, and Application may not see them. Authentication is a host
/// concern in this codebase - the same reasoning that already puts <see cref="JwtTokenService"/> and
/// the schemes here rather than in <c>Ago.Chat.Module</c>. So it is a seam *inside* the host, and it
/// is declared next to its only consumers.</para>
/// </summary>
public interface IVisitorSigningKeyRing
{
    /// <summary>
    /// The one key that signs. Exactly one, always - see
    /// <see cref="VisitorSigningKeyOptions.Keys"/>. Issuing from a set would mean a token whose
    /// signer depends on which host, request or ordering happened to pick it, and a rotation whose
    /// completion nobody could state.
    /// </summary>
    SigningCredentials Signing { get; }

    /// <summary>
    /// Every key a presented token may be signed by, as of now: the active key plus each retired key
    /// still inside its drain window. Evaluated on each call - the set shrinks with time, not with a
    /// deploy.
    /// </summary>
    IReadOnlyList<SecurityKey> ValidationKeys();
}
