using Mono.Cecil;

namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `5-19`: a hub method's parameter list is a contract with clients that cannot be forced to
/// upgrade, and this is what stops it moving.
///
/// <para><b>The failure this exists for.</b> `14-06` appended three optional parameters to
/// <c>VisitorHub.SendMessageAsync</c> and every visitor send on the live deployment began failing
/// immediately: SignalR requires one argument per declared parameter and does not fall back to a C#
/// default, so the deployed widget's four-argument invocation was refused during parsing, with no
/// server-side log and the handler never reached. 697 tests were green throughout, because every one
/// of them called the C# method directly and therefore recompiled against the new signature.</para>
///
/// <para><b>This is the second time.</b> `8-02` found the same dispatcher behaviour the same way -
/// live - and the warning was written down as a comment in `ago-widget/src/connection.ts`, a
/// different repository, which nobody editing a C# hub method would ever open. That is the whole
/// reason this rule is a test in *this* repository rather than a third comment.</para>
///
/// <para>Behaviour is proven separately, over the real wire format, by
/// <c>Ago.Chat.Integration.Tests.HubMethodArityTests</c>. This one is the standing guard: it needs
/// no containers and runs in milliseconds on every build.</para>
/// </summary>
public class HubContractTests
{
    private const string HubBaseType = "Microsoft.AspNetCore.SignalR.Hub";

    /// <summary>SignalR calls these itself; a client never invokes them, so their signatures belong
    /// to ASP.NET Core rather than to this project's contract.</summary>
    private static readonly string[] LifecycleMethods = ["OnConnectedAsync", "OnDisconnectedAsync"];

    [Fact]
    public void NoHubMethodsParameterListHasGrown()
    {
        var declared = ScanHubMethods().ToDictionary(method => method.Key, method => method.Arity);
        var expected = HubContractManifest.Methods.ToDictionary(method => method.Key, method => method.Arity);

        var grown = declared
            .Where(method => expected.TryGetValue(method.Key, out var arity) && method.Value > arity)
            .Select(method => $"{method.Key} now takes {method.Value}, was {expected[method.Key]}")
            .ToList();

        Assert.True(
            grown.Count == 0,
            "A hub method's parameter list grew. SignalR refuses an invocation that supplies fewer "
            + "arguments than the target declares - it does not fall back to a C# default - so every "
            + "client already deployed against the old signature starts failing at once, silently, "
            + "with no server-side log and the handler never reached. This is what `14-06` did to "
            + "every embedded widget, and what `8-02` found before it. Put the new capability on a "
            + "new hub method instead (`VisitorHub.SendStructuredMessageAsync` is the worked "
            + "example), then add it to HubContractManifest. Grown: " + string.Join("; ", grown));
    }

    /// <summary>
    /// Shrinking breaks the same clients just as hard, from the other direction - an invocation
    /// carrying more arguments than the target declares is refused identically. Asserted separately
    /// so the failure message can say which mistake was made.
    /// </summary>
    [Fact]
    public void NoHubMethodsParameterListHasShrunk()
    {
        var declared = ScanHubMethods().ToDictionary(method => method.Key, method => method.Arity);
        var expected = HubContractManifest.Methods.ToDictionary(method => method.Key, method => method.Arity);

        var shrunk = declared
            .Where(method => expected.TryGetValue(method.Key, out var arity) && method.Value < arity)
            .Select(method => $"{method.Key} now takes {method.Value}, was {expected[method.Key]}")
            .ToList();

        Assert.True(
            shrunk.Count == 0,
            "A hub method's parameter list shrank, which SignalR refuses exactly as it refuses a list "
            + "that grew. Shrunk: " + string.Join("; ", shrunk));
    }

    /// <summary>
    /// A method nobody listed, and a listed method that no longer exists. Both are the same failure:
    /// the manifest's whole value is that a reviewer can trust it, and a list that silently falls
    /// behind the code is worse than no list, because it reads as though somebody checked.
    /// </summary>
    [Fact]
    public void TheManifestNamesExactlyTheMethodsAClientCanInvoke()
    {
        var declared = ScanHubMethods().Select(method => method.Key).ToHashSet(StringComparer.Ordinal);
        var expected = HubContractManifest.Methods.Select(method => method.Key).ToHashSet(StringComparer.Ordinal);

        var unlisted = declared.Except(expected).Order().ToList();
        var stale = expected.Except(declared).Order().ToList();

        Assert.True(
            unlisted.Count == 0,
            "These hub methods are invokable by a client and are not in HubContractManifest. Add them, "
            + "and say who already calls them - a new method with no caller is free to change, and one "
            + "with a deployed caller is not: " + string.Join("; ", unlisted));

        Assert.True(
            stale.Count == 0,
            "HubContractManifest names methods that no longer exist. **Deleting a hub method a client "
            + "still invokes breaks it exactly as changing its arity does** - if that was deliberate, "
            + "the deployed clients have to go first: " + string.Join("; ", stale));
    }

    /// <summary>
    /// <b>The rule, proven able to fail.</b> The same permanently-violating-fixture technique
    /// `0-02` used for layering and `17-01` for tenant scoping - a rule that has only ever been
    /// observed passing is not evidence, and this particular rule exists because a green suite was
    /// trusted once already.
    /// </summary>
    [Fact]
    public void TheRule_CountsTheParametersAClientWouldHaveToSupply()
    {
        var fixtureMethods = ScanHubMethods(OwnAssembly())
            .Where(method => method.Key.StartsWith("ArityFixtureHub.", StringComparison.Ordinal))
            .ToDictionary(method => method.Key, method => method.Arity);

        // Optional parameters count. That is the entire misunderstanding behind `14-06`: they are
        // optional to a C# caller and mandatory to a client on the wire.
        Assert.Equal(4, fixtureMethods["ArityFixtureHub.WithOptionalTrailingParametersAsync"]);
        Assert.Equal(1, fixtureMethods["ArityFixtureHub.WithOneParameterAsync"]);

        // And a CancellationToken does not, because SignalR supplies it rather than the client.
        Assert.Equal(1, fixtureMethods["ArityFixtureHub.WithACancellationTokenAsync"]);

        // The lifecycle overrides are excluded, so a hub is never reported as having a contract it
        // does not have.
        Assert.DoesNotContain("ArityFixtureHub.OnConnectedAsync", fixtureMethods.Keys);
    }

    private static IEnumerable<HubContractManifest.HubMethod> ScanHubMethods() =>
        ScanHubMethods(TestAssemblies.Api.Cecil);

    private static IEnumerable<HubContractManifest.HubMethod> ScanHubMethods(AssemblyDefinition assembly) =>
        from type in assembly.MainModule.GetTypes()
        where InheritsFromHub(type)
        from method in type.Methods
        where method.IsPublic
            && !method.IsConstructor
            && !method.IsStatic
            && !method.IsGetter
            && !method.IsSetter
            && !LifecycleMethods.Contains(method.Name, StringComparer.Ordinal)
            && !method.CustomAttributes.Any(a => a.AttributeType.Name == "CompilerGeneratedAttribute")
        select new HubContractManifest.HubMethod($"{type.Name}.{method.Name}", ClientSuppliedArity(method), string.Empty);

    /// <summary>
    /// How many arguments a client has to put on the wire. Every declared parameter counts, optional
    /// or not - and a <c>CancellationToken</c> does not, because SignalR binds that one itself from
    /// the connection rather than from the invocation.
    /// </summary>
    private static int ClientSuppliedArity(MethodDefinition method) =>
        method.Parameters.Count(parameter => parameter.ParameterType.FullName != "System.Threading.CancellationToken");

    private static bool InheritsFromHub(TypeDefinition type)
    {
        for (var current = type.BaseType; current is not null;)
        {
            if (current.FullName == HubBaseType)
            {
                return true;
            }

            var resolved = TryResolve(current);
            if (resolved is null)
            {
                // Reached a type from an assembly Cecil cannot resolve by path - which for a hub is
                // Hub itself, whose FullName was already compared above. Stopping here rather than
                // guessing keeps a resolution failure from silently reading as "not a hub".
                return false;
            }

            current = resolved.BaseType;
        }

        return false;
    }

    private static TypeDefinition? TryResolve(TypeReference reference)
    {
        try
        {
            return reference.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    private static AssemblyDefinition OwnAssembly() =>
        AssemblyDefinition.ReadAssembly(
            Path.Combine(AppContext.BaseDirectory, "Ago.Chat.Architecture.Tests.dll"));
}
