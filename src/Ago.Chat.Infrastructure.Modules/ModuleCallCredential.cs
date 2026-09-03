using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Modules;

/// <summary>
/// `22-02`: mints the per-call proof that a module call is really for the site it claims.
///
/// <para><b>The exact wire shape, hand-synchronized with each module's own validator</b> - the same
/// "no shared package, two hand-kept copies of one agreement" situation <see cref="ModuleWireContract"/>'s
/// own remarks describe for the request/response DTOs, extended to this one extra header:
/// <list type="bullet">
/// <item>Header name: <c>X-Ago-Module-Credential</c>.</item>
/// <item>Value: <c>{base64url(payload JSON)}.{base64url(HMAC-SHA256(secret, UTF8(that same base64url
/// string)))}</c> - the signature covers the exact bytes transmitted in the first segment, never a
/// re-serialization of the JSON, so there is no ambiguity about property order or whitespace to keep in
/// sync between this class and a validator written independently on the other side.</item>
/// <item>Payload: <c>{"siteId":"&lt;guid&gt;","iat":&lt;unix seconds&gt;,"exp":&lt;unix seconds&gt;}</c>.</item>
/// <item><see cref="Ttl"/>: 60 seconds. A module call runs at human conversation pace (`adr/0065`'s own
/// "most steps run at human pace"), so a minute is generous for the request itself while keeping a
/// captured token's replay window short.</item>
/// </list></para>
///
/// <para><b>Not exposed as an <c>Application</c> port.</b> Minting has no external-resource dependency
/// beyond <see cref="IClock"/> (already legitimately visible to Application) and the credential bytes
/// already carried on <see cref="EnabledModuleEndpoint"/> - nothing in Application orchestrates *how*
/// a call proves its site, only *that* <see cref="HttpModuleGateway"/> does it, the same way nothing in
/// Application knows this boundary is JSON-over-HTTP at all. Keeping this class here, one layer below
/// Application, is the direct application of the dependency rule: the mechanism can change (a signed
/// assertion today, mTLS later) without a single Application file noticing.</para>
/// </summary>
internal static class ModuleCallCredential
{
    public const string HeaderName = "X-Ago-Module-Credential";

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Mint(SiteId siteId, ModuleCredential credential, DateTimeOffset now)
    {
        var payload = new Payload(siteId.Value, now.ToUnixTimeSeconds(), now.Add(Ttl).ToUnixTimeSeconds());
        var encodedPayload = Base64Url.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions)));
        var signature = ComputeSignature(encodedPayload, credential);
        return $"{encodedPayload}.{Base64Url.Encode(signature)}";
    }

    private static byte[] ComputeSignature(string encodedPayload, ModuleCredential credential) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(credential.Value), Encoding.UTF8.GetBytes(encodedPayload));

    private sealed record Payload(
        [property: JsonPropertyName("siteId")] Guid SiteId,
        [property: JsonPropertyName("iat")] long Iat,
        [property: JsonPropertyName("exp")] long Exp);
}

/// <summary>RFC 4648 §5 base64url, without padding - .NET's <see cref="Convert"/> only offers the
/// standard alphabet, and this codebase has no other caller that would justify a package for the four
/// lines a hand-rolled version costs (`CLAUDE.md`'s own "say what it replaces and why hand-rolling is
/// worse" rule, applied in the direction of not adding one).</summary>
internal static class Base64Url
{
    public static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        var padding = (4 - (padded.Length % 4)) % 4;
        return Convert.FromBase64String(padded + new string('=', padding));
    }
}
