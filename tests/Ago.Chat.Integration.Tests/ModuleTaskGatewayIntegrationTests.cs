using System.Text;
using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.RouteConversationToModule;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Modules;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `20-07`'s own two Done-when items that need a real HTTP boundary, not a fake port implementation:
/// <list type="bullet">
/// <item>"The same flow completes over a text-only channel using the primitives' text renderings" -
/// narrowed here to its sharpest, most literal proof: a widget-shaped reply and a text-channel reply
/// against the *same* rendered step produce the identical outbound value to the module. Everything
/// upstream of the reply (rendering, resolution) is already proven by
/// <c>Ago.Chat.Domain.Tests.PrimitiveTextRendererTests</c>/<c>ChoiceReplyTextResolverTests</c> and
/// <c>Ago.Chat.Application.Tests.UseCases.RouteConversationToModule.RouteConversationToModuleHandlerTests</c>
/// with a fake gateway; what only a real HTTP round trip can prove is that the bytes actually
/// <em>sent</em> agree.</item>
/// <item>"A module that is unreachable degrades to the escape to an operator... proven by a test, not
/// by inspection" - proven here against a real connection refusal, not a simulated exception.</item>
/// </list>
///
/// <para><b>Deliberately narrower than the full pipeline.</b> This does not exercise Postgres, RabbitMQ,
/// or <see cref="ResilientModuleGateway"/>'s retry/circuit-breaker wrapping - <see cref="HttpModuleGateway"/>
/// is used directly, unwrapped, matching how <c>MaxChannelAdapterResilienceTests</c> separates "does the
/// resilience wrapper behave" (proven once, generically, against a stub) from "does this boundary's own
/// adapter translate correctly" (proven here). <c>testing.md</c>'s own rule - "don't reach for
/// Testcontainers where a Domain/Application unit test with fakes proves the same rule" - is why
/// everything except the module HTTP boundary itself is a minimal in-memory stand-in rather than a real
/// Postgres-backed <see cref="IConversationRepository"/>.</para>
/// </summary>
public class ModuleTaskGatewayIntegrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly ModuleKey Calendar = new("calendar");

    private const string FirstStepJson = """
        {
          "kind": "choice_list",
          "payload": { "prompt": "Which service?" },
          "actions": [ { "label": "Haircut", "value": "svc-1" }, { "label": "Manicure", "value": "svc-2" } ]
        }
        """;

    [Fact]
    public async Task ReplyByIdParity_AWidgetReplyAndATextChannelReply_SubmitTheIdenticalValueToTheModule()
    {
        await using var server = new FakeModuleServer();
        await server.StartAsync(externalTaskId: "external-1", firstStepJson: FirstStepJson, replyCompleteJson: "true");

        // Two independent conversations, both starting the identical task against the same fake
        // module - the "same rendered step" the claim is about.
        var widgetConversation = StartConversationWithTask();
        var textChannelConversation = StartConversationWithTask();

        var widgetGateway = server.BuildGateway();
        var textGateway = server.BuildGateway();

        // The widget path: the visitor's own reply carries structured content whose payload is
        // {"value": "<the chosen action's value>"} - the wire contract's own reply shape.
        var widgetContent = MessageContent.Create(
            new MessageContentKind(PrimitiveKinds.ChoiceList), new MessagePayload("""{"value":"svc-2"}"""));
        widgetConversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("Manicure"), Now, content: widgetContent);
        await RouteAsync(widgetConversation, widgetGateway, server.BaseAddress);

        // The text-channel path: the same choice, answered as a bare number over a channel with no UI.
        textChannelConversation.AddVisitorMessage(
            VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("2"), Now);
        await RouteAsync(textChannelConversation, textGateway, server.BaseAddress);

        Assert.Equal(2, server.ReceivedReplyBodies.Count);
        var widgetReplyBody = server.ReceivedReplyBodies[0];
        var textReplyBody = server.ReceivedReplyBodies[1];

        // The claim, proven on the actual bytes the module received: identical kind, identical value.
        Assert.Equal(ExtractField(widgetReplyBody, "kind"), ExtractField(textReplyBody, "kind"));
        Assert.Equal(ExtractField(widgetReplyBody, "value"), ExtractField(textReplyBody, "value"));
        Assert.Equal("svc-2", ExtractField(widgetReplyBody, "value"));
        Assert.Equal("svc-2", ExtractField(textReplyBody, "value"));
    }

    /// <summary>The trigger-match half over a real HTTP round trip - <c>POST .../module-tasks</c>,
    /// the wire contract's other route, exercised for real rather than only through the fake-gateway
    /// Application-level tests.</summary>
    [Fact]
    public async Task TriggerMatch_CallsTheRealModuleServer_AndStartsTheTaskFromItsResponse()
    {
        await using var server = new FakeModuleServer();
        await server.StartAsync(externalTaskId: "external-1", firstStepJson: FirstStepJson, replyCompleteJson: "true");

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("/booking"), Now);

        var outcome = await RouteAsync(conversation, server.BuildGateway(), server.BaseAddress);

        Assert.Equal(RouteConversationToModuleOutcome.TaskStarted, outcome);
        Assert.NotNull(conversation.ActiveModuleTask);
        Assert.Equal("external-1", conversation.ActiveModuleTask!.ExternalTaskId);
        Assert.Equal(2, conversation.ActiveModuleTask!.LastStepActions.Count);
        Assert.Equal(
            "Which service?\n1) Haircut\n2) Manicure\nReply with the number.", conversation.Messages.Last().Body.Value);
    }

    [Fact]
    public async Task UnreachableModule_ClosesTheTask_AndTellsTheVisitorAPersonWillTakeOver()
    {
        await using var server = new FakeModuleServer();
        await server.StartAsync(externalTaskId: "external-1", firstStepJson: FirstStepJson, replyCompleteJson: "true");

        var conversation = StartConversationWithTask();
        var gateway = server.BuildGateway();
        var entryPoint = server.BaseAddress;

        // The module's own outbound API, stopped mid-conversation - the literal thing the backlog's
        // own Done-when names ("a module that is unreachable"), not a simulated exception.
        await server.StopAsync();

        conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("2"), Now);
        var outcome = await RouteAsync(conversation, gateway, entryPoint);

        Assert.Equal(RouteConversationToModuleOutcome.Escalated, outcome);
        Assert.Null(conversation.ActiveModuleTask);
        var reply = conversation.Messages.Last();
        Assert.Equal(MessageAuthorKind.System, reply.AuthorKind);
        Assert.Contains("person", reply.Body.Value, StringComparison.OrdinalIgnoreCase);

        // "Leave the conversation in whatever state makes it reach the normal operator queue" -
        // nothing about this escalation touches ConversationState; it is still Waiting, which is
        // exactly the state the ordinary unassigned-conversation queue already serves.
        Assert.Equal(ConversationState.Waiting, conversation.State);
    }

    private static Conversation StartConversationWithTask()
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var kind = new MessageContentKind(PrimitiveKinds.ChoiceList);
        var payload = new MessagePayload("""{"prompt":"Which service?"}""");
        IReadOnlyList<MessageAction> actions = [new MessageAction("Haircut", "svc-1"), new MessageAction("Manicure", "svc-2")];
        conversation.StartModuleTask(new ModuleTaskId(Guid.NewGuid()), Calendar, "external-1", Now, kind, payload, actions);
        conversation.ClearDomainEvents();
        return conversation;
    }

    private static async Task<RouteConversationToModuleOutcome> RouteAsync(
        Conversation conversation, IModuleGateway gateway, Uri entryPoint)
    {
        var conversations = new FixedConversationRepository(conversation);
        var readStore = new FixedEnabledModuleReadStore(Calendar, ["/booking"], entryPoint);
        var outbox = new FixedOutboxWriter();
        var inbox = new FixedInboxChecker();

        var handler = new RouteConversationToModuleHandler(
            conversations, readStore, gateway, outbox, inbox, new FixedClock(Now), new FixedIdGenerator());

        var command = new RouteConversationToModule(
            Guid.NewGuid(), SiteId, conversation.Id, MessageAuthorKind.Visitor, conversation.LastSequence);
        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        return result.Value;
    }

    private static string? ExtractField(JsonDocument document, string propertyName) =>
        document.RootElement.TryGetProperty(propertyName, out var element) ? element.GetString() : null;

    // ------------------------------------------------------------------------------------------
    // Minimal local test doubles - deliberately not shared with Ago.Chat.Application.Tests' own
    // Fakes (a cross-test-project reference for a handful of trivial in-memory doubles was judged
    // not worth the coupling; each is a few lines).
    // ------------------------------------------------------------------------------------------

    private sealed class FixedConversationRepository(Conversation conversation) : IConversationRepository
    {
        public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken) =>
            Task.FromResult<Conversation?>(conversation.Id == id ? conversation : null);

        public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> GetWaitingForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(Conversation conversation, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedEnabledModuleReadStore(ModuleKey key, IReadOnlyList<string> triggerWords, Uri entryPoint)
        : IEnabledModuleReadStore
    {
        public Task<IReadOnlyList<EnabledModuleSummary>> GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EnabledModuleSummary>>([new EnabledModuleSummary(key, triggerWords, entryPoint)]);
    }

    private sealed class FixedOutboxWriter : IOutboxWriter
    {
        public void Enqueue(EventEnvelope envelope, string? traceContext = null)
        {
        }
    }

    private sealed class FixedInboxChecker : IInboxChecker
    {
        private readonly HashSet<(Guid, string)> _recorded = [];

        public Task<bool> TryRecordAndSaveAsync(Guid messageId, string consumer, CancellationToken cancellationToken) =>
            Task.FromResult(_recorded.Add((messageId, consumer)));
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class FixedIdGenerator : IIdGenerator
    {
        public Guid NewId(DateTimeOffset now) => Guid.NewGuid();
    }

    /// <summary>The fake module: a minimal Kestrel host answering exactly the two wire-contract routes,
    /// recording every reply body it receives for the parity assertion.</summary>
    private sealed class FakeModuleServer : IAsyncDisposable
    {
        private WebApplication? _app;

        public Uri BaseAddress { get; private set; } = null!;

        public List<JsonDocument> ReceivedReplyBodies { get; } = [];

        public async Task StartAsync(string externalTaskId, string firstStepJson, string replyCompleteJson)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();

            app.MapPost("/api/v1/module-tasks", async context =>
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                _ = await reader.ReadToEndAsync();
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    $$"""{"externalTaskId":"{{externalTaskId}}","step":{{firstStepJson}},"complete":false}""");
            });

            app.MapPost("/api/v1/module-tasks/{externalTaskId}/replies", async context =>
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                lock (ReceivedReplyBodies)
                {
                    ReceivedReplyBodies.Add(JsonDocument.Parse(body));
                }

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync($$"""{"step":null,"complete":{{replyCompleteJson}}}""");
            });

            await app.StartAsync();
            _app = app;

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            BaseAddress = new Uri(addresses.Addresses.First());
        }

        public async Task StopAsync()
        {
            if (_app is not null)
            {
                await _app.StopAsync();
            }
        }

        public HttpModuleGateway BuildGateway() => new(new HttpClient());

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }
        }
    }
}
