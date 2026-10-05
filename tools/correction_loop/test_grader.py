import os
import sys
import unittest
from datetime import datetime, timedelta, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import grader

HEAD = "0123456789abcdef0123456789abcdef01234567"
OLD = "fedcba9876543210fedcba9876543210fedcba98"
CREATED = datetime(2026, 10, 4, 12, 0, tzinfo=timezone.utc)
CFG = {"min_pr_age_minutes": 30, "grader_logins": ["DreadfullyDespized"], "min_sha_chars": 40,
       "require_grader_run": True, "grader_run_max_age_hours": 24}
AUTHOR = "DreadfullyDespized"
HEAD_TIME = CREATED - timedelta(minutes=5)


def run_id(minutes, salt):
    when = CREATED + timedelta(minutes=minutes)
    return f"gr-{when.strftime(grader.RUN_STAMP)}-{salt:016x}"


def with_run(body, minutes, salt, run):
    if run == "auto" and body.startswith("Verdict:"):
        return f"{body}\ngrader-run: {run_id(minutes - 1, salt)}"
    if run and run != "auto":
        return f"{body}\ngrader-run: {run}"
    return body


def pr(state="open", head=HEAD, author=AUTHOR):
    return {"number": 7, "state": state, "created_at": grader.stamp(CREATED), "user": {"login": author, "type": "User"},
            "head": {"sha": head, "ref": "cursor/7-x", "repo": {"full_name": "owner/repo"}}}


def comment(body, minutes=1, login="DreadfullyDespized", assoc="OWNER", kind="User", cid=1, run="auto"):
    return {"id": cid, "user": {"login": login, "type": kind}, "author_association": assoc,
            "created_at": grader.stamp(CREATED + timedelta(minutes=minutes)),
            "updated_at": grader.stamp(CREATED + timedelta(minutes=minutes)), "body": with_run(body, minutes, cid, run),
            "html_url": f"https://github.com/owner/repo/pull/7#issuecomment-{cid}"}


def review(body, minutes=1, state="COMMENTED", rid=9, run="auto"):
    return {"id": rid, "user": {"login": "DreadfullyDespized", "type": "User"}, "author_association": "OWNER",
            "submitted_at": grader.stamp(CREATED + timedelta(minutes=minutes)), "body": with_run(body, minutes, 1000 + rid, run),
            "state": state,
            "html_url": f"https://github.com/owner/repo/pull/7#pullrequestreview-{rid}"}


def run(comments=(), reviews=(), at=45, the_pr=None, head=HEAD, cfg=CFG, head_time=HEAD_TIME):
    items = grader.verdict_items(list(reviews), list(comments))
    return grader.evaluate(the_pr or pr(), head, items, CREATED + timedelta(minutes=at), cfg, head_time=head_time)


class Verdicts(unittest.TestCase):
    def test_default_is_fail(self):
        r = run()
        self.assertFalse(r["ok"])
        self.assertIn("default is FAIL", r["reason"])

    def test_pass_on_head_after_min_age_passes(self):
        r = run([comment(f"Verdict: PASS {HEAD}")])
        self.assertTrue(r["ok"], r["reason"])

    def test_full_sha_with_trailing_text_passes(self):
        self.assertTrue(run([comment(f"Verdict: PASS {HEAD} all proof links checked")])["ok"])

    def test_twelve_char_prefix_is_ignored(self):
        r = run([comment(f"Verdict: PASS {HEAD[:12]}")])
        self.assertFalse(r["ok"])
        self.assertIn("40+", r["considered"][0]["ignored"])

    def test_pr_author_pass_with_grader_run_passes(self):
        r = run([comment(f"Verdict: PASS {HEAD}")], the_pr=pr(author="DreadfullyDespized"))
        self.assertTrue(r["ok"], r["reason"])
        self.assertTrue(r["considered"][0]["grader_run"].startswith("gr-"))

    def test_pass_without_grader_run_is_ignored(self):
        r = run([comment(f"Verdict: PASS {HEAD}", run=None)])
        self.assertFalse(r["ok"])
        self.assertIn("no grader-run line", r["considered"][0]["ignored"])

    def test_malformed_grader_run_is_ignored(self):
        for bad in ("gr-1", "gr-20261004T120000Z-abc", "gr-20261004T120000Z-0123456789ABCDEF", "run-20261004T120000Z-0123456789abcdef"):
            r = run([comment(f"Verdict: PASS {HEAD}", run=bad)])
            self.assertFalse(r["ok"], bad)
            self.assertIn("is not gr-", r["considered"][0]["ignored"])

    def test_two_grader_run_lines_are_ignored(self):
        body = f"Verdict: PASS {HEAD}\ngrader-run: {run_id(0, 1)}\ngrader-run: {run_id(0, 2)}"
        r = run([comment(body, run=None)])
        self.assertFalse(r["ok"])
        self.assertIn("more than one", r["considered"][0]["ignored"])

    def test_reused_grader_run_is_ignored(self):
        rid = run_id(2, 5)
        r = run([comment(f"Verdict: FAIL {OLD}", 3, cid=1, run=rid), comment(f"Verdict: PASS {HEAD}", 6, cid=2, run=rid)])
        self.assertFalse(r["ok"])
        self.assertIn("more than once", r["considered"][1]["ignored"])

    def test_grader_run_planted_in_pr_body_is_ignored(self):
        rid = run_id(2, 5)
        the_pr = pr()
        the_pr["body"] = f"## For Dread\nPre-made grader-run: {rid}"
        r = run([comment(f"Verdict: PASS {HEAD}", 6, run=rid)], the_pr=the_pr)
        self.assertFalse(r["ok"])
        self.assertIn("more than once", r["considered"][0]["ignored"])

    def test_grader_run_dated_after_verdict_is_ignored(self):
        r = run([comment(f"Verdict: PASS {HEAD}", 6, run=run_id(20, 3))])
        self.assertFalse(r["ok"])
        self.assertIn("after the verdict", r["considered"][0]["ignored"])

    def test_grader_run_older_than_max_age_is_ignored(self):
        r = run([comment(f"Verdict: PASS {HEAD}", 6, run=run_id(-60 * 25, 3))], head_time=None)
        self.assertFalse(r["ok"])
        self.assertIn("older than 24 hours", r["considered"][0]["ignored"])

    def test_grader_run_started_before_head_commit_is_ignored(self):
        r = run([comment(f"Verdict: PASS {HEAD}", 6, run=run_id(1, 3))], head_time=CREATED + timedelta(minutes=4))
        self.assertFalse(r["ok"])
        self.assertIn("before the head commit", r["considered"][0]["ignored"])

    def test_new_push_invalidates_pass_even_with_grader_run(self):
        r = run([comment(f"Verdict: PASS {OLD}", 6)])
        self.assertFalse(r["ok"])
        self.assertIn("not the current head", r["considered"][0]["ignored"])

    def test_new_run_id_has_checkable_format(self):
        rid = grader.new_run_id(CREATED)
        self.assertRegex(rid, r"^gr-20261004T120000Z-[0-9a-f]{16}$")
        self.assertNotEqual(rid, grader.new_run_id(CREATED))

    def test_other_trusted_grader_with_empty_logins_passes(self):
        cfg = dict(CFG, grader_logins=[])
        r = run([comment(f"Verdict: PASS {HEAD}", login="grader-bob", assoc="COLLABORATOR")], cfg=cfg)
        self.assertTrue(r["ok"], r["reason"])

    def test_untrusted_other_login_still_ignored_with_empty_logins(self):
        cfg = dict(CFG, grader_logins=[])
        r = run([comment(f"Verdict: PASS {HEAD}", login="stranger", assoc="NONE")], cfg=cfg)
        self.assertFalse(r["ok"])

    def test_short_sha_is_ignored(self):
        r = run([comment(f"Verdict: PASS {HEAD[:7]}")])
        self.assertFalse(r["ok"])
        self.assertIn("40+", r["considered"][0]["ignored"])

    def test_pass_without_sha_is_ignored(self):
        self.assertFalse(run([comment("Verdict: PASS")])["ok"])

    def test_pass_on_old_head_is_stale(self):
        r = run([comment(f"Verdict: PASS {OLD}")])
        self.assertFalse(r["ok"])
        self.assertIn("not the current head", r["considered"][0]["ignored"])

    def test_pass_too_young_fails_and_reports_wait(self):
        r = run([comment(f"Verdict: PASS {HEAD}")], at=1.27)
        self.assertFalse(r["ok"])
        self.assertTrue(r["waiting"])
        self.assertIn("younger than 30 minutes", r["reason"])
        self.assertEqual(r["eligible_at"], "2026-10-04T12:30:00Z")

    def test_exactly_min_age_passes(self):
        self.assertTrue(run([comment(f"Verdict: PASS {HEAD}")], at=30)["ok"])

    def test_min_age_is_data(self):
        cfg = dict(CFG, min_pr_age_minutes=60)
        self.assertFalse(run([comment(f"Verdict: PASS {HEAD}")], at=45, cfg=cfg)["ok"])
        self.assertTrue(run([comment(f"Verdict: PASS {HEAD}")], at=61, cfg=cfg)["ok"])

    def test_later_fail_overrides_pass(self):
        r = run([comment(f"Verdict: PASS {HEAD}", 5, cid=1), comment(f"Verdict: FAIL {HEAD} proof missing", 6, cid=2)])
        self.assertFalse(r["ok"])
        self.assertIn("FAIL", r["reason"])

    def test_later_pass_overrides_fail(self):
        self.assertTrue(run([comment(f"Verdict: FAIL {HEAD}", 5, cid=1), comment(f"Verdict: PASS {HEAD}", 6, cid=2)])["ok"])

    def test_fail_on_old_head_does_not_block_new_head(self):
        self.assertTrue(run([comment(f"Verdict: FAIL {OLD}", 5, cid=1), comment(f"Verdict: PASS {HEAD}", 6, cid=2)])["ok"])

    def test_marker_must_be_first_line(self):
        self.assertFalse(run([comment(f"Looks fine.\nVerdict: PASS {HEAD}")])["ok"])

    def test_marker_is_case_sensitive(self):
        self.assertFalse(run([comment(f"verdict: pass {HEAD}")])["ok"])

    def test_bot_is_ignored(self):
        r = run([comment(f"Verdict: PASS {HEAD}", login="github-actions[bot]", kind="Bot")])
        self.assertFalse(r["ok"])
        self.assertEqual(r["considered"][0]["ignored"], "posted by a bot")

    def test_outside_contributor_is_ignored(self):
        self.assertFalse(run([comment(f"Verdict: PASS {HEAD}", login="DreadfullyDespized", assoc="CONTRIBUTOR")])["ok"])

    def test_login_not_in_grader_logins_is_ignored(self):
        self.assertFalse(run([comment(f"Verdict: PASS {HEAD}", login="someone", assoc="COLLABORATOR")])["ok"])

    def test_review_pass_counts(self):
        self.assertTrue(run(reviews=[review(f"Verdict: PASS {HEAD}")])["ok"])

    def test_dismissed_review_is_ignored(self):
        self.assertFalse(run(reviews=[review(f"Verdict: PASS {HEAD}", state="DISMISSED")])["ok"])

    def test_edited_comment_counts_at_edit_time(self):
        early_fail = comment(f"Verdict: FAIL {HEAD}", 5, cid=1)
        edited = comment(f"Verdict: PASS {HEAD}", 2, cid=2)
        edited["updated_at"] = grader.stamp(CREATED + timedelta(minutes=8))
        self.assertTrue(run([early_fail, edited])["ok"])

    def test_closed_pr_fails(self):
        self.assertFalse(run([comment(f"Verdict: PASS {HEAD}")], the_pr=pr(state="closed"))["ok"])

    def test_run_for_stale_head_fails(self):
        r = run([comment(f"Verdict: PASS {HEAD}")], the_pr=pr(head=OLD))
        self.assertFalse(r["ok"])
        self.assertIn("PR head is now", r["reason"])


class FakeApi:
    def __init__(self, the_pr, comments=(), reviews=(), runs=None):
        self.repo = "owner/repo"
        self.the_pr = the_pr
        self.comments = list(comments)
        self.reviews = list(reviews)
        self.runs = [{"id": 41, "event": "pull_request", "status": "completed", "head_sha": HEAD,
                      "created_at": "2026-10-04T12:00:10Z"}] if runs is None else runs
        self.posts = []

    def get(self, path):
        if f"/actions/workflows/{grader.WORKFLOW_FILE}/runs" in path:
            return {"workflow_runs": self.runs}
        return self.the_pr

    def paged(self, path, key=None, limit=2000):
        return self.reviews if path.endswith("/reviews") else self.comments

    def post(self, path, body):
        self.posts.append((path, body))


class Relay(unittest.TestCase):
    def payload(self, body, assoc="OWNER", kind="User", action="created"):
        return {"action": action, "issue": {"number": 7, "pull_request": {}},
                "comment": {"body": body, "author_association": assoc, "user": {"login": AUTHOR, "type": kind}}}

    def relay(self, api, body, minutes=45, **kw):
        return grader.relay(api, CFG, "issue_comment", self.payload(body, **kw), now=lambda tz: CREATED + timedelta(minutes=minutes))

    def test_comment_verdict_reruns_only_the_latest_pr_gate_run(self):
        runs = [{"id": 41, "event": "pull_request", "status": "completed", "head_sha": HEAD, "created_at": "2026-10-04T12:00:10Z"},
                {"id": 42, "event": "pull_request", "status": "completed", "head_sha": HEAD, "created_at": "2026-10-04T12:05:00Z"},
                {"id": 43, "event": "workflow_dispatch", "status": "completed", "head_sha": HEAD, "created_at": "2026-10-04T12:09:00Z"},
                {"id": 44, "event": "pull_request", "status": "completed", "head_sha": OLD, "created_at": "2026-10-04T12:10:00Z"}]
        api = FakeApi(pr(), [comment(f"Verdict: PASS {HEAD}")], runs=runs)
        out = self.relay(api, f"Verdict: PASS {HEAD}")
        self.assertEqual(out["action"], "rerun")
        self.assertEqual(api.posts, [("/repos/owner/repo/actions/runs/42/rerun", {})])

    def test_running_latest_gate_run_is_left_to_read_the_verdict(self):
        runs = [{"id": 41, "event": "pull_request", "status": "completed", "head_sha": HEAD, "created_at": "2026-10-04T12:00:10Z"},
                {"id": 42, "event": "pull_request", "status": "in_progress", "head_sha": HEAD, "created_at": "2026-10-04T12:05:00Z"}]
        api = FakeApi(pr(), [comment(f"Verdict: FAIL {HEAD}")], runs=runs)
        out = self.relay(api, f"Verdict: FAIL {HEAD}")
        self.assertEqual(out["action"], "skipped")
        self.assertEqual(out["run_id"], 42)
        self.assertEqual(api.posts, [])

    def test_no_pr_run_falls_back_to_dispatch_on_head_branch(self):
        api = FakeApi(pr(), [comment(f"Verdict: PASS {HEAD}")], runs=[])
        out = self.relay(api, f"Verdict: PASS {HEAD}")
        self.assertEqual(out["action"], "dispatched")
        self.assertEqual(api.posts, [("/repos/owner/repo/actions/workflows/gate.yml/dispatches",
                                      {"ref": "cursor/7-x", "inputs": {"pr": "7"}})])

    def test_young_pass_defers_without_sleeping_or_rerunning(self):
        api = FakeApi(pr(), [comment(f"Verdict: PASS {HEAD}")])
        out = self.relay(api, f"Verdict: PASS {HEAD}", minutes=10)
        self.assertEqual(out["action"], "deferred")
        self.assertEqual(out["eligible_at"], grader.stamp(CREATED + timedelta(minutes=30)))
        self.assertIn("/grader", out["why"])
        self.assertEqual(api.posts, [])

    def test_regrade_command_after_min_age_reruns(self):
        api = FakeApi(pr(), [comment(f"Verdict: PASS {HEAD}")])
        out = self.relay(api, "/grader", minutes=31)
        self.assertEqual(out["action"], "rerun")
        self.assertEqual(api.posts, [("/repos/owner/repo/actions/runs/41/rerun", {})])

    def test_regrade_command_from_untrusted_or_bot_is_skipped(self):
        for kw in ({"assoc": "NONE"}, {"kind": "Bot"}):
            api = FakeApi(pr(), [comment(f"Verdict: PASS {HEAD}")])
            out = self.relay(api, "/grader", **kw)
            self.assertEqual(out["action"], "skipped", kw)
            self.assertEqual(api.posts, [])

    def test_regrade_prefix_word_is_not_a_command(self):
        api = FakeApi(pr())
        self.assertEqual(self.relay(api, "/graderish please")["action"], "skipped")

    def test_relay_never_sleeps(self):
        import inspect
        self.assertNotIn("sleep", inspect.signature(grader.relay).parameters)
        with open(grader.__file__, encoding="utf-8") as fh:
            source = fh.read()
        self.assertNotIn("sleep(", source)
        self.assertNotIn("import time", source)

    def test_non_verdict_comment_is_skipped(self):
        api = FakeApi(pr())
        out = self.relay(api, "looks good")
        self.assertEqual(out["action"], "skipped")

    def test_review_fail_reruns_earlier_green_run(self):
        api = FakeApi(pr(), reviews=[review(f"Verdict: FAIL {HEAD}")])
        payload = {"action": "submitted", "review": {"body": f"Verdict: FAIL {HEAD}"}, "pull_request": {"number": 7}}
        out = grader.relay(api, CFG, "pull_request_review", payload, now=lambda tz: CREATED + timedelta(minutes=45))
        self.assertEqual(out["action"], "rerun")
        self.assertEqual(api.posts, [("/repos/owner/repo/actions/runs/41/rerun", {})])

    def test_fork_head_is_not_dispatched(self):
        fork = pr()
        fork["head"]["repo"] = {"full_name": "someone/fork"}
        api = FakeApi(fork, [comment(f"Verdict: PASS {HEAD}")])
        out = self.relay(api, f"Verdict: PASS {HEAD}")
        self.assertEqual(out["action"], "skipped")


class Target(unittest.TestCase):
    def test_pull_request_uses_head_sha_not_merge_sha(self):
        self.assertEqual(grader.target("pull_request", {"pull_request": {"number": 7, "head": {"sha": HEAD}}}), (7, HEAD))

    def test_dispatch_uses_run_sha(self):
        os.environ["GITHUB_SHA"] = HEAD
        try:
            self.assertEqual(grader.target("workflow_dispatch", {"inputs": {"pr": "7"}}), (7, HEAD))
        finally:
            del os.environ["GITHUB_SHA"]

    def test_config_has_grader_section(self):
        cfg = grader.load_config()
        self.assertGreaterEqual(int(cfg["min_pr_age_minutes"]), 30)
        self.assertEqual(int(cfg["min_sha_chars"]), 40)
        self.assertTrue(cfg["require_grader_run"])
        self.assertNotIn("exclude_pr_author", cfg)


if __name__ == "__main__":
    unittest.main()
