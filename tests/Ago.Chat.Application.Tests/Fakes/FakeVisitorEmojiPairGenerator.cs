using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>A fixed, settable pair rather than a real random pick - the same reason
/// <see cref="FakeWebhookSecretGenerator"/> exposes a mutable property instead of a real CSPRNG. Also
/// counts every call, so a test can assert a visitor's pair was minted exactly once even when the same
/// visitor is looked up by several conversations afterward (`25-56` decision 5).</summary>
public sealed class FakeVisitorEmojiPairGenerator(string creature = "🐔", string food = "🍊")
    : IVisitorEmojiPairGenerator
{
    public string Creature { get; set; } = creature;

    public string Food { get; set; } = food;

    public int CallCount { get; private set; }

    public (string Creature, string Food) NextPair()
    {
        CallCount++;
        return (Creature, Food);
    }
}
