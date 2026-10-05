import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import loop
from gh import ApiError

REPO = "owner/repo"
CFG = dict(loop.load_config(), watched_workflows=["CI", "Deploy"], required_checks=[], watch_deployments=True,
           deploy_environments=[])
OWNER = {"login": "DreadfullyDespized", "type": "User"}
BOT = {"login": "github-actions[bot]", "type": "Bot"}


class FakeApi:
    def __init__(self, routes=None, paged=None):
        self.repo = REPO
        self.routes = routes or {}
        self.pages = paged or {}
        self.calls = []
        self.next_issue = 100

    def get(self, path):
        self.calls.append(("GET", path))
        base = path.split("?", 1)[0]
        for key in (path, base):
            if key in self.routes:
                value = self.routes[key]
                if isinstance(value, Exception):
                    raise value
                return value
        raise ApiError(404, path)

    def raw(self, path):
        return self.get(path)

    def paged(self, path, key=None, limit=2000):
        self.calls.append(("PAGED", path))
        base = path.split("?", 1)[0]
        return self.pages.get(path, self.pages.get(base, []))

    def post(self, path, body):
        self.calls.append(("POST", path, body))
        if path.endswith("/issues"):
            self.next_issue += 1
            return {"number": self.next_issue, "html_url": f"https://github.com/{REPO}/issues/{self.next_issue}"}
        return {}

    def patch(self, path, body):
        self.calls.append(("PATCH", path, body))
        return {}

    def posts(self, suffix):
        return [c for c in self.calls if c[0] == "POST" and c[1].endswith(suffix)]


def failed_run(event="push", branch="main", name="CI", conclusion="failure"):
    return {"id": 55, "name": name, "event": event, "head_branch": branch, "conclusion": conclusion,
            "run_number": 3, "html_url": f"https://github.com/{REPO}/actions/runs/55", "head_sha": "a" * 40,
            "pull_requests": [], "head_commit": {"message": "x"}}


def run_api():
    return FakeApi(
        routes={f"/repos/{REPO}/actions/jobs/9/logs": b"2026-10-04T01:02:03.0000000Z ##[error]Build failed: 3 errors in Foo.cs\n"},
        paged={f"/repos/{REPO}/actions/runs/55/jobs": [
            {"id": 9, "name": "build", "conclusion": "failure", "html_url": "https://x/job/9",
             "steps": [{"name": "dotnet build", "conclusion": "failure"}]}]})


class Intake(unittest.TestCase):
    def test_main_ci_failure_opens_level_1_issue(self):
        api = run_api()
        out = loop.intake(api, CFG, "workflow_run", {"workflow_run": failed_run()})
        self.assertEqual(out[0]["action"], "opened")
        body = api.posts("/issues")[0][2]
        self.assertEqual(body["labels"], ["correction"])
        self.assertIn("Required level: 1 (codebase)", body["body"])
        self.assertTrue(out[0]["key"].startswith("ci:CI:"))
        self.assertIn("Build failed", body["title"])

    def test_same_failure_with_other_numbers_has_same_key(self):
        a = loop.intake_findings(run_api(), CFG, "workflow_run", {"workflow_run": failed_run()})[0]["key"]
        api = run_api()
        api.routes[f"/repos/{REPO}/actions/jobs/9/logs"] = b"##[error]Build failed: 7 errors in Foo.cs\n"
        b = loop.intake_findings(api, CFG, "workflow_run", {"workflow_run": failed_run()})[0]["key"]
        self.assertEqual(a, b)

    def test_open_issue_with_same_key_gets_comment(self):
        api = run_api()
        key = loop.intake_findings(api, CFG, "workflow_run", {"workflow_run": failed_run()})[0]["key"]
        api.pages[f"/repos/{REPO}/issues"] = [{"number": 4, "state": "open", "body": f"Correction key: `{key}`"}]
        out = loop.intake(api, CFG, "workflow_run", {"workflow_run": failed_run()})
        self.assertEqual(out[0]["action"], "commented")
        self.assertEqual(api.posts("/issues"), [])
        self.assertEqual(len(api.posts("/issues/4/comments")), 1)

    def test_closed_issue_with_same_key_is_named_as_repeat(self):
        api = run_api()
        key = loop.intake_findings(api, CFG, "workflow_run", {"workflow_run": failed_run()})[0]["key"]
        api.pages[f"/repos/{REPO}/issues"] = [{"number": 4, "state": "closed", "body": f"Correction key: `{key}`"}]
        loop.intake(api, CFG, "workflow_run", {"workflow_run": failed_run()})
        self.assertIn("Repeat of: #4", api.posts("/issues")[0][2]["body"])

    def test_pr_branch_failure_is_ignored_unless_required(self):
        self.assertEqual(loop.intake_findings(run_api(), CFG, "workflow_run",
                                              {"workflow_run": failed_run(event="pull_request", branch="x")}), [])
        cfg = dict(CFG, required_checks=["CI"])
        self.assertEqual(len(loop.intake_findings(run_api(), cfg, "workflow_run",
                                                  {"workflow_run": failed_run(event="pull_request", branch="x")})), 1)

    def test_success_and_unwatched_are_ignored(self):
        self.assertEqual(loop.intake_findings(run_api(), CFG, "workflow_run", {"workflow_run": failed_run(conclusion="success")}), [])
        self.assertEqual(loop.intake_findings(run_api(), CFG, "workflow_run", {"workflow_run": failed_run(name="grader")}), [])

    def test_main_deploy_workflow_failure_opens_issue(self):
        out = loop.intake_findings(run_api(), CFG, "workflow_run", {"workflow_run": failed_run(name="Deploy")})
        self.assertEqual(out[0]["level"], 1)

    def test_deployment_status_failure_opens_issue(self):
        payload = {"deployment_status": {"state": "failure", "description": "Build exited with 1", "log_url": "https://v/log"},
                   "deployment": {"environment": "Production", "sha": "b" * 40, "ref": "main"}}
        out = loop.intake_findings(run_api(), CFG, "deployment_status", payload)
        self.assertEqual(out[0]["key"].split(":")[0:2], ["deploy", "Production"])
        payload["deployment_status"]["state"] = "success"
        self.assertEqual(loop.intake_findings(run_api(), CFG, "deployment_status", payload), [])

    def test_grader_fail_comment_opens_issue(self):
        payload = {"action": "created", "issue": {"number": 7, "title": "t", "pull_request": {}},
                   "comment": {"id": 1, "user": OWNER, "author_association": "OWNER", "body": "Verdict: FAIL proof missing",
                               "html_url": "u"}}
        out = loop.intake_findings(run_api(), CFG, "issue_comment", payload)
        self.assertEqual([f["key"] for f in out], ["grader:pr7"])
        self.assertEqual(out[0]["level"], 2)

    def test_grader_fail_review_opens_issue(self):
        payload = {"action": "submitted", "pull_request": {"number": 7, "title": "t"},
                   "review": {"user": OWNER, "author_association": "OWNER", "body": "Verdict: FAIL x", "html_url": "u"}}
        self.assertEqual(loop.intake_findings(run_api(), CFG, "pull_request_review", payload)[0]["key"], "grader:pr7")

    def test_pass_and_bot_fail_open_nothing(self):
        base = {"action": "created", "issue": {"number": 7, "title": "t", "pull_request": {}}}
        ok = dict(base, comment={"id": 1, "user": OWNER, "author_association": "OWNER", "body": "Verdict: PASS abc"})
        bot = dict(base, comment={"id": 1, "user": BOT, "author_association": "OWNER", "body": "Verdict: FAIL"})
        self.assertEqual(loop.intake_findings(run_api(), CFG, "issue_comment", ok), [])
        self.assertEqual(loop.intake_findings(run_api(), CFG, "issue_comment", bot), [])

    def test_dread_label_opens_issue(self):
        payload = {"action": "labeled", "label": {"name": "dread-correction"},
                   "issue": {"number": 3, "title": "wrong", "user": OWNER, "author_association": "OWNER",
                             "labels": [{"name": "dread-correction"}], "body": "Required level: 2", "html_url": "u"}}
        out = loop.intake_findings(run_api(), CFG, "issues", payload)
        self.assertEqual(out[0]["key"], "dread:issue3")
        self.assertEqual(out[0]["level"], 2)

    def test_correction_comment_command_opens_issue(self):
        payload = {"action": "created", "issue": {"number": 3, "title": "t"},
                   "comment": {"id": 12, "user": OWNER, "author_association": "OWNER", "body": "/correction stop doing X",
                               "html_url": "u"}}
        self.assertEqual(loop.intake_findings(run_api(), CFG, "issue_comment", payload)[0]["key"], "dread:comment12")

    def test_manual_dispatch_opens_dread_correction(self):
        os.environ["GITHUB_RUN_ID"] = "777"
        try:
            api = run_api()
            out = loop.intake(api, CFG, "workflow_dispatch",
                              {"inputs": {"kind": "correction", "target": "Use ubuntu-latest", "body": "Required level: 2"}})
        finally:
            del os.environ["GITHUB_RUN_ID"]
        self.assertEqual(out[0]["key"], "dread:dispatch777")
        self.assertIn("Required level: 2 (lint/CI)", api.posts("/issues")[0][2]["body"])

    def test_correction_issue_itself_never_loops(self):
        payload = {"action": "labeled", "label": {"name": "dread-correction"},
                   "issue": {"number": 3, "title": "t", "user": OWNER, "author_association": "OWNER",
                             "labels": [{"name": "dread-correction"}, {"name": "correction"}]}}
        self.assertEqual(loop.intake_findings(run_api(), CFG, "issues", payload), [])

    def test_keys_group_is_stable(self):
        a = loop.intake_keys(run_api(), CFG, "workflow_run", {"workflow_run": failed_run()})
        b = loop.intake_keys(run_api(), CFG, "workflow_run", {"workflow_run": failed_run()})
        self.assertEqual(a, b)
        self.assertTrue(a["group"])


def merged_pr(n, body, login="DreadfullyDespized", kind="User", merged=True, base="main"):
    return {"number": n, "user": {"login": login, "type": kind}, "merged_at": "2026-10-04T00:00:00Z" if merged else None,
            "state": "closed", "base": {"ref": base}, "body": body}


LEVEL2 = "## For Dread\n**Ask:** Nothing.\n**What changes for you:** x\n**Level:** 2 (lint/CI)\n**Proof:** y\n**NOT done:** z\n"


class CloseGate(unittest.TestCase):
    def gate(self, prs, comments=(), timeline=(), issue_body="Opened by the correction loop\nSee #50", issue_user=BOT):
        routes = {f"/repos/{REPO}/pulls/{p['number']}": p for p in prs}
        api = FakeApi(routes=routes, paged={f"/repos/{REPO}/issues/9/comments": list(comments),
                                            f"/repos/{REPO}/issues/9/timeline": list(timeline)})
        issue = {"number": 9, "state": "closed", "labels": [{"name": "correction"}], "body": issue_body, "user": issue_user}
        return api, loop.close_gate(api, CFG, "issues", {"action": "closed", "issue": issue})

    def test_no_link_reopens(self):
        api, out = self.gate([])
        self.assertEqual(out["action"], "reopened")
        self.assertIn(("PATCH", f"/repos/{REPO}/issues/9", {"state": "open"}), api.calls)

    def test_bot_written_body_link_does_not_count(self):
        api, out = self.gate([merged_pr(50, LEVEL2)])
        self.assertEqual(out["action"], "reopened")

    def test_merged_level_2_pr_linked_by_comment_allows(self):
        api, out = self.gate([merged_pr(50, LEVEL2)], comments=[{"user": OWNER, "body": "Fixed by #50"}])
        self.assertEqual(out["action"], "allowed")
        self.assertFalse([c for c in api.calls if c[0] == "PATCH"])

    def test_merged_level_1_pr_cross_referenced_allows(self):
        timeline = [{"event": "cross-referenced", "source": {"issue": {"number": 51, "pull_request": {},
                                                                       "repository": {"full_name": REPO}}}}]
        _, out = self.gate([merged_pr(51, LEVEL2.replace("2 (lint/CI)", "1 (codebase)"))], timeline=timeline)
        self.assertEqual(out["action"], "allowed")

    def test_closing_commit_pr_allows(self):
        api2 = FakeApi(routes={f"/repos/{REPO}/commits/{'c' * 40}/pulls": [{"number": 52}],
                               f"/repos/{REPO}/pulls/52": merged_pr(52, LEVEL2)},
                       paged={f"/repos/{REPO}/issues/9/timeline": [{"event": "closed", "commit_id": "c" * 40}]})
        issue = {"number": 9, "state": "closed", "labels": [{"name": "correction"}], "body": "", "user": BOT}
        self.assertEqual(loop.close_gate(api2, CFG, "issues", {"action": "closed", "issue": issue})["action"], "allowed")

    def test_unmerged_pr_reopens(self):
        _, out = self.gate([merged_pr(50, LEVEL2, merged=False)], comments=[{"user": OWNER, "body": "#50"}])
        self.assertEqual(out["action"], "reopened")
        self.assertIn("#50: not merged (closed)", out["checked"])

    def test_level_3_pr_reopens(self):
        _, out = self.gate([merged_pr(50, LEVEL2.replace("2 (lint/CI)", "3 (review rules)"))],
                           comments=[{"user": OWNER, "body": "#50"}])
        self.assertEqual(out["action"], "reopened")

    def test_level_outside_for_dread_reopens(self):
        body = "## For Dread\n**Ask:** Nothing.\n**Proof:** Level: 2\n\n## Blast radius\nLevel: 1\n"
        _, out = self.gate([merged_pr(50, body)], comments=[{"user": OWNER, "body": "#50"}])
        self.assertEqual(out["action"], "reopened")

    def test_level_in_html_comment_reopens(self):
        body = "## For Dread\n<!--\n**Level:** 2\n-->\n**Proof:** x\n"
        _, out = self.gate([merged_pr(50, body)], comments=[{"user": OWNER, "body": "#50"}])
        self.assertEqual(out["action"], "reopened")

    def test_bot_pr_reopens(self):
        _, out = self.gate([merged_pr(50, LEVEL2, login="github-actions[bot]", kind="Bot")],
                           comments=[{"user": OWNER, "body": "#50"}])
        self.assertEqual(out["action"], "reopened")

    def test_pr_into_other_branch_reopens(self):
        _, out = self.gate([merged_pr(50, LEVEL2, base="dev")], comments=[{"user": OWNER, "body": "#50"}])
        self.assertEqual(out["action"], "reopened")

    def test_bot_comment_link_does_not_count(self):
        _, out = self.gate([merged_pr(50, LEVEL2)], comments=[{"user": BOT, "body": "#50"}])
        self.assertEqual(out["action"], "reopened")

    def test_written_reason_alone_does_not_close(self):
        reason = "## Why not level 1/2\nThe mIRC client on Windows cannot be driven from CI at all, see #12 and Foo.mrc.\n"
        _, out = self.gate([], comments=[{"user": OWNER, "body": reason}])
        self.assertEqual(out["action"], "reopened")

    def test_issue_without_correction_label_is_skipped(self):
        api = FakeApi()
        issue = {"number": 9, "state": "closed", "labels": [], "body": ""}
        self.assertEqual(loop.close_gate(api, CFG, "issues", {"action": "closed", "issue": issue})["action"], "skipped")
        self.assertEqual([c for c in api.calls if c[0] in ("POST", "PATCH")], [])


class RootLines(unittest.TestCase):
    def test_pytest_ids(self):
        self.assertEqual(loop.extract_root("FAILED tests/test_a.py::test_x - assert 1\n"), "tests failed: tests/test_a.py::test_x")

    def test_unittest_ids(self):
        self.assertEqual(loop.extract_root("FAIL: test_x (test_a.T.test_x)\n"), "tests failed: test_x (test_a.T.test_x)")

    def test_error_line_masks_numbers_and_shas(self):
        self.assertEqual(loop.extract_root("##[error]exit 3 at deadbeefcafe\n"), "exit <n> at <sha>")


if __name__ == "__main__":
    unittest.main()
