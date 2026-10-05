import json
import os
import sys
import unittest

import yaml

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
WF = os.path.join(ROOT, ".github", "workflows")
FORMS = os.path.join(ROOT, ".github", "ISSUE_TEMPLATE")
OURS = ["gate.yml", "grader-relay.yml", "correction-intake.yml", "correction-close-gate.yml"]
RETIRED = ["grader.yml", "correction-loop-tests.yml", "no-comments.yml", "pr-body.yml"]
GATE_STEPS = ["Workflow lint: self-test", "No comments: self-test", "No comments: fail on added comment lines", "PR body: self-test",
              "correction-loop tests", "grader: fail unless"]
FORM_TYPES = {"markdown", "textarea", "input", "dropdown", "checkboxes"}


def load(path):
    with open(path, encoding="utf-8") as fh:
        return yaml.safe_load(fh)


def triggers(doc):
    return doc.get("on", doc.get(True))


def labels(form):
    return [i["attributes"]["label"] for i in form["body"] if i["type"] != "markdown"]


class Workflows(unittest.TestCase):
    def test_ubuntu_latest_only(self):
        for name in OURS:
            for job in load(os.path.join(WF, name))["jobs"].values():
                self.assertEqual(job["runs-on"], "ubuntu-latest", name)

    def test_every_job_has_a_timeout(self):
        for name in os.listdir(WF):
            if name.endswith((".yml", ".yaml")):
                for job_id, job in load(os.path.join(WF, name))["jobs"].items():
                    self.assertIn("timeout-minutes", job, f"{name}:{job_id}")

    def test_gate_is_one_job_named_grader_with_one_step_per_check(self):
        doc = load(os.path.join(WF, "gate.yml"))
        self.assertEqual([j["name"] for j in doc["jobs"].values()], ["grader"])
        on = triggers(doc)
        types = set(on["pull_request"]["types"])
        self.assertTrue({"opened", "edited", "synchronize", "reopened"} <= types)
        self.assertFalse({"labeled", "unlabeled"} & types)
        self.assertNotIn("pull_request_review", on)
        self.assertEqual(on["push"]["branches"], ["main"])
        self.assertIn("pr", on["workflow_dispatch"]["inputs"])
        self.assertNotIn("continue-on-error", json.dumps(doc))
        steps = list(doc["jobs"].values())[0]["steps"]
        names = [s.get("name") or "" for s in steps]
        for want in GATE_STEPS:
            self.assertTrue(any(n.startswith(want) for n in names), want)
        self.assertTrue(names[-1].startswith("grader:"))
        checks = [s for s in steps if (s.get("name") or "").split(":")[0] in ("Workflow lint", "No comments", "PR body", "correction-loop tests", "grader")]
        for step in checks:
            self.assertIn("!cancelled()", step.get("if", ""), step["name"])

    def test_old_per_check_workflows_are_gone(self):
        for name in RETIRED:
            self.assertFalse(os.path.exists(os.path.join(WF, name)), name)

    def test_relay_reruns_on_events_and_never_waits(self):
        doc = load(os.path.join(WF, "grader-relay.yml"))
        on = triggers(doc)
        self.assertIn("created", on["issue_comment"]["types"])
        self.assertTrue({"submitted", "dismissed"} <= set(on["pull_request_review"]["types"]))
        job = doc["jobs"]["relay"]
        self.assertLessEqual(int(job["timeout-minutes"]), 5)
        self.assertIn("/grader", job["if"])

    def test_intake_never_starts_a_runner_for_events_it_ignores(self):
        with open(os.path.join(HERE, "config.json"), encoding="utf-8") as fh:
            cfg = json.load(fh)
        cond = load(os.path.join(WF, "correction-intake.yml"))["jobs"]["signature"]["if"]
        self.assertEqual(cfg["required_checks"], [])
        self.assertIn("github.event.workflow_run.event != 'pull_request'", cond)
        for marker in ("Verdict: FAIL", "/correction", "dread-correction"):
            self.assertIn(marker, cond)

    def test_intake_watches_exactly_the_configured_workflows(self):
        with open(os.path.join(HERE, "config.json"), encoding="utf-8") as fh:
            cfg = json.load(fh)
        on = triggers(load(os.path.join(WF, "correction-intake.yml")))
        self.assertEqual(on["workflow_run"]["workflows"], cfg["watched_workflows"])
        self.assertNotIn("grader", cfg["watched_workflows"])
        names = set()
        for f in os.listdir(WF):
            if f.endswith((".yml", ".yaml")):
                names.add(load(os.path.join(WF, f)).get("name"))
        for w in cfg["watched_workflows"]:
            self.assertIn(w, names | set(cfg.get("pending_workflows", [])), w)


class IssueForms(unittest.TestCase):
    def form(self, name):
        doc = load(os.path.join(FORMS, name))
        self.assertTrue(doc["name"] and doc["description"])
        ids = [i["id"] for i in doc["body"] if "id" in i]
        self.assertEqual(len(ids), len(set(ids)))
        for item in doc["body"]:
            self.assertIn(item["type"], FORM_TYPES)
            if item["type"] == "markdown":
                self.assertTrue(item["attributes"]["value"].strip())
        self.assertEqual(len(labels(doc)), len(set(labels(doc))))
        return doc

    def required(self, doc):
        return [i["attributes"]["label"] for i in doc["body"]
                if i["type"] != "markdown" and (i.get("validations") or {}).get("required")]

    def test_bug(self):
        req = self.required(self.form("bug.yml"))
        for label in ["Problem and evidence", "Option A", "Option B", "Recommendation", "Blast radius", "Root cause",
                      "Regression origin"]:
            self.assertIn(label, req)

    def test_improvement(self):
        req = self.required(self.form("improvement.yml"))
        for label in ["Problem and evidence", "Option A", "Option B", "Recommendation", "Blast radius"]:
            self.assertIn(label, req)

    def test_feature(self):
        req = self.required(self.form("feature.yml"))
        for label in ["What it does for Dread", "Acceptance criteria", "Rollout", "Blast radius"]:
            self.assertIn(label, req)


if __name__ == "__main__":
    unittest.main()
