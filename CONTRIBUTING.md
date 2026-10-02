# Contributing

## Repo label: TEST

**TEST.** Verified work (issue → PR → QA checklist run → grader ≠ doer review) merges to `main` promptly; PRs must not sit unmerged once verified. Dread tests `main` builds afterward. Fixes found in testing go back through the same flow (new issue → PR → merge). Tracked by the GitHub topic `tier-test`.

## Process

1. Open an issue first: problem, evidence, acceptance criteria.
2. Branch `cursor/<issue#>-<slug>`; never commit directly to `main`.
3. One scoped PR per issue with `Closes #<issue>`, what, why, test plan and results, risk/rollback.
4. Never skip checks; no force-push to shared branches.
5. Review by someone other than the author.

## Public repo rule

This repository is public. Never commit server addresses, ports, IPs, patch URLs, keys, secrets or internal paths. Server-specific values are supplied at build time from a private config (see `launcher.build.example.props` once the launcher code lands).
