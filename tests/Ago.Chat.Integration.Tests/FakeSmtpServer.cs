using System.Net;
using System.Net.Sockets;
using System.Text;
using Ago.Chat.Infrastructure.Email;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-09`: a minimal, single-threaded fake SMTP server over a real <see cref="TcpListener"/> - the
/// SMTP-shaped analogue of <see cref="WhatsAppApiClientTests"/>'s own in-process Kestrel host standing in
/// for Meta's own Graph API. Runs exactly one conversation (greeting through <c>QUIT</c> or connection
/// close), which is all <see cref="EmailSmtpClient.SendAsync"/>'s own single-message call ever needs per
/// server. Shared by <see cref="EmailSmtpClientTests"/> (the client's own protocol/MIME behaviour) and
/// <see cref="EmailChannelAdapterTests"/> (the adapter's own routing/threading-header behaviour, over a
/// real SMTP boundary rather than a fake <see cref="EmailSmtpClient"/> substitute) - the same "one real
/// fake server, two test classes each proving a different layer" shape
/// <see cref="WhatsAppApiClientTests"/>/<see cref="WhatsAppChannelAdapterTests"/> already establish for
/// WhatsApp's own HTTP boundary.
/// </summary>
internal sealed class FakeSmtpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly TaskCompletionSource<Transcript> _transcriptSource = new();

    private FakeSmtpServer(TcpListener listener, string rcptToResponse, string dataTerminatorResponse)
    {
        _listener = listener;
        _ = RunAsync(rcptToResponse, dataTerminatorResponse);
    }

    public EmailBotApiOptions Options { get; private set; } = null!;

    public static Task<FakeSmtpServer> StartAsync(
        string rcptToResponse = "250 2.1.5 OK", string dataTerminatorResponse = "250 2.0.0 OK: queued")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = new FakeSmtpServer(listener, rcptToResponse, dataTerminatorResponse)
        {
            Options = new EmailBotApiOptions
            {
                Domain = "ago-chat.example",
                SmtpHost = "127.0.0.1",
                SmtpPort = ((IPEndPoint)listener.LocalEndpoint).Port,
            },
        };
        return Task.FromResult(server);
    }

    public async Task<Transcript> WaitForTranscriptAsync() =>
        await _transcriptSource.Task.WaitAsync(TimeSpan.FromSeconds(10));

    private async Task RunAsync(string rcptToResponse, string dataTerminatorResponse)
    {
        try
        {
            using var tcpClient = await _listener.AcceptTcpClientAsync();
            using var stream = tcpClient.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

            var commands = new List<string>();
            var rawDataLines = new List<string>();

            await writer.WriteLineAsync("220 fake-smtp.example ESMTP ready");

            await reader.ReadLineAsync(); // EHLO - discarded, this fake server does not vary behaviour on it
            await writer.WriteLineAsync("250 fake-smtp.example");

            var mailFrom = await reader.ReadLineAsync() ?? "";
            commands.Add(mailFrom);
            await writer.WriteLineAsync("250 2.1.0 OK");

            var rcptTo = await reader.ReadLineAsync() ?? "";
            commands.Add(rcptTo);
            await writer.WriteLineAsync(rcptToResponse);
            if (rcptToResponse.StartsWith('5') || rcptToResponse.StartsWith('4'))
            {
                _transcriptSource.TrySetResult(new Transcript(commands, string.Empty, rawDataLines));
                return;
            }

            var data = await reader.ReadLineAsync() ?? "";
            commands.Add(data);
            await writer.WriteLineAsync("354 Start mail input; end with <CRLF>.<CRLF>");

            string? line;
            while ((line = await reader.ReadLineAsync()) is not null && line != ".")
            {
                rawDataLines.Add(line);
            }

            await writer.WriteLineAsync(dataTerminatorResponse);

            _transcriptSource.TrySetResult(new Transcript(commands, string.Join("\r\n", rawDataLines), rawDataLines));

            // Best-effort QUIT - EmailSmtpClient.TryQuitAsync's own remarks explain why a client does not
            // require this to succeed.
            await reader.ReadLineAsync();
            await writer.WriteLineAsync("221 2.0.0 Bye");
        }
        catch (Exception ex)
        {
            _transcriptSource.TrySetException(ex);
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Dispose();
    }

    public sealed record Transcript(IReadOnlyList<string> Commands, string DataPayload, IReadOnlyList<string> RawDataLines);
}
