namespace Ago.Chat.Domain;

/// <summary>
/// `20-07`: does a visitor's message text open a module task - pure, no I/O, so it belongs in Domain
/// rather than Application (`clean-architecture.md`'s "pure logic with no I/O belongs in Domain"
/// rule). The caller (Application) is the one that reads a site's enabled modules and their trigger
/// words from <c>IEnabledModuleReadStore</c>; this type only decides whether a candidate list matches.
///
/// <para><b>Exact, case-insensitive, whole-first-token match - deliberately not a fuzzy or partial
/// one.</b> `adr/0065` decision 6 is explicit that there is no intent detection in v1: "a visitor who
/// types 'I'd like to book' gets no special treatment; they use the entry point like a menu." A trigger
/// word is a command, not a phrase to search for - matching only the first whitespace-delimited token
/// is what keeps "book a slot for tomorrow please" from accidentally matching a trigger word that
/// happens to appear mid-sentence, which would be exactly the "smartness" the ADR refuses.</para>
/// </summary>
public static class TriggerCommandMatcher
{
    /// <summary>One site's enabled module and the trigger words that open it - the shape
    /// <c>IEnabledModuleReadStore</c> hands back for a site, reduced to what this pure function needs
    /// and nothing more (no <see cref="EnabledModuleId"/>, no <see cref="Uri"/> - a caller that already
    /// has the full row can always look those up again from the matched <see cref="ModuleKey"/>).</summary>
    public readonly record struct Candidate(ModuleKey ModuleKey, IReadOnlyList<string> TriggerWords);

    /// <summary>
    /// <see langword="null"/> when no candidate's trigger words match the message's first token - the
    /// overwhelming majority of ordinary conversation.
    /// </summary>
    public static ModuleKey? Match(string messageBody, IReadOnlyList<Candidate> candidates)
    {
        if (string.IsNullOrWhiteSpace(messageBody) || candidates.Count == 0)
        {
            return null;
        }

        var firstToken = messageBody.AsSpan().Trim().TrimStart('/');
        var spaceIndex = firstToken.IndexOfAny([' ', '\t', '\n', '\r']);
        if (spaceIndex >= 0)
        {
            firstToken = firstToken[..spaceIndex];
        }

        if (firstToken.IsEmpty)
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            foreach (var word in candidate.TriggerWords)
            {
                // TrimStart('/') on both sides: a trigger word may be registered with or without a
                // leading slash ("/booking" or "booking"), and a visitor may or may not type one -
                // the match is about the command name, not the punctuation convention around it.
                if (firstToken.Equals(word.AsSpan().TrimStart('/'), StringComparison.OrdinalIgnoreCase))
                {
                    return candidate.ModuleKey;
                }
            }
        }

        return null;
    }
}
