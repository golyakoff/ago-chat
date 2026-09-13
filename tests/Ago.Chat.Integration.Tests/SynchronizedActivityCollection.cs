using System.Collections;
using System.Diagnostics;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-71`: the <c>ICollection&lt;Activity&gt;</c> <see cref="OpenTelemetry.Trace.InMemoryExporterHelperExtensions.AddInMemoryExporter{T}"/>
/// asks for, made safe against concurrent writers - which the SDK's own default processor does not
/// guarantee, and which a plain <see cref="List{T}"/> is not: <c>AddHttpClientInstrumentation()</c> (or
/// any wildcard <c>AddSource</c>) observes activity sources for the whole test process, not just the
/// one <c>TracerProvider</c> that happens to be built in a given test, so any other test class running
/// concurrently (xUnit's own default parallelism) can append to the same list a test is enumerating -
/// throwing "Collection was modified" out of an assertion that has nothing to do with the real bug
/// being tested.
///
/// <para><see cref="System.Collections.Concurrent.ConcurrentBag{T}"/> would make <c>Add</c> itself
/// thread-safe but does not implement generic <see cref="ICollection{T}"/>, so <c>AddInMemoryExporter</c>
/// refuses it outright - this wraps a plain <see cref="List{T}"/> behind one lock instead: every
/// mutation and every read take the same lock, and a read returns a full snapshot rather than a live
/// enumerator, so no thread can ever observe - or write into - a half-updated list. Extracted from
/// <c>TelemetryLeakGuardTests</c>'s own private copy (`23-...`'s original author) once a second, then a
/// third, test class hit the identical race - a shared fix rather than three private ones.</para>
/// </summary>
internal sealed class SynchronizedActivityCollection : ICollection<Activity>
{
    private readonly List<Activity> _items = [];
    private readonly Lock _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public bool IsReadOnly => false;

    public void Add(Activity item)
    {
        lock (_gate)
        {
            _items.Add(item);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }

    public bool Contains(Activity item)
    {
        lock (_gate)
        {
            return _items.Contains(item);
        }
    }

    public void CopyTo(Activity[] array, int arrayIndex)
    {
        lock (_gate)
        {
            _items.CopyTo(array, arrayIndex);
        }
    }

    public bool Remove(Activity item)
    {
        lock (_gate)
        {
            return _items.Remove(item);
        }
    }

    // A snapshot copy, not a live view over _items - so a writer on another thread can never
    // invalidate an enumerator a reader already holds, which is exactly the exception this
    // collection exists to make impossible.
    public IEnumerator<Activity> GetEnumerator()
    {
        List<Activity> snapshot;
        lock (_gate)
        {
            snapshot = [.. _items];
        }

        return snapshot.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
