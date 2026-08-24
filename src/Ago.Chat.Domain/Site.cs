namespace Ago.Chat.Domain;

/// <summary>
/// The tenant. <see cref="PublicKey"/> is not a secret - it identifies a tenant and grants nothing
/// beyond starting a visitor session (api-design.md); anything sensitive requires a signed token.
/// </summary>
public sealed class Site
{
    public SiteId Id { get; }

    public string PublicKey { get; } = string.Empty;

    /// <summary>`10-02`: a real gap `10-02-site-and-operator-registration.md`'s own Scope
    /// anticipated ("if implementation finds a real gap, state it here rather than silently adding a
    /// migration this file never scoped") - the backlog item's Goal takes a site display name as a
    /// required registration input, but no such column existed before this stage (`data-model.md`'s
    /// `sites` shape had `id`, `public_key`, `allowed_origins[]`, settings - no name). Added as one
    /// small additive column (`Stage10AddSiteName`) rather than silently discarding the input or
    /// overloading `PublicKey` (which the same item separately requires stay `IIdGenerator`-produced,
    /// not name-derived) - stated here and in `data-model.md` per that item's own instruction, not
    /// added quietly.
    ///
    /// Optional at construction (default `""`), the same "every existing caller keeps compiling"
    /// precedent <see cref="Operator.ExternalSubjectId"/> already established when `5-05` added a
    /// column nothing before it had a value for - the alternative, making every one of this
    /// codebase's ~60 existing `new Site(...)` test call sites pass a fourth argument, was exactly
    /// the unscoped blast radius that precedent exists to avoid.</summary>
    public string Name { get; } = string.Empty;

    private readonly List<string> _allowedOrigins = [];

    public IReadOnlyList<string> AllowedOrigins => _allowedOrigins;

    public Site(SiteId id, string publicKey, IReadOnlyList<string> allowedOrigins, string name = "")
    {
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            throw new ArgumentException("Site public key cannot be empty.", nameof(publicKey));
        }

        Id = id;
        PublicKey = publicKey;
        Name = name;
        _allowedOrigins = [.. allowedOrigins];
    }

    // EF Core materialization only (1-04) - every field above is overwritten via reflection
    // immediately after construction; never called by domain code.
    private Site()
    {
    }
}
