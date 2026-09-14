using Mono.Cecil;

namespace Ago.Chat.Infrastructure.TenantScopeDiagnostics;

/// <summary>
/// `17-01`: the mechanical half of the tenant-isolation guard - given an assembly, work out which of
/// its use-case entry points are RBAC-gated and which are not, so
/// <c>Ago.Chat.Architecture.Tests.TenantScopeTests</c> can insist that every "not" is an argued entry
/// in <see cref="TenantScopeExemptions"/> rather than an omission nobody noticed.
///
/// <para><b>`24-17`: moved here, out of the test project, so a second caller could exist without
/// copying it.</b> This class and <see cref="TenantScopeExemptions"/> used to live in
/// <c>Ago.Chat.Architecture.Tests</c> alone, reachable only at build time. `24-17` needed the identical
/// fact reachable at runtime too - a platform-owner endpoint that answers from the assembly that is
/// actually deployed, not from a checkout somebody may not have open - and the only way for both
/// callers to be reading the *same* fact, rather than two copies free to drift, was to give the logic
/// one home neither of them owns exclusively. <c>Ago.Chat.Infrastructure.TenantScopeDiagnostics</c> is
/// that home: an <c>Infrastructure.*</c> project (CLAUDE.md rule 2 - "every external resource sits
/// behind a port... implemented in an Infrastructure.* project") because what this class actually does
/// is read a concrete external resource, a `.dll` on disk, with Mono.Cecil; the alternative - leaving
/// it in the test project and having `Ago.Chat.Api` reference a test assembly at runtime, or inlining
/// Mono.Cecil into `Ago.Chat.Application` itself - either ships test code in production or puts an
/// IL-reading library inside the layer the dependency rule keeps free of exactly that. The test project
/// now references this one instead of holding its own copy (see its own `.csproj` remarks), and
/// <see cref="Ago.Chat.Application.Abstractions.ITenantScopeInspector"/> is the port that lets
/// `Ago.Chat.Api`'s owner-only endpoint reach it without `Ago.Chat.Application` ever knowing Mono.Cecil
/// exists.</para>
///
/// <para><b>Per public method, not per type.</b> Several handlers carry a visitor entry point and an
/// operator entry point side by side (<c>ConfirmAttachmentHandler</c>,
/// <c>GetConversationHistoryHandler</c>): only the operator half takes a <c>SiteId</c> and only it
/// calls <see cref="Ago.Chat.Application.Abstractions.IPermissionChecker"/>, so a type-level rule
/// would call the whole type compliant on the strength of a check the visitor half never
/// makes.</para>
///
/// <para><b>Async is why this reads IL rather than reflection.</b> An <c>async</c> method's body is
/// moved wholesale into a compiler-generated state machine, so the call to
/// <c>HasPermissionAsync</c> is not in the method a scanner naively looks at - it is in
/// <c>&lt;HandleAsync&gt;d__N.MoveNext</c>. <see cref="BodiesOf"/> follows
/// <c>AsyncStateMachineAttribute</c> to find it; scanning the assembly wholesale would find the call
/// but could not say which entry point made it.</para>
/// </summary>
public static class TenantScopeRule
{
    private const string PermissionCheckerInterface = "Ago.Chat.Application.Abstractions.IPermissionChecker";

    private const string SiteIdType = "Ago.Chat.Domain.SiteId";

    /// <summary>One public entry point of one handler, and the two facts the rule turns on.</summary>
    public sealed record EntryPoint(string Key, bool CarriesSiteId, bool ChecksPermission)
    {
        /// <summary>The shape the rule is built to require: a tenant-scoped input, gated by the one
        /// port that can answer "may this operator act on this site".</summary>
        public bool IsRbacGated => CarriesSiteId && ChecksPermission;
    }

    /// <summary>Every public entry point of every <c>*Handler</c> in <paramref name="assembly"/>,
    /// keyed <c>Namespace.Type.Method</c> - the key an exemption entry names.</summary>
    public static IReadOnlyList<EntryPoint> Scan(AssemblyDefinition assembly) =>
    [
        .. from type in assembly.MainModule.GetTypes()
           where type.IsClass && type.Name.EndsWith("Handler", StringComparison.Ordinal) && !IsCompilerGenerated(type)
           from method in type.Methods
           where IsEntryPoint(method)
           select new EntryPoint(
               $"{type.FullName}.{method.Name}",
               CarriesSiteId(method),
               CallsPermissionChecker(type, method)),
    ];

    private static bool IsEntryPoint(MethodDefinition method) =>
        method.IsPublic
        && !method.IsConstructor
        && !method.IsGetter
        && !method.IsSetter
        && !IsCompilerGenerated(method);

    /// <summary>A parameter that is a <c>SiteId</c>, or a command/query record with a
    /// <c>SiteId</c>-typed member. Deliberately shallow - one level, no recursion into nested
    /// records: every command and query in this codebase is a flat record of primitives and
    /// strongly-typed ids, and a rule that silently reached deeper would be describing a shape the
    /// convention does not actually have.</summary>
    private static bool CarriesSiteId(MethodDefinition method) =>
        method.Parameters.Any(parameter =>
            parameter.ParameterType.FullName == SiteIdType || HasSiteIdMember(parameter.ParameterType));

    private static bool HasSiteIdMember(TypeReference parameterType)
    {
        var resolved = TryResolve(parameterType);
        if (resolved is null)
        {
            return false;
        }

        return resolved.Properties.Any(p => p.PropertyType.FullName == SiteIdType)
            || resolved.Fields.Any(f => f.FieldType.FullName == SiteIdType);
    }

    private static bool CallsPermissionChecker(TypeDefinition handler, MethodDefinition method) =>
        BodiesOf(handler, method).Any(body => body.Body.Instructions.Any(instruction =>
            instruction.Operand is MethodReference called
            && called.DeclaringType.FullName == PermissionCheckerInterface));

    /// <summary>The method's own body plus, for an <c>async</c> method, its compiler-generated state
    /// machine's - see this class's own remarks on why that indirection is unavoidable here.</summary>
    private static IEnumerable<MethodDefinition> BodiesOf(TypeDefinition handler, MethodDefinition method)
    {
        if (method.HasBody)
        {
            yield return method;
        }

        var stateMachine = method.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.Name == "AsyncStateMachineAttribute");
        if (stateMachine?.ConstructorArguments.Count is not > 0
            || stateMachine.ConstructorArguments[0].Value is not TypeReference machineType)
        {
            yield break;
        }

        // Resolved against the declaring type's own nested types first: the state machine is nested
        // inside the handler, and Cecil's own Resolve() needs an assembly resolver that may not be
        // configured for an assembly loaded by path.
        var resolved = handler.NestedTypes.FirstOrDefault(t => t.FullName == machineType.FullName)
            ?? TryResolve(machineType);
        if (resolved is null)
        {
            yield break;
        }

        foreach (var machineMethod in resolved.Methods.Where(m => m.HasBody))
        {
            yield return machineMethod;
        }
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

    private static bool IsCompilerGenerated(ICustomAttributeProvider member) =>
        member.CustomAttributes.Any(a => a.AttributeType.Name == "CompilerGeneratedAttribute");
}
