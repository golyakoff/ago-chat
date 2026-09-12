using System.Security.Cryptography;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `25-56`: implements <see cref="IVisitorEmojiPairGenerator"/> with the platform's CSPRNG, the same
/// source <see cref="DemoCredentialGenerator"/> uses.
///
/// <para>In <c>Infrastructure.Postgres</c> despite having nothing to do with Postgres, following
/// <see cref="DemoCredentialGenerator"/>'s own precedent in this same folder rather than creating a
/// dedicated project for one small class - clean-architecture.md's "one project per external
/// technology" is about things that can be swapped; the BCL's random number generator is not one of
/// them.</para>
/// </summary>
public sealed class VisitorEmojiPairGenerator : IVisitorEmojiPairGenerator
{
    public (string Creature, string Food) NextPair() =>
        (Pick(VisitorEmojiDictionary.Creatures), Pick(VisitorEmojiDictionary.Foods));

    private static string Pick(IReadOnlyList<string> values) => values[RandomNumberGenerator.GetInt32(values.Count)];
}
