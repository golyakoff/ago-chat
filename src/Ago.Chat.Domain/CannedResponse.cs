namespace Ago.Chat.Domain;

/// <summary>
/// `18-03`: one prepared answer an operator can insert into the composer instead of typing it again -
/// a short <see cref="Title"/> to browse by, and the <see cref="Body"/> text that gets inserted.
///
/// <para><b>Why this is not <see cref="OfflineAutoReplyRule"/> reused.</b> The backlog item asks this
/// question explicitly, and the answer is: same general shape at rest (a short per-site list of text
/// an admin edits), genuinely different concept, because the *access pattern* differs, not just the
/// field names. <see cref="OfflineAutoReplyRule"/> exists to be matched - the system reads every rule
/// against a visitor's message text, automatically, with no human in the loop and no UI reader at all
/// (<c>SendOfflineAutoReplyHandler</c>). A <see cref="CannedResponse"/> exists to be browsed - an
/// operator reads a list of titles with their own eyes (or ears, or keyboard-driven filter) and picks
/// one; nothing here is ever compared against message text, and <see cref="OfflineAutoReplySettings"/>'s
/// own <c>Match</c> has no equivalent on this type because there is nothing to match. Forcing one
/// shape to serve both would mean either giving a canned response a <c>Keyword</c> it does not use
/// (dead field, misleading name) or giving an auto-reply rule a human-browsable <c>Title</c> that the
/// keyword-matching consumer never reads (same problem, opposite direction). Two small, honest types
/// cost less than one type carrying a field only half its callers use.</para>
///
/// <para><b>Why <see cref="Body"/> is bounded by <see cref="MessageBody"/>'s own limit and not a
/// smaller number the way <see cref="OfflineAutoReplyRule.MaxReplyLength"/> is.</b> An auto-reply is
/// authored once by a tenant and then sent unread by anyone - deliberately kept short, per that type's
/// own remarks. A canned response is inserted into the composer and can be edited before it is sent,
/// so it needs no artificial ceiling below what a message can actually hold; bounding it at
/// <see cref="MessageBody.MaxLength"/> ties the limit to the real invariant ("this becomes a message
/// body") instead of a second guessed number.</para>
///
/// <para>A <c>sealed record</c> class, not a <c>readonly record struct</c> - the same reason
/// <see cref="OfflineAutoReplyRule"/>'s own remarks give for itself, restated here because it applies
/// identically: this value round-trips through <c>System.Text.Json</c> (the EF value converter,
/// <see cref="CannedResponseConverters"/>), and a struct's implicit parameterless constructor defeats
/// that unless annotated - a crack this codebase has already decided, once, not to open.</para>
/// </summary>
public sealed record CannedResponse
{
    public const int MaxTitleLength = 100;

    public const int MaxBodyLength = MessageBody.MaxLength;

    /// <summary>A bound, not a product requirement - the same "exists so one tenant cannot turn a
    /// list an operator has to browse into an unbounded one" reasoning
    /// <see cref="OfflineAutoReplySettings.MaxRules"/> gives for itself. Set higher than that sibling's
    /// 20: a canned-response library is meant to be browsed by a human, not evaluated per message, so
    /// there is no per-message cost pressure pushing it small - the only cost is a picker list growing
    /// past what "browsable" means, which is a much looser ceiling. Revisit when a real tenant hits
    /// it.</summary>
    public const int MaxCount = 50;

    public string Title { get; }

    public string Body { get; }

    public CannedResponse(string title, string body)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Canned response title cannot be empty.", nameof(title));
        }

        var trimmedTitle = title.Trim();
        if (trimmedTitle.Length > MaxTitleLength)
        {
            throw new ArgumentException(
                $"Canned response title cannot exceed {MaxTitleLength} characters.", nameof(title));
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("Canned response text cannot be empty.", nameof(body));
        }

        if (body.Length > MaxBodyLength)
        {
            throw new ArgumentException(
                $"Canned response text cannot exceed {MaxBodyLength} characters.", nameof(body));
        }

        Title = trimmedTitle;
        Body = body;
    }
}
