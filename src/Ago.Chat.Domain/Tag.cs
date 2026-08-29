namespace Ago.Chat.Domain;

/// <summary>
/// `18-04`: a per-site label an operator can attach to a <see cref="Conversation"/> so it can be found
/// or counted later - the "cheap half of finding things" `18-01`'s own search complements (a phrase
/// match needs the actual words; a tag needs only that someone once applied one). <b>Labels only</b> -
/// this item's own explicit Out-of-scope excludes any tag with meaning to automation (routing, SLAs),
/// so <see cref="Tag"/> carries nothing beyond a name; there is no field here for anything to attach
/// behaviour to.
///
/// <para>Its own aggregate root, scoped by <see cref="SiteId"/> - not an owned collection on
/// <see cref="Site"/> the way <see cref="CannedResponse"/> is. <see cref="CannedResponse"/> is read and
/// written as a whole per-site list (<c>UpdateCannedResponsesHandler</c> replaces the entire set in one
/// call) and never joined against from another table. A tag is the opposite shape: individual rows get
/// created, renamed and deleted one at a time, and - the part that actually forces a real table -
/// filtering the operator queue and the admin conversation list by tag needs a join
/// (<c>conversation_tags</c>) that a JSON blob column cannot serve without loading every conversation's
/// config into memory to test membership. The same "own transaction boundary, own lifecycle" test
/// <see cref="WebhookEndpoint"/>'s own remarks apply.</para>
/// </summary>
public sealed class Tag
{
    // A bound, not a product requirement - the same "browsable list, not evaluated per message"
    // reasoning `CannedResponse.MaxTitleLength` gives for its own title.
    public const int MaxNameLength = 60;

    public TagId Id { get; }

    public SiteId SiteId { get; }

    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; }

    private Tag(TagId id, SiteId siteId, string name, DateTimeOffset createdAt)
    {
        Id = id;
        SiteId = siteId;
        Name = name;
        CreatedAt = createdAt;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private Tag()
    {
    }

    public static Tag Create(TagId id, SiteId siteId, string name, DateTimeOffset now) =>
        new(id, siteId, Validate(name), now);

    /// <summary>
    /// Renaming, not delete-and-recreate: a tag already applied to conversations keeps every existing
    /// <c>conversation_tags</c> row (the join is by <see cref="TagId"/>, never by name), so a rename is
    /// the only operation that changes what operators see without disturbing which conversations already
    /// carry the label.
    /// </summary>
    public void Rename(string name) => Name = Validate(name);

    private static string Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A tag name cannot be empty.", nameof(name));
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"A tag name cannot exceed {MaxNameLength} characters.", nameof(name));
        }

        return trimmed;
    }
}
