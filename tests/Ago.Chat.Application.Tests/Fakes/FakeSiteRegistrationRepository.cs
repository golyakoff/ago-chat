using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>In-memory stand-in for the one wide bootstrap transaction
/// (<see cref="ISiteRegistrationRepository"/>) - tracks every package it was asked to persist, and
/// can be told to simulate losing the unique-index race (<c>RegisterSiteHandlerTests</c>'s own
/// "concurrent registration" case) the same way <see cref="RateLimitedFakeRateLimiter"/> stands in
/// for a real Redis bucket denying.</summary>
public sealed class FakeSiteRegistrationRepository : ISiteRegistrationRepository
{
    private readonly List<SiteRegistration> _registered = [];

    public bool DenyNextRegistration { get; set; }

    public IReadOnlyList<SiteRegistration> Registered => _registered;

    public Task<bool> TryRegisterAsync(SiteRegistration registration, CancellationToken cancellationToken)
    {
        if (DenyNextRegistration)
        {
            DenyNextRegistration = false;
            return Task.FromResult(false);
        }

        _registered.Add(registration);
        return Task.FromResult(true);
    }
}
