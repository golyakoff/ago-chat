namespace Ago.Chat.Domain;

/// <summary>
/// The tenant. <see cref="PublicKey"/> is not a secret - it identifies a tenant and grants nothing
/// beyond starting a visitor session (api-design.md); anything sensitive requires a signed token.
/// </summary>
public sealed class Site
{
    public SiteId Id { get; }

    public string PublicKey { get; } = string.Empty;

    private readonly List<string> _allowedOrigins = [];

    public IReadOnlyList<string> AllowedOrigins => _allowedOrigins;

    public Site(SiteId id, string publicKey, IReadOnlyList<string> allowedOrigins)
    {
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            throw new ArgumentException("Site public key cannot be empty.", nameof(publicKey));
        }

        Id = id;
        PublicKey = publicKey;
        _allowedOrigins = [.. allowedOrigins];
    }

    // EF Core materialization only (1-04) - every field above is overwritten via reflection
    // immediately after construction; never called by domain code.
    private Site()
    {
    }
}
