namespace Ago.Chat.Domain.Tests;

/// <summary>`20-07`/`adr/0065` decision 7: "at most one active task per conversation" -
/// <see cref="Conversation.StartModuleTask"/>/<see cref="Conversation.RecordModuleStep"/>/
/// <see cref="Conversation.CloseModuleTask"/>.</summary>
public class ConversationModuleTaskTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly ModuleKey Calendar = new("calendar");

    private static Conversation StartConversation() =>
        Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);

    private static readonly MessageContentKind ChoiceListKind = new(PrimitiveKinds.ChoiceList);
    private static readonly IReadOnlyList<MessageAction> Actions = [new MessageAction("Option A", "a")];

    [Fact]
    public void StartModuleTask_OnAFreshConversation_OpensATaskAsTheActiveOne()
    {
        var conversation = StartConversation();

        var task = conversation.StartModuleTask(
            new ModuleTaskId(Guid.NewGuid()), Calendar, "ext-task-1", Now, ChoiceListKind, null, Actions);

        Assert.Same(task, conversation.ActiveModuleTask);
        Assert.Equal(ModuleTaskState.Open, task.State);
        Assert.Equal(Calendar, task.ModuleKey);
        Assert.Equal("ext-task-1", task.ExternalTaskId);
        Assert.Equal(ChoiceListKind, task.LastStepKind);
        Assert.Equal(Actions.Count, task.LastStepActions.Count);
    }

    /// <summary>The whole point of this item's own aggregate-level invariant - see
    /// <see cref="Conversation.StartModuleTask"/>'s own remarks on why this is enforced here rather
    /// than trusted to a caller that checked <see cref="Conversation.ActiveModuleTask"/> first.</summary>
    [Fact]
    public void StartModuleTask_WhileOneIsAlreadyActive_Throws()
    {
        var conversation = StartConversation();
        conversation.StartModuleTask(new ModuleTaskId(Guid.NewGuid()), Calendar, "ext-1", Now, ChoiceListKind, null, Actions);

        Assert.Throws<InvalidConversationStateException>(() =>
            conversation.StartModuleTask(
                new ModuleTaskId(Guid.NewGuid()), new ModuleKey("taxi"), "ext-2", Now, ChoiceListKind, null, Actions));
    }

    [Fact]
    public void StartModuleTask_AfterAPriorTaskWasClosed_IsAllowed()
    {
        var conversation = StartConversation();
        conversation.StartModuleTask(new ModuleTaskId(Guid.NewGuid()), Calendar, "ext-1", Now, ChoiceListKind, null, Actions);
        conversation.CloseModuleTask(Now);

        var second = conversation.StartModuleTask(
            new ModuleTaskId(Guid.NewGuid()), Calendar, "ext-2", Now, ChoiceListKind, null, Actions);

        Assert.Same(second, conversation.ActiveModuleTask);
    }

    [Fact]
    public void StartModuleTask_OnAClosedConversation_Throws()
    {
        var conversation = StartConversation();
        conversation.AssignTo(new OperatorId(Guid.NewGuid()), Now);
        conversation.Close(Now);

        Assert.Throws<InvalidConversationStateException>(() =>
            conversation.StartModuleTask(new ModuleTaskId(Guid.NewGuid()), Calendar, "ext-1", Now, ChoiceListKind, null, Actions));
    }

    [Fact]
    public void RecordModuleStep_AdvancesTheActiveTasksLastStep()
    {
        var conversation = StartConversation();
        conversation.StartModuleTask(new ModuleTaskId(Guid.NewGuid()), Calendar, "ext-1", Now, ChoiceListKind, null, Actions);

        var formKind = new MessageContentKind(PrimitiveKinds.Form);
        conversation.RecordModuleStep(formKind, null, []);

        Assert.Equal(formKind, conversation.ActiveModuleTask!.LastStepKind);
        Assert.Empty(conversation.ActiveModuleTask!.LastStepActions);
    }

    [Fact]
    public void RecordModuleStep_WithNoActiveTask_Throws()
    {
        var conversation = StartConversation();

        Assert.Throws<InvalidConversationStateException>(() =>
            conversation.RecordModuleStep(ChoiceListKind, null, []));
    }

    [Fact]
    public void CloseModuleTask_ClosesTheActiveTask_AndItIsNoLongerActive()
    {
        var conversation = StartConversation();
        conversation.StartModuleTask(new ModuleTaskId(Guid.NewGuid()), Calendar, "ext-1", Now, ChoiceListKind, null, Actions);

        conversation.CloseModuleTask(Now);

        Assert.Null(conversation.ActiveModuleTask);
    }

    [Fact]
    public void CloseModuleTask_WithNoActiveTask_Throws()
    {
        var conversation = StartConversation();

        Assert.Throws<InvalidConversationStateException>(() => conversation.CloseModuleTask(Now));
    }

    /// <summary>`20-07`: the module-task caller <see cref="Conversation.AddSystemMessage"/>'s own
    /// remarks describe - a structured system message with no visitor or operator principal behind
    /// it.</summary>
    [Fact]
    public void AddSystemMessage_CarriesStructuredContent_ForAModuleStep()
    {
        var conversation = StartConversation();
        var content = MessageContent.Create(ChoiceListKind, null, Actions);

        var message = conversation.AddSystemMessage(
            new MessageId(Guid.NewGuid()), new MessageBody("Which service?\n1) Option A\nReply with the number."), Now,
            content: content);

        Assert.Equal(MessageAuthorKind.System, message.AuthorKind);
        Assert.NotNull(message.Content);
        Assert.Equal(ChoiceListKind, message.Content!.Kind);
        Assert.Single(message.Content!.Actions);
    }
}
