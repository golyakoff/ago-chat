namespace Ago.Chat.Domain;

/// <summary>
/// `14-12`/`adr/0079`: does a visitor's message text invoke <c>/linkidentity &lt;channel-kind&gt;</c> -
/// pure, no I/O, so it belongs in Domain rather than Application, the identical placement reasoning
/// <see cref="TriggerCommandMatcher"/>'s own remarks give for itself.
///
/// <para><b>Its own type, not a second call site on <see cref="TriggerCommandMatcher"/> - the exact
/// distinction `docs/conventions/text-commands.md` draws.</b> That class answers "does this open a
/// module task", sourced from a site's own <c>IEnabledModuleReadStore</c> configuration; this answers
/// "does this open a link request", a fixed, product-level question with no site configuration involved
/// at all. Reusing that class here would smuggle Chat's own built-in vocabulary through a port meant for
/// per-site data, and would make <see cref="ReservedChatCommands"/>'s own registration-time collision
/// guard pointless - there would be nothing left for a site's trigger word to collide *with*
/// structurally, only a runtime race between two lookups.</para>
///
/// <para><b>Syntax and matching</b> follow `text-commands.md` exactly: the message body's own first
/// whitespace-delimited token, optionally prefixed with <c>/</c>, matched case-insensitively against
/// <see cref="ReservedChatCommands.LinkIdentity"/> - never a substring, never mid-sentence. A second
/// token, if present, is the requested <see cref="ChannelKind"/> by its own CLR member name
/// (case-insensitive) - <c>"telegram"</c>, not a display label, since this is a visitor typing a
/// technical command, not filling in a form.</para>
/// </summary>
public static class LinkIdentityCommandMatcher
{
    /// <summary>
    /// <see cref="LinkIdentityCommandMatch.NotACommand"/> for the overwhelming majority of ordinary
    /// conversation - anything whose first token is not <c>linkidentity</c>/<c>/linkidentity</c>.
    /// <see cref="LinkIdentityCommandMatch.InvalidArgument"/> when the command word matched but the
    /// second token is missing or not a real <see cref="ChannelKind"/> name - still worth a reply (the
    /// visitor clearly meant to invoke the command), just not one this system can act on.
    /// </summary>
    public static LinkIdentityCommandResult Match(string messageBody)
    {
        if (string.IsNullOrWhiteSpace(messageBody))
        {
            return new LinkIdentityCommandResult(LinkIdentityCommandMatch.NotACommand, null);
        }

        var tokens = messageBody.Split(
            [' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return new LinkIdentityCommandResult(LinkIdentityCommandMatch.NotACommand, null);
        }

        var firstToken = tokens[0].AsSpan().TrimStart('/').ToString();
        if (!string.Equals(firstToken, ReservedChatCommands.LinkIdentity, StringComparison.OrdinalIgnoreCase))
        {
            return new LinkIdentityCommandResult(LinkIdentityCommandMatch.NotACommand, null);
        }

        // `Enum.IsDefined` alongside `TryParse`, not `TryParse` alone - `UpdateWidgetConfigHandler`'s
        // own precedent for the same gap: `Enum.TryParse` happily "succeeds" on a numeric string like
        // "4821", parsing it as that raw underlying value cast to the enum even though no member has it.
        if (tokens.Length < 2 || !Enum.TryParse<ChannelKind>(tokens[1], ignoreCase: true, out var kind)
            || !Enum.IsDefined(kind))
        {
            return new LinkIdentityCommandResult(LinkIdentityCommandMatch.InvalidArgument, null);
        }

        return new LinkIdentityCommandResult(LinkIdentityCommandMatch.Matched, kind);
    }
}

public enum LinkIdentityCommandMatch
{
    NotACommand,
    InvalidArgument,
    Matched,
}

public readonly record struct LinkIdentityCommandResult(LinkIdentityCommandMatch Match, ChannelKind? Kind);
