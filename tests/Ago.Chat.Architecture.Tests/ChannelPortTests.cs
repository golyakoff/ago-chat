using System.Reflection;
using NetArchTest.Rules;

namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `14-01`'s own Done-when, made non-negotiable: the inbound-channel seam lives where
/// clean-architecture.md says a product-specific port lives, and no channel provider's own vocabulary
/// is allowed above the Infrastructure boundary.
///
/// <para>These rules are cheap now, when there are zero adapters, and are the only thing that will
/// still be cheap in `14-05` when there are four. The failure they exist to catch is the ordinary
/// one: an adapter author needs "just one field" from a provider's payload and threads a
/// vendor-shaped type up through the port because it is the shortest path.</para>
/// </summary>
public class ChannelPortTests
{
    private const string PortNamespace = "Ago.Chat.Application.Abstractions";

    /// <summary>
    /// Product-specific, and therefore in the product's own Application layer rather than
    /// <c>Ago.Platform.Abstractions</c>. clean-architecture.md's platform test - "can it be described
    /// without naming chat, visitors, or operators?" - fails on this port immediately: its whole
    /// purpose is to route a message to an AGO Chat <c>Visitor</c>. This is also the contrast
    /// `14-01`'s Out of scope asks to be recorded once both exist: AGO Calendar's `20-05`
    /// <c>ISmsSender</c> is outbound-only, fixed-template and genuinely platform-shaped; this one is
    /// bidirectional, arbitrary-conversation and product-shaped. Neither should ever implement the
    /// other.
    /// </summary>
    [Fact]
    public void InboundChannelPort_LivesInApplicationAbstractions()
    {
        foreach (var typeName in new[]
                 {
                     "IInboundChannelAdapter", "IInboundChannelAdapterRegistry", "IChannelIdentityRepository",
                     "OutboundChannelMessage", "ChannelSendOutcome",
                 })
        {
            var type = TestAssemblies.Application.Reflection
                .GetTypes()
                .SingleOrDefault(t => t.Name == typeName);

            Assert.True(type is not null, $"{typeName} must exist in Ago.Chat.Application");
            Assert.Equal(PortNamespace, type!.Namespace);
        }
    }

    /// <summary>
    /// The port must be describable, and implementable, without naming a provider. A MAX DTO, a
    /// Telegram <c>Update</c>, a carrier's delivery-receipt type - none of them may appear in Domain,
    /// Application or Contracts, in a member signature or anywhere else. Checked by name fragment
    /// because the offending types do not exist yet: this rule's whole job is to be already in place
    /// when `14-02` writes the first one.
    /// </summary>
    [Fact]
    public void NoProviderVocabulary_AppearsAboveInfrastructure()
    {
        string[] providerWords = ["Max", "Telegram", "WhatsApp", "Twilio", "Viber", "Vk", "Smpp"];

        foreach (var assembly in new[]
                 {
                     TestAssemblies.Domain, TestAssemblies.Application, TestAssemblies.Contracts,
                 })
        {
            var offenders = assembly.Reflection.GetTypes()
                .Where(type => !IsChannelKindItself(type))
                .Where(type => providerWords.Any(word =>
                    type.Name.StartsWith(word, StringComparison.Ordinal)
                    || type.Name.Contains(word + "Message", StringComparison.Ordinal)
                    || type.Name.Contains(word + "Payload", StringComparison.Ordinal)
                    || type.Name.Contains(word + "Update", StringComparison.Ordinal)
                    || type.Name.Contains(word + "Dto", StringComparison.Ordinal)))
                .Select(type => type.FullName)
                .ToList();

            Assert.True(offenders.Count == 0,
                $"{assembly.Name} names a channel provider directly: {string.Join(", ", offenders)}. "
                + "A provider's own vocabulary belongs below the Infrastructure boundary (adr/0006, adr/0055).");
        }
    }

    /// <summary>
    /// <c>ChannelKind</c> is the one deliberate exception to the rule above and is worth its own
    /// assertion rather than a silent skip: it names every provider, and that is exactly what it is
    /// for - a closed discriminator the Domain owns, holding no provider's data shape whatsoever. If
    /// it ever grows a member that is not a plain enum value, this stops being a safe exception.
    ///
    /// <para><b>Unlike every other test in this change, this one was never shown to fail.</b> Both
    /// assertions can only go red if <c>ChannelKind</c> stops being an enum, and no edit does that
    /// without breaking every call site, so the mutation run could not produce a build to test. It is
    /// kept rather than deleted because the change it guards is real and does compile - a "smart
    /// enum" struct with static readonly members and an implicit conversion would satisfy every call
    /// site here - but it is weaker evidence than the tests around it, and saying so is cheaper than
    /// someone later assuming it was proved.</para>
    /// </summary>
    [Fact]
    public void ChannelKind_IsAPlainEnum_AndTheOnlyPlaceProvidersAreNamed()
    {
        var channelKind = TestAssemblies.Domain.Reflection.GetTypes().Single(t => t.Name == "ChannelKind");

        Assert.True(channelKind.IsEnum, "ChannelKind must stay a plain enum - see this test's remarks");
        Assert.Empty(channelKind.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    }

    /// <summary>
    /// CLAUDE.md rules 6 and 11, enforced at the one boundary where an external clock could enter the
    /// system. Every channel provider stamps its deliveries, and sorting by that stamp is the obvious
    /// wrong thing to do; the cheapest defence is to give the inbound command no place to put one.
    /// A later item that genuinely needs the provider's "sent at" for display should read `adr/0055`
    /// and change this test deliberately, which is the entire point of it failing.
    /// </summary>
    [Fact]
    public void ReceiveChannelMessage_CarriesNoTimestamp()
    {
        var command = TestAssemblies.Application.Reflection
            .GetTypes().Single(t => t.Name == "ReceiveChannelMessage");

        var timestamps = command.GetProperties()
            .Where(p => p.PropertyType == typeof(DateTimeOffset) || p.PropertyType == typeof(DateTimeOffset?))
            .Select(p => p.Name)
            .ToList();

        Assert.True(timestamps.Count == 0,
            $"ReceiveChannelMessage carries a timestamp ({string.Join(", ", timestamps)}). "
            + "Per-conversation order is the server-assigned Message.Sequence, never a provider's clock.");
    }

    /// <summary>
    /// <see cref="LayeringTests"/> already forbids Application depending on Infrastructure or a host.
    /// This adds the half `14-01` introduces the risk of: the resilience machinery. The wrapping
    /// mechanism is real and shipped (<c>Ago.Chat.Module.Channels.ResilientInboundChannelAdapter</c>),
    /// and the temptation for a future adapter is to reach for a <c>ResiliencePipeline</c> or a Polly
    /// attribute from the port itself. A port that knew how it was protected would make every
    /// implementation pay for that choice, which is the opposite of what
    /// <see cref="Ago.Chat.Application.Abstractions.IWebhookDeliveryClient"/> established.
    /// </summary>
    [Fact]
    public void ChannelPort_DoesNotKnowHowItIsProtected() =>
        Types.InAssembly(TestAssemblies.Application.Reflection)
            .Should()
            .NotHaveDependencyOnAny("Ago.Platform.Resilience", "Polly")
            .GetResult()
            .ShouldPass("Ago.Chat.Application must not reference the resilience machinery that wraps its ports");

    private static bool IsChannelKindItself(Type type) => type.Name == "ChannelKind";
}
