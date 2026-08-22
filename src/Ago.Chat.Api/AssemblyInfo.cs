using System.Runtime.CompilerServices;

// `5-05`: AgoClaimTypes/ClaimsPrincipalExtensions/OperatorIdentityClaimsTransformation stay internal
// to everything else - OperatorOidcAuthenticationTests builds its own minimal TestServer host
// replicating Program.cs's Operator-scheme wiring (real Postgres + real Keycloak, no mocking) and
// needs them directly, the same reason Ago.Chat.Infrastructure.Postgres grants this same test
// project access to its own internals.
[assembly: InternalsVisibleTo("Ago.Chat.Integration.Tests")]
