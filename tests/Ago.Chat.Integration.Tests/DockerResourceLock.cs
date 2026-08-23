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
/// Implemented as an exclusive-open lock file (<see cref="FileShare.None"/>), not a named
/// <see cref="Semaphore"/>/<see cref="Mutex"/>: named kernel synchronization objects are a
/// Windows-only feature in .NET - <c>new Semaphore(1, 1, "name")</c> throws
/// <see cref="PlatformNotSupportedException"/> on Linux, which is exactly what this project's CI
/// runner is (found live, the hard way, when this first shipped Windows-only-tested). A file opened
/// with <see cref="FileShare.None"/> gives the same cross-process mutual exclusion on every platform
/// .NET supports - Windows enforces it natively, Unix via an advisory <c>flock</c> under the hood -
/// and releases itself automatically if the holding process dies, no stale-lock cleanup needed.
///
/// Trade-off, stated explicitly: every fixture's container lifetime becomes fully sequential, even
/// within a single test run that would otherwise start several collections' containers in parallel.
/// That is the requested behaviour - protect against Docker resource contention from any source, not
/// only cross-process - at the cost of a slower single-process test run than before this existed. If
/// that cost turns out to matter more than the contention risk it prevents, capping the number of
/// concurrent holders instead of enforcing strict exclusivity is the next thing to try; not done here
/// since it was not asked for and would need its own justification.
/// </summary>
public static class DockerResourceLock
{
    private static readonly string LockFilePath = Path.Combine(Path.GetTempPath(), "ago-chat-testcontainers.lock");

    /// <summary>Blocks (asynchronously, polling) until this fixture is the only one starting/holding
    /// Testcontainers-based containers on this machine, then returns a token that releases the slot
    /// on disposal. Acquire before <c>StartAsync</c>-ing any container; dispose after every container
    /// in the fixture has been disposed, not before - the whole "containers are alive" window is what
    /// this protects, not just the startup burst.</summary>
    public static async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                var stream = new FileStream(LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new Releaser(stream);
            }
            catch (IOException)
            {
                // Another fixture holds it - normal contention, not an error. Poll rather than block a
                // thread; this runs under an async fixture lifecycle, not inside a lock statement.
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
    }

    private sealed class Releaser(FileStream stream) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            stream.Dispose();
        }
    }
}
