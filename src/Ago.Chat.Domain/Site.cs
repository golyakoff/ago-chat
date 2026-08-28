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

    /// <summary>
    /// `8-07`: when this tenant self-destructs, or <see langword="null"/> for an ordinary tenant.
    /// Non-null is the single fact that makes a site a demo tenant - there is deliberately no separate
    /// `is_demo` boolean, because two columns that must agree are two columns that can disagree, and
    /// the expiry is the one the sweeper actually reads.
    ///
    /// <para><b>Why this is on `Site` rather than an `Account` above it.</b> `10-02` rejected an
    /// `Account` aggregate above `Site` deliberately, and nothing here re-opens that: a demo tenant is
    /// an ordinary tenant with a death date, produced by the same bootstrap transaction as a real one
    /// (`RegisterSiteHandler`/`ISiteRegistrationRepository`). Everything downstream - conversations,
    /// operators, RBAC, the widget - treats it as the tenant it is, which is exactly what makes the
    /// demo a real demonstration rather than a special case that proves nothing.</para>
    ///
    /// <para>Optional at construction, the same precedent <see cref="Name"/> and
    /// <see cref="CreatedAt"/> already set for a column added to a type with many existing call
    /// sites.</para>
    /// </summary>
    public DateTimeOffset? DemoExpiresAt { get; }

    /// <summary>A tenant that will be deleted, with everything under it, when its window passes
    /// (`8-07`, `adr/0058`). Never a real customer - `12-03`'s owner view and
    /// `create-demo-tenant.sh`'s seeded `8-05` tenants are both expected to distinguish the two.</summary>
    public bool IsDemo => DemoExpiresAt is not null;

    /// <summary>Whether this demo tenant's window has passed - the predicate the expiry sweeper acts
    /// on, expressed here rather than as a `WHERE` clause alone so a test can state it without a
    /// database. An ordinary tenant is never expired, whatever the time is.</summary>
    public bool HasExpired(DateTimeOffset now) => DemoExpiresAt is { } expiry && now >= expiry;

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

    // `14-04`: three more flat backing fields, the same shape `11-01` chose just above and for the
    // same reason - each gets its own column (Stage14AddSiteOfflineAutoReply) without introducing EF's
    // owned-type machinery. The rules list is the one that could have argued for it, and does not: it
    // is a small, opaque-to-SQL list nothing ever queries into, so it maps through a converter to one
    // column exactly like `14-06`'s messages.actions already does (MessageContentConverters' own
    // remarks on why that is text and not jsonb apply verbatim).
    private bool _offlineAutoReplyEnabled;
    private string _offlineAutoReplyFallback = string.Empty;
    private List<OfflineAutoReplyRule>? _offlineAutoReplyRules;

    /// <summary>`14-04`: this site's offline auto-reply script. Off, with nothing to say, for every
    /// row that predates the feature - see <see cref="OfflineAutoReplySettings.Disabled"/>.</summary>
    public OfflineAutoReplySettings OfflineAutoReply =>
        new(_offlineAutoReplyEnabled, _offlineAutoReplyFallback, _offlineAutoReplyRules ?? []);

    // `11-10`: one more flat backing field, the same shape `11-01`/`14-04` established just above and
    // for the same reason - its own column (Stage11AddSiteWidgetLocale) rather than nesting under
    // WidgetConfig, since the research behind this item found locale is not widget *appearance* and a
    // future consumer that cares about one and not the other needs to tell them apart at the domain
    // level (SiteLocaleUpdated's own remarks). The field initialiser is the default every row that
    // predates this column reads back as - `Locale.En` is also `default(Locale)`, so this is belt and
    // braces, not load-bearing on its own.
    private Locale _locale = Locale.En;

    /// <summary>`11-10`: the language the widget renders in for this tenant. <see cref="Locale.En"/>
    /// for every row that predates this column - see <see cref="Locale"/>'s own remarks on why that is
    /// the safe default rather than an arbitrary one.</summary>
    public Locale Locale => _locale;

    // `13-01`: the seat-entitlement columns this item ships. Two flat properties, not a value object -
    // there is exactly one caller that ever reads either (`OperatorInviteRedemptionRepository`'s own
    // row-locked seat check) and nothing yet writes them to anything but their own default (`13-02`'s
    // job, once a real payment exists to drive it) - the same "one column, one column, no object to
    // bundle them into yet" judgement `clean-architecture.md`'s qualifying rules ask for before adding
    // structure nothing requires.
    /// <summary>`13-01`: the billing tier driving <see cref="SeatLimit"/> - `"free"` for every existing
    /// and newly registered site until `13-02` gives a real payment somewhere to write a different
    /// value from. Not an enum: `13-02`'s own tiers are not decided yet, and a `text` column with no
    /// fixed set of legal values it must not close over is one fewer thing this item has to guess at.
    ///
    /// <para>`13-02`: <c>private set</c>, not the original get-only shape - <see cref="ActivateSubscription"/>
    /// is this property's first real writer. A plain private setter, not a backing field routed through
    /// `SiteConfiguration`'s `Property&lt;T&gt;("_field")` shape the way `WidgetConfig`/`OfflineAutoReply`
    /// need - `13-01`'s own remarks on `SiteConfiguration.cs` already anticipated this: "there is nothing
    /// for a backing field to buy here," because `Tier`/`SeatLimit` are plain scalars with no wrapping
    /// value object to compute, unlike those two. EF Core maps a private setter by convention with no
    /// configuration change needed.</para></summary>
    public string Tier { get; private set; } = "free";

    /// <summary>`13-01`: how many `operators` rows this site may hold at once, enforced only at
    /// `OperatorInviteRedemptionRepository`'s own row-locked check - never here, and never against
    /// `10-02`'s own registration flow, which already has a hard, structural one-operator cap by
    /// construction and needs no check against this column at all (this item's own Out of scope).
    /// Defaults to `1`, matching every site's `Tier` default of `"free"` - a lone self-registered
    /// operator is exactly what the free tier already allows before this item exists.
    ///
    /// <para>`13-02`: <c>private set</c> for the identical reason <see cref="Tier"/>'s own remarks
    /// give.</para></summary>
    public int SeatLimit { get; private set; } = 1;

    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    public Site(
        SiteId id,
        string publicKey,
        IReadOnlyList<string> allowedOrigins,
        string name = "",
        DateTimeOffset? createdAt = null,
        DateTimeOffset? demoExpiresAt = null,
        string tier = "free",
        int seatLimit = 1)
    {
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            throw new ArgumentException("Site public key cannot be empty.", nameof(publicKey));
        }

        Id = id;
        PublicKey = publicKey;
        Name = name;
        CreatedAt = createdAt;
        DemoExpiresAt = demoExpiresAt;
        Tier = tier;
        SeatLimit = seatLimit;
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

    /// <summary>
    /// `14-04`: the second update path <see cref="Site"/> has, and the one a visitor can feel.
    /// <paramref name="settings"/> arrives already-validated (<see cref="OfflineAutoReplySettings"/>'s
    /// own constructor threw if an enabled configuration had nothing to say, or if a rule was empty or
    /// oversized), so this method's only job is applying it and recording that it happened - the same
    /// split <see cref="UpdateWidgetConfig"/> already draws.
    ///
    /// <para>Raises <see cref="SiteOfflineAutoReplyUpdated"/>, which maps to the same
    /// <c>SiteSettingsChanged</c> integration event the widget-config write uses. That is what makes
    /// the console toggle live config rather than a redeploy: the event evicts this site's cached
    /// config on every node, so the very next visitor message reads the new value instead of waiting
    /// out a five-minute TTL (`caching.md`).</para>
    /// </summary>
    public void UpdateOfflineAutoReply(OfflineAutoReplySettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _offlineAutoReplyEnabled = settings.Enabled;
        _offlineAutoReplyFallback = settings.FallbackReply;
        _offlineAutoReplyRules = [.. settings.Rules];
        _domainEvents.Add(new SiteOfflineAutoReplyUpdated(Id, PublicKey, now));
    }

    /// <summary>
    /// `11-10`: <see cref="Site"/>'s third update path. A separate method from
    /// <see cref="UpdateWidgetConfig"/> rather than folding <paramref name="locale"/> into
    /// <see cref="WidgetConfig"/> itself - <see cref="SiteLocaleUpdated"/>'s own remarks explain why
    /// the two stay distinguishable domain events even though both are written by the same console
    /// screen and the same HTTP call (`UpdateWidgetConfigHandler` calls both methods in one request,
    /// same transaction). No validation here: <see cref="Locale"/> is a plain CLR enum with no
    /// invalid representable value the way a hex string or a rule list has, so there is nothing for
    /// this method to guard the way <see cref="UpdateWidgetConfig"/> guards a malformed
    /// <see cref="WidgetConfig"/> - an undefined enum value is exactly what
    /// <c>UpdateWidgetConfigHandler</c>'s own <c>Enum.TryParse</c>/<c>Enum.IsDefined</c> check exists
    /// to keep out before it ever reaches here.
    /// </summary>
    public void UpdateLocale(Locale locale, DateTimeOffset now)
    {
        _locale = locale;
        _domainEvents.Add(new SiteLocaleUpdated(Id, PublicKey, now));
    }

    /// <summary>
    /// `13-02`: `Site`'s fourth update path, and the first real writer of <see cref="Tier"/>/
    /// <see cref="SeatLimit"/> since `13-01` gave them a default. Called from exactly one place -
    /// the webhook applier's own transaction, on a verified `payment.succeeded` event, never from a
    /// checkout-session *creation* (the redirect alone proves nothing - `roadmap.md`'s own wording,
    /// restated in this item's Goal). <paramref name="tier"/>/<paramref name="seatLimit"/> arrive
    /// already resolved (<see cref="SubscriptionTierBands"/>), so - matching <see cref="UpdateWidgetConfig"/>'s
    /// own split - this method's only job is applying them and recording that it happened.
    ///
    /// <para>Raises <see cref="SiteSubscriptionActivated"/>, mapped to the same `SiteSettingsChanged`
    /// integration event every other `Site` write path already converges on - a tier change is exactly
    /// the kind of "this site's settings changed, drop its cache entries" fact
    /// <see cref="SiteOfflineAutoReplyUpdated"/>'s own remarks describe, and nothing about `13-02`'s own
    /// scope needs a fourth distinct cache-invalidation shape.</para>
    /// </summary>
    public void ActivateSubscription(string tier, int seatLimit, DateTimeOffset now)
    {
        Tier = tier;
        SeatLimit = seatLimit;
        _domainEvents.Add(new SiteSubscriptionActivated(Id, PublicKey, tier, seatLimit, now));
    }

    public void ClearDomainEvents() => _domainEvents.Clear();
}
