# Ago.Chat.FakeCrm

A small, real HTTP server standing in for a shop's CRM - the thing `6-05`'s webhook dispatcher and
`6-06`'s load test point outbound deliveries at instead of a real third party, so "the breaker opens,
the bulkhead holds" (`docs/architecture/resilience.md`) is provable against a process that genuinely
hangs, genuinely 5xxs, and genuinely refuses a connection, not a mock that always answers instantly.

Backlog item `6-04` (`docs/backlog/6-04-fake-crm-test-harness.md`). Out of scope: any persistence or
delivery log of its own (disposable test double, not a product), multiple simultaneous behaviours
(one behavior-switchable server, not a fleet), and load-generation itself.

## Running it

```
cd ago-chat
dotnet run --project tests/Ago.Chat.FakeCrm
```

Binds `http://localhost:5290` by default (`Properties/launchSettings.json`) - that is a convenience
default for a human running it by hand locally, **not a fixed contract** `6-05`/`6-06` are required to
use; override it the normal ASP.NET Core ways (`--urls`, `ASPNETCORE_URLS`) when a driver needs a
specific or dynamically-chosen port, the same way `Ago.Chat.FakeCrm.Tests`' own
`FakeCrmProcessFixture` does (binds port 0, reads back whatever the OS actually assigned, to avoid
colliding with anything else on the machine).

Required configuration: `FakeCrm:SigningSecret` (env var `FakeCrm__SigningSecret`) - the HMAC key the
signature check below verifies against. There is no built-in default; the process fails fast at
startup if it is empty. `appsettings.Development.json` ships a fixed, published, test-only value
(`fake-crm-test-signing-secret-do-not-use-in-prod`) for the local `dotnet run` loop - whatever calls
this harness must sign with the same value it is configured with.

## The endpoint

`POST /webhooks/deliver` on the main port. Every request, regardless of which personality it goes on
to ask for, must carry:

```
X-Ago-Signature: t=<unix-seconds>,v1=<hex-hmac-sha256 over "{t}.{raw request body}">
```

HMAC-SHA256, keyed with `FakeCrm:SigningSecret`. A request is rejected with `401` and a JSON body
naming the reason (`HeaderMissing`, `HeaderMalformed`, `TimestampStale`, `SignatureMismatch`) if:

- the header is absent or does not parse as `t=...,v1=...`,
- the HMAC does not match the raw body actually sent (a tampered body), or
- `|now - t|` exceeds `FakeCrm:SignatureToleranceSeconds` (default **300s** - a replay window, not a
  request-processing timeout).

**Assumption flagged for reconciliation**: this scheme is `6-03`'s webhook-registration ADR to own,
not this project's, and that ADR was being written concurrently with this harness - its final text was
not readable while this was built. The header shape (`t=`/`v1=` prefixes), the HMAC-SHA256 algorithm,
and the "hash `{t}.{body}`" construction all come directly from this backlog item's own description.
The **300-second tolerance** and the **401 status code** are this project's own choices where that
description did not give a number - 300s is Stripe's own published default for the same style of
check. If `6-03`'s actual text picks a different tolerance or response shape, `FakeCrmOptions` is the
one place to change it.

`Ago.Chat.FakeCrm.WebhookSignatureVerifier` is a plain static class with no HTTP/DI dependency
(`Verify` and `Sign`), so it is usable directly by `6-05`'s own tests to build correctly-signed
requests without depending on this project's ASP.NET Core wiring.

## Selecting a personality

Three of the four are chosen by the `X-Fake-Crm-Behavior` header on `/webhooks/deliver` (parsed by
`FakeCrmBehavior.Parse`):

| Header value | Behaviour |
|---|---|
| absent, or `succeeds` | Immediate `200` |
| `500` | Immediate `500` |
| `503` or `5xx` | Immediate `503` |
| `hang` | Never responds on its own - only the caller's own timeout/cancellation ends the call |
| `hang-<seconds>` (e.g. `hang-30s`) | Holds the connection open for exactly that long, then `200` |

An unrecognised value gets `400`, not `401` - distinct from a signature failure, so a typo in a driver
script does not look like a broken signing implementation.

**`6-05` addition**: `FakeCrm:DefaultBehavior` (env var `FakeCrm__DefaultBehavior`) sets the personality
used when a request carries *no* `X-Fake-Crm-Behavior` header at all - the header still wins outright
when present, so this is purely additive and every existing header-driven test is unaffected. This
exists because `6-05`'s own dispatcher is a production HTTP client calling what it treats as a real
tenant endpoint; it has no business ever sending this harness's own test-only header. Proving its
per-endpoint circuit breaker or per-tenant bulkhead needs several endpoints answering with different,
fixed personalities at once, driven only by which URL a registered `WebhookEndpoint` points at - so
`6-05`'s own integration tests start one `Ago.Chat.FakeCrm` process per fixed personality needed
(`FakeCrm__DefaultBehavior=5xx`, `FakeCrm__DefaultBehavior=hang-30s`, etc.), each on its own port,
rather than one process serving every personality via a header the dispatcher never sends.

The fourth personality, **disappears**, is deliberately **not** a header value. Refusing a TCP
connection has to happen before any HTTP request on it is even readable, so it cannot be chosen from
inside the request it would need to inspect to make that choice - the backlog's own illustrative
header list (`X-Fake-Crm-Behavior: hang-30s|5xx|refuse`) cannot be taken literally for this one
personality, and this harness resolves that by giving "disappears" its **own port**:

- Connect to `FakeCrm:DisappearPort` (default `5291`) instead of the main port.
- Default (`FakeCrm:DisappearPortListens=true`): every connection is accepted, then immediately reset
  (`DisappearedConnectionListener` - `SO_LINGER` with a zero timeout, forcing a TCP RST instead of a
  graceful close, before a single byte of the request is ever read). The caller sees a real
  `SocketException` (`SocketError.ConnectionReset`), not an HTTP-level error.
- `FakeCrm:DisappearPortListens=false`: the port is never bound at all, proving the backlog's other
  named form ("closed port") - `SocketError.ConnectionRefused` at connect time.

A live finding while proving this personality: on this .NET/Windows combination, `TcpClient.LingerState
= new LingerOption(true, 0)` followed by `Close()` silently produced an *ordinary* graceful close (the
client's read returned 0 bytes, no exception) rather than an RST - setting the same option via
`Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, ...)` directly, and reading
it back to confirm it stuck, is what actually forces the reset. `DisappearedConnectionListener` uses
the latter; see its own remarks.

## Health

`GET /healthz/live`, `GET /healthz/ready` on the main port - both trivial (no dependency to report on,
matching `Ago.Chat.Webhooks`' own `Program.cs` before it had one).

## Proof (`../Ago.Chat.FakeCrm.Tests/`)

- `FakeCrmProcessFixture` launches this project's own built `.dll` as a genuinely separate `dotnet`
  process (`Process.Start`, located via `typeof(Program).Assembly.Location`) on dynamically-assigned
  ports, waits for `/healthz/live` to answer, and tears it down after the test collection - not an
  in-process `TestServer`.
- `FakeCrmPersonalityTests` proves all four personalities against that real running process with a
  real `HttpClient`/`TcpClient`: `succeeds` (default header), `500`/`503`/`5xx` (near-instant),
  `hang-2s` (holds for at least 2s then `200`), indefinite `hang` (a 1s client-side timeout is what
  ends it, not the harness), and `disappears` (both a raw-socket read and an `HttpClient` call
  surfacing `SocketException`/`ConnectionReset`). It also proves the signature check rejects a
  tampered body, a stale timestamp, and a missing header, all with `401`, over that same real process.
- `WebhookSignatureVerifierTests` unit-tests `WebhookSignatureVerifier`'s own logic directly (valid,
  tampered, wrong secret, stale, future-skewed, missing, and several malformed-header shapes) -
  fast and deterministic, no process involved.
