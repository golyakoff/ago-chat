using System.Text.Json;
using Ago.Chat.Domain;

namespace Ago.Chat.Domain.Tests;

/// <summary>
/// `14-06`'s fourth Done-when, and the half the item says is easy to skip: <b>one payload, rendered
/// two ways</b> - as a UI element, and as text plus a numbered choice a person could answer over SMS.
///
/// <para><b>Why this is a test and not a paragraph.</b> A payload that only a browser can render
/// fails `21-01` on arrival, and nothing about the type system would notice: a message with a
/// gorgeous card and no usable text is well-formed, valid, storable and deliverable. The only way to
/// know the shape survives a channel with no UI is to write the renderer that has no UI, which is
/// what the second half of each test below is.</para>
///
/// <para><b>Both renderers live here, in a test, on purpose.</b> Neither belongs in
/// <c>Ago.Chat.*</c>: a real UI renderer is a client's job, and a real SMS renderer belongs to
/// whichever channel adapter `14-03` eventually builds. What is being proven is a property of the
/// <i>contract</i> - that both renderers are writable at all, from the same three fields, without
/// either needing to know a single key of the payload.</para>
///
/// <para><b>The payload used below is deliberately not a booking.</b> A booking is what this will
/// carry first, and writing one here would have put another product's vocabulary into
/// <c>Ago.Chat.Domain.Tests</c> - which is the exact thing <c>MessageOpacityTests</c> forbids, and
/// which would have been a boundary crossing performed by the test that exists to prove the boundary
/// holds. A library hold request makes the same point with nothing borrowed from a real product.</para>
/// </summary>
public class StructuredContentRenderingTests
{
    /// <summary>
    /// One structured message: prose, an opaque document, three choices.
    ///
    /// <para>The <c>body</c> is the load-bearing part of the contract - it is mandatory, and it must
    /// describe the same thing the payload does, because a text channel has nothing else. Nothing in
    /// AGO Chat can check that a producer wrote a body matching its payload, which is why
    /// <see cref="MessageContent"/> states it as a rule rather than enforcing it.</para>
    /// </summary>
    private const string Body = "Your hold is ready to collect. Which branch would you like to pick it up from?";

    private const string Payload =
        """
        {"title":"Hold ready","reference":"H-4417","branches":[
          {"id":"cen","name":"Central Library","closesAt":"18:00"},
          {"id":"riv","name":"Riverside Branch","closesAt":"17:00"},
          {"id":"nor","name":"Northgate Branch","closesAt":"20:00"}]}
        """;

    private static MessageContent Content() => MessageContent.Create(
        new MessageContentKind("holds.pickup_choice"),
        new MessagePayload(Payload),
        [
            new MessageAction("Central Library", "cen"),
            new MessageAction("Riverside Branch", "riv"),
            new MessageAction("Northgate Branch", "nor"),
        ]);

    [Fact]
    public void RenderedAsAUiElement_TheClientReadsThePayloadItsProductUnderstands()
    {
        var content = Content();

        // A rich client knows this kind - it was written by the same product that produced the
        // payload - so it looks inside and draws a card. This is the *only* renderer that parses the
        // payload, and it is a client, not AGO Chat.
        Assert.Equal("holds.pickup_choice", content.Kind.Value);

        using var document = JsonDocument.Parse(content.Payload!.Value.Value);
        var title = document.RootElement.GetProperty("title").GetString();
        var branches = document.RootElement.GetProperty("branches");

        Assert.Equal("Hold ready", title);
        Assert.Equal(3, branches.GetArrayLength());

        // The card's buttons come from the actions, not from the payload's own array - so a client
        // that ignored the payload entirely would still produce working buttons, and a client that
        // parsed the payload gets richer labels (a closing time) for the same choices.
        Assert.Equal(
            ["Central Library", "Riverside Branch", "Northgate Branch"],
            content.Actions.Select(action => action.Label));
    }

    [Fact]
    public void RenderedAsTextPlusANumberedChoice_ItWorksOnAChannelWithNoUiAtAll()
    {
        var content = Content();

        var rendered = RenderForATextOnlyChannel(Body, content);

        // Asserted line by line rather than against a raw string literal: a literal's newlines are
        // whatever the source file was saved with, so on a CRLF checkout the test would be asserting
        // the repository's line endings instead of the renderer's - and the renderer's are the whole
        // point of the previous paragraph.
        Assert.Equal(
            string.Join(
                '\n',
                "Your hold is ready to collect. Which branch would you like to pick it up from?",
                string.Empty,
                "1) Central Library",
                "2) Riverside Branch",
                "3) Northgate Branch",
                string.Empty,
                "Reply with a number."),
            rendered);
    }

    [Fact]
    public void TheTextRenderer_NeverLooksInsideThePayload()
    {
        // The property that makes the whole design work: swap the payload for a document with
        // completely different keys - or no payload at all - and the SMS rendering is byte-identical,
        // because it is built from the body and the action labels alone. A renderer that had to
        // understand the payload would be a renderer per product per channel, which is the shape the
        // boundary review rules out.
        var withPayload = RenderForATextOnlyChannel(Body, Content());

        var withoutPayload = RenderForATextOnlyChannel(Body, MessageContent.Create(
            new MessageContentKind("something.else"),
            payload: null,
            actions:
            [
                new MessageAction("Central Library", "cen"),
                new MessageAction("Riverside Branch", "riv"),
                new MessageAction("Northgate Branch", "nor"),
            ]));

        Assert.Equal(withPayload, withoutPayload);
    }

    [Fact]
    public void AReplyOfANumber_ResolvesBackToTheProducersOwnOpaqueValue()
    {
        // The return direction. The channel adapter turns "2" into the action at index 1 and sends an
        // ordinary message whose body is the label a human would have typed and whose payload carries
        // the producer's own value. AGO Chat stores and delivers that message exactly like any other;
        // it does not route it, and it does not know that "riv" means anything.
        var content = Content();

        var chosen = ResolveNumberedReply(content, "2");

        Assert.Equal("Riverside Branch", chosen!.Label);
        Assert.Equal("riv", chosen.Value);

        // Out of range and non-numeric replies are the channel adapter's problem, not a message model
        // problem - it re-prompts. Asserted so that the contract's edge is stated rather than assumed.
        Assert.Null(ResolveNumberedReply(content, "9"));
        Assert.Null(ResolveNumberedReply(content, "yes please"));
    }

    [Fact]
    public void AMessageWithNoActions_StillRendersAsPlainText()
    {
        // A card nobody has to answer - a confirmation, a receipt. The text channel prints the body
        // and stops, with no dangling "Reply with a number" for a message that offers no numbers.
        var content = MessageContent.Create(
            new MessageContentKind("holds.collected"), new MessagePayload("""{"reference":"H-4417"}"""));

        Assert.Equal("Your hold has been collected.", RenderForATextOnlyChannel("Your hold has been collected.", content));
    }

    /// <summary>
    /// <b>The renderer a channel with no UI would ship</b> - the whole of it, in eleven lines, using
    /// nothing but the body and the action labels.
    ///
    /// <para>That it fits in eleven lines and needs no knowledge of any product is the result this
    /// test file exists to demonstrate. Written once here, it would be written once per channel
    /// adapter (`14-02` MAX, `14-03` SMS, `14-04` Telegram) and never once per payload kind.</para>
    /// </summary>
    private static string RenderForATextOnlyChannel(string body, MessageContent? content)
    {
        if (content is not { Actions.Count: > 0 })
        {
            return body;
        }

        var lines = new List<string> { body, string.Empty };
        lines.AddRange(content.Actions.Select((action, index) => $"{index + 1}) {action.Label}"));
        lines.Add(string.Empty);
        lines.Add("Reply with a number.");

        // A bare LF, not Environment.NewLine: this string goes onto a wire to a phone, not into a
        // file on whichever OS the process happens to run on. A renderer that emitted CRLF because
        // it was built on Windows is a bug nobody would see until an SMS gateway did.
        return string.Join('\n', lines);
    }

    /// <summary>The inverse, and equally product-blind: a digit is an index into the list AGO Chat
    /// already owns the schema of.</summary>
    private static MessageAction? ResolveNumberedReply(MessageContent content, string reply) =>
        int.TryParse(reply, out var choice) && choice >= 1 && choice <= content.Actions.Count
            ? content.Actions[choice - 1]
            : null;
}
