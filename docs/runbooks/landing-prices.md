# Updating the prices shown on the landing page

`ago-landing` (`ago-root/docs/architecture/repositories.md`) is one self-contained static HTML file
with no build step and no server-side dependency — deliberately, since it is a single marketing page,
not a product. It has no live connection to this database, and it should not gain one: a public page
fetching prices from a database on every visit is a live read path for something that changes rarely,
and a new public, unauthenticated endpoint this deployment does not otherwise need
(`ago-root/docs/architecture/personal-data.md`'s and `secrets.md`'s own "no new public surface without
a real reason" instinct, applied here).

Instead, whenever the platform owner publishes a real price (`/owner`'s pricing screen,
`PublishPriceVersionHandler`, `25-43`), this procedure takes a snapshot of every currently-published
price and opens a pull request against `ago-landing` carrying it as a plain data file — a deliberate,
on-demand act, run by a person, the same shape every other rare, owner-triggered action in this project
already takes (`ago-root/docs/runbooks/module-grant-and-revoke.md`'s own reasoning for why a console
screen is the ordinary route and a runbook is the fallback applies here in reverse: there is no console
screen for this at all yet, only this procedure).

## What you need before you start

- A connection to the live `ago_chat` database — the same reachability
  `ago-root/docs/runbooks/backup-and-restore.md`'s own drills need: either a direct connection if you
  have one, or an SSH-tunnelled `kubectl port-forward` chain against the real node.
- `ago-landing` cloned as a sibling of `ago-chat` (`ago-root/docs/runbooks/workspace.md`), and `gh`
  already authenticated for it — `gh` itself prompts interactively if it is not.
- `psql` on your own machine — a container works too (e.g.
  `docker run --rm --entrypoint psql postgres:17-alpine ...`) if you don't want it installed directly.
- `python3` or `python` for pretty-printing (falls back to unformatted JSON with a warning if neither
  works — nothing about the price data itself depends on Python).

## Running it

```bash
cd C:/git/ago/ago-chat
bash tools/update-landing-prices-from-db.sh "host=<host> port=<port> dbname=ago_chat user=<user> password=<password>"
```

The connection string is whatever `psql` itself accepts — a libpq keyword string (shown above), a
`postgresql://` URI, or nothing at all if `PGHOST`/`PGDATABASE`/`PGUSER`/`PGPASSWORD` are already
exported in your shell. The script never logs, stores, or echoes it.

## What it does, and what it deliberately leaves to you

1. Reads every key in `priced_resources` that has at least one published version (a key with none —
   `PricedResourceKeys.All` lists three today, `25-43`'s own "not yet for sale" state — is correctly
   absent from the output, not an error) and formats them into `prices.json`.
2. If nothing changed since the last snapshot, it says so and stops — no empty PR.
3. If something changed, it branches, commits, pushes, and opens a pull request against
   `ago-landing`'s `main` — **found running this for real, not assumed**: that branch requires a PR and
   a passing `build-image` check, so a plain push is refused outright by GitHub's own branch protection.

**It never merges what it opens, and it does not deploy anything.** Review the diff, wait for the
`build-image` check, merge by hand. `ago-landing`'s own CI then publishes
`ghcr.io/golyakoff/ago-landing:<sha>` — wait for that run to finish, then, from `ago-deploy/k8s` on the
node:

```bash
./deploy.sh landing <sha>
```

the same manual step `ago-landing/README.md`'s own "Deployment" section already documents for any
other change to that page. Running this script again later, whenever a price actually changes, is the
whole point — nothing here needs to run on a schedule.

## What `ago-landing` does with `prices.json`

Nothing yet — the pricing section that would read this file has not been built. This procedure exists
ahead of that page, deliberately: the data path is real and provable (run it, look at the PR) before
any markup depends on it, rather than the two being built and wired together at once.
