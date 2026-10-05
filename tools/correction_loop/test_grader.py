import os
import sys
import unittest
from datetime import datetime, timedelta, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import grader

HEAD = "0123456789abcdef0123456789abcdef01234567"
OLD = "fedcba9876543210fedcba9876543210fedcba98"
CREATED = datetime(2026, 10, 4, 12, 0, tzinfo=timezone.utc)
CFG = {"min_pr_age_minutes": 30, "grader_logins": ["DreadfullyDespized"], "min_sha_chars": 12,
       "max_relay_wait_minutes": 30}


def pr(state="open", head=HEAD):
    return {"number": 7, "state": state, "created_at": grader.stamp(CREATED),
            "head": {"sha": head, "ref": "cursor/7-x", "repo": {"full_name": "owner/repo"}}}


def comment(body, minutes=1, login="DreadfullyDespized", assoc="OWNER", kind="User", cid=1):
    return {"id": cid, "user": {"login": login, "type": kind}, "author_association": assoc,
            "created_at": grader.stamp(CREATED + timedelta(minutes=minutes)),
            "updated_at": grader.stamp(CREATED + timedelta(minutes=minutes)), "body": body,
            "html_url": f"https://github.com/owner/repo/pull/7#issuecomment-{cid}"}


def review(body, minutes=1, state="COMMENTED", rid=9):
    return {"id": rid, "user": {"login": "DreadfullyDespized", "type": "User"}, "author_association": "OWNER",
            "submitted_at": grader.stamp(CREATED + timedelta(minutes=minutes)), "body": body, "state": state,
            "html_url": f"https://github.com/owner/repo/pull/7#pullrequestreview-{rid}"}


def run(comments=(), reviews=(), at=45, the_pr=None, head=HEAD, cfg=CFG):
    items = grader.verdict_items(list(reviews), list(comments))
    return grader.evaluate(the_pr or pr(), head, items, CREATED + timedelta(minutes=at), cfg)


class Verdicts(unittest.TestCase):
    def test_default_is_fail(self):
        r = run()
        self.assertFalse(r["ok"])
        self.assertIn("default is FAIL", r["reason"])

    def test_pass_on_head_after_min_age_passes(self):
        r = run([comment(f"Verdict: PASS {HEAD}")])
        self.assertTrue(r["ok"], r["reason"])

    def test_pass_with_twelve_char_prefix_passes(self):
        self.assertTrue(run([comment(f"Verdict: PASS {HEAD[:12]} all proof links checked")])["ok"])

    def test_short_sha_is_ignored(self):
        r = run([comment(f"Verdict: PASS {HEAD[:7]}")])
        self.assertFalse(r["ok"])
        self.assertIn("12+", r["considered"][0]["ignored"])

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
    def __init__(self, the_pr, comments=(), reviews=()):
        self.repo = "owner/repo"
        self.the_pr = the_pr
        self.comments = list(comments)
        self.reviews = list(reviews)
        self.posts = []

    def get(self, path):
        return self.the_pr

    def paged(self, path, key=None, limit=2000):
        return self.reviews if path.endswith("/reviews") else self.comments

    def post(self, path, body):
        self.posts.append((path, body))


class Relay(unittest.TestCase):
    def payload(self, body):
        return {"action": "created", "issue": {"number": 7, "pull_request": {}}, "comment": {"body": body}}

    def test_comment_verdict_dispatches_grader_on_head_branch(self):
        api = FakeApi(pr(), [comment(f"Verdict: PASS {HEAD}")])
        out = grader.relay(api, CFG, "issue_comment", self.payload(f"Verdict: PASS {HEAD}"),
                           sleep=lambda s: None, now=lambda tz: CREATED + timedelta(minutes=45))
        self.assertEqual(out["action"], "dispatched")
        self.assertEqual(api.posts, [("/repos/owner/repo/actions/workflows/grader.yml/dispatches",
                                      {"ref": "cursor/7-x", "inputs": {"pr": "7"}})])

    def test_young_pass_waits_then_dispatches(self):
        slept = []
        api = FakeApi(pr(), [comment(f"Verdict: PASS {HEAD}")])
        out = grader.relay(api, CFG, "issue_comment", self.payload(f"Verdict: PASS {HEAD}"),
                           sleep=slept.append, now=lambda tz: CREATED + timedelta(minutes=10))
        self.assertEqual(slept, [20 * 60 + 5])
        self.assertEqual(out["action"], "dispatched")

    def test_head_moved_while_waiting_skips(self):
        api = FakeApi(pr(), [comment(f"Verdict: PASS {HEAD}")])

        def moved(seconds):
            api.the_pr = pr(head=OLD)

        out = grader.relay(api, CFG, "issue_comment", self.payload(f"Verdict: PASS {HEAD}"),
                           sleep=moved, now=lambda tz: CREATED + timedelta(minutes=10))
        self.assertEqual(out["action"], "skipped")
        self.assertEqual(api.posts, [])

    def test_non_verdict_comment_is_skipped(self):
        api = FakeApi(pr())
        out = grader.relay(api, CFG, "issue_comment", self.payload("looks good"), sleep=lambda s: None)
        self.assertEqual(out["action"], "skipped")

    def test_review_without_wait_does_not_dispatch(self):
        api = FakeApi(pr(), reviews=[review(f"Verdict: PASS {HEAD}")])
        payload = {"action": "submitted", "review": {"body": f"Verdict: PASS {HEAD}"}, "pull_request": {"number": 7}}
        out = grader.relay(api, CFG, "pull_request_review", payload, sleep=lambda s: None,
                           now=lambda tz: CREATED + timedelta(minutes=45))
        self.assertEqual(out["action"], "skipped")
        self.assertEqual(api.posts, [])

    def test_fork_head_is_not_dispatched(self):
        fork = pr()
        fork["head"]["repo"] = {"full_name": "someone/fork"}
        api = FakeApi(fork, [comment(f"Verdict: PASS {HEAD}")])
        out = grader.relay(api, CFG, "issue_comment", self.payload(f"Verdict: PASS {HEAD}"),
                           sleep=lambda s: None, now=lambda tz: CREATED + timedelta(minutes=45))
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
        self.assertGreaterEqual(int(cfg["min_sha_chars"]), 12)


if __name__ == "__main__":
    unittest.main()
