namespace Ago.Chat.Domain;

/// <summary>
/// `14-12`/`docs/conventions/text-commands.md`: Chat's own fixed, closed, product-level command
/// vocabulary - never sourced from a site's own configuration, never module-routed, and structurally
/// kept apart from <see cref="TriggerCommandMatcher"/>'s per-site module-trigger vocabulary so the two
/// can never collide at runtime (that document's own "Two vocabularies, never one parser" section).
/// The collision is instead refused once, at registration time - <c>EnableModuleForSiteHandler</c>
/// checks every candidate trigger word here before it lets a site register it, the same "catch it once,
/// at the boundary" discipline that document names explicitly.
///
/// <para>Exactly one member today. A future Chat-native command adds its own name here - and nowhere
/// else - per that document's own "Adding a new command" section.</para>
/// </summary>
public static class ReservedChatCommands
{
    /// <summary>`14-12`/`adr/0079`: verified channel-identity linking, started from inside the channel
    /// the visitor is already using - see <see cref="LinkIdentityCommandMatcher"/>'s own remarks for the
    /// full syntax and why this is not a <see cref="TriggerCommandMatcher"/> candidate.</summary>
    public const string LinkIdentity = "linkidentity";

    public static readonly IReadOnlyList<string> All = [LinkIdentity];

    /// <summary>Case-insensitive, tolerant of a leading slash on either side - the identical comparison
    /// <see cref="TriggerCommandMatcher"/> already applies, because a reserved word and a trigger word
    /// are compared under the same convention (`text-commands.md`'s "Matching" section states this once
    /// for every command in the codebase).</summary>
    public static bool IsReserved(string word) =>
        All.Any(reserved => string.Equals(
            reserved, word.AsSpan().TrimStart('/').ToString(), StringComparison.OrdinalIgnoreCase));
}
