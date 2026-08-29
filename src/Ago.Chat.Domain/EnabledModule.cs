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

    public DateTimeOffset EnabledAt { get; }

    public EnabledModule(
        EnabledModuleId id, SiteId siteId, ModuleKey moduleKey, IReadOnlyList<string> triggerWords,
        Uri entryPoint, DateTimeOffset enabledAt)
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

        Id = id;
        SiteId = siteId;
        ModuleKey = moduleKey;
        TriggerWords = [.. triggerWords.Select(w => w.Trim())];
        EntryPoint = entryPoint;
        EnabledAt = enabledAt;
    }

    // EF Core materialization only.
    private EnabledModule()
    {
    }
}
