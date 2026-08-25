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

    /// <summary>`12-02`: when this tenant was created. A second real gap of the same kind
    /// <see cref="Name"/> records above - `12-02`'s own Scope requires a per-site `created_at` in the
    /// platform-owner overview, and `data-model.md`'s `sites` shape had no such column (`conversations`,
    /// `messages` and `attachments` all carry one; the tenant row itself never did). Added as one
    /// additive, reversible column (`Stage12AddSiteCreatedAt`), stated here rather than added quietly.
    ///
    /// <para><b>Nullable on purpose, and never backfilled.</b> Rows that predate the column - the
    /// `1-05`/`create-demo-tenant.sh` demo site, every site registered before this migration - have no
    /// recorded creation time, and this system does not know one. Giving them a `DEFAULT now()` at
    /// migration time would stamp every one of them with the moment the migration ran and present that
    /// as fact, which is exactly the invented figure `CLAUDE.md` forbids. `null` says "not recorded",
    /// which is true; the overview response carries it through as `null` rather than substituting
    /// anything (`OwnerSiteSummaryDto`).</para>
    ///
    /// <para>Optional at construction for the same reason <see cref="Name"/> is: ~60 existing
    /// `new Site(...)` call sites keep compiling. Set from <c>IClock</c> by the one real writer
    /// (`RegisterSiteHandler`), never from <c>DateTimeOffset.UtcNow</c> and never from the database's
    /// own clock (`CLAUDE.md` rule 11).</para></summary>
    public DateTimeOffset? CreatedAt { get; }

    private readonly List<string> _allowedOrigins = [];

    public IReadOnlyList<string> AllowedOrigins => _allowedOrigins;

    // `11-01`: two flat backing fields, not an EF-mapped WidgetConfig struct directly - the same
    // "computed property over a private field EF is pointed at by name" shape AllowedOrigins/
    // _allowedOrigins already established just above, extended to two fields instead of one so each
    // gets its own column (Stage11AddSiteWidgetConfig) without introducing EF's owned-type/complex-type
    // mapping machinery for a single caller (clean-architecture.md's qualifying rule - a second value
    // object needing the same shape is what would justify that, not this one).
    private string? _widgetPrimaryColorHex;
    private Position _widgetPosition = Position.BottomRight;

    public WidgetConfig WidgetConfig => new(_widgetPrimaryColorHex, _widgetPosition);

    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    public Site(
        SiteId id,
        string publicKey,
        IReadOnlyList<string> allowedOrigins,
        string name = "",
        DateTimeOffset? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            throw new ArgumentException("Site public key cannot be empty.", nameof(publicKey));
        }

        Id = id;
        PublicKey = publicKey;
        Name = name;
        CreatedAt = createdAt;
        _allowedOrigins = [.. allowedOrigins];
        // WidgetConfig.Default's own values (null color, BottomRight) - a freshly created Site never
        // renders broken just because nobody has configured a widget appearance yet.
        _widgetPrimaryColorHex = WidgetConfig.Default.PrimaryColorHex;
        _widgetPosition = WidgetConfig.Default.Position;
    }

    // EF Core materialization only (1-04) - every field above is overwritten via reflection
    // immediately after construction; never called by domain code.
    private Site()
    {
    }

    /// <summary>
    /// `11-01`: the first update path <see cref="Site"/> has ever had - create-only since `1-04`.
    /// <paramref name="config"/> arrives already-validated (<see cref="WidgetConfig"/>'s own
    /// constructor threw if the hex color was malformed), so this method's only job is applying it and
    /// recording that it happened - the same "validate once, at construction of the value object"
    /// split <see cref="Conversation.Close"/> draws between its own state-machine guard and the values
    /// it is handed. Raises <see cref="SiteWidgetConfigUpdated"/>, mapped to the
    /// <c>SiteSettingsChanged</c> integration event every other write path already uses this same
    /// domain-event -> integration-event shape for (`Ago.Chat.Application/Mapping`).
    /// </summary>
    public void UpdateWidgetConfig(WidgetConfig config, DateTimeOffset now)
    {
        _widgetPrimaryColorHex = config.PrimaryColorHex;
        _widgetPosition = config.Position;
        _domainEvents.Add(new SiteWidgetConfigUpdated(Id, PublicKey, now));
    }

    public void ClearDomainEvents() => _domainEvents.Clear();
}
