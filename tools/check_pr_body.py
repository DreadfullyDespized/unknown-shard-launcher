#!/usr/bin/env python3
import argparse
import json
import os
import re
import sys

FOR_DREAD = "For Dread"
FIELDS = ("Ask", "What changes for you", "Proof", "NOT done")
BLAST = "Blast radius"
BUG_SECTIONS = ("Root cause", "Regression origin")
OPTIONS = "Options"
BUG_LABELS = {"bug", "correction"}

PLACEHOLDERS = {
    "tbd", "tba", "todo", "n/a", "na", "none", "nothing", "-", "--", "...", "?", "x",
    "fill in", "fill me in", "to do", "later", "same", "see above", "nil", "null", "pending",
}
FIELD_PLACEHOLDERS = {
    "tbd", "tba", "todo", "to do", "fill in", "fill me in", "-", "--", "...", "?", "x", "pending", "later",
}

HEADING = re.compile(r"^##\s+(.*?)\s*#*\s*$")
HTML_NOTE = re.compile(r"<!--.*?-->", re.S)
ISSUE_LINK = re.compile(r"^\s*(close[sd]?|fix(e[sd])?|resolve[sd]?|refs?|part of)\b[\s:]*([\w.-]+/[\w.-]+)?#?\d*[\s,.]*$", re.I | re.M)
FIELD_LINE = re.compile(r"^\s*(?:[-*+]\s+)?\*{0,2}_{0,2}\s*(" + "|".join(re.escape(f) for f in FIELDS) + r")\s*(?::\s*\*{0,2}_{0,2}|\*{0,2}_{0,2}\s*:)\s*(.*)$", re.I)
URL = re.compile(r"https?://[^\s)\]>\"'`]+")
GITHUB_REF_LINK = re.compile(r"^https?://(?:www\.)?github\.com/[^/]+/[^/]+/(blob|tree)/([^/#?]+)", re.I)
SHA = re.compile(r"^[0-9a-f]{40}$")
OPTION_START = re.compile(r"^(?:\s{0,1}(?:[-*+]|\d+[.)])\s+\S|###\s+\S)")
BUG_TITLE = re.compile(r"^\s*fix(\([^)]*\))?!?:", re.I)
PLACEHOLDER_WORDS = {"tbd", "tba", "tbc", "todo", "fixme", "xxx", "wip"}
PLACEHOLDER_PHRASES = ("fill in", "fill me in", "fill this in", "to be determined", "to be decided", "to be confirmed", "coming soon")
FENCE_OPEN = re.compile(r"^ {0,3}(`{3,}|~{3,})")
INLINE_CODE = re.compile(r"(`+)(?!`).*?(?<!`)\1", re.S)


def strip_hidden(body):
    body = HTML_NOTE.sub("", body.replace("\r\n", "\n"))
    body = re.sub(r"<!--.*\Z", "", body, flags=re.S)
    kept = []
    fence = None
    for line in body.split("\n"):
        if fence is None:
            m = FENCE_OPEN.match(line)
            if m:
                fence = m.group(1)
                continue
            kept.append(line)
        elif re.match(r"^ {0,3}" + re.escape(fence[0]) + "{" + str(len(fence)) + r",}\s*$", line):
            fence = None
    return "\n".join(kept)


def has_placeholder_word(text):
    words = re.findall(r"[a-z0-9]+", normalize(INLINE_CODE.sub(" ", text)))
    joined = " ".join(words)
    return any(w in PLACEHOLDER_WORDS for w in words) or any(re.search(r"\b" + p + r"\b", joined) for p in PLACEHOLDER_PHRASES)


def sections(body):
    found = {}
    current = None
    for line in strip_hidden(body).split("\n"):
        m = HEADING.match(line)
        if m and not line.startswith("###"):
            current = m.group(1).strip().lower()
            found.setdefault(current, [])
            continue
        if current is not None:
            found[current].append(line)
    return {k: "\n".join(v) for k, v in found.items()}


def normalize(text):
    cleaned = HTML_NOTE.sub(" ", text)
    cleaned = ISSUE_LINK.sub(" ", cleaned)
    cleaned = re.sub(r"[*_`>#|\[\]()]", " ", cleaned)
    cleaned = re.sub(r"^\s*([-+]|\d+\.)\s+", " ", cleaned, flags=re.M)
    return " ".join(cleaned.split()).strip().lower().rstrip(".:!")


def is_real_text(text, placeholders=PLACEHOLDERS, minimum=5):
    normalized = normalize(text)
    if not normalized or normalized in placeholders:
        return False
    if has_placeholder_word(text):
        return False
    if all(part.strip(" .,:;") in placeholders or not part.strip(" .,:;") for part in re.split(r"[\n,;]", normalized)):
        return False
    return len(re.findall(r"[a-z0-9]", normalized)) >= minimum


def for_dread_fields(text):
    values = {}
    current = None
    for line in text.split("\n"):
        m = FIELD_LINE.match(line)
        if m:
            current = next(f for f in FIELDS if f.lower() == m.group(1).lower())
            values.setdefault(current, []).append(m.group(2))
        elif current is not None:
            values[current].append(line)
    return {k: "\n".join(v) for k, v in values.items()}


def unpinned_links(text):
    bad = []
    for url in URL.findall(text):
        m = GITHUB_REF_LINK.match(url)
        if m and not SHA.match(m.group(2).lower()):
            bad.append(url)
    return bad


def option_blocks(text):
    blocks = []
    for line in HTML_NOTE.sub(" ", text).split("\n"):
        if OPTION_START.match(line):
            blocks.append([line])
        elif blocks and line.strip():
            blocks[-1].append(line)
    return ["\n".join(b) for b in blocks]


def is_bug_pr(title, labels):
    return bool(BUG_TITLE.match(title or "")) or any(BUG_LABELS & set(re.findall(r"[a-z]+", l.lower())) for l in labels)


def check(body, title="", labels=()):
    if body is None or not body.strip():
        return ["PR body is empty; start from .github/pull_request_template.md."]
    found = sections(body)
    problems = []
    if FOR_DREAD.lower() not in found:
        problems.append("PR body has no `## %s` section." % FOR_DREAD)
    else:
        values = for_dread_fields(found[FOR_DREAD.lower()])
        for field in FIELDS:
            if field not in values:
                problems.append("`## %s` is missing **%s:**." % (FOR_DREAD, field))
            elif not is_real_text(values[field], FIELD_PLACEHOLDERS, 2):
                problems.append("`## %s` **%s:** is empty or a placeholder." % (FOR_DREAD, field))
        proof = values.get("Proof", "")
        if "Proof" in values and not URL.search(proof):
            problems.append("**Proof:** has no link; link the CI run, commit or log that proves each claim.")
        for url in unpinned_links(proof):
            problems.append("**Proof:** link is not pinned to a 40-hex commit SHA: %s" % url)
    if BLAST.lower() not in found:
        problems.append("PR body has no `## %s` section." % BLAST)
    elif not is_real_text(found[BLAST.lower()]):
        problems.append("`## %s` is empty or a placeholder; say what this change can affect and what it cannot." % BLAST)
    if is_bug_pr(title, labels):
        for name in BUG_SECTIONS:
            if name.lower() not in found:
                problems.append("Bug/correction PR has no `## %s` section." % name)
            elif not is_real_text(found[name.lower()]):
                problems.append("Bug/correction PR: `## %s` is empty or a placeholder." % name)
        if OPTIONS.lower() not in found:
            problems.append("Bug/correction PR has no `## %s` section." % OPTIONS)
        else:
            blocks = option_blocks(found[OPTIONS.lower()])
            if len(blocks) < 2:
                problems.append("Bug/correction PR: `## %s` needs at least 2 options (list items or ### headings)." % OPTIONS)
            for i, block in enumerate(blocks, 1):
                if not URL.search(block):
                    problems.append("Bug/correction PR: option %d in `## %s` has no evidence link." % (i, OPTIONS))
    return problems


def parse_labels(raw):
    if not raw:
        return []
    raw = raw.strip()
    if raw.startswith("["):
        try:
            return [str(x) for x in json.loads(raw)]
        except ValueError:
            pass
    return [x for x in raw.split(",") if x.strip()]


SHA40 = "0123456789abcdef0123456789abcdef01234567"
RUN = "https://github.com/o/r/actions/runs/1"
GOOD_DREAD = "## For Dread\n**Ask:** Nothing.\n**What changes for you:** New CI check.\n**Proof:** green run %s\n**NOT done:** None.\n\n" % RUN
GOOD_BLAST = "## Blast radius\nCI only: one new workflow.\n\nCloses #3\n"
GOOD = GOOD_DREAD + GOOD_BLAST
BUG_EXTRA = (
    "\n## Root cause\nThe parser skipped CRLF lines.\n"
    "\n## Regression origin\nIntroduced in https://github.com/o/r/commit/%s\n"
    "\n## Options\n- Normalize CRLF first: %s\n- Reject CRLF bodies: https://github.com/o/r/issues/4\n" % (SHA40, RUN)
)


def dread(ask="Nothing.", what="New CI check.", proof="green run " + RUN, notdone="None.", skip=None):
    parts = [("Ask", ask), ("What changes for you", what), ("Proof", proof), ("NOT done", notdone)]
    return "## For Dread\n" + "".join("**%s:** %s\n" % (k, v) for k, v in parts if k != skip) + "\n"


SELF_TEST_CASES = (
    ("", "", (), False),
    (GOOD, "", (), True),
    (GOOD.replace("\n", "\r\n"), "", (), True),
    (GOOD_BLAST, "", (), False),
    (dread(skip="Ask") + GOOD_BLAST, "", (), False),
    (dread(skip="What changes for you") + GOOD_BLAST, "", (), False),
    (dread(skip="Proof") + GOOD_BLAST, "", (), False),
    (dread(skip="NOT done") + GOOD_BLAST, "", (), False),
    (dread(ask="") + GOOD_BLAST, "", (), False),
    (dread(what="TBD") + GOOD_BLAST, "", (), False),
    (dread(notdone="_pending_") + GOOD_BLAST, "", (), False),
    (dread(ask="None.", notdone="Nothing.") + GOOD_BLAST, "", (), True),
    ("## For Dread\n- **Ask**: Nothing.\n- **What changes for you**: new check\n- **Proof**:\n  - run " + RUN + "\n- **NOT done**: none\n\n" + GOOD_BLAST, "", (), True),
    (dread(proof="I ran it locally.") + GOOD_BLAST, "", (), False),
    ("## For Dread\nAsk Dread later.\n**What changes for you:** x y z\n**Proof:** %s\n**NOT done:** none\n\n" % RUN + GOOD_BLAST, "", (), False),
    (dread(proof="see https://github.com/o/r/blob/main/tools/x.py") + GOOD_BLAST, "", (), False),
    (dread(proof="see https://github.com/o/r/tree/cursor/1-x/tools") + GOOD_BLAST, "", (), False),
    (dread(proof="see https://github.com/o/r/blob/%s/tools/x.py#L3" % SHA40) + GOOD_BLAST, "", (), True),
    (dread(proof="see https://github.com/o/r/tree/%s/tools and %s" % (SHA40.upper().lower(), RUN)) + GOOD_BLAST, "", (), True),
    (dread(proof="https://github.com/o/r/pull/5 and https://github.com/o/r/issues/6") + GOOD_BLAST, "", (), True),
    (dread(proof="%s plus https://github.com/o/r/blob/%s/a.py" % (RUN, SHA40[:7])) + GOOD_BLAST, "", (), False),
    (GOOD_DREAD + "## Blast radius\n\n## Test\nran it\n", "", (), False),
    (GOOD_DREAD + "## Blast radius\nTBD\n\nCloses #9\n\n## What\nx\n", "", (), False),
    (GOOD_DREAD + "## Blast radius\n\nFixes DreadfullyDespized/unknown-shard#12\n", "", (), False),
    (GOOD_DREAD + "## Blast radius\n<!-- describe -->\n", "", (), False),
    (GOOD_DREAD + "## Blast radius\n_None._\n", "", (), False),
    (GOOD_DREAD + "## Blast radius\nTBD, N/A\n## Risk\nlow\n", "", (), False),
    (GOOD_DREAD + "## blast radius ##\n### Runtime\nCI only.\n", "", (), True),
    (GOOD_DREAD + "**Proof:** see `## Blast radius` below\n## Blast radius\nN/A\n", "", (), False),
    (GOOD, "fix: crlf bodies", (), False),
    (GOOD, "Fix(ci): crlf bodies", (), False),
    (GOOD, "Add check", ("bug",), False),
    (GOOD, "Add check", ("Correction",), False),
    (GOOD, "Fixes the docs", (), True),
    (GOOD + BUG_EXTRA, "fix: crlf bodies", ("bug",), True),
    (GOOD + BUG_EXTRA.replace("## Root cause\nThe parser skipped CRLF lines.", "## Root cause\nTBD"), "fix: x", (), False),
    (GOOD + BUG_EXTRA.replace("## Regression origin", "## Origin"), "fix: x", (), False),
    (GOOD + BUG_EXTRA.split("\n## Options")[0], "fix: x", (), False),
    (GOOD + BUG_EXTRA.split("\n- Reject")[0] + "\n", "fix: x", (), False),
    (GOOD + BUG_EXTRA.replace(": https://github.com/o/r/issues/4", " only"), "fix: x", (), False),
    (GOOD + BUG_EXTRA.split("\n## Options")[0] + "\n## Options\n### A\nKeep it: %s\n### B\nDrop it: %s\n" % (RUN, RUN), "", ("correction",), True),
    (GOOD_DREAD + "## Blast radius\nTBD later\n", "", (), False),
    (GOOD_DREAD + "## Blast radius\nTBD - will fill in\n", "", (), False),
    (GOOD_DREAD + "## Blast radius\ntbd tbd\n", "", (), False),
    (GOOD_DREAD + "<!--\n## Blast radius\nCI only, nothing runtime.\n-->\n", "", (), False),
    (GOOD_DREAD + "```\n## Blast radius\nCI only, nothing runtime.\n```\n", "", (), False),
    ("<!--\n## For Dread\n**Ask:** Nothing.\n**What changes for you:** x y z\n**Proof:** %s\n**NOT done:** none\n-->\n" % RUN + GOOD_BLAST, "", (), False),
    ("~~~md\n" + GOOD_DREAD + "~~~\n" + GOOD_BLAST, "", (), False),
    (GOOD_DREAD + "## Blast radius\nCI only.\n<!-- unclosed note\n## Test\n", "", (), True),
    (GOOD_DREAD + "## Blast radius\nCI only; the workflow rejects `TBD` and `todo` as text.\n", "", (), True),
    (GOOD_DREAD + "## Blast radius\nOnly the checker.\n```sh\n## Blast radius\n```\n", "", (), True),
    (dread(notdone="Will fill in later.") + GOOD_BLAST, "", (), False),
    (GOOD, "Add check", ("type: bug",), False),
    (GOOD, "Add check", ("debugging",), True),
)


def self_test():
    failures = 0
    for body, title, labels, expected_ok in SELF_TEST_CASES:
        problems = check(body, title, labels)
        if (not problems) != expected_ok:
            failures += 1
            print("FAIL expected %s for title=%r labels=%r body=%r -> %r" % ("pass" if expected_ok else "fail", title, labels, body, problems))
    if parse_labels('["bug","ui"]') != ["bug", "ui"] or parse_labels("bug,ui") != ["bug", "ui"] or parse_labels("") != []:
        failures += 1
        print("FAIL parse_labels")
    print("self-test: %d cases, %d failures" % (len(SELF_TEST_CASES) + 1, failures))
    return failures == 0


def main():
    parser = argparse.ArgumentParser(description="Fail when the PR body lacks the required sections.")
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--body-file")
    parser.add_argument("--title")
    parser.add_argument("--labels", help="comma-separated or JSON array")
    args = parser.parse_args()
    if args.self_test:
        return 0 if self_test() else 1
    if args.body_file:
        with open(args.body_file, encoding="utf-8") as handle:
            body = handle.read()
    else:
        body = os.environ.get("PR_BODY")
    title = args.title if args.title is not None else os.environ.get("PR_TITLE", "")
    labels = parse_labels(args.labels if args.labels is not None else os.environ.get("PR_LABELS", ""))
    problems = check(body, title, labels)
    for problem in problems:
        print("::error::" + problem)
    if problems:
        return 1
    kind = "bug/correction PR" if is_bug_pr(title, labels) else "PR"
    print("PR body has every required section for a %s." % kind)
    return 0


if __name__ == "__main__":
    sys.exit(main())
