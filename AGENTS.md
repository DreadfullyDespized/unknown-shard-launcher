# unknown-shard-launcher — agent outline

## What this repo is for
Public MIT ClassicUO auto-updater and launcher. Holds no server-specific values; host, patch URL, and signing key are supplied at build time.

## Allowed
- Code, tests, docs, CI config, and tooling needed for this repo's purpose.
- Opening issues and PRs on branch `cursor/<issue>-<slug>` per CONTRIBUTING.md.
- Local verify / grader tooling runs before push.

## NOT allowed
- Code comments of any kind (enforced by `tools/check_no_comments.py`).
- Committing server addresses, ports, IPs, patch URLs, keys, secrets, or internal paths (public repo).
- Extra outline docs beside this file.

## Pointers
- [CONTRIBUTING.md](CONTRIBUTING.md) — process, grader/correction loop, public-repo rule
- [README.md](README.md)
- `docs/correction-loop.md` when present
- `tools/check_no_comments.py`, `tools/check_pr_body.py`
