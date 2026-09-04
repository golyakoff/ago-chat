namespace Ago.Chat.Domain;

/// <summary>
/// `20-07`/`adr/0065` decision 2: "site X has a module with key K enabled" - the one row the whole
/// design rests on. A separate aggregate rather than a field on <see cref="Site"/>: it has its own
/// lifecycle (enabled, later perhaps disabled or re-pointed at a new <see cref="EntryPoint"/>), no other
/// use case needs it loaded alongside a <see cref="Site"/>, and keeping it out of that aggregate keeps
/// `SiteConfiguration`'s own already-large surface untouched by this item.
///
/// <para><see cref="TriggerWords"/> is opaque, case-insensitive-compared text this entity stores and
/// <see cref="TriggerCommandMatcher"/> compares - never interpreted, never validated against anything
/// but shape (non-empty, bounded count/length). What a trigger word *means* is entirely the site owner's
/// choice.</para>
///
/// <para><b>`22-02`: <see cref="Credential"/> makes this row's own <see cref="SiteId"/> provable, not
/// merely asserted.</b> Before this item, <c>Ago.Chat.Infrastructure.Modules.HttpModuleGateway</c> sent
/// a module the caller's claimed site id in the request body and nothing else - a module had no way to
/// tell a real chat-originated call from anyone who could reach its entry point and had guessed a site
/// id. This field is what closes that gap: it belongs on the registry row, next to
/// <see cref="EntryPoint"/>, because both are per-(site, module) coordinates the module deployment's own
/// operator configures out of band - Chat never learns what a "calendar" or "faq" is by holding
/// one.</para>
/// </summary>
public sealed class EnabledModule
{
    /// <summary>A site may register at most this many trigger words for one module - a bound against an
    /// unbounded write, the same reasoning <see cref="MessageContent.MaxActions"/> gives for its own
    /// ceiling, not a measured number.</summary>
    public const int MaxTriggerWords = 10;

    /// <summary>Long enough for a slash-command-shaped phrase ("/book-a-table"), short enough that a
    /// list of <see cref="MaxTriggerWords"/> of them cannot become a place to smuggle real content past
    /// this entity's own bound.</summary>
    public const int MaxTriggerWordLength = 64;

    public EnabledModuleId Id { get; }

    public SiteId SiteId { get; }

    public ModuleKey ModuleKey { get; }

    public IReadOnlyList<string> TriggerWords { get; } = [];

    public Uri EntryPoint { get; } = null!;

    /// <summary>`22-02`: proves a call claiming to be for this site actually is - see
    /// <see cref="ModuleCredential"/>'s own remarks for what it is and is not.</summary>
    public ModuleCredential Credential { get; }

    public DateTimeOffset EnabledAt { get; }

    /// <summary>`22-17`: <see langword="true"/> when this row was written by
    /// <c>EnableModuleForSiteAsOwnerHandler</c> rather than the tenant's own
    /// <c>EnableModuleForSiteHandler</c> - the audit distinction the item's own brief requires
    /// ("an owner writing into a tenant's entitlements must be distinguishable... from the tenant
    /// doing it"). Lives on this row rather than in a side table because this row already *is* the
    /// one place "site X has module K enabled" is recorded (this type's own opening remarks); a
    /// second table carrying the identical (SiteId, ModuleKey) key would be a second place the two
    /// facts could drift apart, for a flag with none of <see cref="EnabledModule"/>'s own lifecycle
    /// (it never changes after the row is written - a rotation or an expiry does not change who
    /// originally granted it).</summary>
    public bool GrantedByOwner { get; }

    /// <summary>`22-17`: when this grant stops being honoured, or <see langword="null"/> for a grant
    /// that does not expire. Checked live, in <see cref="Infrastructure.Postgres"/>'s own read-store
    /// query, every time this row is consulted to decide whether a module may act for this site
    /// (rule 8: a write/routing decision never reads a cache) - never by a scheduled sweep that
    /// deletes or disables the row, which would need its own worker, its own failure mode, and its
    /// own test for "the sweep has not run yet". A self-service <c>EnableModuleForSiteHandler</c>
    /// grant is always <see langword="null"/> here: a tenant who paid did not buy a trial.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    public EnabledModule(
        EnabledModuleId id, SiteId siteId, ModuleKey moduleKey, IReadOnlyList<string> triggerWords,
        Uri entryPoint, ModuleCredential credential, DateTimeOffset enabledAt, bool grantedByOwner = false,
        DateTimeOffset? expiresAt = null)
    {
        if (triggerWords.Count == 0)
        {
            throw new ArgumentException("A module needs at least one trigger word.", nameof(triggerWords));
        }

        if (triggerWords.Count > MaxTriggerWords)
        {
            throw new ArgumentException(
                $"A module cannot register more than {MaxTriggerWords} trigger words; got {triggerWords.Count}.",
                nameof(triggerWords));
        }

        foreach (var word in triggerWords)
        {
            if (string.IsNullOrWhiteSpace(word))
            {
                throw new ArgumentException("A trigger word cannot be empty.", nameof(triggerWords));
            }

            if (word.Length > MaxTriggerWordLength)
            {
                throw new ArgumentException(
                    $"A trigger word cannot exceed {MaxTriggerWordLength} characters: '{word}'.", nameof(triggerWords));
            }
        }

        // Two different-cased spellings of the same word registered on one module would make this
        // entity's own trigger list internally ambiguous before it is ever compared against another
        // module's - the same "duplicate values make a reply ambiguous" reasoning MessageContent.Create
        // already applies to MessageAction.Value.
        var distinctByOrdinalIgnoreCase = triggerWords
            .Select(w => w.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (distinctByOrdinalIgnoreCase != triggerWords.Count)
        {
            throw new ArgumentException(
                "Two trigger words on the same module cannot be the same word in different casing.",
                nameof(triggerWords));
        }

        if (!entryPoint.IsAbsoluteUri || (entryPoint.Scheme != Uri.UriSchemeHttp && entryPoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("A module entry point must be an absolute http(s) URI.", nameof(entryPoint));
        }

        // `22-17`: an expiry that has already passed (or that is exactly "now") is not a grant, it is
        // a refusal wearing a grant's shape - refused here, at construction, the same
        // "there is no such thing as a validated-somewhere-else entity" reasoning `Tenant.Register`'s
        // own remarks give on the calendar side for an identical judgement call.
        if (expiresAt is { } expiry && expiry <= enabledAt)
        {
            throw new ArgumentException(
                "A module grant's expiry must be after the moment it was enabled.", nameof(expiresAt));
        }

        Id = id;
        SiteId = siteId;
        ModuleKey = moduleKey;
        TriggerWords = [.. triggerWords.Select(w => w.Trim())];
        EntryPoint = entryPoint;
        Credential = credential;
        EnabledAt = enabledAt;
        GrantedByOwner = grantedByOwner;
        ExpiresAt = expiresAt;
    }

    // EF Core materialization only.
    private EnabledModule()
    {
    }

    /// <summary>`22-11`: the write `RotateModuleCredentialHandler` persists - a new instance with every
    /// other field unchanged, the same "reconstruct rather than mutate" shape this type's own
    /// constructor-only field set already implies (no setters exist to mutate in place). Re-runs this
    /// constructor's own validation, which is harmless here: every other field was already valid on the
    /// instance this was called on.
    ///
    /// <para>`22-17`: <see cref="GrantedByOwner"/> and <see cref="ExpiresAt"/> carry over unchanged - a
    /// credential rotation is not a re-grant, so it must not silently clear an owner grant's own
    /// end date or turn an owner grant back into an ordinary one.</para></summary>
    public EnabledModule WithCredential(ModuleCredential newCredential) =>
        new(Id, SiteId, ModuleKey, TriggerWords, EntryPoint, newCredential, EnabledAt, GrantedByOwner, ExpiresAt);
}
