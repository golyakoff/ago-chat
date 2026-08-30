using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.YandexGpt;
using Ago.Chat.Module.Categorization;
using Ago.Platform.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.CircuitBreaker;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `19-02`: proves the resilience wrapping is actually wired, the identical shape and reasoning
/// <see cref="ResilientReplyDraftGeneratorTests"/> establishes for `19-01` - no container, no fixture,
/// just a Polly pipeline and a decorator with an in-process fake provider underneath.
///
/// <para><b>The one behaviour this class proves that its `19-01` counterpart does not need to</b>: every
/// fault degrades to <see cref="CategorizationResult.Unavailable"/> so
/// `Ago.Chat.Worker.ConversationCategorizationJob`'s own loop can move on to the next candidate in its
/// batch rather than the whole tick dying on the first down-provider call
/// (<see cref="ResilientConversationCategorizer"/>'s own remarks).</para>
/// </summary>
public sealed class ResilientConversationCategorizerTests
{
    private static readonly CategorizationCandidateTag Billing = new(new TagId(Guid.NewGuid()), "Billing");

    private static readonly CategorizationRequest Request = new(
        [new CategorizationHistoryMessage(CategorizationAuthorKind.Visitor, "hi")], [Billing]);

    private static CategorizationResiliencePipeline Pipelines(Action<ResiliencePipelineOptions> configure)
    {
        var options = new ResiliencePipelineOptions();
        configure(options);
        return new CategorizationResiliencePipeline(options);
    }

    [Fact]
    public async Task WhenTheProviderAnswers_TheResultIsPassedThroughUnchanged()
    {
        var inner = new StubCategorizer { Result = new CategorizationResult.Success([Billing.TagId]) };
        var wrapped = new ResilientConversationCategorizer(inner, Pipelines(_ => { }), NullLogger<ResilientConversationCategorizer>.Instance);

        var result = await wrapped.CategorizeAsync(Request, CancellationToken.None);

        var success = Assert.IsType<CategorizationResult.Success>(result);
        Assert.Equal([Billing.TagId], success.TagIds);
        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task ATransientFault_IsRetried_AndTheEventualSuccessIsReturned()
    {
        var inner = new StubCategorizer { FailuresBeforeSuccess = 2, Result = new CategorizationResult.Success([]) };
        var wrapped = new ResilientConversationCategorizer(inner, Pipelines(options =>
            options.Retry = new ResilienceRetryOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
            }), NullLogger<ResilientConversationCategorizer>.Instance);

        var result = await wrapped.CategorizeAsync(Request, CancellationToken.None);

        Assert.IsType<CategorizationResult.Success>(result);
        Assert.Equal(3, inner.Attempts);
    }

    /// <summary>A fault that survives every retry never reaches the caller as an exception - it
    /// degrades to <see cref="CategorizationResult.Unavailable"/>, the outcome
    /// `Ago.Chat.Worker.ConversationCategorizationJob` needs to move on to its next candidate.</summary>
    [Fact]
    public async Task WhenEveryRetryIsExhausted_DegradesToUnavailable_RatherThanThrowing()
    {
        var inner = new StubCategorizer { FailuresBeforeSuccess = int.MaxValue };
        var wrapped = new ResilientConversationCategorizer(inner, Pipelines(options =>
            options.Retry = new ResilienceRetryOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
            }), NullLogger<ResilientConversationCategorizer>.Instance);

        var result = await wrapped.CategorizeAsync(Request, CancellationToken.None);

        Assert.IsType<CategorizationResult.Unavailable>(result);
    }

    [Fact]
    public async Task WhenTheBreakerIsOpen_DegradesToUnavailable()
    {
        var pipelines = Pipelines(options => options.CircuitBreaker = new ResilienceCircuitBreakerOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 2,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(30),
        });

        var inner = new StubCategorizer { FailuresBeforeSuccess = int.MaxValue };
        var wrapped = new ResilientConversationCategorizer(inner, pipelines, NullLogger<ResilientConversationCategorizer>.Instance);

        for (var i = 0; i < 4; i++)
        {
            await wrapped.CategorizeAsync(Request, CancellationToken.None);
        }

        var attemptsBefore = inner.Attempts;
        var result = await wrapped.CategorizeAsync(Request, CancellationToken.None);

        Assert.IsType<CategorizationResult.Unavailable>(result);
        Assert.Equal(attemptsBefore, inner.Attempts);
    }

    [Fact]
    public async Task ATerminalRefusal_IsNotRetried_AndStillDegradesToUnavailable()
    {
        var inner = new StubCategorizer { ThrowRefused = true };
        var wrapped = new ResilientConversationCategorizer(inner, Pipelines(options =>
            options.Retry = new ResilienceRetryOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
            }), NullLogger<ResilientConversationCategorizer>.Instance);

        var result = await wrapped.CategorizeAsync(Request, CancellationToken.None);

        Assert.IsType<CategorizationResult.Unavailable>(result);
        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task ACancellation_PropagatesRatherThanDegrading()
    {
        var inner = new StubCategorizer { ThrowCancelled = true };
        var wrapped = new ResilientConversationCategorizer(inner, Pipelines(_ => { }), NullLogger<ResilientConversationCategorizer>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => wrapped.CategorizeAsync(Request, CancellationToken.None));
    }

    private sealed class StubCategorizer : IConversationCategorizer
    {
        public int Attempts { get; private set; }

        public int FailuresBeforeSuccess { get; set; }

        public bool ThrowRefused { get; set; }

        public bool ThrowCancelled { get; set; }

        public CategorizationResult Result { get; set; } = new CategorizationResult.Success([]);

        public Task<CategorizationResult> CategorizeAsync(CategorizationRequest request, CancellationToken cancellationToken)
        {
            Attempts++;

            if (ThrowCancelled)
            {
                throw new OperationCanceledException();
            }

            if (ThrowRefused)
            {
                throw new ConversationCategorizationProviderRefusedException("stub terminal refusal");
            }

            if (FailuresBeforeSuccess > 0)
            {
                if (FailuresBeforeSuccess != int.MaxValue)
                {
                    FailuresBeforeSuccess--;
                }

                throw new InvalidOperationException("stub transient provider fault");
            }

            return Task.FromResult(Result);
        }
    }
}
