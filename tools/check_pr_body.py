#!/usr/bin/env python3
import argparse
import os
import re
import sys

REQUIRED_SECTION = "Blast radius"

PLACEHOLDERS = {
    "tbd", "tba", "todo", "n/a", "na", "none", "nothing", "-", "--", "...", "?", "x",
    "fill in", "fill me in", "to do", "later", "same", "see above", "nil", "null",
}

HEADING = re.compile(r"^##\s+(.*?)\s*#*\s*$")
HTML_NOTE = re.compile(r"<!--.*?-->", re.S)
ISSUE_LINK = re.compile(r"^\s*(close[sd]?|fix(e[sd])?|resolve[sd]?|refs?|part of)\b[\s:]*([\w.-]+/[\w.-]+)?#?\d*[\s,.]*$", re.I | re.M)


def section_text(body, title):
    lines = body.replace("\r\n", "\n").split("\n")
    inside = False
    collected = []
    found = False
    for line in lines:
        m = HEADING.match(line)
        if m and not line.startswith("###"):
            if inside:
                break
            if m.group(1).strip().lower() == title.lower():
                inside = True
                found = True
            continue
        if inside:
            collected.append(line)
    if not found:
        return None
    return "\n".join(collected)


def is_real_text(text):
    cleaned = HTML_NOTE.sub(" ", text)
    cleaned = ISSUE_LINK.sub(" ", cleaned)
    cleaned = re.sub(r"[*_`>#|\[\]()]", " ", cleaned)
    cleaned = re.sub(r"^\s*([-+]|\d+\.)\s+", " ", cleaned, flags=re.M)
    normalized = " ".join(cleaned.split()).strip().lower().rstrip(".:!")
    if not normalized:
        return False
    if normalized in PLACEHOLDERS:
        return False
    if all(part.strip(" .,:;") in PLACEHOLDERS or not part.strip(" .,:;") for part in re.split(r"[\n,;]", normalized)):
        return False
    return len(re.findall(r"[a-z0-9]", normalized)) >= 5


def check(body):
    if body is None or not body.strip():
        return "PR body is empty; add a `## %s` section." % REQUIRED_SECTION
    text = section_text(body, REQUIRED_SECTION)
    if text is None:
        return "PR body has no `## %s` section." % REQUIRED_SECTION
    if not is_real_text(text):
        return "`## %s` is empty or a placeholder; say what this change can affect and what it cannot." % REQUIRED_SECTION
    return None


SELF_TEST_CASES = (
    ("", False),
    ("## For Dread\n**Ask:** Nothing.\n", False),
    ("## Blast radius\n\n## Test\nran it\n", False),
    ("## Blast radius\n   \n", False),
    ("## Blast radius\nTBD\n", False),
    ("## Blast radius\n- N/A\n", False),
    ("## Blast radius\n_None._\n", False),
    ("## Blast radius\n<!-- describe -->\n", False),
    ("## Blast radius\nTBD, N/A\n## Risk\nlow\n", False),
    ("## blast radius\nOnly CI: one new workflow on pull_request.\n", True),
    ("## Blast radius\n### Runtime\nNone: CI-only change, no deployed file touched.\n## Test\nok\n", True),
    ("## For Dread\nx\n\n## Blast radius ##\n- Scripts/ only; next deploy restarts ServUO.\n\nCloses #1\n", True),
    ("## Blast radius\r\nTouches tools/ only.\r\n", True),
    ("## Blast radius\nCI only.\n", True),
    ("## Blast radius\nTBD\n\nCloses #9\n\n## What\nx\n", False),
    ("## Blast radius\n\n\nCloses #\n\n## What\n", False),
    ("## Blast radius\n\nFixes DreadfullyDespized/unknown-shard#12\n", False),
    ("## Blast radius\nCloses the gap in CI only.\n", True),
    ("**Proof:** see `## Blast radius` below\n## Blast radius\nN/A\n", False),
)


def self_test():
    failures = 0
    for body, expected_ok in SELF_TEST_CASES:
        ok = check(body) is None
        if ok != expected_ok:
            failures += 1
            print("FAIL expected %s for %r" % ("pass" if expected_ok else "fail", body))
    print("self-test: %d cases, %d failures" % (len(SELF_TEST_CASES), failures))
    return failures == 0


def main():
    parser = argparse.ArgumentParser(description="Fail when the PR body lacks a non-empty Blast radius section.")
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--body-file")
    args = parser.parse_args()
    if args.self_test:
        return 0 if self_test() else 1
    if args.body_file:
        with open(args.body_file, encoding="utf-8") as handle:
            body = handle.read()
    else:
        body = os.environ.get("PR_BODY")
    problem = check(body)
    if problem:
        print("::error::" + problem)
        return 1
    print("PR body has a non-empty `## %s` section." % REQUIRED_SECTION)
    return 0


if __name__ == "__main__":
    sys.exit(main())
