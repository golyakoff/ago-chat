# Running the attachment quota reconciliation report

`25-79`. `DeleteAttachmentHandler` never released `ISiteAttachmentStorageBudget`'s reservation from
`5-08` until this item fixed it — every attachment ever deleted through the console's per-message
delete action stayed counted against its tenant's quota forever, a one-directional leak that could
only grow `sites.attachment_bytes_reserved`, never shrink it. The fix stops the leak going forward. It
does nothing for bytes a real deployment already double-counted before it shipped. This procedure is
how a person finds out whether that already happened, on demand.

**It reports. It never repairs.** The identical posture
`ago-deploy/k8s/tenancy-reconciliation-report.sh` already takes, for the identical reason
(`ago-root/docs/runbooks/tenancy-reconciliation.md`): a script that rewrites a tenant's own
billing/quota number on the strength of its own join is catastrophic the first time the join is
wrong. What a non-zero result means, and what to do about it, is a person's decision — this file's
last section says where that decision leads.

**Manual, on demand — deliberately not a scheduled workload.** The same choice
`tenancy-reconciliation-report.sh` and `tools/update-landing-prices-from-db.sh` (this repo) both
already made: a person runs this when they want the answer to "is it drifting right now?", not a
continuously-running guarantee.

**One database, one join** — unlike the tenancy report, which spans two separate Postgres databases,
`sites.attachment_bytes_reserved` and `attachments` live in the same `ago_chat` database, so this
needs exactly one connection string, the same single-database on-demand shape
`tools/update-landing-prices-from-db.sh`'s own runbook already documents.

## What you need before you start

- A connection to the live `ago_chat` database — the same reachability
  `ago-root/docs/runbooks/backup-and-restore.md`'s own drills need: either a direct connection if you
  have one, or an SSH-tunnelled `kubectl port-forward` chain against the real node.
- `psql` on your own machine, or a container (e.g.
  `docker run --rm --entrypoint psql postgres:17-alpine ...`) if you don't want it installed directly.

## Running it

```bash
cd C:/git/ago/ago-chat
bash tools/attachment-quota-reconciliation-report.sh "host=<host> port=<port> dbname=ago_chat user=<user> password=<password>"
```

The connection string is whatever `psql` itself accepts — a libpq keyword string (shown above), a
`postgresql://` URI, or nothing at all if `PGHOST`/`PGDATABASE`/`PGUSER`/`PGPASSWORD` are already
exported in your shell. The script never logs, stores, or echoes it.

## What it checks

For every site, whether `sites.attachment_bytes_reserved` — the number
`SiteAttachmentStorageBudgetStore` enforces and `GetSiteAttachmentStorageSummaryHandler` shows the
tenant on the storage screen (`23-80`) — agrees with the actual sum of `size_bytes` across that
site's own `Ready` attachments (`ix_attachments_site_state_size` already indexes exactly this
predicate, so the query costs nothing extra to run). A `Pending` or already-`Deleted` attachment is
correctly excluded from the actual figure either way — a `Pending` upload never finished reserving
against this number in the first place, and a `Deleted` one's bytes are gone.

## What it reports, and what it deliberately does not

**Identifiers and byte counts only** — a site id, its name, and three numbers (reserved, actual,
drift). Never a credential, an attachment id, an object key, or any other column either table holds.

A clean run says so plainly:

```
RESULT: no drift - every site's attachment_bytes_reserved agrees with the actual sum of its Ready attachments' size_bytes.
```

A non-zero run lists each drifting site with `reserved`, `actual_ready_bytes`, and `drift` (reserved
minus actual), ordered worst-drift-first, then states the same thing every time: this script only
reports, it repairs nothing.

Run for real against a local, disposable Postgres container (never the live deployment) while
building this script, `2026-09-14`: a site seeded with `attachment_bytes_reserved = 5000` and two
`Ready` attachments summing to `2500` bytes was correctly reported with `drift=2500`; two sites whose
reserved figure already agreed with their actual `Ready` sum did not appear in the report; and a
connection failure (wrong port) exited loudly with `psql`'s own error rather than being silently
absorbed into a false "no drift" — an earlier draft of this script piped `psql` straight into
`grep -v ... || true`, and under `pipefail` a `psql` failure was getting masked by `grep`'s own
"nothing matched" exit code on the resulting empty output, found and fixed before this ever touched a
real connection string.

## If it finds drift

**The repair, if one is warranted, is always a release, never a reserve.** The only known cause of
drift is under-release (this item's own bug) — a real deployment's `attachment_bytes_reserved` can
only be too high relative to the truth, never too low, from this cause. So for a given drifting
`site_id`, the correction is:

```sql
update sites
set attachment_bytes_reserved = attachment_bytes_reserved - <drift>
where id = '<site_id>';
```

using the exact `drift` figure this report printed for that site, run by hand, by a person, after
reading the report — never by this script, and never against the live deployment by an AI session
without the author doing it personally (the same posture this codebase already takes for
`DisconnectNonEntitledChannelCredentialsAsOwnerHandler`'s own kind of tool: built and tested, not
executed for real by anything other than a person's own deliberate act). If the report ever shows the
opposite sign — `reserved` lower than `actual` — stop and investigate rather than running the
statement above with a negative drift; that would mean a second, different bug this item never
anticipated, not this one.

## Run against the live deployment

Not yet run. As of this item shipping, no real tenant has reported hitting `23-76`'s quota ceiling
unexpectedly, and the fix itself (the actual `ReleaseAsync` call, not this report) is what stops the
leak from growing further starting now. Running this report against the real node — to learn whether
any already-onboarded tenant has already accumulated drift — is the author's own next step, following
this runbook, not a background worker's or a managing session's to do without the author present.
