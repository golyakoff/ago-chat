namespace Ago.Chat.Domain.Tests;

/// <summary>`25-153`: <see cref="PrimitiveKinds.IsPhoneCollectionStep"/>, promoted here from a private
/// helper `25-152` first wrote inside `DeliverChannelMessageHandler` - the wider edge cases (a
/// non-"phone" `fieldId`, a malformed payload, a `choice_list` step that happens to carry a `fieldId`
/// key it has no business having) already have regression coverage in
/// <c>Ago.Chat.Application.Tests.UseCases.DeliverChannelMessage.DeliverChannelMessageHandlerTests</c>,
/// unchanged by the move since this method's own behaviour is identical to what that class's private
/// copy did. This file proves the method itself, directly, now that it is Domain-owned vocabulary
/// knowledge rather than one handler's private implementation detail.</summary>
public class PrimitiveKindsTests
{
    [Theory]
    [InlineData(PrimitiveKinds.Form)]
    [InlineData(PrimitiveKinds.VerifiedPhoneForm)]
    public void IsPhoneCollectionStep_APhoneShapedFormOrVerifiedPhoneForm_ReturnsTrue(string kind) =>
        Assert.True(PrimitiveKinds.IsPhoneCollectionStep(
            kind, new MessagePayload("""{"prompt":"What's your phone?","fieldId":"phone","fieldLabel":"Phone"}""")));

    [Fact]
    public void IsPhoneCollectionStep_AFormWithADifferentFieldId_ReturnsFalse() =>
        Assert.False(PrimitiveKinds.IsPhoneCollectionStep(
            PrimitiveKinds.Form, new MessagePayload("""{"prompt":"What's your postcode?","fieldId":"postcode"}""")));

    [Theory]
    [InlineData(PrimitiveKinds.ChoiceList)]
    [InlineData(PrimitiveKinds.ConfirmationCard)]
    [InlineData(PrimitiveKinds.DateTimePicker)]
    [InlineData(PrimitiveKinds.Escalate)]
    public void IsPhoneCollectionStep_AnyOtherKind_ReturnsFalseEvenWithAPhoneFieldId(string kind) =>
        Assert.False(PrimitiveKinds.IsPhoneCollectionStep(
            kind, new MessagePayload("""{"prompt":"Which service?","fieldId":"phone"}""")));

    [Fact]
    public void IsPhoneCollectionStep_NoPayload_ReturnsFalse() =>
        Assert.False(PrimitiveKinds.IsPhoneCollectionStep(PrimitiveKinds.Form, null));

    // No "malformed JSON payload" case here, unlike this method's own try/catch might suggest is
    // needed: MessagePayload's own constructor already refuses anything that is not well-formed JSON,
    // so no test in this codebase can ever construct one to pass in. The catch stays as defence for a
    // payload reconstructed some other way (Message.Content's own read path bypasses re-validation on
    // the way back out of storage, MessagePayload's own remarks) - not something a public-API test can
    // exercise directly.
    [Fact]
    public void IsPhoneCollectionStep_PayloadWithNoFieldId_ReturnsFalse() =>
        Assert.False(PrimitiveKinds.IsPhoneCollectionStep(PrimitiveKinds.Form, new MessagePayload("""{"prompt":"Phone?"}""")));
}
