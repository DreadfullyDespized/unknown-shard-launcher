import json
import os
import urllib.error
import urllib.request


class ApiError(RuntimeError):
    def __init__(self, status, message):
        super().__init__(f"HTTP {status}: {message}")
        self.status = status


class Api:
    def __init__(self, repo, token=None, base=None):
        self.repo = repo
        self.token = token or os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN") or ""
        self.base = (base or os.environ.get("GITHUB_API_URL") or "https://api.github.com").rstrip("/")

    def request(self, method, path, body=None, raw=False):
        url = path if path.startswith("http") else self.base + path
        data = json.dumps(body).encode("utf-8") if body is not None else None
        req = urllib.request.Request(url, data=data, method=method)
        req.add_unredirected_header("Authorization", "Bearer " + self.token)
        req.add_header("Accept", "application/vnd.github+json")
        req.add_header("X-GitHub-Api-Version", "2022-11-28")
        req.add_header("User-Agent", "correction-loop")
        if data is not None:
            req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=60) as resp:
                payload = resp.read()
        except urllib.error.HTTPError as err:
            detail = err.read().decode("utf-8", "replace")[:500]
            raise ApiError(err.code, detail) from None
        if raw:
            return payload
        return json.loads(payload) if payload else None

    def get(self, path):
        return self.request("GET", path)

    def post(self, path, body):
        return self.request("POST", path, body)

    def patch(self, path, body):
        return self.request("PATCH", path, body)

    def raw(self, path):
        return self.request("GET", path, raw=True)

    def paged(self, path, key=None, limit=2000):
        out = []
        sep = "&" if "?" in path else "?"
        page = 1
        while len(out) < limit:
            data = self.get(f"{path}{sep}per_page=100&page={page}")
            items = data.get(key, []) if key else data
            out.extend(items)
            if len(items) < 100:
                break
            page += 1
        return out
