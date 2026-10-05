import argparse
import json
import os
import re
import secrets
import sys
import time
from datetime import datetime, timedelta, timezone
from zoneinfo import ZoneInfo

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

from gh import Api, ApiError

TRUSTED = {"OWNER", "MEMBER", "COLLABORATOR"}
VERDICT_RX = re.compile(r"^Verdict: (PASS|FAIL)\b(.*)$")
SHA_RX = re.compile(r"(?<![0-9A-Za-z])[0-9a-f]{7,40}(?![0-9A-Za-z])")
RUN_LINE_RX = re.compile(r"^grader-run: (\S+)\s*$", re.M)
RUN_ID_RX = re.compile(r"^gr-(\d{8}T\d{6}Z)-([0-9a-f]{16})$")
RUN_STAMP = "%Y%m%dT%H%M%SZ"
CLOCK_SKEW = timedelta(minutes=2)
WORKFLOW_FILE = "grader.yml"


def load_config(path=None):
    with open(path or os.path.join(HERE, "config.json"), encoding="utf-8") as fh:
        return json.load(fh)["grader"]


def parse_time(stamp):
    return datetime.strptime(stamp, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)


def stamp(when):
    return when.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def local(when, tz):
    return when.astimezone(ZoneInfo(tz)).strftime("%Y-%m-%d %H:%M %Z")


def parse_verdict(text):
    line = (text or "").replace("\r\n", "\n").lstrip().split("\n", 1)[0].strip()
    m = VERDICT_RX.match(line)
    if not m:
        return None
    return m.group(1), SHA_RX.findall(m.group(2))


def new_run_id(now=None):
    when = (now or datetime.now(timezone.utc)).astimezone(timezone.utc)
    return f"gr-{when.strftime(RUN_STAMP)}-{secrets.token_hex(8)}"


def parse_run_id(text):
    lines = RUN_LINE_RX.findall((text or "").replace("\r\n", "\n"))
    if len(lines) != 1:
        return None, ("has no grader-run line" if not lines else "has more than one grader-run line")
    m = RUN_ID_RX.match(lines[0])
    if not m:
        return None, f"grader-run {lines[0][:40]} is not gr-YYYYMMDDTHHMMSSZ-<16 hex>"
    when = datetime.strptime(m.group(1), RUN_STAMP).replace(tzinfo=timezone.utc)
    return {"id": lines[0], "at": when}, None


def run_id_counts(items, pr_text):
    counts = {}
    for item in items:
        for rid in set(RUN_LINE_RX.findall((item.get("body") or "").replace("\r\n", "\n"))):
            counts[rid] = counts.get(rid, 0) + 1
    for rid in set(re.findall(r"gr-\d{8}T\d{6}Z-[0-9a-f]{16}", pr_text or "")):
        counts[rid] = counts.get(rid, 0) + 1
    return counts


def verdict_items(reviews, comments):
    items = []
    for r in reviews:
        items.append({"kind": "review", "id": r.get("id"), "user": r.get("user") or {},
                      "association": r.get("author_association"), "at": r.get("submitted_at") or "",
                      "body": r.get("body") or "", "url": r.get("html_url"), "state": r.get("state")})
    for c in comments:
        items.append({"kind": "comment", "id": c.get("id"), "user": c.get("user") or {},
                      "association": c.get("author_association"), "at": c.get("updated_at") or c.get("created_at") or "",
                      "body": c.get("body") or "", "url": c.get("html_url")})
    return [i for i in items if i["at"] and i.get("state") != "DISMISSED"]


def run_reason(item, cfg, head_time, counts):
    run, why = parse_run_id(item["body"])
    if why:
        return why
    if counts.get(run["id"], 0) > 1:
        return f"grader-run {run['id']} appears more than once on this PR; each verdict needs a fresh grader session"
    said = parse_time(item["at"])
    if run["at"] > said + CLOCK_SKEW:
        return f"grader-run {run['id']} is dated after the verdict was posted"
    max_age = timedelta(hours=int(cfg.get("grader_run_max_age_hours", 24)))
    if said - run["at"] > max_age:
        return f"grader-run {run['id']} is older than {max_age.total_seconds() / 3600:g} hours at verdict time"
    if head_time and run["at"] + CLOCK_SKEW < head_time:
        return f"grader-run {run['id']} started before the head commit was made at {stamp(head_time)}"
    return None


def ignore_reason(item, cfg, head_sha, head_time=None, counts=None):
    user = item["user"]
    login = user.get("login") or ""
    if user.get("type") == "Bot" or login.endswith("[bot]"):
        return "posted by a bot"
    if item["association"] not in TRUSTED:
        return f"author association {item['association']} is not owner, member or collaborator"
    logins = cfg.get("grader_logins") or []
    if logins and login not in logins:
        return f"{login} is not in grader_logins"
    verdict, shas = parse_verdict(item["body"])
    min_chars = int(cfg.get("min_sha_chars", 12))
    named = [s for s in shas if len(s) >= min_chars]
    if not named:
        return f"names no commit SHA of {min_chars}+ hex characters on the Verdict line"
    if not any(head_sha.startswith(s) for s in named):
        return f"names {', '.join(s[:12] for s in named)}, not the current head {head_sha[:12]}"
    if cfg.get("require_grader_run", True):
        return run_reason(item, cfg, head_time, counts or {})
    return None


def evaluate(pr, head_sha, items, now, cfg, tz="America/Chicago", head_time=None):
    considered = []
    applicable = []
    pr_author = (pr.get("user") or {}).get("login") or ""
    counts = run_id_counts(items, f"{pr.get('title') or ''}\n{pr.get('body') or ''}")
    for item in sorted(items, key=lambda i: (i["at"], str(i["id"]))):
        parsed = parse_verdict(item["body"])
        if not parsed:
            continue
        why = ignore_reason(item, cfg, head_sha, head_time, counts)
        considered.append({"verdict": parsed[0], "kind": item["kind"], "url": item["url"], "at": item["at"],
                           "login": item["user"].get("login"), "grader_run": (parse_run_id(item["body"])[0] or {}).get("id"),
                           "ignored": why})
        if not why:
            applicable.append((parsed[0], item))
    min_age = int(cfg.get("min_pr_age_minutes", 30))
    created = parse_time(pr["created_at"])
    eligible = created + timedelta(minutes=min_age)
    result = {"pr": pr.get("number"), "head_sha": head_sha, "pr_author": pr_author, "min_pr_age_minutes": min_age,
              "pr_created_at": pr["created_at"], "eligible_at": stamp(eligible), "considered": considered}
    if pr.get("state") != "open":
        return dict(result, ok=False, waiting=False, reason=f"PR is {pr.get('state')}, not open")
    if (pr.get("head") or {}).get("sha") != head_sha:
        return dict(result, ok=False, waiting=False,
                    reason=f"this run is for {head_sha[:12]} but the PR head is now {(pr.get('head') or {}).get('sha', '')[:12]}")
    if not applicable:
        return dict(result, ok=False, waiting=False,
                    reason=f"no grader verdict recorded for the current head {head_sha}; default is FAIL")
    verdict, item = applicable[-1]
    result["verdict_url"] = item["url"]
    if verdict != "PASS":
        return dict(result, ok=False, waiting=False, reason=f"latest grader verdict for {head_sha[:12]} is FAIL: {item['url']}")
    if now < eligible:
        left = int((eligible - now).total_seconds())
        return dict(result, ok=False, waiting=True, wait_seconds=left,
                    reason=(f"grader PASS is recorded ({item['url']}) but the PR is younger than {min_age} minutes; "
                            f"it is eligible at {local(eligible, tz)}"))
    return dict(result, ok=True, waiting=False,
                reason=f"grader PASS for {head_sha[:12]} ({item['url']}) and the PR is at least {min_age} minutes old")


def fetch(api, number):
    repo = api.repo
    pr = api.get(f"/repos/{repo}/pulls/{number}")
    reviews = api.paged(f"/repos/{repo}/pulls/{number}/reviews")
    comments = api.paged(f"/repos/{repo}/issues/{number}/comments")
    return pr, verdict_items(reviews, comments)


def head_time_of(api, sha):
    if not sha:
        return None
    try:
        data = api.get(f"/repos/{api.repo}/commits/{sha}") or {}
    except ApiError:
        return None
    date = ((data.get("commit") or {}).get("committer") or {}).get("date")
    return datetime.fromisoformat(date.replace("Z", "+00:00")).astimezone(timezone.utc) if date else None


def target(event_name, payload):
    if event_name in ("pull_request", "pull_request_target", "pull_request_review"):
        pr = payload.get("pull_request") or {}
        return pr["number"], (pr.get("head") or {}).get("sha")
    if event_name == "workflow_dispatch":
        n = int(((payload.get("inputs") or {}).get("pr") or "0").strip() or 0)
        if not n:
            raise SystemExit("workflow_dispatch needs the pr input")
        return n, os.environ.get("GITHUB_SHA") or ""
    raise SystemExit(f"grader check does not handle event {event_name}")


def report(result):
    text = json.dumps(result, indent=2)
    print(text)
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if path:
        status = "PASS" if result.get("ok") else "FAIL"
        with open(path, "a", encoding="utf-8") as fh:
            fh.write(f"**grader: {status}**: {result.get('reason')}\n\n```json\n{text}\n```\n")
    if not result.get("ok"):
        print(f"::error::grader FAIL: {result.get('reason')}")


def check(api, cfg, event_name, payload, now=None):
    number, head = target(event_name, payload)
    pr, items = fetch(api, number)
    return evaluate(pr, head, items, now or datetime.now(timezone.utc), cfg, head_time=head_time_of(api, head))


def relay_target(event_name, payload):
    if event_name == "issue_comment":
        issue = payload.get("issue") or {}
        if "pull_request" not in issue or payload.get("action") not in ("created", "edited"):
            return None
        if not parse_verdict((payload.get("comment") or {}).get("body")):
            return None
        return issue["number"]
    if event_name == "pull_request_review":
        if not parse_verdict((payload.get("review") or {}).get("body")):
            return None
        return (payload.get("pull_request") or {})["number"]
    return None


PR_RUN_EVENTS = ("pull_request", "pull_request_review")
MAX_RERUNS = 20


def retrigger(api, pr):
    head = pr.get("head") or {}
    if ((head.get("repo") or {}).get("full_name")) != api.repo:
        return {"action": "skipped", "why": "PR head is in another repository"}
    sha = head.get("sha")
    runs = (api.get(f"/repos/{api.repo}/actions/workflows/{WORKFLOW_FILE}/runs?head_sha={sha}&per_page=100") or {}).get("workflow_runs") or []
    mine = [r for r in runs if r.get("event") in PR_RUN_EVENTS and r.get("head_sha") == sha]
    done = sorted((r for r in mine if r.get("status") == "completed"),
                  key=lambda r: (r.get("created_at") or "", r.get("id") or 0), reverse=True)[:MAX_RERUNS]
    if done:
        for run in done:
            api.post(f"/repos/{api.repo}/actions/runs/{run['id']}/rerun", {})
        return {"action": "rerun", "run_ids": [r["id"] for r in done], "head_sha": sha, "pr": pr["number"]}
    api.post(f"/repos/{api.repo}/actions/workflows/{WORKFLOW_FILE}/dispatches",
             {"ref": head.get("ref"), "inputs": {"pr": str(pr["number"])}})
    return {"action": "dispatched", "ref": head.get("ref"), "head_sha": sha, "pr": pr["number"]}


def relay(api, cfg, event_name, payload, sleep=time.sleep, now=datetime.now):
    found = relay_target(event_name, payload)
    if not found:
        return {"action": "skipped", "why": "no Verdict line on a pull request"}
    number = found
    pr, items = fetch(api, number)
    head = (pr.get("head") or {}).get("sha") or ""
    result = evaluate(pr, head, items, now(timezone.utc), cfg, head_time=head_time_of(api, head))
    waited = 0
    if result.get("waiting"):
        cap = int(cfg.get("max_relay_wait_minutes", 30)) * 60
        if result["wait_seconds"] > cap:
            return {"action": "skipped", "why": f"PASS is recorded but the wait is over {cap // 60} minutes; re-run grader later",
                    "result": result}
        waited = result["wait_seconds"] + 5
        sleep(waited)
        pr, items = fetch(api, number)
        if (pr.get("head") or {}).get("sha") != head:
            return {"action": "skipped", "why": "the PR head moved while waiting; that push re-runs grader"}
    out = retrigger(api, pr)
    out["waited_seconds"] = waited
    return out


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("mode", choices=["check", "relay", "new-run-id"])
    ap.add_argument("--event", default=os.environ.get("GITHUB_EVENT_PATH"))
    ap.add_argument("--event-name", default=os.environ.get("GITHUB_EVENT_NAME"))
    ap.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY"))
    args = ap.parse_args(argv)
    if args.mode == "new-run-id":
        print(new_run_id())
        return 0
    with open(args.event, encoding="utf-8") as fh:
        payload = json.load(fh)
    api = Api(args.repo)
    cfg = load_config()
    if args.mode == "relay":
        result = relay(api, cfg, args.event_name, payload)
        print(json.dumps(result, indent=2))
        return 0
    result = check(api, cfg, args.event_name, payload)
    report(result)
    return 0 if result.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
