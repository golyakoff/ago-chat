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
            "fallback body", PrimitiveKinds.ChoiceList, payload, ChoiceActions, nameof(Locale.En));

        Assert.Equal("Which service?\n1) Haircut\n2) Manicure\nReply with the number.", rendered);
    }

    /// <summary>`25-134`: the trailing instruction line follows the site's own locale, the same as
    /// every other piece of text a Russian-configured site's visitor already sees.</summary>
    [Fact]
    public void Render_ChoiceList_WithRussianLocale_RendersTheInstructionInRussian()
    {
        var payload = new MessagePayload("""{"prompt":"Which service?"}""");

        var rendered = PrimitiveTextRenderer.Render(
            "fallback body", PrimitiveKinds.ChoiceList, payload, ChoiceActions, nameof(Locale.Ru));

        Assert.Equal("Which service?\n1) Haircut\n2) Manicure\nОтветьте номером.", rendered);
    }

    /// <summary>`25-134`: an unset or unrecognised locale must still render English - the same
    /// safe-default posture <c>Ago.Calendar</c>'s own <c>ModuleStepFactory.Strings.For</c> already
    /// takes, never a throw.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr")]
    [InlineData("not-a-locale")]
    public void Render_ChoiceList_WithAnUnsetOrUnrecognisedLocale_FallsBackToEnglish(string? locale)
    {
        var payload = new MessagePayload("""{"prompt":"Which service?"}""");

        var rendered = PrimitiveTextRenderer.Render(
            "fallback body", PrimitiveKinds.ChoiceList, payload, ChoiceActions, locale);

        Assert.Equal("Which service?\n1) Haircut\n2) Manicure\nReply with the number.", rendered);
    }

    /// <summary>`25-135`: the fix itself, fails-before/passes-after. Before this item,
    /// <see cref="PrimitiveKinds.ConfirmationCard"/> had no "prompt" field for <c>TryReadPrompt</c> to
    /// find and fell through to the caller's own fallback body - the visitor's own last message at both
    /// real call sites - so a completed booking rendered as an echo of whatever the visitor last typed,
    /// never an actual confirmation. <paramref name="fallbackBody"/> is deliberately something the
    /// rendering must NOT reproduce, the same proof technique
    /// <c>DeliverChannelMessageHandlerTests.HandleAsync_ForAModuleTaskPromptSystemMessage_RelaysThePrimitiveTextRendering</c>
    /// already uses for <c>"prompt"</c>.</summary>
    [Fact]
    public void Render_ConfirmationCard_RendersTheTitleAndLines_NotTheFallbackBody()
    {
        var payload = new MessagePayload(
            """{"title":"Confirm your booking","lines":[{"label":"When","value":"Monday 10:00"},{"label":"Service","value":"Haircut"}]}""");

        var rendered = PrimitiveTextRenderer.Render(
            "the visitor's own last message - must not appear in the output", PrimitiveKinds.ConfirmationCard,
            payload, [], nameof(Locale.En));

        Assert.Equal("Confirm your booking\nWhen: Monday 10:00\nService: Haircut", rendered);
    }

    /// <summary>The shipped booking module never populates a confirmation card's own actions
    /// (<c>Ago.Calendar</c>'s own <c>ModuleStep.ConfirmationStep</c> passes an empty list) - proven here
    /// anyway, since this vocabulary's own remarks describe actions as merely "typical", not
    /// guaranteed, for this kind: the title/lines rendering must not depend on there being none.</summary>
    [Fact]
    public void Render_ConfirmationCard_WithActionsPresent_StillRendersTitleAndLinesNotTheActions()
    {
        var payload = new MessagePayload("""{"title":"Confirm your booking","lines":[{"label":"When","value":"Monday 10:00"}]}""");
        var actions = new MessageAction[] { new("Confirm", "yes"), new("Cancel", "no") };

        var rendered = PrimitiveTextRenderer.Render(
            "fallback", PrimitiveKinds.ConfirmationCard, payload, actions, nameof(Locale.En));

        Assert.Equal("Confirm your booking\nWhen: Monday 10:00", rendered);
    }

    [Fact]
    public void Render_ConfirmationCard_WithNoTitle_FallsBackToTheMessageBody()
    {
        var payload = new MessagePayload("""{"lines":[{"label":"When","value":"Monday 10:00"}]}""");

        var rendered = PrimitiveTextRenderer.Render(
            "The message's own body.", PrimitiveKinds.ConfirmationCard, payload, [], nameof(Locale.En));

        Assert.Equal("The message's own body.", rendered);
    }

    [Fact]
    public void Render_ConfirmationCard_WithNoLines_RendersTheTitleAlone()
    {
        var payload = new MessagePayload("""{"title":"You're booked!"}""");

        var rendered = PrimitiveTextRenderer.Render(
            "fallback", PrimitiveKinds.ConfirmationCard, payload, [], nameof(Locale.En));

        Assert.Equal("You're booked!", rendered);
    }

    [Fact]
    public void Render_ConfirmationCard_WithNoPayload_FallsBackToTheMessageBody()
    {
        var rendered = PrimitiveTextRenderer.Render(
            "The message's own body.", PrimitiveKinds.ConfirmationCard, null, [], nameof(Locale.En));

        Assert.Equal("The message's own body.", rendered);
    }

    [Fact]
    public void Render_DateTimePicker_IsChoiceShapedTooAndNumbersItsSlots()
    {
        var payload = new MessagePayload(
            """{"prompt":"Pick a time","slots":[{"value":"slot-1","startsAt":"2026-09-01T10:00:00+00:00","label":"Tue 10:00"}]}""");
        var actions = new MessageAction[] { new("Tue 10:00", "slot-1") };

        var rendered = PrimitiveTextRenderer.Render(
            "fallback", PrimitiveKinds.DateTimePicker, payload, actions, nameof(Locale.En));

        Assert.Equal("Pick a time\n1) Tue 10:00\nReply with the number.", rendered);
    }

    [Fact]
    public void Render_Form_ReturnsThePromptAloneWithNoNumbering()
    {
        var payload = new MessagePayload("""{"prompt":"What's your phone number?","fieldId":"phone","fieldLabel":"Phone"}""");

        var rendered = PrimitiveTextRenderer.Render("fallback", PrimitiveKinds.Form, payload, [], nameof(Locale.En));

        Assert.Equal("What's your phone number?", rendered);
    }

    [Fact]
    public void Render_ChoiceListWithNoActions_ReturnsThePromptAlone()
    {
        var payload = new MessagePayload("""{"prompt":"Nothing to choose yet."}""");

        var rendered = PrimitiveTextRenderer.Render(
            "fallback", PrimitiveKinds.ChoiceList, payload, [], nameof(Locale.En));

        Assert.Equal("Nothing to choose yet.", rendered);
    }

    [Fact]
    public void Render_WithNoPayload_FallsBackToTheMessageBody()
    {
        var rendered = PrimitiveTextRenderer.Render(
            "The message's own body.", PrimitiveKinds.Form, null, [], nameof(Locale.En));

        Assert.Equal("The message's own body.", rendered);
    }

    /// <summary>`19-03`: escalate renders like `form` - the prompt alone, no numbering, since there is
    /// nothing to pick from.</summary>
    [Fact]
    public void Render_Escalate_ReturnsThePromptAloneWithNoNumbering()
    {
        var payload = new MessagePayload("""{"prompt":"I'm not sure about that one."}""");

        var rendered = PrimitiveTextRenderer.Render("fallback", PrimitiveKinds.Escalate, payload, [], nameof(Locale.En));

        Assert.Equal("I'm not sure about that one.", rendered);
    }

    /// <summary>A module may escalate with nothing more to say - the caller's own fallback text is
    /// used, exactly like every other kind's no-payload case.</summary>
    [Fact]
    public void Render_EscalateWithNoPayload_FallsBackToTheCallersFallbackText()
    {
        var rendered = PrimitiveTextRenderer.Render(
            "Let me get a team member to help with that.", PrimitiveKinds.Escalate, null, [], nameof(Locale.En));

        Assert.Equal("Let me get a team member to help with that.", rendered);
    }

    /// <summary>Forward compatibility: an unrecognised kind renders as plain prose rather than
    /// throwing - see the type's own remarks.</summary>
    [Fact]
    public void Render_WithAnUnrecognisedKind_FallsBackToTheMessageBody()
    {
        var payload = new MessagePayload("""{"prompt":"Some future primitive"}""");

        var rendered = PrimitiveTextRenderer.Render(
            "The message's own body.", "some_future_kind", payload, ChoiceActions, nameof(Locale.En));

        Assert.Equal("The message's own body.", rendered);
    }

    /// <summary>`25-134`: <see cref="RouteConversationToModule.RouteConversationToModuleHandler"/>'s own
    /// "module finished with nothing further to say" fallback reads this same table directly (it never
    /// goes through <see cref="PrimitiveTextRenderer.Render"/> at all, since there is no step to
    /// render) - proven here at the table's own level, and end-to-end against the real handler in
    /// <c>RouteConversationToModuleHandlerTests</c>.</summary>
    [Fact]
    public void Strings_For_Ru_ReturnsTheRussianModuleTaskDoneText()
    {
        Assert.Equal("Готово, спасибо.", PrimitiveTextRenderer.Strings.For(nameof(Locale.Ru)).ModuleTaskDone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some-unconfigured-value")]
    public void Strings_For_AnUnsetOrUnrecognisedLocale_ReturnsEnglish(string? locale)
    {
        Assert.Equal("Done - thank you.", PrimitiveTextRenderer.Strings.For(locale).ModuleTaskDone);
    }
}
