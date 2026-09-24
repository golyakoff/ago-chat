namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `26-04`/`adr/0179` §5: <see cref="Ago.Chat.Application.Abstractions.IPushSender"/>'s own layering
/// rules, made non-negotiable the identical way <see cref="ChannelPortTests"/> already does for
/// <see cref="Ago.Chat.Application.Abstractions.IInboundChannelAdapter"/>. Cheap now, while there is
/// exactly one adapter, and the only thing that will still be cheap the day a second push provider (or
/// APNs) exists.
/// </summary>
public class PushPortTests
{
    private const string PortNamespace = "Ago.Chat.Application.Abstractions";

    [Fact]
    public void PushPort_LivesInApplicationAbstractions()
    {
        foreach (var typeName in new[] { "IPushSender", "IPushSenderResolver", "PushMessage", "PushSendOutcome" })
        {
            var type = TestAssemblies.Application.Reflection
                .GetTypes()
                .SingleOrDefault(t => t.Name == typeName);

            Assert.True(type is not null, $"{typeName} must exist in Ago.Chat.Application");
            Assert.Equal(PortNamespace, type!.Namespace);
        }
    }

    // No separate "does not know how it is protected" assertion here: it would be a second copy of
    // ChannelPortTests.ChannelPort_DoesNotKnowHowItIsProtected's own assembly-wide check (Ago.Chat.
    // Application must not reference Ago.Platform.Resilience/Polly, full stop) - that test already
    // covers IPushSender the moment it exists in this assembly, and a duplicate would drift from it
    // rather than add coverage.
}
