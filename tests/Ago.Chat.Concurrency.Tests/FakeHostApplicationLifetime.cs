using Microsoft.Extensions.Hosting;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>A minimal, test-only <see cref="IHostApplicationLifetime"/> - these tests construct
/// pipeline pieces directly rather than through a real generic host (matching this project's own
/// precedent for hand-built consumers in end-to-end tests), so there is no real host to fire
/// <see cref="ApplicationStopping"/> for them. <see cref="TriggerStopping"/> is the test's stand-in
/// for a real shutdown starting.</summary>
public sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
{
    private readonly CancellationTokenSource _stopping = new();

    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => _stopping.Token;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void TriggerStopping() => _stopping.Cancel();

    public void StopApplication() => TriggerStopping();
}
