namespace Ago.Chat.Domain;

/// <summary>
/// `20-07`: the one piece of central, generic reply-parsing the backlog item calls for - resolving a
/// text-channel reply (a bare number) against the last step's own <see cref="MessageAction"/> list.
/// Pure, no I/O, so it belongs in Domain (`clean-architecture.md`).
///
/// <para><b>Must not special-case per primitive kind - the item's own explicit constraint.</b> This
/// function does not know or ask which <see cref="MessageContentKind"/> produced <paramref
/// name="actions"/>; it only resolves a 1-based index against a list. The caller decides *whether* to
/// call it at all - <see cref="PrimitiveKinds.IsChoiceShaped"/> is that decision, made once, in
/// Application, not inside this function.</para>
///
/// <para><b>1-based index only, no fuzzy matching.</b> `MessageContent`'s own remarks already establish
/// that action labels exist "so that a text renderer can number them" - <see
/// cref="PrimitiveTextRenderer"/> is what numbers them 1..N, and this is its exact inverse. Matching a
/// label's text instead would silently accept a typo as intentional and would behave differently per
/// locale; matching a number is unambiguous in every language this system renders in.</para>
/// </summary>
public static class ChoiceReplyTextResolver
{
    /// <summary>
    /// <see langword="null"/> for anything that is not a bare positive integer within range - out of
    /// range, non-numeric, or an empty action list. The caller (Application) decides what "could not
    /// resolve" means for its own flow; this function only ever answers the question it was asked.
    /// </summary>
    public static string? Resolve(string rawText, IReadOnlyList<MessageAction> actions)
    {
        if (actions.Count == 0)
        {
            return null;
        }

        var trimmed = rawText?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (!int.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            return null;
        }

        return index is >= 1 && index <= actions.Count ? actions[index - 1].Value : null;
    }
}
