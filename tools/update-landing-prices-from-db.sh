#!/usr/bin/env bash
# `docs/runbooks/landing-prices.md` (this repo) is the runbook this script belongs to - read that
# first.
#
# Snapshots every currently-published price (`25-43`'s own priced_resources/published_price_versions
# tables) into ago-landing/prices.json, then opens a pull request against ago-landing - that repo's
# own branch protection (a required "build-image" status check, no direct push to main) means a plain
# push is refused outright, found running this script for real rather than assumed from its README.
#
# Nothing secure is hardcoded or read from a default location. The database connection comes from
# whatever `psql` itself already reads - PGHOST/PGPORT/PGDATABASE/PGUSER/PGPASSWORD, a `PGSERVICE`
# entry, or a libpq connection string passed as this script's own first argument - so a real password
# is never a literal this script or its output could leak. Opening the PR uses your own already-
# authenticated `gh` CLI session; if none exists, `gh` itself prompts interactively, the same as any
# other `gh` command. This script never merges what it opens - the same "a rare, deliberate act, left
# to a person" shape every other on-demand owner action in this project already takes.
set -euo pipefail

CONNECTION="${1:-}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LANDING_DIR="${AGO_LANDING_DIR:-$SCRIPT_DIR/../../ago-landing}"

if [ ! -d "$LANDING_DIR/.git" ]; then
  echo "error: ago-landing not found at $LANDING_DIR" >&2
  echo "       clone it as a sibling of ago-chat, or set AGO_LANDING_DIR." >&2
  exit 1
fi

QUERY="
select json_agg(row_to_json(t) order by t.price_key) from (
  select
    pr.price_key,
    ppv.amount_rub,
    ppv.version,
    ppv.published_at
  from priced_resources pr
  join published_price_versions ppv
    on ppv.priced_resource_id = pr.id
   and ppv.sequence = pr.last_sequence
) t;
"

echo "Reading current prices..." >&2
RAW_JSON="$(psql "$CONNECTION" -X -q -t -A -c "$QUERY")"

if [ -z "$RAW_JSON" ] || [ "$RAW_JSON" = "null" ]; then
  RAW_JSON="[]"
fi

# Pretty-printed, stable key order (the JSON module sorts nothing on its own - the SQL above already
# orders by price_key, this just indents) so a real price change produces a real, reviewable diff and
# a re-run with nothing changed produces none. Tries each interpreter for real (not just
# `command -v`, which is true even for Windows's own broken `python3` app-execution-alias stub -
# found running this script for real, not read off documentation) and falls back to the raw
# single-line JSON, with a warning, rather than failing the whole snapshot over formatting alone.
FORMATTED=""
for PY in python3 python; do
  if command -v "$PY" >/dev/null 2>&1; then
    if CANDIDATE="$(printf '%s' "$RAW_JSON" | "$PY" -m json.tool 2>/dev/null)"; then
      FORMATTED="$CANDIDATE"
      break
    fi
  fi
done
if [ -z "$FORMATTED" ]; then
  echo "warning: no working python found - writing unformatted JSON." >&2
  FORMATTED="$RAW_JSON"
fi

PRICES_FILE="$LANDING_DIR/prices.json"
PREVIOUS="$(cat "$PRICES_FILE" 2>/dev/null || echo "")"

if [ "$FORMATTED" = "$PREVIOUS" ]; then
  echo "No price change since the last snapshot - nothing to do." >&2
  exit 0
fi

cd "$LANDING_DIR"
git fetch origin --quiet
BRANCH="chore/price-snapshot-$(date -u +%Y%m%dT%H%M%SZ)"
git checkout -b "$BRANCH" origin/main --quiet

printf '%s\n' "$FORMATTED" > prices.json
git add prices.json
git commit -m "chore: price snapshot $(date -u +%Y-%m-%dT%H:%M:%SZ)" --quiet
git push -u origin "$BRANCH" --quiet

PR_URL="$(gh pr create \
  --title "chore: price snapshot $(date -u +%Y-%m-%d)" \
  --body "Automated price snapshot from ago_chat's own priced_resources/published_price_versions tables - review the diff, wait for the build-image check, then merge. Deploying it afterward is still a manual step (ago-landing/README.md, 'Deployment')." \
)"

git checkout main --quiet

echo "" >&2
echo "Opened: $PR_URL" >&2
echo "Merging is left to you - this script never merges what it opens." >&2
echo "After merge, wait for CI to publish the image, then on the node run" >&2
echo "  ./deploy.sh landing <sha>" >&2
echo "from ago-deploy/k8s (ago-landing's own README, 'Deployment')." >&2
