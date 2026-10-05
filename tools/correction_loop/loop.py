import argparse
import hashlib
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

from gh import Api, ApiError

CORRECTION = "correction"
DREAD_LABEL = "dread-correction"
LABELS = {
    CORRECTION: ("b60205", "Auto-opened by the correction loop; closes only with a merged level 1/2 fix PR"),
    DREAD_LABEL: ("d93f0b", "Dread corrected this; the correction loop opens a correction issue for it"),
}
LEVEL_NAMES = {1: "codebase", 2: "lint/CI", 3: "review rules", 4: "skill", 5: "style guide"}
TRUSTED = {"OWNER", "MEMBER", "COLLABORATOR"}
FAILED = {"failure", "timed_out", "startup_failure"}
DEPLOY_FAILED = {"failure", "error"}
MAIN_EVENTS = {"push", "schedule", "workflow_dispatch", "merge_group"}
PR_EVENTS = {"pull_request", "pull_request_target"}
GRADER_MARKER = "Verdict: FAIL"
DREAD_COMMAND = "/correction"

TS_RX = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z ?")
ANSI_RX = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")
HEX_RX = re.compile(r"\b[0-9a-f]{7,64}\b")
NUM_RX = re.compile(r"\d+")
GENERIC_ERR_RX = re.compile(r"(?i)\b(error|failed|failure|assert(ion)?)\b")
COMMENT_RX = re.compile(r"<!--.*?-->", re.S)
FENCE_RX = re.compile(r"^[ ]{0,3}(`{3,}|~{3,}).*?^[ ]{0,3}\1[ \t]*$", re.S | re.M)
HEADING_RX = re.compile(r"^[ ]{0,3}(#{1,6})[ \t]+(.*?)[ \t#]*$", re.M)
LEVEL_LINE_RX = re.compile(r"(?i)^[ \t]*(?:[-*+][ \t]+)?(?:\*\*|__)?level(?:\*\*|__)?[ \t]*:[ \t]*(?:\*\*|__)?[ \t]*([1-5])\b")
PART_RX = re.compile(r"(?i)^[ \t]*(?:[-*+][ \t]+)?(?:\*\*|__)?(ask|what changes for you|proof|not done)(?:\*\*|__)?[ \t]*:")
LEVEL_PARTS = {None, "ask", "what changes for you"}
TESTS_FIRST_RX = re.compile(r"(?i)\btests?[- ]first\b")
MAX_PR_CANDIDATES = 100
REQUESTED_LEVEL_RX = re.compile(r"(?i)\brequired level\W*:\W*([1-5])\b")
RULE_RX = re.compile(r"\bR\d+[a-z]?-[a-z][a-z-]*[a-z]\b")


def load_config(path=None):
    with open(path or os.path.join(HERE, "config.json"), encoding="utf-8") as fh:
        return json.load(fh)


def first_line(text):
    return (text or "").replace("\r\n", "\n").lstrip().split("\n", 1)[0].strip()


def is_grader_fail(text):
    return first_line(text).startswith(GRADER_MARKER)


def is_dread_command(text):
    line = first_line(text)
    return line == DREAD_COMMAND or line.startswith(DREAD_COMMAND + " ")


def level_text(spec):
    if isinstance(spec, int):
        return f"{spec} ({LEVEL_NAMES[spec]})"
    return spec


def requested_level(text):
    m = REQUESTED_LEVEL_RX.search(text or "")
    return int(m.group(1)) if m else None


def normalize(line):
    line = ANSI_RX.sub("", line)
    line = HEX_RX.sub("<sha>", line)
    line = NUM_RX.sub("<n>", line)
    return re.sub(r"\s+", " ", line).strip()[:240]


def step_output(lines):
    end = next((i for i in range(len(lines) - 1, -1, -1) if "Process completed with exit code" in lines[i]), len(lines))
    start = max((i for i in range(end) if "##[endgroup]" in lines[i]), default=-1) + 1
    return [l for l in lines[start:end] if l.strip() and "##[" not in l]


def extract_root(log):
    lines = [TS_RX.sub("", ANSI_RX.sub("", l)).rstrip() for l in (log or "").splitlines()]
    failed = []
    for l in lines:
        s = l.strip()
        if s.startswith("FAILED ") and not s.startswith("FAILED ("):
            failed.append(s[7:].split(" - ", 1)[0].strip())
        elif re.match(r"^(FAIL|ERROR): \S", s):
            failed.append(s.split(": ", 1)[1].strip())
    if failed:
        uniq = sorted(set(failed))
        more = f" (+{len(uniq) - 3} more)" if len(uniq) > 3 else ""
        return "tests failed: " + ", ".join(uniq[:3]) + more
    for l in lines:
        if "##[error]" in l and "Process completed with exit code" not in l:
            return normalize(l.split("##[error]", 1)[1])
    out = step_output(lines)
    rules = sorted(set(RULE_RX.findall("\n".join(out))))
    if rules:
        return "rule violations: " + ", ".join(rules)
    for l in out:
        if GENERIC_ERR_RX.search(l):
            return normalize(l)
    if out:
        return normalize(out[0])
    for l in lines:
        if "Process completed with exit code" in l:
            return normalize(l.split("##[error]", 1)[-1])
    return "no failure line found in the job log"


def signature(job, step, root):
    return hashlib.sha1(f"{job}|{step}|{root}".encode("utf-8")).hexdigest()[:12]


def short(text, n=90):
    text = re.sub(r"\s+", " ", text or "").strip()
    return text if len(text) <= n else text[: n - 3] + "..."


def quote(text, max_lines=40):
    lines = (text or "").replace("\r\n", "\n").strip().split("\n")
    out = lines[:max_lines]
    if len(lines) > max_lines:
        out.append(f"... ({len(lines) - max_lines} more lines)")
    return "\n".join("> " + l for l in out)


def server():
    return os.environ.get("GITHUB_SERVER_URL", "https://github.com")


def finding(key, title, source, level, evidence, details):
    return {"key": key, "title": short(title, 200), "source": source, "level": level,
            "evidence": evidence, "details": details}


def ci_finding(api, cfg, run, default_branch):
    if run.get("conclusion") not in FAILED:
        return None
    name = run.get("name") or ""
    if name not in cfg["watched_workflows"]:
        return None
    event = run.get("event") or ""
    branch = run.get("head_branch") or ""
    prs = [p["number"] for p in run.get("pull_requests") or []]
    if event in PR_EVENTS:
        if name not in cfg.get("required_checks", []):
            return None
        if TESTS_FIRST_RX.search((run.get("head_commit") or {}).get("message") or ""):
            return None
        context = "PR " + ", ".join(f"#{n}" for n in prs) if prs else f"PR branch {branch}"
    elif event in MAIN_EVENTS and branch == default_branch:
        context = default_branch
    else:
        return None
    repo = api.repo
    jobs = api.paged(f"/repos/{repo}/actions/runs/{run['id']}/jobs?filter=latest", key="jobs")
    failed = [j for j in jobs if j.get("conclusion") in FAILED]
    roots = []
    for job in failed[:3]:
        step = next((s.get("name", "") for s in job.get("steps") or [] if s.get("conclusion") in FAILED), "")
        try:
            log = api.raw(f"/repos/{repo}/actions/jobs/{job['id']}/logs").decode("utf-8", "replace")
        except ApiError:
            log = ""
        roots.append((job, step, extract_root(log)))
    if not roots:
        roots = [(None, "", f"run concluded {run.get('conclusion')} with no failed job")]
    job0, step0, root0 = roots[0]
    job_name = job0.get("name", "") if job0 else ""
    sig = signature(job_name, step0, root0)
    evidence = [(f"{name} run #{run.get('run_number', run['id'])} ({run.get('conclusion')})", run["html_url"])]
    for job, step, _ in roots:
        if job:
            evidence.append((f"job '{job.get('name')}' (failed step: {step or 'none'})", job.get("html_url")))
    sha = run.get("head_sha") or ""
    if sha:
        evidence.append((f"commit {sha[:7]}", f"{server()}/{repo}/commit/{sha}"))
    for n in prs:
        evidence.append((f"PR #{n}", f"{server()}/{repo}/pull/{n}"))
    details = [f"- Check: `{name}`", f"- Where: {context}", f"- Job: `{job_name or 'none'}`",
               f"- Failed step: `{step0 or 'none'}`", f"- Root signature: `{sig}` from `{root0}`"]
    for job, step, root in roots[1:]:
        details.append(f"- Also failed: job `{job.get('name')}` step `{step or 'none'}`: `{root}`")
    level = 1 if event in MAIN_EVENTS else 2
    return finding(f"ci:{name}:{sig}", f"correction: {name} failed on {context}: {root0}",
                   f"CI failure ({name} on {context})", level, evidence, "\n".join(details))


def deploy_finding(api, cfg, payload):
    if not cfg.get("watch_deployments"):
        return None
    status = payload.get("deployment_status") or {}
    deployment = payload.get("deployment") or {}
    if status.get("state") not in DEPLOY_FAILED:
        return None
    env = deployment.get("environment") or status.get("environment") or "unknown"
    envs = cfg.get("deploy_environments") or []
    if envs and env not in envs:
        return None
    desc = normalize(status.get("description") or f"deployment {status.get('state')}")
    sig = signature("deploy", env, desc)
    sha = deployment.get("sha") or ""
    evidence = []
    for label, url in (("deployment log", status.get("log_url") or status.get("target_url")),
                       (f"commit {sha[:7]}", f"{server()}/{api.repo}/commit/{sha}" if sha else "")):
        if url:
            evidence.append((label, url))
    details = "\n".join([f"- Environment: `{env}`", f"- State: `{status.get('state')}`",
                         f"- Ref: `{deployment.get('ref') or ''}`", f"- Root signature: `{sig}` from `{desc}`"])
    return finding(f"deploy:{env}:{sig}", f"correction: deploy to {env} failed: {desc}",
                   f"deploy failure ({env})", 1, evidence, details)


def grader_finding(pr_number, pr_title, url, body):
    repo = os.environ.get("GITHUB_REPOSITORY", "")
    evidence = [("grader verdict", url)]
    if repo:
        evidence.append((f"PR #{pr_number}", f"{server()}/{repo}/pull/{pr_number}"))
    return finding(f"grader:pr{pr_number}", f"correction: grader FAIL on PR #{pr_number}: {pr_title}",
                   f"grader FAIL on PR #{pr_number}", 2, evidence, quote(body))


def dread_finding(key, title, url, text, where):
    lvl = requested_level(text)
    level = lvl if lvl else "1 or 2 (codebase or lint/CI)"
    return finding(key, f"correction: Dread correction in {where}: {title}",
                   f"Dread correction in {where}", level, [("Dread's correction", url)] if url else [], quote(text))


def trusted(obj):
    user = obj.get("user") or {}
    return obj.get("author_association") in TRUSTED and user.get("type") != "Bot"


def intake_findings(api, cfg, event_name, payload):
    default_branch = (payload.get("repository") or {}).get("default_branch") or "main"
    out = []
    if event_name == "workflow_run":
        f = ci_finding(api, cfg, payload.get("workflow_run") or {}, default_branch)
        return [f] if f else []
    if event_name == "deployment_status":
        f = deploy_finding(api, cfg, payload)
        return [f] if f else []
    if event_name == "dread_dispatch":
        inputs = payload.get("inputs") or {}
        title = short(inputs.get("target") or "manual correction", 160)
        body = inputs.get("body") or title
        return [dread_finding(f"dread:dispatch{payload.get('run_id') or os.environ.get('GITHUB_RUN_ID', '0')}",
                              title, run_url(), body, "a manual dispatch")]
    if event_name == "issue_comment" and payload.get("action") == "created":
        comment = payload.get("comment") or {}
        issue = payload.get("issue") or {}
        if not trusted(comment):
            return []
        body = comment.get("body") or ""
        if "pull_request" in issue and is_grader_fail(body):
            out.append(grader_finding(issue["number"], issue.get("title", ""), comment.get("html_url"), body))
        if is_dread_command(body):
            rest = first_line(body)[len(DREAD_COMMAND):].strip() or issue.get("title", "")
            out.append(dread_finding(f"dread:comment{comment.get('id')}", rest, comment.get("html_url"), body,
                                     f"#{issue['number']}"))
        return out
    if event_name == "pull_request_review" and payload.get("action") == "submitted":
        review = payload.get("review") or {}
        pr = payload.get("pull_request") or {}
        if trusted(review) and is_grader_fail(review.get("body")):
            out.append(grader_finding(pr["number"], pr.get("title", ""), review.get("html_url"), review.get("body")))
        return out
    if event_name == "issues":
        issue = payload.get("issue") or {}
        action = payload.get("action")
        if not trusted(issue):
            return []
        names = {l.get("name") for l in issue.get("labels") or []}
        if CORRECTION in names:
            return []
        added = (payload.get("label") or {}).get("name")
        if (action == "labeled" and added == DREAD_LABEL) or (action == "opened" and DREAD_LABEL in names):
            out.append(dread_finding(f"dread:issue{issue['number']}", issue.get("title", ""), issue.get("html_url"),
                                     issue.get("body") or "", f"#{issue['number']}"))
        return out
    return out


def ensure_labels(api, names):
    for name in names:
        try:
            api.get(f"/repos/{api.repo}/labels/{name}")
        except ApiError as err:
            if err.status != 404:
                raise
            color, desc = LABELS[name]
            api.post(f"/repos/{api.repo}/labels", {"name": name, "color": color, "description": desc})


def run_url():
    repo = os.environ.get("GITHUB_REPOSITORY", "")
    run_id = os.environ.get("GITHUB_RUN_ID", "")
    return f"{server()}/{repo}/actions/runs/{run_id}" if repo and run_id else ""


def docs_url(api, cfg, default_branch):
    return f"{server()}/{api.repo}/blob/{default_branch}/{cfg['docs']}"


def issue_body(f, docs, repeat_of=None):
    lines = [f"Opened by the correction loop ([how it works]({docs})).", "",
             f"Source: {f['source']}", f"Required level: {level_text(f['level'])}", f"Correction key: `{f['key']}`"]
    if repeat_of:
        lines.append("Repeat of: " + ", ".join(f"#{n}" for n in repeat_of) + " (closed earlier with the same key)")
    lines += ["", "## Evidence"] + [f"- [{label}]({url})" for label, url in f["evidence"] if url]
    if run_url():
        lines.append(f"- [correction-intake run]({run_url()})")
    lines += ["", "## Details", f["details"], "", "## How this issue closes",
              "Link a merged, human-opened PR into the default branch whose For Dread section has a dedicated "
              "`Level: 1` (codebase) or `Level: 2` (lint/CI) line, on its own line before Proof. `Closes #N` in the "
              "PR body or a comment here linking the PR both count. Links in this bot-written body never count. "
              "The correction-close-gate workflow reopens this issue when no such PR is linked."]
    return "\n".join(lines) + "\n"


def file_finding(api, cfg, f, default_branch):
    marker = f"Correction key: `{f['key']}`"
    issues = [i for i in api.paged(f"/repos/{api.repo}/issues?labels={CORRECTION}&state=all")
              if "pull_request" not in i and marker in (i.get("body") or "")]
    open_ones = [i for i in issues if i.get("state") == "open"]
    if open_ones:
        target = open_ones[0]
        lines = [f"Seen again: {f['source']}"] + [f"- [{label}]({url})" for label, url in f["evidence"] if url]
        if run_url():
            lines.append(f"- [correction-intake run]({run_url()})")
        api.post(f"/repos/{api.repo}/issues/{target['number']}/comments", {"body": "\n".join(lines)})
        return {"action": "commented", "number": target["number"], "url": target.get("html_url"), "key": f["key"]}
    repeat_of = sorted(i["number"] for i in issues)
    body = issue_body(f, docs_url(api, cfg, default_branch), repeat_of)
    created = api.post(f"/repos/{api.repo}/issues", {"title": f["title"], "body": body, "labels": [CORRECTION]})
    return {"action": "opened", "number": created["number"], "url": created.get("html_url"), "key": f["key"]}


def replay(api, payload):
    inputs = payload.get("inputs") or {}
    kind = (inputs.get("kind") or "").strip()
    target = (inputs.get("target") or "").strip()
    action = (inputs.get("action") or "").strip()
    base = {"repository": payload.get("repository") or {}}
    repo = api.repo
    if kind == "correction":
        if not target:
            raise SystemExit("workflow_dispatch: kind=correction needs a target (the correction title)")
        return "dread_dispatch", dict(base, inputs=inputs)
    if kind == "run":
        return "workflow_run", dict(base, action="completed", workflow_run=api.get(f"/repos/{repo}/actions/runs/{int(target)}"))
    if kind == "comment":
        comment = api.get(f"/repos/{repo}/issues/comments/{int(target)}")
        return "issue_comment", dict(base, action="created", comment=comment, issue=api.get(comment["issue_url"]))
    if kind == "review":
        pr, review_id = target.split("/")
        review = api.get(f"/repos/{repo}/pulls/{int(pr)}/reviews/{int(review_id)}")
        return "pull_request_review", dict(base, action="submitted", review=review,
                                           pull_request=api.get(f"/repos/{repo}/pulls/{int(pr)}"))
    if kind == "issue":
        issue = api.get(f"/repos/{repo}/issues/{int(target)}")
        out = dict(base, action=action or "opened", issue=issue)
        if out["action"] == "labeled":
            out["label"] = {"name": DREAD_LABEL}
        return "issues", out
    raise SystemExit(f"workflow_dispatch: unknown kind {kind!r} (correction, run, comment, review, issue)")


def strip_quoted(text):
    return FENCE_RX.sub("", COMMENT_RX.sub("", (text or "").replace("\r\n", "\n")))


def section(text, title_rx):
    heads = list(HEADING_RX.finditer(text))
    for i, h in enumerate(heads):
        if len(h.group(1)) <= 3 and re.fullmatch(title_rx, h.group(2).strip(), re.I):
            end = len(text)
            for nxt in heads[i + 1:]:
                if len(nxt.group(1)) <= len(h.group(1)):
                    end = nxt.start()
                    break
            return text[h.end():end]
    return None


def fordread_level(body):
    sec = section(strip_quoted(body), r"for dread")
    if sec is None:
        return None
    part = None
    for line in sec.split("\n"):
        pm = PART_RX.match(line)
        if pm:
            part = re.sub(r"\s+", " ", pm.group(1).lower())
            continue
        lm = LEVEL_LINE_RX.match(line)
        if lm and part in LEVEL_PARTS:
            return int(lm.group(1))
    return None


def bot_pr(pr):
    user = pr.get("user") or {}
    login = user.get("login") or ""
    return user.get("type") == "Bot" or login.endswith("[bot]")


def human(obj):
    user = obj.get("user") or {}
    return user.get("type") != "Bot" and not (user.get("login") or "").endswith("[bot]")


def pr_refs(text, repo):
    pat = r"(?:https://github\.com/" + re.escape(repo) + r"/(?:pull|issues)/|(?<![\w/])#)(\d+)\b"
    return [int(m.group(1)) for m in re.finditer(pat, strip_quoted(text))]


def evaluate_close(api, issue, default_branch):
    repo = api.repo
    number = issue["number"]
    comments = api.paged(f"/repos/{repo}/issues/{number}/comments")
    texts = ([issue.get("body") or ""] if human(issue) else []) + [c.get("body") or "" for c in comments if human(c)]
    closing, linked, xrefs = [], [], []
    for ev in api.paged(f"/repos/{repo}/issues/{number}/timeline"):
        src = (ev.get("source") or {}).get("issue") or {}
        if ev.get("event") == "cross-referenced" and "pull_request" in src and \
                (src.get("repository") or {}).get("full_name", repo) == repo:
            xrefs.append(src["number"])
        if ev.get("event") == "closed" and ev.get("commit_id"):
            try:
                for pr in api.get(f"/repos/{repo}/commits/{ev['commit_id']}/pulls"):
                    closing.append(pr["number"])
            except ApiError:
                pass
    for text in texts:
        linked.extend(pr_refs(text, repo))
    checked = []
    seen = set()
    for n in closing + linked + xrefs:
        if n in seen or n == number:
            continue
        seen.add(n)
        if len(seen) > MAX_PR_CANDIDATES:
            checked.append(f"stopped after {MAX_PR_CANDIDATES} linked PRs")
            break
        try:
            pr = api.get(f"/repos/{repo}/pulls/{n}")
        except ApiError as err:
            if err.status == 404:
                continue
            raise
        if bot_pr(pr):
            checked.append(f"#{n}: opened by a bot or the workflow ({(pr.get('user') or {}).get('login')}), ignored")
            continue
        base = (pr.get("base") or {}).get("ref")
        if not pr.get("merged_at"):
            checked.append(f"#{n}: not merged ({pr.get('state')})")
            continue
        if base != default_branch:
            checked.append(f"#{n}: merged into `{base}`, not `{default_branch}`")
            continue
        lvl = fordread_level(pr.get("body") or "")
        if lvl in (1, 2):
            return True, f"#{n} is merged and its For Dread section has a `Level: {lvl}` line ({LEVEL_NAMES[lvl]}).", checked
        checked.append(f"#{n}: merged, but its For Dread section has " +
                       (f"`Level: {lvl}` ({LEVEL_NAMES[lvl]}), not 1 or 2" if lvl else "no dedicated `Level: 1` or `Level: 2` line"))
    if not seen:
        checked.append("no linked pull request found")
    return False, "", checked


def close_gate(api, cfg, event_name, payload):
    default_branch = (payload.get("repository") or {}).get("default_branch") or "main"
    if event_name == "workflow_dispatch":
        number = int(((payload.get("inputs") or {}).get("issue") or "0").strip() or 0)
        issue = api.get(f"/repos/{api.repo}/issues/{number}")
    elif event_name == "issues" and payload.get("action") == "closed":
        issue = payload.get("issue") or {}
    else:
        return {"action": "skipped", "why": f"event {event_name}"}
    if issue.get("state") != "closed":
        return {"action": "skipped", "why": f"#{issue.get('number')} is not closed"}
    if CORRECTION not in {l.get("name") for l in issue.get("labels") or []}:
        return {"action": "skipped", "why": f"#{issue.get('number')} has no '{CORRECTION}' label"}
    allowed, reason, checked = evaluate_close(api, issue, default_branch)
    n = issue["number"]
    link = f" ([gate run]({run_url()}))" if run_url() else ""
    if allowed:
        api.post(f"/repos/{api.repo}/issues/{n}/comments", {"body": f"Closing gate: close allowed{link}: {reason}"})
        return {"action": "allowed", "number": n, "reason": reason}
    api.patch(f"/repos/{api.repo}/issues/{n}", {"state": "open"})
    text = (f"Closing gate: reopened{link}. A `{CORRECTION}` issue closes only when it links a merged, human-opened PR "
            f"into `{default_branch}` whose For Dread section has a dedicated `Level: 1` (codebase) or `Level: 2` "
            f"(lint/CI) line.\n\nChecked:\n" + "\n".join(f"- {c}" for c in checked))
    api.post(f"/repos/{api.repo}/issues/{n}/comments", {"body": text})
    return {"action": "reopened", "number": n, "checked": checked}


def intake(api, cfg, event_name, payload):
    if event_name == "workflow_dispatch":
        event_name, payload = replay(api, payload)
    default_branch = (payload.get("repository") or {}).get("default_branch") or "main"
    found = intake_findings(api, cfg, event_name, payload)
    if not found:
        return []
    ensure_labels(api, [CORRECTION, DREAD_LABEL])
    return [file_finding(api, cfg, f, default_branch) for f in found]


def intake_keys(api, cfg, event_name, payload):
    if event_name == "workflow_dispatch":
        event_name, payload = replay(api, payload)
    keys = sorted(f["key"] for f in intake_findings(api, cfg, event_name, payload))
    group = re.sub(r"[^A-Za-z0-9:+._-]+", "-", "+".join(keys))[:180]
    path = os.environ.get("GITHUB_OUTPUT")
    if path:
        with open(path, "a", encoding="utf-8") as fh:
            fh.write(f"group={group}\n")
    return {"keys": keys, "group": group}


def summarize(results):
    text = json.dumps(results, indent=2)
    print(text)
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if path:
        with open(path, "a", encoding="utf-8") as fh:
            fh.write("```json\n" + text + "\n```\n")


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("mode", choices=["keys", "intake", "close-gate"])
    ap.add_argument("--event", default=os.environ.get("GITHUB_EVENT_PATH"))
    ap.add_argument("--event-name", default=os.environ.get("GITHUB_EVENT_NAME"))
    ap.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY"))
    args = ap.parse_args(argv)
    with open(args.event, encoding="utf-8") as fh:
        payload = json.load(fh)
    api = Api(args.repo)
    cfg = load_config()
    handler = {"keys": intake_keys, "intake": intake, "close-gate": close_gate}[args.mode]
    summarize(handler(api, cfg, args.event_name, payload))
    return 0


if __name__ == "__main__":
    sys.exit(main())
