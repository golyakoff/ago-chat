namespace Ago.Chat.Domain;

/// <summary>
/// `24-13`: which unit an erasure receipt (<c>erasure_records</c>) is proof of - domain vocabulary,
/// the same placement reasoning <see cref="ExportStatus"/>'s own remarks give for its enum: both
/// <c>Ago.Chat.Application</c> (the request handlers that mint a record) and <c>Ago.Chat.Worker</c>
/// (the jobs that complete or fail one) need to agree on this word, so it cannot live in either alone.
///
/// <para>Deliberately just these two, matching the two erasure endpoints that exist
/// (<c>ConversationsEndpoints.cs</c>'s erase route, <c>SitesEndpoints.cs</c>'s own) - not a value per
/// future erasure kind that does not exist yet (`24-04`'s hypothetical per-operator erasure would add
/// a third member when and if it ships, not before).</para>
/// </summary>
public enum ErasureScope
{
    Conversation,
    Site,
}
