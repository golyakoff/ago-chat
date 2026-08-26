using Ago.Chat.Domain;

namespace Ago.Chat.Domain.Tests;

/// <summary>
/// `14-06`'s value objects: what the envelope refuses, and - just as importantly - what it declines
/// to have an opinion about.
/// </summary>
public class StructuredMessageContentTests
{
    private static readonly MessageContentKind AnyKind = new("some.kind");

    [Fact]
    public void APayloadIsStoredVerbatim_KeysAndOrderUntouched()
    {
        // The producer's own bytes, not a re-serialisation. A product that signs or hashes its
        // payload is entitled to - AGO Chat promised to carry this, not to read it - and a round trip
        // through a parsed representation would reorder keys and collapse duplicates.
        const string Original = """{"z":1,"a":{"nested":[3,2,1]},"z2":"  spaced  "}""";

        Assert.Equal(Original, new MessagePayload(Original).Value);
    }

    [Fact]
    public void APayloadMustBeWellFormedJson()
    {
        // A message is immutable and fans out to every participant plus every history read, forever.
        // A malformed payload accepted here cannot be repaired by its producer and breaks rendering
        // permanently, for everyone - so it is refused at send time, where exactly one caller sees it
        // and can fix it.
        Assert.Throws<ArgumentException>(() => new MessagePayload("""{"unclosed": """));
        Assert.Throws<ArgumentException>(() => new MessagePayload("not json at all"));
    }

    [Fact]
    public void APayloadMustBeAnObject_NotAnArrayOrAScalar()
    {
        // The one structural requirement, and it is what lets a generic renderer walk named fields
        // without knowing a single name. A bare array or scalar has no names to walk.
        Assert.Throws<ArgumentException>(() => new MessagePayload("""[1,2,3]"""));
        Assert.Throws<ArgumentException>(() => new MessagePayload("42"));
        Assert.Throws<ArgumentException>(() => new MessagePayload("\"a string\""));

        // An empty object is fine: "there is structure here and it happens to be empty" is a producer's
        // business, not this product's.
        Assert.Equal("{}", new MessagePayload("{}").Value);
    }

    [Fact]
    public void APayloadIsBounded_AndThatIsADenialOfServiceControl()
    {
        // This field rides the message-send path, which accepts input from unauthenticated visitors on
        // the public internet. One send is stored forever, fanned out to every connected participant,
        // and replayed on every history read - so an unbounded opaque field is an amplification
        // vector, not a validation nicety.
        var atTheLimit = "{\"a\":\"" + new string('x', MessagePayload.MaxLength - 8) + "\"}";
        Assert.Equal(MessagePayload.MaxLength, atTheLimit.Length);
        Assert.Equal(atTheLimit, new MessagePayload(atTheLimit).Value);

        var overTheLimit = "{\"a\":\"" + new string('x', MessagePayload.MaxLength - 7) + "\"}";
        Assert.Equal(MessagePayload.MaxLength + 1, overTheLimit.Length);
        var rejected = Assert.Throws<ArgumentException>(() => new MessagePayload(overTheLimit));
        Assert.Contains(MessagePayload.MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture), rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AContentKindIsAShapeCheckAndNeverAMembershipCheck()
    {
        // Any lowercase namespaced label a product invents is accepted, including ones this codebase
        // has never heard of - which is the whole point. A closed set here would be AGO Chat owning
        // the vocabulary of every product that ever writes into a conversation.
        Assert.Equal("anything.a-product_invents1", new MessageContentKind("anything.a-product_invents1").Value);

        // Shape, though, is checked: this value is echoed into JSON, log lines and possibly a URL.
        Assert.Throws<ArgumentException>(() => new MessageContentKind(""));
        Assert.Throws<ArgumentException>(() => new MessageContentKind("Has Spaces"));
        Assert.Throws<ArgumentException>(() => new MessageContentKind("UpperCase"));
        Assert.Throws<ArgumentException>(() => new MessageContentKind("has/slash"));
        Assert.Throws<ArgumentException>(() => new MessageContentKind(new string('k', MessageContentKind.MaxLength + 1)));
    }

    [Fact]
    public void ActionsAreBoundedByWhatAPersonCouldAnswerOverText()
    {
        var tenActions = Enumerable.Range(0, MessageContent.MaxActions)
            .Select(i => new MessageAction($"Choice {i}", $"v{i}"))
            .ToList();

        Assert.Equal(MessageContent.MaxActions, MessageContent.Create(AnyKind, actions: tenActions).Actions.Count);

        var elevenActions = tenActions.Append(new MessageAction("One too many", "v10")).ToList();
        Assert.Throws<ArgumentException>(() => MessageContent.Create(AnyKind, actions: elevenActions));
    }

    [Fact]
    public void TwoActionsCannotShareAValue()
    {
        // The one thing about actions AGO Chat can check without knowing what any of them mean: two
        // choices with the same value produce a reply their own producer cannot tell apart.
        Assert.Throws<ArgumentException>(() => MessageContent.Create(AnyKind, actions:
        [
            new MessageAction("Morning", "same"),
            new MessageAction("Afternoon", "same"),
        ]));

        // Two labels the same is fine - a producer's business, and harmless, since the value is what
        // comes back.
        var content = MessageContent.Create(AnyKind, actions:
        [
            new MessageAction("Later", "a"),
            new MessageAction("Later", "b"),
        ]);
        Assert.Equal(2, content.Actions.Count);
    }

    [Fact]
    public void AnActionsLabelIsRequired_BecauseATextChannelHasNothingElseToPrint()
    {
        Assert.Throws<ArgumentException>(() => new MessageAction("", "v"));
        Assert.Throws<ArgumentException>(() => new MessageAction("   ", "v"));
        Assert.Throws<ArgumentException>(() => new MessageAction(new string('l', MessageAction.MaxLabelLength + 1), "v"));
    }

    [Fact]
    public void AnActionsValueIsRequired_BecauseItsProducerHasToRecogniseTheReply()
    {
        Assert.Throws<ArgumentException>(() => new MessageAction("Label", ""));
        Assert.Throws<ArgumentException>(() => new MessageAction("Label", new string('v', MessageAction.MaxValueLength + 1)));

        // Whitespace *is* a legal value: it is opaque, and deciding that a producer's token may not
        // be a space would be an opinion about what the token means.
        Assert.Equal(" ", new MessageAction("Label", " ").Value);
    }

    [Fact]
    public void ContentNeedsOnlyAKind()
    {
        // A payload with no actions is a card nobody has to answer; actions with no payload are a
        // plain "pick one", which is what a text channel would produce natively. Requiring a payload
        // would force a producer to invent an empty one.
        var justAKind = MessageContent.Create(AnyKind);

        Assert.Null(justAKind.Payload);
        Assert.Empty(justAKind.Actions);
    }
}
