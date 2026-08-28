using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Ago.Chat.FakeMax.Tests;

/// <summary>
/// `14-02`: <c>Ago.Chat.FakeCrm.Tests.FakeCrmProcessFixture</c>'s own technique, for MAX's outbound
/// API instead of a shop's CRM. Launches a real, separate <c>dotnet Ago.Chat.FakeMax.dll</c> process on
/// a dynamically chosen port and waits for its own health check to answer over a real socket before
/// handing control to a test.
///
/// <para><c>DefaultBehavior</c> is settable before <see cref="InitializeAsync"/> (not through
/// <c>ICollectionFixture&lt;T&gt;</c>, which offers no way to parameterise a shared fixture per test) -
/// <c>FakeCrmProcessFixture.DefaultBehavior</c>'s own remarks explain why this has to be a property
/// rather than a constructor parameter (xUnit's single-public-constructor rule for fixture
/// activation).</para>
/// </summary>
public sealed class FakeMaxProcessFixture : IAsyncLifetime
{
    public string DefaultBehavior { get; set; } = "ok";

    public int HangSeconds { get; set; } = 30;

    private Process? _process;

    public int Port { get; private set; }

    public Uri BaseAddress => new($"http://127.0.0.1:{Port}/");

    public async Task InitializeAsync()
    {
        Port = GetFreeTcpPort();

        var fakeMaxDllPath = typeof(Program).Assembly.Location;
        _process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", $"\"{fakeMaxDllPath}\" --urls http://127.0.0.1:{Port}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Environment =
                {
                    ["FakeMax__DefaultBehavior"] = DefaultBehavior,
                    ["FakeMax__HangSeconds"] = HangSeconds.ToString(),
                    ["DOTNET_ENVIRONMENT"] = "Development",
                },
            },
        };

        _process.Start();

        await WaitUntilHealthyAsync();
    }

    public async Task DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
    }

    /// <summary>The "stopped/unreachable" half of this item's own Done-when, made literal: kills the
    /// real process mid-test so every subsequent call genuinely finds nothing listening (a real
    /// <see cref="SocketException"/> with <see cref="SocketError.ConnectionRefused"/>), rather than a
    /// simulated failure a fake could get subtly wrong.</summary>
    public async Task StopAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        // WaitForExitAsync only proves the Process object is gone, not that a caller dialling
        // 127.0.0.1:Port right now gets a refusal - on a loaded CI runner those two facts can be a
        // scheduling gap apart, and a resilience test that starts asserting on breaker behaviour
        // before the port is actually dead is racing that gap rather than testing "MAX is
        // unreachable." WaitUntilHealthyAsync's mirror image: poll a real socket until it stops
        // answering, the same kind of synchronization point that method already uses to prove the
        // opposite fact before releasing control to a test.
        await WaitUntilUnreachableAsync();
    }

    private async Task WaitUntilHealthyAsync()
    {
        using var client = new HttpClient();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
            {
                var stderr = await _process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"Ago.Chat.FakeMax exited early (code {_process.ExitCode}):\n{stderr}");
            }

            try
            {
                var response = await client.GetAsync(new Uri(BaseAddress, "healthz/live"));
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or SocketException)
            {
                lastError = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        throw new TimeoutException("Ago.Chat.FakeMax did not become healthy within 30s.", lastError);
    }

    /// <summary>Polls a real HTTP call against the port this process was using until it fails at the
    /// connection level, rather than trusting process-exit timing alone. Bounded short: the process
    /// really is dead by the time this runs, so the overwhelming case returns on the first attempt -
    /// this loop exists for the rare gap between "the process object reports exited" and "the OS has
    /// released the port," not to wait out anything genuinely slow.</summary>
    private async Task WaitUntilUnreachableAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await client.GetAsync(new Uri(BaseAddress, "healthz/live"));
                // Still answering - the port has not actually been released yet.
            }
            catch (Exception ex) when (ex is HttpRequestException or SocketException or TaskCanceledException)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException($"Ago.Chat.FakeMax on port {Port} was still reachable 5s after being killed.");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
