using System.Net.Sockets;
using Ago.Chat.Infrastructure.Postgres.Schema;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `8-10`'s open question, answered as a table: <b>what is "not yet" and what is "not going to be".</b>
///
/// <para>The item filed it as the part that could make the fix worse than the bug it fixes - "a wait
/// that swallows an authentication failure turns a wrong password into a timeout, and that is a worse
/// error message than the one this item is fixing." So the half of this file that matters most is the
/// negative half: every case below that must <b>not</b> wait.</para>
///
/// <para>No database and no container. These are classification decisions about exception values, and
/// a real Postgres cannot be made to produce <c>53300</c> or <c>3D000</c> on demand without more setup
/// than the assertion is worth. The two cases that <em>were</em> reproduced against a real server -
/// <c>57P03</c> from a crash-restart, and the mid-handshake EOF a just-started container produces -
/// are marked below, because a case that has been seen is worth more than a case that was reasoned
/// about.</para>
/// </summary>
public class DatabaseAvailabilityClassificationTests
{
    private static PostgresException Postgres(string sqlState) =>
        new("simulated", "FATAL", "FATAL", sqlState);

    private static SocketException Socket(SocketError error)
    {
        var exception = new SocketException((int)error);
        // Self-check rather than trust: SocketException(int) is documented in terms of a native error
        // code, and this suite runs on Windows locally and Linux in CI. If the numbering ever stopped
        // round-tripping, every case below would silently classify something other than what it names.
        Assert.Equal(error, exception.SocketErrorCode);
        return exception;
    }

    private static NpgsqlException Wrapped(Exception inner) =>
        new("Failed to connect to a-host:5432", inner);

    [Theory]
    // 57P03 cannot_connect_now. OBSERVED: SIGKILL a postgres:17-alpine container holding a 300k-row
    // table, restart it, and every attempt before the last answers "the database system is starting up".
    [InlineData("57P03")]
    // The server terminating connections because it is going down, or because a sibling backend
    // crashed. Both are a restart in progress - the incident's own shape.
    [InlineData("57P01")]
    [InlineData("57P02")]
    // Resolves only with time: a deploy in which a dozen pods reconnect at once transiently exhausts
    // the connection slots, and failing here makes the migrator the first casualty of a herd it did
    // not cause.
    [InlineData("53300")]
    public void AServerSayingNotYet_IsWaitedThrough(string sqlState)
    {
        Assert.True(DatabaseAvailabilityWait.IsWorthWaitingFor(Postgres(sqlState)));
    }

    /// <summary>
    /// <b>The assertion this whole item turns on.</b> Each of these is permanent, each is a
    /// configuration mistake, and each would be reported as an unexplained ninety-second timeout by a
    /// more generous rule - which is a worse error message than the <c>Connection refused</c> that
    /// prompted `8-10`.
    /// </summary>
    [Theory]
    // 28P01 invalid_password - the exact case the item warned about.
    [InlineData("28P01")]
    // 28000 invalid_authorization_specification, which is also "no pg_hba.conf entry for host".
    [InlineData("28000")]
    // 3D000 invalid_catalog_name - the database does not exist. Creating it is not this deployable's
    // job (adr/0056), and it will not appear on its own.
    [InlineData("3D000")]
    // 42501 insufficient_privilege - the role cannot do DDL. `17-03`'s split makes this reachable.
    [InlineData("42501")]
    // 08P01 protocol_violation - whatever is on that port is not Postgres.
    [InlineData("08P01")]
    // Not a connection-level error at all; nothing about waiting is relevant to it.
    [InlineData("42P07")]
    public void AServerSayingNotEver_IsNotWaitedThrough(string sqlState)
    {
        Assert.False(DatabaseAvailabilityWait.IsWorthWaitingFor(Postgres(sqlState)));
    }

    /// <summary>
    /// The production incident's own error, as Npgsql actually reports it: an
    /// <see cref="NpgsqlException"/> saying "Failed to connect to host:5432" wrapping the
    /// <see cref="SocketException"/> that says why. The wrapper carries no verdict, so the walk has to
    /// reach the inner one.
    /// </summary>
    [Theory]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.ConnectionReset)]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.TryAgain)]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.NetworkDown)]
    [InlineData(SocketError.TimedOut)]
    public void ANetworkPathThatIsNotUpYet_IsWaitedThrough(SocketError error)
    {
        Assert.True(DatabaseAvailabilityWait.IsWorthWaitingFor(Wrapped(Socket(error))));
        // And directly, not only through a wrapper - the classification is the leaf's, either way.
        Assert.True(DatabaseAvailabilityWait.IsWorthWaitingFor(Socket(error)));
    }

    [Theory]
    [InlineData(SocketError.AccessDenied)]
    [InlineData(SocketError.AddressFamilyNotSupported)]
    [InlineData(SocketError.ProtocolNotSupported)]
    public void ASocketErrorThatIsNotOnTheList_IsNotWaitedThrough(SocketError error)
    {
        Assert.False(DatabaseAvailabilityWait.IsWorthWaitingFor(Wrapped(Socket(error))));
    }

    /// <summary>
    /// OBSERVED, and not anticipated when `8-10` was written. Starting <c>postgres:17-alpine</c> and
    /// connecting immediately does not produce <c>Connection refused</c>: Docker's port proxy binds the
    /// published port the moment the container is created, accepts the connection, and closes it
    /// because nothing is listening behind it yet. Npgsql surfaces that as "Exception while reading
    /// from stream" wrapping an <see cref="EndOfStreamException"/>. Kubernetes has the same shape
    /// wherever something terminates a connection ahead of the backend.
    /// </summary>
    [Fact]
    public void AConnectionClosedMidHandshake_IsWaitedThrough()
    {
        var observed = new NpgsqlException(
            "Exception while reading from stream",
            new EndOfStreamException("Attempted to read past the end of the stream."));

        Assert.True(DatabaseAvailabilityWait.IsWorthWaitingFor(observed));
    }

    /// <summary>Npgsql's own connect timeout: the server never answered inside the connection string's
    /// <c>Timeout</c>, which during a restart is indistinguishable from a dropped SYN.</summary>
    [Fact]
    public void AConnectTimeout_IsWaitedThrough()
    {
        Assert.True(DatabaseAvailabilityWait.IsWorthWaitingFor(
            new NpgsqlException("Exception while connecting", new TimeoutException())));
    }

    /// <summary>
    /// The walk stops at the first link that is a verdict. A <see cref="PostgresException"/> found
    /// inside a wrapper is still the server's own answer and is classified by its SQLSTATE - reaching
    /// past it, or classifying by the wrapper, would turn a wrong password into a wait.
    /// </summary>
    [Fact]
    public void AServerVerdictInsideAWrapper_IsClassifiedByItsSqlState()
    {
        Assert.False(DatabaseAvailabilityWait.IsWorthWaitingFor(Wrapped(Postgres("28P01"))));
        Assert.True(DatabaseAvailabilityWait.IsWorthWaitingFor(Wrapped(Postgres("57P03"))));
    }

    /// <summary>
    /// The default, and the design: an allow-list, so anything unrecognised fails rather than waits.
    /// The failure mode of wrongly failing is a loud, accurate error quoting the provider; the failure
    /// mode of wrongly waiting is a ninety-second silence followed by the wrong diagnosis.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnrecognisedFailures))]
    public void AnythingUnrecognised_IsNotWaitedThrough(Exception exception)
    {
        Assert.False(DatabaseAvailabilityWait.IsWorthWaitingFor(exception));
    }

    public static TheoryData<Exception> UnrecognisedFailures() =>
    [
        // A malformed connection string - a manifest mistake, not a slow start.
        new ArgumentException("Couldn't set unknown_key"),
        // An NpgsqlException that explains nothing and wraps nothing.
        new NpgsqlException("something went wrong"),
        new InvalidOperationException("the DbContext is disposed"),
    ];

    [Fact]
    public void NoExceptionAtAll_IsNotWaitedThrough() =>
        Assert.False(DatabaseAvailabilityWait.IsWorthWaitingFor(null));

    /// <summary>
    /// The give-up message names where it was trying to reach, and everything in these repositories is
    /// public - so a diagnostic that printed the password would be a worse bug than the one it was
    /// diagnosing.
    /// </summary>
    [Fact]
    public void DescribingTheTarget_NamesHostPortAndDatabase_AndNeverThePassword()
    {
        const string password = "not-a-real-password-8-10";
        var described = DatabaseAvailabilityWait.DescribeTarget(
            $"Host=a-host;Port=5433;Database=ago_chat;Username=ago;Password={password}");

        Assert.Equal("a-host:5433/ago_chat", described);
        Assert.DoesNotContain(password, described, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribingAnUnparseableTarget_SaysSoRatherThanThrowing() =>
        Assert.Equal(
            "(unparseable connection string)",
            DatabaseAvailabilityWait.DescribeTarget("Host=a-host;NotAKey=1"));
}
