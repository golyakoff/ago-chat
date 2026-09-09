using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-93`: the leg `adr/0164` decided to encrypt - `Ago.Chat.Api`'s outbound provisioning call
/// (`HttpModuleRegistrationGateway`, `X-Ago-Module-Provisioning-Secret`) reaching `ago-calendar-api`
/// over TLS, trusting `22-24`'s own internal CA rather than any public one.
///
/// <para><b>What this proves, and what it deliberately does not.</b> The real production mechanism
/// (`ago-chat`'s own `Dockerfile`) trusts the internal CA by baking `internal-ca.crt` into the image's
/// OS certificate store with `update-ca-certificates` - there is no C# code path that decides to trust
/// it, because <c>ChatModule.ConfigureServices</c> registers `HttpModuleRegistrationGateway` against a
/// perfectly plain `HttpClient` (<c>services.AddHttpClient&lt;HttpModuleRegistrationGateway&gt;()</c>,
/// no certificate handling of any kind). That means the interesting behaviour cannot be unit-tested -
/// it lives entirely in what the OS trust store contains at the moment `SslStream` builds a chain, and
/// mutating the real OS trust store from a test would be destructive, `sudo`-gated, and not portable
/// across the machines this suite runs on. <see cref="X509ChainTrustMode.CustomRootTrust"/> is the
/// sanctioned, mutation-free .NET equivalent: "trust exactly this one extra root, and only for this
/// call" is exactly what `update-ca-certificates` achieves for the whole process, at the scope a test
/// can safely control. This is not a test of the Dockerfile itself (`22-24`'s own report already
/// verified that step, offline, against a real built image) - it is a test that the *application code*
/// this item touches (nothing) behaves correctly against a real TLS handshake once a caller trusts the
/// signing root, and correctly refuses when it does not, using the exact production class rather than a
/// stand-in.</para>
///
/// <para><b>A real Kestrel host on a real loopback socket, not `TestServer`.</b> `TestServer`'s
/// in-memory transport never runs a TLS handshake at all, so it cannot exercise the one thing this
/// item is about. This mirrors this same test project's own `SubscriptionRenewalJobTests.
/// BuildFakeYooKassaHostAsync` precedent for a real external HTTP dependency, extended to bind
/// `UseHttps` with a certificate generated in-process (`System.Security.Cryptography.X509Certificates.
/// CertificateRequest`) rather than the real `internal-ca.key` - that private key is deliberately
/// never committed anywhere (`ago-deploy`'s own `internal-ca.key.example`), so a committed, repeatable
/// test needs its own throwaway root, signing its own throwaway leaf, proving the identical mechanism
/// without ever touching the real one.</para>
/// </summary>
public sealed class ModuleRegistrationGatewayInternalTlsTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly ModuleKey ModuleKey = new("calendar");

    /// <summary>
    /// `23-93`'s own fails-before: the exact same leaf certificate, signed by a root nobody has told
    /// this caller to trust, is refused - the production default (a freshly built `HttpClient` with no
    /// extra trust configured) never reaches the module at all, and `HttpModuleRegistrationGateway`
    /// reports that the same way it reports any other unreachable module, per its own remarks: one
    /// exception, whatever the underlying cause. This is what a pod that has not yet had the internal
    /// CA baked into its trust store actually experiences against `https://ago-calendar-api:443` -
    /// the exact failure `23-92`'s own comment in `ago-deploy` warned against switching to blindly.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_OverHttps_WhenTheCallerDoesNotTrustTheInternalCa_IsRefused()
    {
        using var pki = GenerateRootAndLeaf();
        await using var host = await BuildHttpsModuleHostAsync(pki.Leaf);

        using var untrustingClient = new HttpClient(new SocketsHttpHandler());
        var gateway = new HttpModuleRegistrationGateway(untrustingClient);

        var target = new ModuleRegistrationTarget(ModuleKey, SiteId, new Uri(host.BaseUrl));

        var exception = await Assert.ThrowsAsync<ModuleUnreachableException>(
            () => gateway.RegisterAsync(
                target, new ModuleCredential("cred_1234567890123456789012345678"), new ModuleProvisioningSecret("secret_1234567890123456789012"),
                "Test Site", CancellationToken.None));

        Assert.Equal(ModuleKey, exception.ModuleKey);
    }

    /// <summary>
    /// `23-93`'s own real proof: the identical leaf, over the identical connection, succeeds the
    /// moment the caller trusts the signing root - <see cref="X509ChainTrustMode.CustomRootTrust"/> is
    /// this test's stand-in for what `update-ca-certificates` does to the whole OS trust store inside
    /// the real image (this class's own remarks). Proves the leg end to end through the real
    /// production gateway class: a real TCP connection, a real TLS 1.2+ handshake, a real chain build,
    /// and the real `PUT .../module-registrations/{siteId}` request `RegisterAsync` sends.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_OverHttps_WhenTheCallerTrustsTheInternalCa_Succeeds()
    {
        using var pki = GenerateRootAndLeaf();
        await using var host = await BuildHttpsModuleHostAsync(pki.Leaf);

        using var trustingHandler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is not X509Certificate2 presented)
                    {
                        return false;
                    }

                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(pki.Root);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    return chain.Build(presented);
                },
            },
        };
        using var trustingClient = new HttpClient(trustingHandler);
        var gateway = new HttpModuleRegistrationGateway(trustingClient);

        var target = new ModuleRegistrationTarget(ModuleKey, SiteId, new Uri(host.BaseUrl));

        // Throws ModuleUnreachableException on failure - reaching the line after it is the proof.
        await gateway.RegisterAsync(
            target, new ModuleCredential("cred_1234567890123456789012345678"), new ModuleProvisioningSecret("secret_1234567890123456789012"),
            "Test Site", CancellationToken.None);

        Assert.True(host.SawRegisterRequest);
    }

    /// <summary>
    /// A throwaway root CA and a leaf it signs for `127.0.0.1` - built with
    /// <see cref="CertificateRequest"/> rather than shelling out to `openssl`, so this test has no
    /// external tool dependency and runs identically wherever `dotnet test` does. ECDSA P-256,
    /// matching the algorithm `ago-deploy`'s own real leaf (`internal-tls.yaml`) uses - not because
    /// the algorithm matters to what this test proves, but so a reader comparing the two is not left
    /// wondering whether it does.
    /// </summary>
    private static GeneratedPki GenerateRootAndLeaf()
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest(
            new X500DistinguishedName("CN=ago-internal-ca-test-root"), rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));

        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafRequest = new CertificateRequest(
            new X500DistinguishedName("CN=127.0.0.1"), leafKey, HashAlgorithmName.SHA256);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        leafRequest.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true)); // serverAuth
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
        sanBuilder.AddDnsName("localhost");
        leafRequest.CertificateExtensions.Add(sanBuilder.Build());

        using var leafWithoutKey = leafRequest.Create(
            root, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1), Guid.NewGuid().ToByteArray());
        // `CreateSelfSigned`/`Create` return a certificate with no exportable private key attached in
        // a form Kestrel's `UseHttps` can present - re-combining with the leaf's own key is the
        // documented pattern (`X509Certificate2.CopyWithPrivateKey`) for exactly this case.
        var leafWithKey = leafWithoutKey.CopyWithPrivateKey(leafKey);

        return new GeneratedPki(
            X509CertificateLoader.LoadCertificate(root.Export(X509ContentType.Cert)),
            X509CertificateLoader.LoadPkcs12(leafWithKey.Export(X509ContentType.Pfx), password: null));
    }

    /// <summary>A real Kestrel host bound to `https://127.0.0.1:0` (the OS assigns the port, read back
    /// through <see cref="IServerAddressesFeature"/> - `SubscriptionRenewalJobTests.
    /// BuildFakeYooKassaHostAsync`'s own precedent, extended with `UseHttps` on the listener). Answers
    /// exactly the one route `HttpModuleRegistrationGateway.RegisterAsync` calls -
    /// `PUT /api/v1/module-registrations/{siteId}` - with 200, the only thing that handler's own
    /// `SendAsync` checks (it never parses `RegisterAsync`'s response body).</summary>
    private static async Task<FakeModuleHost> BuildHttpsModuleHostAsync(X509Certificate2 leaf)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(System.Net.IPAddress.Loopback, 0, listenOptions => listenOptions.UseHttps(leaf)));

        var app = builder.Build();
        var state = new FakeModuleHost.State();
        app.MapPut(
            "api/v1/module-registrations/{siteId}",
            (Guid siteId) =>
            {
                state.SawRegisterRequest = true;
                return Results.Ok();
            });

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var baseUrl = addresses.First(a => a.StartsWith("https://", StringComparison.Ordinal)) + "/";

        return new FakeModuleHost(app, baseUrl, state);
    }

    private sealed record GeneratedPki(X509Certificate2 Root, X509Certificate2 Leaf) : IDisposable
    {
        public void Dispose()
        {
            Root.Dispose();
            Leaf.Dispose();
        }
    }

    private sealed class FakeModuleHost(WebApplication app, string baseUrl, FakeModuleHost.State state) : IAsyncDisposable
    {
        public string BaseUrl => baseUrl;

        public bool SawRegisterRequest => state.SawRegisterRequest;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        public sealed class State
        {
            public bool SawRegisterRequest { get; set; }
        }
    }
}
