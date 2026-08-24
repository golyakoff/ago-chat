namespace Ago.Chat.Application.UseCases.RegisterSite;

/// <summary>
/// `10-02`'s own Scope: "validated as a well-formed origin, matching whatever shape
/// `Site.AllowedOrigins` already expects." `Site.AllowedOrigins` itself never validated its own
/// entries before this item (every existing writer was either this codebase's own seed script or a
/// value already compared verbatim against a browser's `Origin` header - `AuthEndpoints`,
/// `SiteOriginCorsPolicyProvider`) - this is the first *user-supplied* value ever written into it, so
/// it is also the first place that needs to reject a malformed one before it lands.
///
/// An origin (the Fetch/WHATWG definition every browser's own `Origin` header already follows) is
/// exactly `scheme://host[:port]` - no path, no query, no fragment, no trailing slash. Deliberately
/// narrower than <c>Ago.Chat.Application.UseCases.RegisterWebhookEndpoint.WebhookUrlValidator</c>: that
/// validator rejects private/loopback targets because it protects an *outbound* request this system
/// makes to a URL a caller supplied (SSRF); this one only ever gets compared against an *inbound*
/// browser's own `Origin` header (`AuthEndpoints.HandleVisitorSessionAsync`,
/// `HubOriginValidator`) - nothing here ever dials out to it, so there is no SSRF surface to close,
/// and a private-network origin (`http://localhost:5173`, a legitimate local dev frontend - see this
/// codebase's own seed data) must stay allowed.
/// </summary>
public static class OriginValidator
{
    public static string? Validate(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return "Allowed origin cannot be empty.";
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return "Allowed origin must be an absolute URL (scheme://host[:port]).";
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return "Allowed origin must use http:// or https://.";
        }

        // An origin carries no path/query/fragment - PathAndQuery is "/" for a bare
        // "scheme://host[:port]" input and anything else means the caller supplied more than an
        // origin (e.g. a full page URL).
        if (uri.PathAndQuery != "/" || !string.IsNullOrEmpty(uri.Fragment))
        {
            return "Allowed origin must not include a path, query string, or fragment.";
        }

        // Exact match against the input, not a lenient trim-and-compare - GetLeftPart(Authority)
        // never carries a trailing slash, so an input that does (e.g. "https://shop.example.com/")
        // fails here even though its PathAndQuery already normalized to "/" above. Deliberately
        // strict: the comparisons this value is later checked against (AuthEndpoints/
        // HubOriginValidator) are a plain string equality check against a browser's own `Origin`
        // header, which never carries a trailing slash or a default port either - accepting a
        // not-quite-canonical form here would silently store a value that can never match.
        return uri.GetLeftPart(UriPartial.Authority) == origin
            ? null
            : "Allowed origin must be in normalized form, e.g. 'https://shop.example.com' (no trailing slash, no default port).";
    }
}
