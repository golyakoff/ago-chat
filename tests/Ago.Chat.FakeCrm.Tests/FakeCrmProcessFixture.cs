using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Ago.Chat.FakeCrm.Tests;

/// <summary>
/// Launches a real, separate `dotnet Ago.Chat.FakeCrm.dll` process and waits for it to answer its own
/// health check over a real socket before handing control to a test - the literal thing the backlog's
/// done-when insists on ("an actual process a real request reaches... not an in-process TestServer
/// shortcut that never touches a real socket"). One process per test collection
/// (<see cref="FakeCrmCollection"/>, xUnit's own collection-fixture lifetime), so every test sharing
/// the collection reuses one running instance instead of paying process start-up cost per test.
///
/// Ports are picked dynamically (<see cref="GetFreeTcpPort"/>) rather than using this project's own
/// documented defaults (5290/5291, README) - those are for a human running `dotnet run` by hand;
/// a test run needs to survive parallel xUnit test collections and repeated CI runs without a stale
/// port collision.
/// </summary>
public sealed class FakeCrmProcessFixture : IAsyncLifetime
{
    // Not a secret in any real sense - it exists only so this fixture's own requests and the running
    // FakeCrm process agree on what to HMAC with, the same throwaway-credential category as
    // AttachmentFixture's MinIO username/password.
    public const string SigningSecret = "fake-crm-tests-only-signing-secret";

    // `6-05`: settable, defaults to null (unset) - existing callers get exactly today's behaviour
    // (FakeCrmOptions.DefaultBehavior null -> Program.cs falls back to the header alone, "succeeds" if
    // that is absent too). `6-05`'s own tests set this before calling InitializeAsync (not through
    // FakeCrmCollection/ICollectionFixture, which offers no way to parameterise a shared fixture per
    // test) to run one instance per fixed personality needed, several running concurrently.
    //
    // A property, not a second constructor overload - xUnit's own `ICollectionFixture<T>` requires
    // exactly one public constructor on the fixture type (`"may only define a single public
    // constructor"`, found by actually running `Ago.Chat.FakeCrm.Tests` after first trying a second
    // constructor overload, not assumed) and does not supply C# default argument values for an
    // optional parameter on a lone constructor either (found the same way, with the equally real
    // "unresolved constructor arguments" failure a bare optional parameter produced). A mutable
    // property set before `InitializeAsync` is called satisfies both constraints: the parameterless
    // constructor xUnit's own reflection-based activation needs stays exactly that, and `6-05`'s own
    // tests still get a fixed personality per instance.
    public string? DefaultBehavior { get; set; }

    private Process? _process;

    public int Port { get; private set; }

    public int DisappearPort { get; private set; }

    public Uri BaseAddress => new($"http://127.0.0.1:{Port}/");

    public async Task InitializeAsync()
    {
        Port = GetFreeTcpPort();
        DisappearPort = GetFreeTcpPort();

        var fakeCrmDllPath = typeof(Program).Assembly.Location;
        _process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", $"\"{fakeCrmDllPath}\" --urls http://127.0.0.1:{Port}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Environment =
                {
                    ["FakeCrm__SigningSecret"] = SigningSecret,
                    ["FakeCrm__DisappearPort"] = DisappearPort.ToString(),
                    ["DOTNET_ENVIRONMENT"] = "Development",
                },
            },
        };
        if (DefaultBehavior is not null)
        {
            _process.StartInfo.Environment["FakeCrm__DefaultBehavior"] = DefaultBehavior;
        }

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
                throw new InvalidOperationException($"Ago.Chat.FakeCrm exited early (code {_process.ExitCode}):\n{stderr}");
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

        throw new TimeoutException("Ago.Chat.FakeCrm did not become healthy within 30s.", lastError);
    }

    private static int GetFreeTcpPort()
    {
        // Bind port 0 (OS picks a free one), read it back, release it immediately - the standard
        // .NET test-fixture technique for a free port. A small window exists between Stop() here and
        // the FakeCrm process's own bind, same as every other test fixture using this trick; not
        // eliminated, just narrow enough that it has not been observed to flake in practice.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

[CollectionDefinition(Name)]
public sealed class FakeCrmCollection : ICollectionFixture<FakeCrmProcessFixture>
{
    public const string Name = "FakeCrm";
}
