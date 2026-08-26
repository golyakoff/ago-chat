namespace Ago.Chat.Domain;

/// <summary>
/// What a structured message's payload *is*, as a label the producer chooses and AGO Chat never
/// interprets - <c>"invoice.summary"</c>, <c>"survey.rating"</c>, whatever a product needs.
///
/// <para><b>A string, deliberately, and not an enum.</b> An enum would be a closed set AGO Chat
/// owns, so every new kind of structured content any product ever produces would need a member added
/// here - and the first such member would be the moment AGO Chat learned what another product's
/// domain contains. That is the dependency the repository split exists to prevent
/// (<c>reviews/2026-08-26-platform-boundary.md</c>, second pass), and it would arrive through a data
/// model rather than a <c>ProjectReference</c>, which is why nobody would notice.</para>
///
/// <para><b>Shape is checked here; membership is not.</b> This type rejects an empty value, a value
/// that is too long to be a label, and characters that would make it unsafe to put in a JSON key, a
/// URL segment or a log line. It does not, and must never, compare the value against a list of known
/// kinds. A row in <c>messages.content_kind</c> holding some product's vocabulary is *data*; a
/// <c>switch</c> in <c>Ago.Chat.*</c> over that vocabulary would be *knowledge*, and
/// <see cref="MessageOpacityRule"/>'s namesake architecture test exists to keep the two
/// apart.</para>
///
/// <para>The same "validate the shape, refuse to validate the meaning" split
/// <c>Ago.Calendar.Domain.CalendarTimeZone</c> uses for an IANA zone id - there because resolving a
/// zone is Infrastructure's job, here because resolving a *kind* is nobody in this product's
/// job.</para>
/// </summary>
public readonly record struct MessageContentKind
{
    /// <summary>Long enough for a namespaced label a human can read
    /// (<c>"something.something_else"</c>); short enough that it can never be a place to smuggle a
    /// payload past <see cref="MessagePayload"/>'s own ceiling.</summary>
    public const int MaxLength = 64;

    public MessageContentKind(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A message content kind cannot be empty.", nameof(value));
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException(
                $"A message content kind cannot exceed {MaxLength} characters.", nameof(value));
        }

        // Lowercase letters, digits, and the three separators a namespaced label needs. Restrictive
        // on purpose: this value is echoed into JSON, into log lines and (by a channel adapter that
        // has not been written yet) possibly into a URL, and a permissive charset would make every
        // one of those a place somebody has to remember to escape it.
        foreach (var character in trimmed)
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character)
                && character is not ('.' or '_' or '-'))
            {
                throw new ArgumentException(
                    $"'{value}' is not a valid message content kind: only lowercase ASCII letters, digits, " +
                    "'.', '_' and '-' are allowed.",
                    nameof(value));
            }
        }

        Value = trimmed;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
