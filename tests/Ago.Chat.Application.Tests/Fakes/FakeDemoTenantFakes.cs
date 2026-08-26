using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>
/// `8-07`: in-memory <see cref="IDemoTenantRepository"/>. <see cref="LiveCount"/> is settable so the
/// cap can be driven to its boundary without minting fifty tenants first - the cap is a number the
/// handler compares against, and a test that had to reach it the slow way would be testing the loop
/// rather than the comparison.
/// </summary>
public sealed class FakeDemoTenantRepository : IDemoTenantRepository
{
    private readonly List<SiteId> _deleted = [];

    public int LiveCount { get; set; }

    public IReadOnlyList<ExpiredDemoTenant> Expired { get; set; } = [];

    public IReadOnlyList<string> AttachmentObjectKeys { get; set; } = [];

    public IReadOnlyList<SiteId> Deleted => _deleted;

    public Task<int> CountLiveAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(LiveCount);

    public Task<IReadOnlyList<ExpiredDemoTenant>> ListExpiredAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExpiredDemoTenant>>([.. Expired.Take(limit)]);

    public Task<IReadOnlyList<string>> ListAttachmentObjectKeysAsync(
        SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult(AttachmentObjectKeys);

    public Task DeleteSiteAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        _deleted.Add(siteId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// `8-07`: records what it was asked to create and delete, and can be told to refuse.
///
/// <para><see cref="Deleted"/> holding the identity it just created is what proves
/// <c>MintDemoTenantHandler</c> compensates when the registration that follows fails - the window the
/// handler cannot order its way out of, because Keycloak refuses a caller-chosen id.</para>
/// </summary>
public sealed class FakeDemoIdentityProvisioner : IDemoIdentityProvisioner
{
    private readonly List<(string SubjectId, string Username, string Password)> _created = [];
    private readonly List<string> _deleted = [];

    public Error? RefuseWith { get; set; }

    public IReadOnlyList<(string SubjectId, string Username, string Password)> Created => _created;

    public IReadOnlyList<string> Deleted => _deleted;

    /// <summary>The subject id this fake hands back, standing in for the one Keycloak assigns. Fixed
    /// rather than random so a test can assert the operator row carries exactly it.</summary>
    public string AssignedSubjectId { get; set; } = "11111111-1111-1111-1111-111111111111";

    public Task<Result<string>> CreateAsync(
        string username, string password, CancellationToken cancellationToken)
    {
        if (RefuseWith is { } error)
        {
            return Task.FromResult(Result<string>.Failure(error));
        }

        _created.Add((AssignedSubjectId, username, password));
        return Task.FromResult(Result<string>.Success(AssignedSubjectId));
    }

    public Task DeleteAsync(string subjectId, CancellationToken cancellationToken)
    {
        _deleted.Add(subjectId);
        return Task.CompletedTask;
    }
}

/// <summary>Fixed output, so a test can assert the password reached the identity provider and the
/// response unchanged. The real generator's randomness is its whole point and is therefore the one
/// thing a handler test must not depend on.</summary>
public sealed class FakeDemoCredentialGenerator(string password = "fake-demo-password")
    : IDemoCredentialGenerator
{
    private int _suffixes;

    public string NewPassword() => password;

    // Fixed but increasing: a handler test needs the username to be predictable, and two mints in one
    // test still have to differ - which is the property whose absence caused the real bug this port
    // documents.
    public string NewUsernameSuffix() => $"aaaa{++_suffixes:0000}";
}
