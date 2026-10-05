# Correction loop and grader check (level 2: GitHub Actions)

Dread's rule: every correction is encoded at the strongest level (1 codebase, 2 lint/CI, 3 review rules, 4 skill, 5 style guide).
This is the same design as Rig's correction loop in dungeon-python ([PR #389 at 2607d3d](https://github.com/DreadfullyDespized/dungeon-python/blob/2607d3d03874a04a620da857090f16ef9d8153f7/docs/correction-loop.md)), adapted for this repo.
Every workflow uses only the workflow `GITHUB_TOKEN` and runs on `ubuntu-latest`.

| Workflow | Trigger | What it does |
|---|---|---|
| `grader.yml` (check `grader`) | PR opened, synchronize, reopened, labeled, unlabeled, ready for review; review submitted, edited, dismissed; manual dispatch | FAILS unless a grader PASS names the current head SHA and the PR is old enough |
| `grader-relay.yml` | a PR comment or review whose first line starts `Verdict: ` | re-runs the PR's own `grader` runs for the current head (so the result shows on the PR), waiting out the minimum age first when a PASS arrives early |
| `correction-intake.yml` | a watched workflow fails on `main`, a deployment fails, a grader FAIL, a Dread correction | opens an issue labeled `correction`, or comments on the open one with the same key |
| `correction-close-gate.yml` | an issue labeled `correction` is closed | reopens it unless it links a merged level 1 or level 2 fix PR |
| `correction-loop tests` | every PR and every push to `main` | unit tests for all of the above plus the issue forms |

Code: `tools/correction_loop/` (Python stdlib; the tests also use PyYAML). Settings: `tools/correction_loop/config.json`.

## Grader check

`grader` is red by default. It turns green only when all of these hold:

1. A PR comment or PR review has the FIRST line `Verdict: PASS <sha>`, where `<sha>` is at least `min_sha_chars` (12) hex
   characters of the PR's current head commit. Text after the SHA on the same line is allowed.
2. It is the latest verdict that names the current head. A later `Verdict: FAIL <sha>` for the same head turns the check red again.
3. Its author is not the PR author, is the repo owner, a member or a collaborator, is not a bot account, and is in `grader_logins`
   when that list is not empty (it is empty by default, so any such collaborator can grade). Dismissed reviews do not count.
   An edited comment counts at the time of its last edit.
4. The PR was opened at least `min_pr_age_minutes` (30) minutes ago.

Why the PR author is excluded: the doer cannot grade their own PR. Agents that open PRs as DreadfullyDespized therefore
need a grader on a different GitHub account (a collaborator), or Dread can set `exclude_pr_author` to `false` in
`tools/correction_loop/config.json`.

Why a SHA on the Verdict line: commits never carry a verdict, and a PASS cannot be written before the commit it names exists, so any
new push makes the old PASS stale and the check goes red until the grader re-grades the new head.

Verdicts without a SHA, with a short SHA or with another commit's SHA are listed under `considered` in the run summary with the reason they were ignored.

Example verdict comment:

```text
Verdict: PASS 0123456789abcdef0123456789abcdef01234567
Checked: proof links, CI runs, blast radius.
```

Every new verdict makes `grader-relay` re-run each completed `grader` run of the PR for the current head (pull_request and
review runs), so every `grader` entry on the PR shows the current answer. A PASS posted before the PR is 30 minutes old leaves
the check red; `grader-relay` waits until the PR is old enough first (at most `max_relay_wait_minutes`).
To re-run by hand: `gh run rerun <grader run id>` on the PR's latest `grader` run. `gh workflow run grader.yml --ref <head branch> -f pr=<N>`
also works and checks that the branch tip is still the PR head, but a dispatched run is recorded on the commit only and is not listed in the PR's checks.

Branch protection and rulesets are not changed by this repo's files. CONTRIBUTING.md asks for `grader` to be green before merge.

## What opens a correction issue

| Source | Marker | Correction key (dedupe) | Required level |
|---|---|---|---|
| CI or deploy workflow failure on `main` | a watched workflow (`watched_workflows`) concludes failure, timed_out or startup_failure on `main` | `ci:<workflow>:<root signature>` | 1 (codebase) |
| Required-check failure on a PR | only for workflows listed in `required_checks` (empty here, so PR-branch failures open nothing) | `ci:<workflow>:<root signature>` | 2 (lint/CI) |
| Deployment failure | a `deployment_status` event with state failure or error (`watch_deployments`) | `deploy:<environment>:<signature>` | 1 (codebase) |
| Grader FAIL | a PR comment or review whose first line starts `Verdict: FAIL` | `grader:pr<N>` | 2 (lint/CI) |
| Dread correction | label `dread-correction` on an issue, a comment whose first line starts `/correction`, or a manual dispatch | `dread:issue<N>` / `dread:comment<id>` / `dread:dispatch<run id>` | 1 or 2, or the level written as `Required level: N` |

Manual dispatch: `gh workflow run correction-intake.yml -f kind=correction -f target="<title>" -f body="<what was wrong>"`.

Only the repo owner, members and collaborators trigger it; bot accounts never do. The root signature hashes the failed job,
the failed step and the root line of its log (failing test ids, else the first `##[error]` line with numbers and SHAs masked),
so the same failure on another commit has the same key. An open issue with the same key gets a "Seen again" comment instead of
a new issue; a closed one is named as "Repeat of" in the new issue. Two runs with the same key are serialized by a concurrency group.

The other event types can be replayed by id: `gh workflow run correction-intake.yml -f kind=run -f target=<run id>`
(also `comment`, `review` with `<pr>/<review id>`, and `issue` with `-f action=labeled`).

## How a correction issue closes

The close gate reopens the issue and comments with what it checked unless it links a MERGED PR into `main`, opened by a
person (not a bot or the workflow), whose `## For Dread` section has a dedicated level line: a line of its own that starts
with `Level: 1` or `Level: 2` (`**Level:** 2 (lint/CI)` is fine), placed after Ask or What changes for you and before Proof.
A PR counts as linked when its merge commit closed the issue, when it references the issue (`Closes #N`), or when a human
comment on the issue links it. Links in the bot-written issue body do not count. There is no written-reason escape:
a correction that cannot be fixed at level 1 or 2 stays open.

Re-check by hand: `gh workflow run correction-close-gate.yml -f issue=<N>`.

## Issue forms

`.github/ISSUE_TEMPLATE/` has three forms (level 1, the codebase): **Bug** (problem and evidence, options A and B plus more,
recommendation, blast radius, root cause, regression origin), **Improvement** (problem and evidence, options A and B plus more,
recommendation, blast radius) and **Feature** (what it does for Dread, acceptance criteria, rollout, blast radius). Every field is required except "More options".

## Settings (`tools/correction_loop/config.json`)

- `watched_workflows`: workflow names whose failure on `main` opens a correction issue. `correction-intake.yml` lists the same names; a test checks they match.
- `required_checks`: workflows whose PR-branch failure also opens one (empty by default).
- `watch_deployments`, `deploy_environments`: deployment failures (empty list means every environment).
- `grader.min_pr_age_minutes`, `grader.min_sha_chars`, `grader.exclude_pr_author`, `grader.grader_logins`, `grader.max_relay_wait_minutes`.
