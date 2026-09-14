#!/usr/bin/env bash
# `ago-root/docs/backlog/25-79-deleting-an-attachment-never-releases-its-quota-reservation.md`;
# `docs/runbooks/attachment-quota-reconciliation.md` is the runbook this script belongs to - read
# that first, especially before interpreting a non-empty report.
#
# `DeleteAttachmentHandler` never released `ISiteAttachmentStorageBudget`'s reservation from `5-08`
# until `25-79` fixed it - every attachment ever deleted through the console's per-message delete
# action stayed counted against its tenant's quota forever. That fix stops the leak going forward; it
# does nothing for bytes already double-counted before it shipped. This script answers the one
# question that fix cannot: for a given site, does `sites.attachment_bytes_reserved` (the number
# `SiteAttachmentStorageBudgetStore` enforces and `GetSiteAttachmentStorageSummaryHandler` shows the
# tenant) still agree with the truth - the actual sum of `size_bytes` across that site's own `Ready`
# attachments, the exact number `ix_attachments_site_state_size` already indexes for.
#
# ONE DATABASE, ONE JOIN - unlike `tenancy-reconciliation-report.sh` (ago-deploy/k8s), which reconciles
# two separate Postgres databases, `attachment_bytes_reserved` and `attachments` live in the same
# `ago_chat` database, so this needs one connection and one query, the same "single-database on-demand
# read" shape `update-landing-prices-from-db.sh` (this repo's own tools/) already uses.
#
# REPORTS ONLY. NEVER REPAIRS - the identical posture `tenancy-reconciliation-report.sh`'s own header
# states and for the identical reason: a script that rewrites a tenant's own billing/quota number on
# the strength of its own join is catastrophic the first time the join is wrong. A non-zero result
# here is a person's decision. What repairing one drifting site actually means - the release amount is
# always `reserved - actual` here, because the only known cause is under-release, never over-release -
# is written out in the runbook, as a manual statement a person runs after reading this report, not as
# a button this script presses itself.
#
# MANUAL, ON DEMAND - not a CronJob, not a systemd timer, the same choice
# `tenancy-reconciliation-report.sh` and `update-landing-prices-from-db.sh` both already made for a
# check that answers "is it drifting right now", not a continuously-running guarantee.
#
# Nothing secure is hardcoded. The connection is whatever `psql` itself accepts - a libpq keyword
# string, a `postgresql://` URI, or nothing at all if `PGHOST`/`PGDATABASE`/`PGUSER`/`PGPASSWORD` are
# already exported - passed as this script's own first argument. Never logged, never echoed.
#
# IDENTIFIERS AND BYTE COUNTS ONLY - a site id, its name, and three numbers. Never a credential, an
# attachment id, an object key, or any other column `sites`/`attachments` holds.
set -euo pipefail

CONNECTION="${1:-}"

QUERY="
select s.id, s.name, s.attachment_bytes_reserved,
       coalesce(sum(a.size_bytes) filter (where a.state = 'Ready'), 0) as actual_ready_bytes
from sites s
left join attachments a on a.site_id = s.id
group by s.id, s.name, s.attachment_bytes_reserved
having s.attachment_bytes_reserved
    <> coalesce(sum(a.size_bytes) filter (where a.state = 'Ready'), 0)
order by (s.attachment_bytes_reserved
    - coalesce(sum(a.size_bytes) filter (where a.state = 'Ready'), 0)) desc;
"

echo "reading ago_chat (sites.attachment_bytes_reserved vs. actual Ready attachment bytes)..." >&2
# `psql`'s own output is captured on its own, not piped straight into `grep -v ... || true` - under
# `pipefail`, the rightmost command in a pipeline decides the pipeline's exit status, so a `psql`
# connection failure (bad host, bad password, unreachable network) piped into a `grep` that then finds
# zero matching lines on empty input (grep's own "nothing matched" exit 1) would have its real failure
# masked by grep's, and the blanket `|| true` needed to tolerate a genuinely empty, successful result
# would silently swallow it - a false "no drift" on a report this tool's own README calls a person's
# decision is worse than the leak this tool exists to find. Capturing `psql`'s own output first means
# `set -e` stops the script the moment `psql` itself fails, before grep ever runs.
RAW="$(psql "$CONNECTION" -X -q -t -A -F'|' -c "$QUERY")"
ROWS="$(printf '%s\n' "$RAW" | grep -v '^[[:space:]]*$' || true)"

STAMP="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

if [ -z "$ROWS" ]; then
  echo "attachment quota reconciliation report - $STAMP"
  echo
  echo "RESULT: no drift - every site's attachment_bytes_reserved agrees with the actual sum of its Ready attachments' size_bytes."
  exit 0
fi

DRIFT_COUNT="$(printf '%s\n' "$ROWS" | wc -l | tr -d '[:space:]')"

echo "attachment quota reconciliation report - $STAMP"
echo
echo "== sites where attachment_bytes_reserved disagrees with the actual sum of Ready attachments =="
echo "count: $DRIFT_COUNT"
printf '%s\n' "$ROWS" | while IFS='|' read -r site_id name reserved actual; do
  drift=$((reserved - actual))
  echo "  site_id=$site_id  name=\"$name\"  reserved=$reserved  actual_ready_bytes=$actual  drift=$drift (reserved minus actual)"
done
echo
echo "RESULT: drift found - $DRIFT_COUNT site(s) need a person's decision (see the runbook). This script only reports; it repairs nothing."
