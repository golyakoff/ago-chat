using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Ago.Chat.FakeCrm;

/// <summary>
/// Implements the "disappears" personality - the one of the four that cannot be chosen from inside an
/// HTTP request, because refusing a TCP connection has to happen before any request on it is readable
/// (Program.cs's own remark; this project's README has the full reasoning). Binds its own port
/// (<see cref="FakeCrmOptions.DisappearPort"/>), separate from the main webhook-delivery port: a
/// caller picks this personality by which port it connects to, not by a header.
///
/// Every connection accepted here is closed with <see cref="LingerOption"/> set to discard-and-close-
/// now, which the OS implements as an immediate TCP RST rather than the normal FIN/ACK four-way close -
/// not one byte of the request is ever read, matching a real dead endpoint failing at the transport
/// layer before TLS or HTTP exist at all, as distinct from <c>5xx</c> (an HTTP-level answer) or
/// <c>hang</c> (a slow one). When <see cref="FakeCrmOptions.DisappearPortListens"/> is false this
/// service does not bind the port at all, proving the backlog's other named form of "disappears" -
/// connection refused outright, before any accept ever happens.
/// </summary>
public sealed class DisappearedConnectionListener(IOptions<FakeCrmOptions> options, ILogger<DisappearedConnectionListener> logger)
    : BackgroundService
{
    private TcpListener? _listener;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.DisappearPortListens)
        {
            logger.LogInformation(
                "FakeCrm:DisappearPortListens is false - port {Port} stays closed (connection refused), not RST'd.",
                options.Value.DisappearPort);
            return Task.CompletedTask;
        }

        _listener = new TcpListener(IPAddress.Any, options.Value.DisappearPort);
        _listener.Start();
        logger.LogInformation("FakeCrm 'disappears' listener bound to port {Port}.", options.Value.DisappearPort);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_listener is null)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            // Linger(true, 0): "discard anything unsent and close right now" - the standard sockets
            // trick to force a TCP RST instead of a graceful FIN close. Set via SetSocketOption
            // directly, not the TcpClient.LingerState property - found live while proving this
            // personality: on this .NET/Windows combination, setting LingerState through the property
            // silently produced an ordinary graceful close (the client-side read returned 0 bytes, no
            // exception at all) where the two behave identically on paper. SetSocketOption, read back
            // to confirm it stuck, is what actually produces the RST a caller observes as
            // SocketException/ConnectionReset - proven in FakeCrmPersonalityTests against a real
            // socket, not assumed from the docs.
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(true, 0));
            client.Client.Close();
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _listener?.Stop();
        return base.StopAsync(cancellationToken);
    }
}
