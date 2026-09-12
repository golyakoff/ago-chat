namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-56`: picks the pair a brand-new <see cref="Domain.Visitor"/> is assigned, once, at first
/// contact. A port for the identical reason <see cref="IWebhookSecretGenerator"/>/
/// <see cref="IDemoCredentialGenerator"/> already are one - the pick is genuinely random (which
/// member of <see cref="Domain.VisitorEmojiDictionary.Creatures"/>/<see cref="Domain.VisitorEmojiDictionary.Foods"/>
/// a caller gets is not determined by anything Application knows), and Application may not reach for
/// <c>RandomNumberGenerator</c> any more than it may reach for <c>Guid.NewGuid()</c> (CLAUDE.md rule 2)
/// - a handler whose output cannot be pinned in a test is a handler whose output cannot be asserted.
///
/// <para>Its own port, not a reuse of any existing generator interface - the identical "different
/// contract, different shape" reasoning <see cref="IDemoCredentialGenerator"/>'s own remarks give for
/// not folding into <see cref="IWebhookSecretGenerator"/>: this one draws from two fixed, shipped lists
/// rather than a cryptographically strong alphabet, and returns two values rather than one.</para>
/// </summary>
public interface IVisitorEmojiPairGenerator
{
    /// <summary>One member of <see cref="Domain.VisitorEmojiDictionary.Creatures"/> and one member of
    /// <see cref="Domain.VisitorEmojiDictionary.Foods"/>, each chosen independently and uniformly -
    /// decision 4 is explicit that repeats across visitors are fine, so nothing here needs to check
    /// what any other visitor already has.</summary>
    (string Creature, string Food) NextPair();
}
