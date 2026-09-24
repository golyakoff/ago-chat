using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// One read-model row's latest message to one list row's preview string - the same
/// "one mapping, not one copy per caller" reasoning <see cref="MessageDtoMapper"/>'s own remarks give.
///
/// <para>Extracted in `26-90` because a second caller appeared. <c>GetOperatorQueueHandler</c> owned
/// this rule privately from `26-29`/`26-76`; `26-90` gives the admin site-wide list
/// (<c>GetAllConversationsForSiteHandler</c>) the same <see cref="Contracts.ConversationSummaryDto.LastMessagePreview"/>
/// field, and two handlers deciding independently how long a preview is, whether a module step shows its
/// first line, and whether an attachment caption is safe to render is exactly the "a fourth field would
/// have been a fourth chance for two of them to disagree" failure <see cref="MessageDtoMapper"/> was
/// extracted to stop. The rule itself is unchanged - this file is a move, not a new decision.</para>
///
/// <para><b>Why here and not on <see cref="LatestMessageSummary"/> itself.</b> That record is
/// deliberately "raw, undecided facts" (its own remarks): what the database held, with no rendering
/// policy attached, so an Infrastructure adapter can build one without knowing what a list row looks
/// like. Truncation length and which content kinds preview at all are Application policy, so they live
/// on the Application side of that line.</para>
/// </summary>
public static class LastMessagePreviewMapper
{
    // `26-29`: a list row has room for a few dozen characters, never a full 8000-character
    // MessageBody (MessageBody.MaxLength) - 80 is generous enough that almost no ordinary sentence
    // is cut, small enough that this DTO stays a summary rather than a second copy of the transcript.
    // Every client still ellipsises for its own actual pixel width (26-30's own snippet line); this
    // number only bounds the wire payload, it does not try to guess anyone's layout.
    public const int MaxPreviewLength = 80;

    /// <summary>
    /// `26-29`/`26-76`: <see langword="null"/> when there is no last message at all, and also when there
    /// is one but it references an attachment (<see cref="LatestMessageSummary.AttachmentId"/>) - a
    /// genuine client-supplied placeholder standing in for a file, which the backend cannot tell apart
    /// from a real human caption, so an honest "no preview" beats a guess dressed up as one. A message
    /// carrying structured content (<see cref="LatestMessageSummary.ContentKind"/>, e.g. a module step)
    /// is <b>not</b> the same case as of `26-76`: its <see cref="LatestMessageSummary.Body"/> is not a
    /// guess at all - it is Chat's own <c>PrimitiveTextRenderer</c> output, already composed and already
    /// shipped verbatim to text-only channels - so it previews as its own first line (see below).
    /// Otherwise (plain text, no content kind, no attachment), the existing collapse-to-one-line
    /// truncation applies unchanged.
    /// </summary>
    public static string? ToPreview(LatestMessageSummary? latestMessage)
    {
        if (latestMessage is null || latestMessage.AttachmentId is not null)
        {
            return null;
        }

        if (latestMessage.ContentKind is not null)
        {
            // `26-76`: only the first line - everything after the first `\n` in a module step's rendered
            // Body is channel-rendering chrome (the numbered menu, "Ответьте номером.", a confirmation
            // card's own label/value detail lines), not part of "what was asked". Collapsing it into the
            // first line the way plain prose is collapsed below would read as a run-on menu, not a
            // preview - PrimitiveTextRenderer's own remarks are the reasoning for why the first line
            // alone is always the real prompt or title.
            var firstLine = latestMessage.Body.AsSpan();
            var newlineIndex = firstLine.IndexOfAny('\r', '\n');
            if (newlineIndex >= 0)
            {
                firstLine = firstLine[..newlineIndex];
            }

            return Truncate(firstLine);
        }

        // A list row is one line - collapsing embedded newlines is part of truncation, not an
        // afterthought, since a multi-line message would otherwise break that guarantee regardless of
        // its character count.
        var singleLine = latestMessage.Body.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

        return Truncate(singleLine);
    }

    /// <summary>
    /// `26-76`: the one truncation rule <see cref="ToPreview"/>'s two text-producing branches (collapsed
    /// plain prose, a module step's own first line) both apply identically - extracted only once a
    /// second branch needed the identical "cut to <see cref="MaxPreviewLength"/>, ellipsis on the last
    /// character" behaviour, not introduced ahead of that need.
    /// </summary>
    private static string Truncate(ReadOnlySpan<char> text) =>
        text.Length <= MaxPreviewLength
            ? text.ToString()
            : string.Concat(text[..(MaxPreviewLength - 1)], "…");
}
