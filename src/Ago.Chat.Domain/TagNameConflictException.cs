namespace Ago.Chat.Domain;

/// <summary>
/// Raised by the Postgres-backed <c>ITagRepository</c> when a save collides with the unique
/// `(site_id, lower(name))` index (`TagConfiguration`'s own remarks) - the database's own
/// enforcement of the invariant <c>CreateTagHandler</c>/<c>RenameTagHandler</c> already check
/// optimistically before saving. A genuine race (two operators creating the same name at once) is
/// rare enough that a check-then-act pre-check is the right primary UX (an instant, cheap rejection
/// for the overwhelmingly common case), but the database index is what actually prevents two rows
/// with the same name from ever existing - this exception is the translation of that guarantee back
/// into the same <c>ConversationErrors.TagAlreadyExists</c> vocabulary the pre-check produces, so a
/// caller sees one consistent error regardless of which of the two caught it.
/// </summary>
public sealed class TagNameConflictException(string message) : Exception(message);
