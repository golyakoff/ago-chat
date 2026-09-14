namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `24-17`: `ITenantScopeInspector`'s answer - deliberately plain data (no Mono.Cecil type appears
/// anywhere in this record), because a port's return type is part of `Application`'s own public
/// surface and must stay readable by a caller that has never heard of the technology behind it.
///
/// <para><b>`UnaccountedKeys` is the field the backlog item (`24-17`) built this whole endpoint to
/// surface distinctly.</b> Everything else here can drift purely because the codebase grew - a
/// documentation-staleness fact. A non-empty `UnaccountedKeys` cannot: it means an entry point exists
/// that is neither RBAC-gated nor listed in `TenantScopeExemptions` with a reason, which is exactly
/// what `Ago.Chat.Architecture.Tests.TenantScopeTests` already fails the build over - so seeing it
/// non-empty here can only mean the running deployment is newer than the last green build, never that
/// isolation itself has a hole `TenantScopeTests` would have missed.</para>
///
/// <para><b>`ExemptButAlsoLooksGated` is the second integrity check `scan_entry_points.py` and
/// `TenantScopeTests.NoExemption_IsStale` both make</b> - an exemption entry whose handler has since
/// grown a real permission check is a stale claim sitting in a file whose entire value is that a
/// reviewer can trust what it says. Reported for the same reason `UnaccountedKeys` is: a fact worth a
/// human's attention, not merely a number that moved.</para>
/// </summary>
/// <param name="EntryPoints">Every public method of every `*Handler` class in
/// `Ago.Chat.Application.UseCases` - row 1's first half.</param>
/// <param name="HandlerClasses">How many distinct `*Handler` classes those entry points come from -
/// row 1's second half.</param>
/// <param name="RbacGated">Entry points that take a `SiteId` and call `IPermissionChecker` - row 2.</param>
/// <param name="ExemptListed">Entry points listed in `TenantScopeExemptions` with a stated reason -
/// row 3. Deliberately just the count: the reasons themselves are prose meant for a reviewer reading
/// source, not a wire payload, and `docs/architecture/tenant-isolation.md`/`TenantScopeExemptions.cs`
/// are still where a person reads them.</param>
/// <param name="UnaccountedKeys">Entry points that are neither `RbacGated` nor `ExemptListed` - see
/// this record's own remarks above for why a non-empty list here is a build-already-red state, not a
/// new finding.</param>
/// <param name="ExemptButAlsoLooksGated">Exemption entries whose own entry point now also satisfies
/// `IsRbacGated` - a stale exemption, not a security gap.</param>
public sealed record TenantScopeSnapshot(
    int EntryPoints,
    int HandlerClasses,
    int RbacGated,
    int ExemptListed,
    IReadOnlyList<string> UnaccountedKeys,
    IReadOnlyList<string> ExemptButAlsoLooksGated);
