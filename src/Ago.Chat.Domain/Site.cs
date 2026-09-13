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

    // `16-04`: two more flat backing fields on the same terms - the tenant's processing-notice text and
    // link, each its own column (Stage16AddSiteWidgetNotice), no owned-type mapping introduced for two
    // more callers of the same shape.
    private string? _widgetNoticeText;
    private string? _widgetNoticeUrl;

    // `24-05`: one more flat backing field, the same shape as the pair above - its own column
    // (Stage24AddSiteRequireContactConsent), a plain `bool` (no owned-type machinery) for the same
    // "one more caller of an already-established shape" reason `16-04`'s own comment gives for its two.
    private bool _requireContactConsent;

    // `23-63`: one more flat backing field, the same shape as the pair above - its own column
    // (Stage23AddSiteWidgetAttractAttention), a plain `bool` for the same "one more caller of an
    // already-established shape" reason `24-05`'s own comment gives for itself.
    private bool _attractAttention;

    // `23-64`: three more flat backing fields, the same shape as the pair above - each gets its own
    // column (the migration this item adds), a plain scalar/enum/string for the same "one more
    // caller of an already-established shape" reason `23-63`'s own comment gives for itself.
    private bool _autoOpenEnabled;
    private AutoOpenDelay _autoOpenDelaySeconds = AutoOpenDelay.Seconds30;
    private string? _autoOpenGreetingText;

    // `25-39`: one more flat backing field, the same shape as the pair above - its own column (the
    // migration this item adds), a plain `bool` for the identical "one more caller of an
    // already-established shape" reason `23-63`'s own comment gives for itself.
    private bool _acceptUnverifiedPhone;

    // `23-78`: one more flat backing field, the same shape as the pair above - its own column (this
    // item's one migration), a plain `bool` for the identical "one more caller of an
    // already-established shape" reason `25-39`'s own comment gives for itself. Read by
    // `StartConversationHandler` (through `SiteConfigDto`, never directly from this aggregate on the
    // hot conversation-start path) to seed a brand-new `Conversation`'s own upload grant.
    private bool _allowAttachmentUploadsByDefault;

    public WidgetConfig WidgetConfig =>
        new(_widgetPrimaryColorHex, _widgetPosition, _widgetNoticeText, _widgetNoticeUrl, _requireContactConsent,
            _attractAttention, _autoOpenEnabled, _autoOpenDelaySeconds, _autoOpenGreetingText,
            _acceptUnverifiedPhone, _allowAttachmentUploadsByDefault);

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
    ///
    /// <para>`13-08`: defaults to `2`, not `1` - the author's own decision, matching Jivo's published
    /// free plan: "the free tier is two operators with two months of history." A freshly self-registered
    /// site still has exactly one operator by construction (`10-02`'s own hard cap, unaffected), so this
    /// default is headroom for one invited colleague, not a second automatic seat - `OperatorInvite`
    /// redemption is still the only path that ever fills it. `Stage13RaiseFreeTierSeatLimit` raises every
    /// existing free-tier row still on the old default the same way, so a site that predates this item
    /// gets the same allowance a freshly registered one does, not a two-tier "free" in practice.</para>
    ///
    /// <para>`13-02`: <c>private set</c> for the identical reason <see cref="Tier"/>'s own remarks
    /// give.</para></summary>
    public int SeatLimit { get; private set; } = 2;

    /// <summary>`25-25`: how many `operators` rows this site may hold with the seeded `"Admin"` role at
    /// once - `ago-business` decision `0011`'s own finding, confirmed against the real code while
    /// building `25-18`'s per-role seat message: <see cref="SeatLimit"/> was the only capacity this
    /// aggregate ever tracked, and every seat-holding role - Operator or Administrator alike - was
    /// gated against it identically. This is the second, independent limit `0011` describes.
    ///
    /// <para><b>Not a constructor parameter, unlike <see cref="Tier"/>/<see cref="SeatLimit"/>.</b> A
    /// buyer chooses how many operator seats to purchase, so <see cref="SeatLimit"/> has to arrive from
    /// outside; nobody chooses how many administrators a tier includes - <c>ago-business</c> decision
    /// `0012`'s own grid fixes that number per tier ("Solo" one, "Business" two), so
    /// <see cref="SubscriptionTierBands.ResolveAdminLimit"/> derives it from <see cref="Tier"/> alone,
    /// here and in <see cref="ActivateSubscription"/>. Threading a fourth constructor parameter that
    /// must always agree with a third would only invite the two to drift - exactly the coupling this
    /// item exists to remove for <see cref="SeatLimit"/>'s own sibling, not to reintroduce for this
    /// one.</para>
    ///
    /// <para><c>private set</c>, the same shape <see cref="Tier"/>/<see cref="SeatLimit"/> already
    /// establish for themselves - there is nothing for a backing field to buy here either.</para>
    /// </summary>
    public int AdminLimit { get; private set; } = SubscriptionTierBands.FreeAdminsIncluded;

    /// <summary>`23-05`: how long a `Waiting` conversation may sit with nobody having taken it before
    /// the assignment engine assigns it anyway, capacity ignored - `decisions.md` §2's own words, "two
    /// minutes is the default, not the rule." A plain scalar with a private setter, the identical shape
    /// <see cref="Tier"/>/<see cref="SeatLimit"/> already establish just above and for the same
    /// reason: there is no cross-field invariant here to justify a wrapping value object the way
    /// <see cref="WidgetConfig"/>/<see cref="OfflineAutoReplySettings"/> need one, so nothing routes
    /// through a `Property&lt;T&gt;("_field")` shadow mapping either.
    ///
    /// <para><b>120, not a value every existing row must be migrated to.</b> The database column
    /// carries the same default, so a row written before this column existed reads back `120` without
    /// a backfill - the identical "additive column, database default, no data migration" shape
    /// <see cref="Tier"/>'s own remarks describe for itself (not <see cref="SeatLimit"/>'s: that one
    /// needed `13-08`'s backfill because its default changed after rows already existed; this default
    /// has never changed).</para>
    ///
    /// <para><b>Read by the claimer, never through the site-settings cache.</b> `CLAUDE.md` rule 8:
    /// this is configuration a write decision depends on - <c>SkipLockedAssignmentClaimer</c> and
    /// <c>RedisLockAssignmentClaimer</c> both query this column directly, inside the same transaction
    /// that performs the compare-free claim, through <c>SiteAssignmentPenaltyQuery</c> rather than
    /// loading a whole <see cref="Site"/> aggregate or reading the cached <c>SiteConfigDto</c> - see
    /// that type's own remarks.</para></summary>
    public int AssignmentPenaltySeconds { get; private set; } = 120;

    // `18-03`: a fourth flat-backing-field list, the same shape `14-04`'s _offlineAutoReplyRules
    // established just above - opaque to SQL, read and written as a unit, mapped through a converter
    // to one column (CannedResponseConverters' own remarks restate OfflineAutoReplyConverters' - text,
    // not jsonb, for the identical reason).
    private List<CannedResponse>? _cannedResponses;

    /// <summary>`23-11`/`decisions.md` §5: the account-wide rung this site's contact surfaces obey -
    /// <see cref="ContactVisibility.Visible"/> for every row that predates this column, so an existing
    /// tenant keeps exactly the behaviour it has today (this item's own Done-when: "asserted, because
    /// this must not change the micro case"). An ordinary mapped scalar with a private setter, the
    /// same "no wrapping value object" shape <see cref="Tier"/>/<see cref="SeatLimit"/>/
    /// <see cref="AssignmentPenaltySeconds"/> already establish for themselves - there is no
    /// cross-field invariant here either, only a closed two-member enum a `CHECK` constraint can
    /// enforce directly (<c>SiteConfiguration</c>'s own remarks).</summary>
    public ContactVisibility ContactVisibility { get; private set; } = ContactVisibility.Visible;

    /// <summary>
    /// `22-08`/`adr/0166`: the account-wide enforcement freeze, an ordinary mapped scalar with a
    /// private setter - the same "no wrapping value object" shape <see cref="Tier"/>/
    /// <see cref="ContactVisibility"/> already establish for themselves, since there is no
    /// cross-field invariant here either, only a nullable instant. <see langword="null"/> means "not
    /// suspended", the same "absence is the ordinary case, not a sentinel" reading
    /// <see cref="DemoExpiresAt"/> already gives an optional instant on this same aggregate.
    ///
    /// <para><b>Never read from a cache and never compared anywhere but live, against
    /// <see cref="Ago.Platform.Abstractions.IClock"/>.</b> `CLAUDE.md` rule 8's own words -
    /// "never cache what a write decision depends on" - apply to this column exactly as
    /// <see cref="Application.Abstractions.IEnabledModuleReadStore"/>'s own remarks state for an
    /// entitlement's expiry: every caller that gates on this value
    /// (<c>Api.Auth.AuthEndpoints.HandleVisitorSessionAsync</c>, minting a fresh visitor session;
    /// <c>Application.UseCases.SendMessage.SendOperatorMessageHandler</c>, an operator's own send) reads
    /// it through <see cref="Application.Abstractions.ISiteSuspensionReadStore"/>, a live Dapper read
    /// with no TTL, never through the 5-minute cached <c>SiteConfigDto</c>
    /// <c>GetSiteConfigByPublicKeyHandler</c> already serves the widget handshake from - a suspended
    /// site whose config happens to be cached must still be refused the instant the owner suspends
    /// it, not up to five minutes later.</para>
    ///
    /// <para><b>A suspension nobody extends lifts itself with no manual step</b> - this column is
    /// simply compared against "now" on every read, so its own expiry needs no sweep, the identical
    /// "expiry checked live, never swept by a job" shape `adr/0149` rule 1 already states for the
    /// lease this column's own value feeds across the product boundary
    /// (<see cref="Domain.SiteSuspensionChanged"/>'s own remarks).</para>
    /// </summary>
    public DateTimeOffset? SuspendedUntil { get; private set; }

    /// <summary>`18-03`: this site's prepared-answer library. Empty for every row that predates the
    /// feature - the same "list defaults to nothing rather than throwing" shape
    /// <see cref="OfflineAutoReply"/> already established for its own rules.</summary>
    public IReadOnlyList<CannedResponse> CannedResponses => _cannedResponses ?? [];

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
        int seatLimit = 2)
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
        // `25-25`: derived from `tier`, never a fifth constructor parameter - see AdminLimit's own
        // remarks for why. This also means every existing call site that already passes `tier:` (a
        // test seeding a paid-tier Site directly, never through ActivateSubscription) gets the correct
        // Administrator ceiling for that tier with no change of its own required.
        AdminLimit = SubscriptionTierBands.ResolveAdminLimit(tier);
        _allowedOrigins = [.. allowedOrigins];
        // WidgetConfig.Default's own values (null color, BottomRight, no notice) - a freshly created
        // Site never renders broken, and shows no processing notice, just because nobody has configured
        // a widget appearance yet.
        _widgetPrimaryColorHex = WidgetConfig.Default.PrimaryColorHex;
        _widgetPosition = WidgetConfig.Default.Position;
        _widgetNoticeText = WidgetConfig.Default.NoticeText;
        _widgetNoticeUrl = WidgetConfig.Default.NoticeUrl;
        _requireContactConsent = WidgetConfig.Default.RequireContactConsent;
        _attractAttention = WidgetConfig.Default.AttractAttention;
        _autoOpenEnabled = WidgetConfig.Default.AutoOpenEnabled;
        _autoOpenDelaySeconds = WidgetConfig.Default.AutoOpenDelaySeconds;
        _autoOpenGreetingText = WidgetConfig.Default.AutoOpenGreetingText;
        _acceptUnverifiedPhone = WidgetConfig.Default.AcceptUnverifiedPhone;
        _allowAttachmentUploadsByDefault = WidgetConfig.Default.AllowAttachmentUploadsByDefault;
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
        _widgetNoticeText = config.NoticeText;
        _widgetNoticeUrl = config.NoticeUrl;
        _requireContactConsent = config.RequireContactConsent;
        _attractAttention = config.AttractAttention;
        _autoOpenEnabled = config.AutoOpenEnabled;
        _autoOpenDelaySeconds = config.AutoOpenDelaySeconds;
        _autoOpenGreetingText = config.AutoOpenGreetingText;
        _acceptUnverifiedPhone = config.AcceptUnverifiedPhone;
        _allowAttachmentUploadsByDefault = config.AllowAttachmentUploadsByDefault;
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
    /// `23-05`: the console's own write for <see cref="AssignmentPenaltySeconds"/> -
    /// <c>UpdateAssignmentPenaltyHandler</c> is this method's only caller, gated the same
    /// `site:configure` way every other settings write on this aggregate already is.
    /// <paramref name="seconds"/> arrives pre-validated (positive, the handler's own job matching
    /// <see cref="UpdateWidgetConfig"/>'s split), so this method's only job is applying it and
    /// recording that it happened.
    ///
    /// <para>Raises <see cref="SiteAssignmentPenaltyUpdated"/>, mapped to the same
    /// `SiteSettingsChanged` integration event every other write path on this aggregate converges on -
    /// see that event's own remarks for why this still holds even though the one real consumer that
    /// matters (the assignment claimers) never reads the cache this eviction clears.</para>
    /// </summary>
    public void UpdateAssignmentPenalty(int seconds, DateTimeOffset now)
    {
        if (seconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seconds), seconds, "Assignment penalty must be a positive number of seconds.");
        }

        AssignmentPenaltySeconds = seconds;
        _domainEvents.Add(new SiteAssignmentPenaltyUpdated(Id, PublicKey, now));
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
    ///
    /// <para><b>`25-41`: <paramref name="extraAdministrators"/> - the caller's own currently-correct
    /// figure, never recomputed here.</b> This aggregate has no way to know what a caller's own
    /// <see cref="BillingSubscription.ExtraAdministratorsPurchased"/> currently holds (a different
    /// aggregate, `clean-architecture.md`'s dependency rule), so every call site must pass its own
    /// subscription's current value through unchanged when nothing about the Administrator purchase
    /// itself is what triggered this call (`SeatChangeApplier`, `BillingWebhookApplier`, a renewal
    /// re-applying the same tier) - and pass a real, already-decided new value when it is (this item's
    /// own `AdministratorSlotChangeApplier`, and the full-lapse path's own `0`, `SubscriptionRenewalApplier`'s
    /// own remarks). <b>No longer "never blocked or demoted here"</b> - the paragraph this replaces said
    /// a site left over its Administrator ceiling was allowed to sit there indefinitely, matching
    /// `decisions/0006`'s own seat-count precedent. The author's own decision, 2026-09-10, deliberately
    /// diverges from that precedent for this one ceiling: unlike a worker's own booking history
    /// (`23-88`'s "downgrade destroys nothing"), a role grant has no meaningful frozen state to sit in,
    /// so a site whose <see cref="AdminLimit"/> just dropped below its live Administrator count is
    /// brought back into compliance automatically, not left over-limit. This aggregate still cannot do
    /// that demotion itself (the same cross-aggregate reason it cannot read
    /// <see cref="ExtraAdministratorsPurchased"/>) - every caller of this method compares
    /// <see cref="AdminLimit"/> before and after and, when it dropped, calls
    /// <c>IAdministratorLimitEnforcer.DemoteExcessAdministratorsAsync</c> in the same transaction, the
    /// identical "the aggregate exposes the fact, the caller enforces the cross-aggregate consequence"
    /// split <see cref="Operator.ToggleSeat"/>'s own remarks already draw for seat capacity.</para>
    /// </summary>
    public void ActivateSubscription(string tier, int seatLimit, int extraAdministrators, DateTimeOffset now)
    {
        Tier = tier;
        SeatLimit = seatLimit;
        // `25-25`: re-derived on every activation, the same way the constructor derives it - a seat
        // count changes what the buyer chose to pay for (SeatChangeApplier), a tier changes what a
        // subscription includes (BillingWebhookApplier/SubscriptionRenewalApplier). `25-41`: now also
        // the purchased extra-Administrator count, added rather than replacing the tier-derived
        // baseline - see this method's own remarks above for the automatic-demotion consequence a
        // caller must apply when this sum comes out lower than it went in.
        AdminLimit = SubscriptionTierBands.ResolveAdminLimit(tier) + extraAdministrators;
        _domainEvents.Add(new SiteSubscriptionActivated(Id, PublicKey, tier, seatLimit, now));
    }

    /// <summary>
    /// `18-03`: `Site`'s fifth update path, and the first one that raises no domain event - a
    /// deliberate departure from every method above it, stated here rather than left to look like an
    /// oversight.
    ///
    /// <para><b>Why no <c>SiteCannedResponsesUpdated</c> / <c>SiteSettingsChanged</c>.</b> Every event
    /// <see cref="UpdateWidgetConfig"/>, <see cref="UpdateOfflineAutoReply"/>, <see cref="UpdateLocale"/>
    /// and <see cref="ActivateSubscription"/> raise exists for exactly one real consumer:
    /// <c>SiteCacheInvalidationConsumer</c>, evicting the cached <c>SiteConfigDto</c> the visitor-facing,
    /// per-message hot path reads (<c>caching.md</c>). Canned responses are never in that cache and
    /// never read on that path - the only reader is the console's own settings screen and composer
    /// picker, both operator-authenticated reads that go straight to the database, uncached, the same
    /// deliberate choice <c>GetOfflineAutoReplyHandler</c>'s own remarks make for its sibling admin read
    /// ("a low-frequency admin read, not the per-message path"). An uncached read already sees this
    /// write on its very next call - there is no propagation delay for an event to solve, so raising one
    /// would mean paying a fake fact through the outbox (an eviction for cache keys this change never
    /// touches) to make a form look consistent with its neighbours rather than to tell any consumer
    /// something true. <see cref="Operator.GoOnline"/>/<see cref="Operator.GoOffline"/>/
    /// <see cref="Operator.ToggleSeat"/> already establish, elsewhere in this same codebase, that not
    /// every aggregate mutation needs one - this method follows that precedent rather than
    /// <see cref="Site"/>'s own more recent one, because the reason for the recent ones (a cache with a
    /// consumer) genuinely does not apply here.</para>
    ///
    /// <para>The list-length cap lives here, in the aggregate, rather than in a wrapping value object
    /// the way <see cref="OfflineAutoReplySettings.MaxRules"/> does - `18-03` has no cross-field
    /// invariant like "enabled needs a fallback" to justify a wrapper type with nothing else to hold, so
    /// the collection invariant is guarded at the one place that receives the whole list, matching
    /// <see cref="Ago.Platform.Kernel"/>-wide "validate once, at the boundary that owns the whole value"
    /// discipline without inventing a type nothing else needs.</para>
    /// </summary>
    /// <summary>
    /// `23-11`: `Site`'s sixth update path, and the second to raise one domain event mapped to *two*
    /// integration events - see <see cref="SiteContactVisibilityUpdated"/>'s own remarks for why one
    /// write needs both a chat-internal cache-invalidation fact and a cross-boundary one.
    /// <paramref name="rung"/> arrives already validated (a real, defined <see cref="ContactVisibility"/>
    /// member - `Enum.IsDefined` is the handler's own guard, the same "validate at the Application
    /// boundary, this method just applies it" split every other `Site` write path already draws), so
    /// this method's only job is applying it and recording that it happened.
    /// </summary>
    public void UpdateContactVisibility(ContactVisibility rung, DateTimeOffset now)
    {
        ContactVisibility = rung;
        _domainEvents.Add(new SiteContactVisibilityUpdated(Id, PublicKey, rung, now));
    }

    /// <summary>
    /// `22-08`: sets <see cref="SuspendedUntil"/> to a real instant - the one write path both an
    /// initial suspend and an extension go through (<c>SuspendTenantAsOwnerHandler</c>,
    /// <c>ExtendSuspensionAsOwnerHandler</c>). Whether the caller may call this at all - a site not
    /// already suspended, for a suspend; a site already suspended, for an extension - is an Application
    /// boundary check, the same "validate once, at the Application boundary, this method's only job is
    /// applying it and recording that it happened" split <see cref="UpdateContactVisibility"/>'s own
    /// remarks state for its own rung: this aggregate has no invariant of its own that a second
    /// suspend-while-suspended would break (it would simply overwrite <paramref name="until"/>, which
    /// is exactly what an extension *is*), so there is nothing here for a domain guard to protect.
    /// </summary>
    public void Suspend(DateTimeOffset until, DateTimeOffset now)
    {
        SuspendedUntil = until;
        _domainEvents.Add(new SiteSuspensionChanged(Id, until, now));
    }

    /// <summary>
    /// `22-08`: the reversal, by hand - lifting early rather than letting <see cref="SuspendedUntil"/>
    /// pass on its own. Raises the identical <see cref="SiteSuspensionChanged"/>
    /// <see cref="Suspend"/> does, carrying <see langword="null"/> - the snapshot this aggregate's
    /// whole suspension state ever needs to publish, never a delta, so a caller two states removed from
    /// "currently suspended" reads the identical fact whether it arrived by an explicit lift or (once
    /// `SuspendedUntil` itself has passed) simply stopped mattering.
    /// </summary>
    public void LiftSuspension(DateTimeOffset now)
    {
        SuspendedUntil = null;
        _domainEvents.Add(new SiteSuspensionChanged(Id, null, now));
    }

    /// <summary>
    /// `23-48`: `Site`'s seventh update path, and the first to ever change <see cref="AllowedOrigins"/>
    /// after construction - `10-02`'s registration handler is the only other writer, and it only ever
    /// sets the list once, at creation. Called from exactly one place,
    /// <c>UpdateSiteAllowedOriginsAsOwnerHandler</c> - the author's own decision (`23-48`'s "the
    /// answer") is that nobody else may ever call this, not even the tenant whose site it is.
    ///
    /// <para><paramref name="origins"/> arrives already-validated (each entry checked by
    /// <c>Ago.Chat.Application.UseCases.RegisterSite.OriginValidator</c>, the same validator `10-02`'s
    /// registration handler already uses for the first origin a site is ever given - reused rather
    /// than a second wording for the identical rule, matching this method's own split from
    /// <see cref="UpdateWidgetConfig"/>: validate once, at the Application boundary, this method's
    /// only job is applying it and recording that it happened.</para>
    ///
    /// <para>Raises <see cref="SiteAllowedOriginsUpdated"/>, carrying <b>both</b> the list this write
    /// replaces and the list it leaves behind - see that event's own remarks for why a delta-free
    /// "current value only" shape (every other <see cref="Site"/> event since `11-01`) is not enough
    /// here: the CORS-layer cache this event's own consumer must also evict is keyed by origin string,
    /// not by site, so evicting it correctly needs to know what the removed origins actually
    /// were.</para>
    /// </summary>
    public void UpdateAllowedOrigins(IReadOnlyList<string> origins, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(origins);

        var previous = _allowedOrigins.ToList();
        // In-place mutation, not a reassignment - _allowedOrigins stays `readonly`, matching every
        // other List<T>-backed field on this aggregate (AllowedOrigins' own `[.. allowedOrigins]` in
        // the constructor is the sole exception, since a constructor may still write a readonly field).
        _allowedOrigins.Clear();
        _allowedOrigins.AddRange(origins);
        _domainEvents.Add(new SiteAllowedOriginsUpdated(Id, PublicKey, previous, [.. _allowedOrigins], now));
    }

    public void UpdateCannedResponses(IReadOnlyList<CannedResponse> responses)
    {
        ArgumentNullException.ThrowIfNull(responses);

        if (responses.Count > CannedResponse.MaxCount)
        {
            throw new ArgumentException(
                $"A site cannot have more than {CannedResponse.MaxCount} canned responses.", nameof(responses));
        }

        _cannedResponses = [.. responses];
    }

    public void ClearDomainEvents() => _domainEvents.Clear();
}
