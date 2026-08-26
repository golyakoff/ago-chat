namespace Ago.Chat.Api.Auth;

/// <summary>
/// `17-03`/`adr/0067`: the visitor signing key set - <b>one key issues, several keys validate</b>.
///
/// <para>Bound from <c>Auth:VisitorSigningKeys:*</c>. Before this item there was a single
/// <c>Auth:SigningKey</c> and therefore no way to change it that was not a simultaneous mass logout
/// of every visitor on every site: the only key that validated was the only key that signed, so the
/// instant it changed, every outstanding token became a 401. That made rotation a customer-visible
/// incident, which in practice means it never happens, which means the key's effective lifetime is
/// "forever". This type is what removes that.</para>
///
/// <para><b>The active key is the one with no <see cref="VisitorSigningKeyEntry.RetiredAt"/>, and
/// there must be exactly one.</b> Not "the first", not "the newest by id" - a rule that picks a
/// winner out of several candidates is a rule that can pick the wrong one silently. Zero or two
/// active keys is a refusal to start (<see cref="VisitorSigningKeyRing"/>).</para>
///
/// <para><b><see cref="RetirementDelay"/> is configuration, deliberately, and that is the whole
/// point of this type rather than a constant.</b> It is the drain window: how long after a key is
/// retired it still validates tokens signed before the rotation. It must be at least the visitor
/// token's own lifetime, because that is the longest an honest visitor can still be holding a token
/// signed by the outgoing key. That lifetime has already moved once - thirty days
/// (`17-06`/`adr/0034`) to seven (`17-07`+`17-08`/`adr/0048`) - so a literal <c>7</c> compiled into
/// the validation path would be a number that has demonstrably changed, in a place that needs a
/// release to change again.</para>
/// </summary>
public sealed class VisitorSigningKeyOptions
{
    public const string SectionName = "Auth:VisitorSigningKeys";

    /// <summary>
    /// The single-key configuration form this item found in place: one base64 key, no rotation.
    /// Still honoured (see <see cref="VisitorSigningKeyRing.FromConfiguration"/>) so that deploying
    /// this change invalidates nothing, and refused when the key set below is *also* configured -
    /// two config surfaces both claiming to say which key issues is precisely the ambiguity this
    /// type exists to remove.
    /// </summary>
    public const string LegacySingleKeyName = "Auth:SigningKey";

    /// <summary>
    /// Defaults to the visitor token's own lifetime (<see cref="JwtTokenService.VisitorTokenLifetime"/>),
    /// which is the shortest window that cannot evict a visitor holding a legitimately-issued token.
    /// A deployment may set it longer (more slack, a leaked old key stays usable longer) but never
    /// shorter, and <see cref="VisitorSigningKeyRing"/> refuses to start if it is.
    /// </summary>
    public TimeSpan RetirementDelay { get; set; } = JwtTokenService.VisitorTokenLifetime;

    public IList<VisitorSigningKeyEntry> Keys { get; set; } = [];
}

/// <summary>One entry in <see cref="VisitorSigningKeyOptions.Keys"/>.</summary>
public sealed class VisitorSigningKeyEntry
{
    /// <summary>
    /// Written into the JWT header as <c>kid</c>. Diagnostics, not a security control - the
    /// validation path deliberately does not require it to match anything
    /// (<see cref="VisitorSigningKeyRing.ValidationKeys"/> says why). Any stable, non-empty,
    /// non-secret label; a rotation date reads best.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Base64, at least 32 bytes - HMAC-SHA256's key size. <b>A secret.</b> It arrives from
    /// the deployment's own credential store like every other one (`architecture/secrets.md`); it is
    /// never written down in this repository, and neither is any part of it.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// <c>null</c> for the one active key. Set to the instant of the rotation for a key being drained:
    /// it keeps validating until <c>RetiredAt + RetirementDelay</c> and stops on its own after that,
    /// with no second deploy. Once past that point the entry may be deleted from configuration at
    /// leisure - it is already inert.
    /// </summary>
    public DateTimeOffset? RetiredAt { get; set; }
}
