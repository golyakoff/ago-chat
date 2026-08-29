using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records every call it receives (for assertions on exactly what was sent) and lets a test
/// script either a success response or an unreachable failure per call - the two shapes
/// <see cref="RouteConversationToModuleHandler"/> reacts to differently.</summary>
public sealed class FakeModuleGateway : IModuleGateway
{
    public List<(EnabledModuleEndpoint Module, StartModuleTaskRequest Request)> StartCalls { get; } = [];

    public List<(EnabledModuleEndpoint Module, SubmitModuleReplyRequest Request)> ReplyCalls { get; } = [];

    public Func<StartModuleTaskRequest, StartModuleTaskResult>? OnStartTask { get; set; }

    public Func<SubmitModuleReplyRequest, SubmitModuleReplyResult>? OnSubmitReply { get; set; }

    public bool UnreachableOnStart { get; set; }

    public bool UnreachableOnReply { get; set; }

    public Task<StartModuleTaskResult> StartTaskAsync(
        EnabledModuleEndpoint module, StartModuleTaskRequest request, CancellationToken cancellationToken)
    {
        StartCalls.Add((module, request));
        if (UnreachableOnStart)
        {
            throw new ModuleUnreachableException(module.ModuleKey, "fake unreachable (start)");
        }

        var result = OnStartTask?.Invoke(request)
            ?? throw new InvalidOperationException("FakeModuleGateway.OnStartTask was not configured for this test.");
        return Task.FromResult(result);
    }

    public Task<SubmitModuleReplyResult> SubmitReplyAsync(
        EnabledModuleEndpoint module, SubmitModuleReplyRequest request, CancellationToken cancellationToken)
    {
        ReplyCalls.Add((module, request));
        if (UnreachableOnReply)
        {
            throw new ModuleUnreachableException(module.ModuleKey, "fake unreachable (reply)");
        }

        var result = OnSubmitReply?.Invoke(request)
            ?? throw new InvalidOperationException("FakeModuleGateway.OnSubmitReply was not configured for this test.");
        return Task.FromResult(result);
    }
}
