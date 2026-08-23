namespace Ago.Chat.Integration.Tests;

/// <summary>
/// Machine-wide lock so that starting a Testcontainers-based fixture's containers never overlaps with
/// another one - whether from a different xUnit collection in this same test run, or from a
/// completely separate <c>dotnet test</c> process (parallel background workers, each in their own git
/// worktree, running integration tests at the same time). Testcontainers already isolates each
/// fixture's own containers correctly (dynamic ports, separate containers, no shared state) - this
/// lock exists purely to bound how many container fleets are alive on this machine's Docker daemon at
/// once, a real CPU/memory contention risk Testcontainers' own isolation does not address.
///
/// A <see cref="Semaphore"/>, not a <see cref="Mutex"/>, is used deliberately: <c>Mutex.Release</c>
/// must run on the thread that called <c>WaitOne</c>, which an <c>async</c>/<c>await</c> continuation
/// does not guarantee - <see cref="Semaphore"/> has no such thread affinity.
///
/// Trade-off, stated explicitly: every fixture's container lifetime becomes fully sequential, even
/// within a single test run that would otherwise start several collections' containers in parallel.
/// That is the requested behaviour - protect against Docker resource contention from any source, not
/// only cross-process - at the cost of a slower single-process test run than before this existed. If
/// that cost turns out to matter more than the contention risk it prevents, raising
/// <c>MaxConcurrentFixtures</c> to a small bounded count instead of strict 1 is the next thing to try;
/// not done here since it was not asked for and would need its own justification.
/// </summary>
public static class DockerResourceLock
{
    private const int MaxConcurrentFixtures = 1;

    private static readonly Semaphore Semaphore = new(
        initialCount: MaxConcurrentFixtures,
        maximumCount: MaxConcurrentFixtures,
        name: @"Local\AgoChat.TestContainers.Lock");

    /// <summary>Blocks (asynchronously) until this fixture is the only one starting/holding
    /// Testcontainers-based containers on this machine, then returns a token that releases the slot
    /// on disposal. Acquire before <c>StartAsync</c>-ing any container; dispose after every container
    /// in the fixture has been disposed, not before - the whole "containers are alive" window is what
    /// this protects, not just the startup burst.</summary>
    public static async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() => Semaphore.WaitOne(), cancellationToken);
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            Semaphore.Release();
        }
    }
}
