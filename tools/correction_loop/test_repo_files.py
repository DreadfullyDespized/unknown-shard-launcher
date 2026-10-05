import json
import os
import sys
import unittest

import yaml

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
WF = os.path.join(ROOT, ".github", "workflows")
FORMS = os.path.join(ROOT, ".github", "ISSUE_TEMPLATE")
OURS = ["grader.yml", "grader-relay.yml", "correction-intake.yml", "correction-close-gate.yml", "correction-loop-tests.yml"]
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

    def test_grader_job_is_named_grader_and_reruns_on_events(self):
        doc = load(os.path.join(WF, "grader.yml"))
        self.assertEqual([j["name"] for j in doc["jobs"].values()], ["grader"])
        on = triggers(doc)
        self.assertTrue({"synchronize", "labeled", "unlabeled", "opened", "reopened"} <= set(on["pull_request"]["types"]))
        self.assertIn("submitted", on["pull_request_review"]["types"])
        self.assertIn("pr", on["workflow_dispatch"]["inputs"])
        self.assertNotIn("continue-on-error", json.dumps(doc))

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
