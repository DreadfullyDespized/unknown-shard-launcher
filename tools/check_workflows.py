#!/usr/bin/env python3
import argparse
import os
import re
import sys
import tempfile

try:
    import yaml
except ImportError:
    sys.exit("check_workflows.py needs PyYAML: python3 -m pip install pyyaml==6.0.2")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PR_EVENTS = ("pull_request", "pull_request_target")
LABEL_TYPES = ("labeled", "unlabeled")
MAX_SLEEP_SECONDS = 30
HOSTED_RUNNER = "ubuntu-latest"
HOSTED_PREFIXES = ("ubuntu-", "windows-", "macos-")
GATE_NAME_RX = re.compile(
    r"(?i)(^|[^a-z])(gate|grader|no[-_ ]?(added[-_ ])?comments?|pr[-_ ]?body|blast[-_ ]?radius|correction[-_ ]?loop|check[-_ ]?workflows?)([^a-z]|$)"
)
GATE_SCRIPT_RX = re.compile(r"check_no_comments\.py|check_pr_body\.py|check_workflows\.py|correction_loop|grader\.py")
UNITS = {"": 1, "s": 1, "m": 60, "h": 3600, "d": 86400}
SHELL_SLEEP_RX = re.compile(r"(?<![\w.-])sleep[ \t]+(\S+)")
PS_SLEEP_RX = re.compile(r"(?i)\bStart-Sleep\b([^;\n|]*)")
PY_SLEEP_RX = re.compile(r"\btime\.sleep\(\s*([^)]*)\)")
NUMBER_UNIT_RX = re.compile(r"^(\d+(?:\.\d+)?)([smhd]?)$")


def triggers(doc):
    on = doc.get("on", doc.get(True))
    if isinstance(on, str):
        return {on: None}
    if isinstance(on, list):
        return {name: None for name in on}
    return on or {}


def event_types(spec):
    if not isinstance(spec, dict):
        return []
    types = spec.get("types") or []
    return [types] if isinstance(types, str) else list(types)


def shell_sleep_seconds(token):
    token = token.strip("\"'")
    m = NUMBER_UNIT_RX.match(token)
    if not m:
        return None
    return float(m.group(1)) * UNITS[m.group(2)]


def powershell_sleep_seconds(args):
    words = args.split()
    scale = 1.0
    value = None
    i = 0
    while i < len(words):
        word = words[i].lower()
        if word.startswith("-"):
            if "-milliseconds".startswith(word) and len(word) >= 2 and word.startswith("-m"):
                scale = 0.001
            elif word not in ("-seconds", "-s", "-second"):
                i += 1
                continue
            if i + 1 < len(words):
                value = words[i + 1]
                i += 2
                continue
        else:
            value = word
        i += 1
    if value is None:
        return None
    try:
        return float(value.strip("\"'")) * scale
    except ValueError:
        return None


def sleep_problems(text):
    found = []
    for m in SHELL_SLEEP_RX.finditer(text):
        seconds = shell_sleep_seconds(m.group(1))
        if seconds is None:
            found.append("`sleep %s` has no literal duration, so it cannot be shown to be %ds or less" % (m.group(1), MAX_SLEEP_SECONDS))
        elif seconds > MAX_SLEEP_SECONDS:
            found.append("`sleep %s` waits %gs on a paid runner (limit %ds); re-trigger on an event instead" % (m.group(1), seconds, MAX_SLEEP_SECONDS))
    for m in PS_SLEEP_RX.finditer(text):
        seconds = powershell_sleep_seconds(m.group(1))
        if seconds is None:
            found.append("`Start-Sleep%s` has no literal duration" % m.group(1).rstrip())
        elif seconds > MAX_SLEEP_SECONDS:
            found.append("`Start-Sleep%s` waits %gs on a paid runner (limit %ds)" % (m.group(1).rstrip(), seconds, MAX_SLEEP_SECONDS))
    for m in PY_SLEEP_RX.finditer(text):
        try:
            seconds = float(m.group(1))
        except ValueError:
            found.append("`time.sleep(%s)` has no literal duration" % m.group(1))
            continue
        if seconds > MAX_SLEEP_SECONDS:
            found.append("`time.sleep(%s)` waits %gs on a paid runner (limit %ds)" % (m.group(1), seconds, MAX_SLEEP_SECONDS))
    return found


def runner_labels(runs_on):
    if isinstance(runs_on, str):
        return [runs_on]
    if isinstance(runs_on, list):
        return [str(x) for x in runs_on]
    if isinstance(runs_on, dict):
        labels = runs_on.get("labels") or []
        return [labels] if isinstance(labels, str) else [str(x) for x in labels]
    return []


def is_gate_job(workflow_name, job_id, job):
    if any(GATE_NAME_RX.search(str(x or "")) for x in (workflow_name, job_id, job.get("name"))):
        return True
    for step in job.get("steps") or []:
        if GATE_SCRIPT_RX.search(str(step.get("run") or "")):
            return True
    return False


def check_docs(docs):
    problems = []
    gates = []
    for path, doc in sorted(docs.items()):
        if not isinstance(doc, dict):
            problems.append("%s: not a workflow mapping" % path)
            continue
        on = triggers(doc)
        for event in PR_EVENTS:
            if event in on:
                bad = [t for t in event_types(on[event]) if t in LABEL_TYPES]
                if bad:
                    problems.append("%s: `%s` triggers on %s; label changes must not start a runner" % (path, event, "/".join(bad)))
        pr_triggered = any(event in on for event in PR_EVENTS)
        for job_id, job in (doc.get("jobs") or {}).items():
            where = "%s: job `%s`" % (path, job_id)
            if not isinstance(job, dict):
                problems.append("%s is not a mapping" % where)
                continue
            if "uses" not in job and "timeout-minutes" not in job:
                problems.append("%s has no timeout-minutes" % where)
            for label in runner_labels(job.get("runs-on")):
                if label.startswith(HOSTED_PREFIXES) and label != HOSTED_RUNNER:
                    problems.append("%s runs on hosted `%s`; hosted jobs use %s only" % (where, label, HOSTED_RUNNER))
            for index, step in enumerate(job.get("steps") or []):
                text = str(step.get("run") or "")
                for problem in sleep_problems(text):
                    problems.append("%s step %d (%s): %s" % (where, index + 1, step.get("name") or step.get("uses") or "run", problem))
            if pr_triggered and is_gate_job(doc.get("name"), job_id, job):
                gates.append("%s/%s" % (path, job_id))
    if len(gates) > 1:
        problems.append("%d gate-type jobs run on pull_request (%s); merge them into one job with one step per check" % (len(gates), ", ".join(gates)))
    return problems


def load_dir(directory):
    docs = {}
    for name in sorted(os.listdir(directory)):
        if name.endswith((".yml", ".yaml")):
            with open(os.path.join(directory, name), encoding="utf-8") as handle:
                docs[name] = yaml.safe_load(handle)
    return docs


GOOD_GATE = """
name: gate
on:
  pull_request:
    types: [opened, edited, synchronize, reopened]
  push:
    branches: [main]
jobs:
  grader:
    name: grader
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - run: python3 tools/check_no_comments.py --self-test
      - run: python3 tools/check_pr_body.py --self-test
      - run: sleep 30
"""

BUILD = """
name: CI
on: [push, pull_request]
jobs:
  build:
    name: Build and test
    runs-on: ubuntu-latest
    timeout-minutes: 15
    steps:
      - run: npm test
"""

SELF_HOSTED = """
name: Deploy
on:
  push:
    branches: [main]
jobs:
  deploy:
    runs-on: [self-hosted, Windows]
    timeout-minutes: 30
    steps:
      - shell: pwsh
        run: Start-Sleep -Seconds 5
"""


def wf(on, job="", steps="      - run: true\n", runs_on="ubuntu-latest", timeout="    timeout-minutes: 5\n", name="x", job_id="build"):
    return "name: %s\non:\n%sjobs:\n  %s:\n%s    runs-on: %s\n%s    steps:\n%s" % (name, on, job_id, job, runs_on, timeout, steps)


PR_ON = "  pull_request:\n"
SELF_TEST_CASES = (
    ("one gate plus build CI passes", {"gate.yml": GOOD_GATE, "ci.yml": BUILD, "deploy.yml": SELF_HOSTED}, []),
    ("two gate workflows fail", {"gate.yml": GOOD_GATE, "no-comments.yml": wf(PR_ON, name="No comments", job_id="no-comments")}, ["gate-type jobs"]),
    ("two gate jobs in one workflow fail", {"g.yml": wf(PR_ON, name="checks", job_id="pr-body") + "  grader:\n    runs-on: ubuntu-latest\n    timeout-minutes: 5\n    steps:\n      - run: true\n"}, ["gate-type jobs"]),
    ("gate found by the script it runs", {"gate.yml": GOOD_GATE, "misc.yml": wf(PR_ON, steps="      - run: python3 tools/check_pr_body.py\n")}, ["gate-type jobs"]),
    ("a push-only gate does not count", {"gate.yml": GOOD_GATE, "nightly.yml": wf("  push:\n", name="grader nightly")}, []),
    ("missing timeout fails", {"ci.yml": wf(PR_ON, timeout="")}, ["no timeout-minutes"]),
    ("expression timeout passes", {"ci.yml": wf(PR_ON, timeout="    timeout-minutes: ${{ fromJSON('5') }}\n")}, []),
    ("reusable workflow call needs no timeout", {"ci.yml": "name: x\non: [push]\njobs:\n  call:\n    uses: ./.github/workflows/b.yml\n"}, []),
    ("labeled trigger fails", {"ci.yml": wf("  pull_request:\n    types: [opened, labeled]\n")}, ["labeled"]),
    ("unlabeled scalar trigger fails", {"ci.yml": wf("  pull_request_target:\n    types: unlabeled\n")}, ["unlabeled"]),
    ("issue labels are not PR triggers", {"ci.yml": wf("  issues:\n    types: [opened, labeled]\n")}, []),
    ("string on form parses", {"ci.yml": "name: x\non: pull_request\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n      - run: true\n"}, ["no timeout-minutes"]),
    ("sleep 31 fails", {"ci.yml": wf(PR_ON, steps="      - run: sleep 31\n")}, ["waits 31s"]),
    ("sleep 30 passes", {"ci.yml": wf(PR_ON, steps="      - run: |\n          sleep 30\n          sleep 0.5\n")}, []),
    ("sleep with unit fails", {"ci.yml": wf(PR_ON, steps="      - run: echo a && sleep 2m\n")}, ["waits 120s"]),
    ("sleep with a variable fails", {"ci.yml": wf(PR_ON, steps="      - run: sleep \"$WAIT\"\n")}, ["no literal duration"]),
    ("Start-Sleep seconds fails", {"ci.yml": wf(PR_ON, steps="      - shell: pwsh\n        run: Start-Sleep -Seconds 45\n")}, ["Start-Sleep -Seconds 45"]),
    ("Start-Sleep milliseconds passes under the limit", {"ci.yml": wf(PR_ON, steps="      - shell: pwsh\n        run: Start-Sleep -Milliseconds 500\n")}, []),
    ("Start-Sleep milliseconds fails over the limit", {"ci.yml": wf(PR_ON, steps="      - shell: pwsh\n        run: Start-Sleep -m 40000\n")}, ["waits 40s"]),
    ("python time.sleep fails", {"ci.yml": wf(PR_ON, steps="      - run: python3 -c 'import time; time.sleep(60)'\n")}, ["time.sleep(60)"]),
    ("words containing sleep pass", {"ci.yml": wf(PR_ON, steps="      - run: echo no-sleep 99 && ./asleep 99\n")}, []),
    ("hosted non-ubuntu-latest fails", {"ci.yml": wf(PR_ON, runs_on="windows-latest")}, ["hosted `windows-latest`"]),
    ("pinned ubuntu image fails", {"ci.yml": wf(PR_ON, runs_on="ubuntu-22.04")}, ["hosted `ubuntu-22.04`"]),
    ("self-hosted labels pass", {"ci.yml": wf(PR_ON, runs_on="[self-hosted, Windows]")}, []),
)


def self_test():
    failures = 0
    with tempfile.TemporaryDirectory() as tmp:
        for label, files, expected in SELF_TEST_CASES:
            case_dir = tempfile.mkdtemp(dir=tmp)
            for name, text in files.items():
                with open(os.path.join(case_dir, name), "w", encoding="utf-8") as handle:
                    handle.write(text)
            problems = check_docs(load_dir(case_dir))
            ok = (not problems) if not expected else all(any(e in p for p in problems) for e in expected) and len(problems) == len(expected)
            print("%s %s%s" % ("ok  " if ok else "FAIL", label, "" if ok else " -> %s" % problems))
            failures += 0 if ok else 1
    print("self-test: %d cases, %d failed" % (len(SELF_TEST_CASES), failures))
    return 1 if failures else 0


def main():
    parser = argparse.ArgumentParser(description="Fail when workflows waste paid Actions minutes.")
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--dir", default=os.path.join(ROOT, ".github", "workflows"))
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    problems = check_docs(load_dir(args.dir))
    for problem in problems:
        print("::error::%s" % problem)
    if problems:
        print("check_workflows: %d problem(s) in %s" % (len(problems), args.dir))
        return 1
    print("check_workflows: %d workflow file(s) in %s pass (one PR gate job, timeouts, no label triggers, no sleep over %ds, hosted jobs on %s)" % (len(load_dir(args.dir)), args.dir, MAX_SLEEP_SECONDS, HOSTED_RUNNER))
    return 0


if __name__ == "__main__":
    sys.exit(main())
