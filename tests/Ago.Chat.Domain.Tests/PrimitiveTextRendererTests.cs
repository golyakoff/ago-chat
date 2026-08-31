namespace Ago.Chat.Domain.Tests;

/// <summary>`20-07`: "a text rendering for every primitive... a primitive without one is not finished"
/// (backlog item's own Scope) - see <see cref="PrimitiveTextRenderer"/>'s own remarks.</summary>
public class PrimitiveTextRendererTests
{
    private static readonly IReadOnlyList<MessageAction> ChoiceActions =
    [
        new MessageAction("Haircut", "svc-1"),
        new MessageAction("Manicure", "svc-2"),
    ];

    [Fact]
    public void Render_ChoiceList_NumbersEachActionAndAsksForTheNumber()
    {
        var payload = new MessagePayload("""{"prompt":"Which service?"}""");

        var rendered = PrimitiveTextRenderer.Render(
            "fallback body", PrimitiveKinds.ChoiceList, payload, ChoiceActions);

        Assert.Equal("Which service?\n1) Haircut\n2) Manicure\nReply with the number.", rendered);
    }

    [Fact]
    public void Render_ConfirmationCard_NumbersItsOwnActionsTheSameWay()
    {
        // ConfirmationCard's payload has no "prompt" field (title/lines instead) - the renderer falls
        // back to the message's own body as the prompt line, exactly as it would for a kind it has
        // never heard of.
        var payload = new MessagePayload("""{"title":"Confirm your booking","lines":[{"label":"When","value":"Monday 10:00"}]}""");
        var actions = new MessageAction[] { new("Confirm", "yes"), new("Cancel", "no") };

        var rendered = PrimitiveTextRenderer.Render(
            "Please confirm your booking for Monday 10:00.", PrimitiveKinds.ConfirmationCard, payload, actions);

        Assert.Equal(
            "Please confirm your booking for Monday 10:00.\n1) Confirm\n2) Cancel\nReply with the number.",
            rendered);
    }

    [Fact]
    public void Render_DateTimePicker_IsChoiceShapedTooAndNumbersItsSlots()
    {
        var payload = new MessagePayload(
            """{"prompt":"Pick a time","slots":[{"value":"slot-1","startsAt":"2026-09-01T10:00:00+00:00","label":"Tue 10:00"}]}""");
        var actions = new MessageAction[] { new("Tue 10:00", "slot-1") };

        var rendered = PrimitiveTextRenderer.Render("fallback", PrimitiveKinds.DateTimePicker, payload, actions);

        Assert.Equal("Pick a time\n1) Tue 10:00\nReply with the number.", rendered);
    }

    [Fact]
    public void Render_Form_ReturnsThePromptAloneWithNoNumbering()
    {
        var payload = new MessagePayload("""{"prompt":"What's your phone number?","fieldId":"phone","fieldLabel":"Phone"}""");

        var rendered = PrimitiveTextRenderer.Render("fallback", PrimitiveKinds.Form, payload, []);

        Assert.Equal("What's your phone number?", rendered);
    }

    [Fact]
    public void Render_ChoiceListWithNoActions_ReturnsThePromptAlone()
    {
        var payload = new MessagePayload("""{"prompt":"Nothing to choose yet."}""");

        var rendered = PrimitiveTextRenderer.Render("fallback", PrimitiveKinds.ChoiceList, payload, []);

        Assert.Equal("Nothing to choose yet.", rendered);
    }

    [Fact]
    public void Render_WithNoPayload_FallsBackToTheMessageBody()
    {
        var rendered = PrimitiveTextRenderer.Render("The message's own body.", PrimitiveKinds.Form, null, []);

        Assert.Equal("The message's own body.", rendered);
    }

    /// <summary>`19-03`: escalate renders like `form` - the prompt alone, no numbering, since there is
    /// nothing to pick from.</summary>
    [Fact]
    public void Render_Escalate_ReturnsThePromptAloneWithNoNumbering()
    {
        var payload = new MessagePayload("""{"prompt":"I'm not sure about that one."}""");

        var rendered = PrimitiveTextRenderer.Render("fallback", PrimitiveKinds.Escalate, payload, []);

        Assert.Equal("I'm not sure about that one.", rendered);
    }

    /// <summary>A module may escalate with nothing more to say - the caller's own fallback text is
    /// used, exactly like every other kind's no-payload case.</summary>
    [Fact]
    public void Render_EscalateWithNoPayload_FallsBackToTheCallersFallbackText()
    {
        var rendered = PrimitiveTextRenderer.Render("Let me get a team member to help with that.", PrimitiveKinds.Escalate, null, []);

        Assert.Equal("Let me get a team member to help with that.", rendered);
    }

    /// <summary>Forward compatibility: an unrecognised kind renders as plain prose rather than
    /// throwing - see the type's own remarks.</summary>
    [Fact]
    public void Render_WithAnUnrecognisedKind_FallsBackToTheMessageBody()
    {
        var payload = new MessagePayload("""{"prompt":"Some future primitive"}""");

        var rendered = PrimitiveTextRenderer.Render("The message's own body.", "some_future_kind", payload, ChoiceActions);

        Assert.Equal("The message's own body.", rendered);
    }
}
