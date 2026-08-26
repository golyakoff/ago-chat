using System.Buffers;
using System.Reflection;
using Ago.Chat.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `5-19`: the test whose absence let `14-06` break every visitor send on the live deployment.
///
/// <para><b>What broke.</b> `14-06` appended three optional parameters to
/// <see cref="VisitorHub.SendMessageAsync"/>, taking it from four to seven, and claimed that SignalR
/// "binds hub arguments positionally and fills the rest with their defaults". It does not. The
/// deployed widget invokes with exactly four arguments, and every send failed with SignalR's generic
/// <i>"Failed to invoke 'SendMessageAsync' due to an error on the server"</i> - no server log, and
/// <c>SendVisitorMessageHandler</c> never reached, because the invocation is rejected while its
/// arguments are still being parsed, long before a hub instance exists.</para>
///
/// <para><b>Why 697 green tests said nothing.</b> Every other hub test in this repository constructs
/// <see cref="VisitorHub"/> and calls the C# method directly, so each one recompiles against whatever
/// the signature currently is. A test written that way cannot express "a client built last month" -
/// it is always a client built against `HEAD`. This one goes through the wire format instead.</para>
///
/// <para><b>Why the protocol and not a whole host.</b> The failure is in argument binding:
/// <see cref="JsonHubProtocol"/> asks the dispatcher's binder for the target's parameter types and
/// refuses an invocation that does not supply one value per parameter. Standing up a
/// <c>TestServer</c>, a transport and six stubbed dependencies would exercise the same line through
/// four more layers, and would still be the same assertion. The parameter types here are read off
/// the real method by reflection, exactly as <c>DefaultHubDispatcher</c> reads them, so this tracks
/// the hub rather than a copy of its signature.</para>
///
/// <para>This is the second time this dispatcher has cost real breakage - `8-02` found it first, and
/// the warning lived only as a comment in another repository's TypeScript. It is a test here now.</para>
/// </summary>
public sealed class HubMethodArityTests
{
    /// <summary>
    /// Exactly what `ago-widget/src/connection.ts` puts on the wire today, and has since `5-07`:
    /// <c>invoke("SendMessageAsync", conversationId, body, attachmentId ?? null, clientMessageId)</c>.
    /// Four arguments. Any client already embedded on somebody's site sends this and cannot be made
    /// to send anything else.
    /// </summary>
    private const string DeployedWidgetSend =
        """
        {"type":1,"invocationId":"1","target":"SendMessageAsync","arguments":["11111111-1111-1111-1111-111111111111","hello",null,"22222222-2222-2222-2222-222222222222"]}
        """;

    /// <summary>The console's own send, also four arguments (`5-16`).</summary>
    private const string DeployedConsoleSend =
        """
        {"type":1,"invocationId":"1","target":"SendMessageAsync","arguments":["11111111-1111-1111-1111-111111111111","hello",null,"22222222-2222-2222-2222-222222222222"]}
        """;

    [Fact]
    public void TheDeployedWidgetsFourArgumentSend_StillBindsToVisitorHub()
    {
        var parsed = Parse(DeployedWidgetSend, typeof(VisitorHub), "SendMessageAsync");

        Assert.Equal("SendMessageAsync", parsed.Target);
        Assert.Equal(4, parsed.Arguments.Length);
    }

    [Fact]
    public void TheDeployedConsolesFourArgumentSend_StillBindsToOperatorHub()
    {
        var parsed = Parse(DeployedConsoleSend, typeof(OperatorHub), "SendMessageAsync");

        Assert.Equal("SendMessageAsync", parsed.Target);
        Assert.Equal(4, parsed.Arguments.Length);
    }

    /// <summary>
    /// The rule, stated as arithmetic rather than as behaviour, so the failure message says what to
    /// do about it. A client cannot supply an argument for a parameter that did not exist when it was
    /// built, and an embeddable client cannot be forced to upgrade - so a hub method's parameter list
    /// is append-only in the sense that it may never grow at all. New capability goes on a new method
    /// (`5-19`, and `adr/0048`'s "a second endpoint, not a flag on the mint" for the same reasoning
    /// about a route).
    /// </summary>
    [Theory]
    [InlineData(typeof(VisitorHub), "SendMessageAsync", 4)]
    [InlineData(typeof(OperatorHub), "SendMessageAsync", 4)]
    public void AHubMethodADeployedClientCalls_KeepsTheArityThatClientWasBuiltAgainst(
        Type hub, string method, int arity)
    {
        var parameters = MethodOn(hub, method).GetParameters();

        Assert.True(
            parameters.Length == arity,
            $"{hub.Name}.{method} takes {parameters.Length} parameter(s); clients already deployed "
            + $"invoke it with {arity}. SignalR rejects an invocation that supplies fewer arguments "
            + "than the target declares - it does not fall back to a C# default - so growing this "
            + "list breaks every embedded client at once, silently, with no server-side log. Add a "
            + "new hub method instead.");
    }

    private static InvocationMessage Parse(string json, Type hub, string method)
    {
        var protocol = new JsonHubProtocol();
        var input = new ReadOnlySequence<byte>(Frame(json));
        var binder = new HubMethodBinder(MethodOn(hub, method));

        Assert.True(protocol.TryParseMessage(ref input, binder, out var message));

        // An arity mismatch does not throw out of TryParseMessage - it parses into an
        // InvocationBindingFailureMessage, which DefaultHubDispatcher turns into a completion
        // carrying "Failed to invoke '<target>' due to an error on the server". That is precisely
        // why the live symptom had no server-side log and never reached the handler: the invocation
        // is answered from the parse layer, and no hub is ever constructed. Surfaced here rather
        // than left as an IsType failure, so the test says what went wrong.
        if (message is InvocationBindingFailureMessage failure)
        {
            Assert.Fail(
                $"SignalR refused the deployed client's invocation of {hub.Name}.{method}: "
                + failure.BindingFailure.SourceException.Message);
        }

        return Assert.IsType<InvocationMessage>(message);
    }

    /// <summary>SignalR frames each message with a trailing record separator (0x1E); the parser
    /// requires it, and a test that forgot it would fail for a reason that has nothing to do with
    /// arity.</summary>
    private static byte[] Frame(string json)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(json);
        var framed = new byte[payload.Length + 1];
        payload.CopyTo(framed, 0);
        framed[^1] = 0x1E;
        return framed;
    }

    private static MethodInfo MethodOn(Type hub, string method) =>
        hub.GetMethod(method, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"{hub.Name} has no public {method}.");

    /// <summary>
    /// What <c>DefaultHubDispatcher</c> does: answer "what types does this target take" from the
    /// method's own parameter list. Reflection rather than a hand-written array, so the test cannot
    /// drift from the hub the way the widget's comment drifted from the server.
    /// </summary>
    private sealed class HubMethodBinder(MethodInfo method) : IInvocationBinder
    {
        public IReadOnlyList<Type> GetParameterTypes(string methodName) =>
            [.. method.GetParameters().Select(parameter => parameter.ParameterType)];

        public Type GetReturnType(string invocationId) => typeof(object);

        public Type GetStreamItemType(string streamId) => throw new NotSupportedException();
    }
}
