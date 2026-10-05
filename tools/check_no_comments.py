#!/usr/bin/env python3
import argparse
import bisect
import fnmatch
import os
import re
import subprocess
import sys
import tempfile

EXEMPT_PATHS = (
)

GLOBAL_EXEMPT_PATHS = (
    ".git/*",
    "node_modules/*",
    "*/node_modules/*",
    "dist/*",
    "build/*",
    ".vercel/*",
    ".godot/*",
    "addons/*",
    "*.min.js",
    "*.min.css",
    "*.gen.ts",
    "*.import",
    "*.tscn",
    "*.tres",
    "*.md",
    "LICENSE*",
    "package-lock.json",
    "project.godot",
    "export_presets.cfg",
)

DIRECTIVES = (
    ("shebang", r"#!.*", 1),
    ("python-encoding-cookie", r"#.*coding[:=][ \t]*[-\w.]+.*", 2),
    ("python-type-ignore", r"#[ \t]*type:[ \t]*ignore(\[[\w, -]+\])?[ \t]*", None),
    ("python-noqa", r"#[ \t]*noqa(:[ \t]*[\w, ]+)?[ \t]*", None),
    ("shellcheck", r"#[ \t]*shellcheck[ \t]+(disable|enable|shell|source|source-path)=[\w,./-]+[ \t]*", None),
    ("yaml-language-server", r"#[ \t]*yaml-language-server:[ \t]*\S+[ \t]*", None),
    ("powershell-requires", r"#Requires[ \t]+-\w+.*", None),
    ("eslint-line", r"//[ \t]*eslint-(disable|enable|disable-next-line|disable-line)([ \t]+[\w@/-]+([ \t]*,[ \t]*[\w@/-]+)*)?[ \t]*", None),
    ("eslint-block", r"/\*[ \t]*eslint-(disable|enable)([ \t]+[\w@/-]+([ \t]*,[ \t]*[\w@/-]+)*)?[ \t]*\*/", None),
    ("ts-directive", r"//[ \t]*@ts-(expect-error|nocheck)[ \t]*", None),
    ("ts-triple-slash-reference", r"///[ \t]*<reference[ \t]+[^>]*/>[ \t]*", None),
    ("cs-auto-generated", r"//[ \t]*</?auto-generated[^>]*>[ \t]*", None),
)

C_LIKE = {".c", ".h", ".cc", ".cpp", ".hpp", ".cs", ".java", ".kt", ".swift", ".gdshader", ".shader", ".glsl", ".hlsl", ".json", ".jsonc"}
JS_LIKE = {".js", ".mjs", ".cjs", ".ts", ".mts", ".cts"}
JSX_LIKE = {".jsx", ".tsx"}
GO_LIKE = {".go"}
CSS_LIKE = {".css"}
SCSS_LIKE = {".scss", ".less"}
PY_LIKE = {".py", ".pyw", ".gd", ".toml", ".r", ".rb", ".pl"}
SHELL_LIKE = {".sh", ".bash", ".zsh"}
POWERSHELL = {".ps1", ".psm1", ".psd1"}
YAML_LIKE = {".yml", ".yaml"}
INI_LIKE = {".ini", ".cfg", ".conf", ".properties", ".godot"}
XML_LIKE = {".xml", ".html", ".htm", ".svg", ".xaml", ".csproj", ".props", ".targets", ".config", ".npc", ".resx", ".vue"}
AHK = {".ahk", ".ah2"}
MIRC = {".mrc", ".ini_mirc"}
LUA = {".lua"}
SQL = {".sql"}
BATCH = {".bat", ".cmd"}
NAMED = {"Dockerfile": "shell", "Makefile": "shell", ".prettierrc": "c"}


def language_for(path, text):
    name = os.path.basename(path)
    if name in NAMED:
        return NAMED[name]
    ext = os.path.splitext(name)[1].lower()
    for group, lang in (
        (C_LIKE, "c"), (JS_LIKE, "js"), (JSX_LIKE, "jsx"), (GO_LIKE, "go"), (CSS_LIKE, "css"), (SCSS_LIKE, "c"),
        (PY_LIKE, "py"), (SHELL_LIKE, "shell"), (POWERSHELL, "powershell"), (YAML_LIKE, "yaml"),
        (INI_LIKE, "ini"), (XML_LIKE, "xml"), (AHK, "ahk"), (MIRC, "mirc"), (LUA, "lua"),
        (SQL, "sql"), (BATCH, "batch"),
    ):
        if ext in group:
            return "cs" if ext == ".cs" else lang
    if not ext and text.startswith("#!"):
        first = text.split("\n", 1)[0]
        if "python" in first:
            return "py"
        if re.search(r"\b(ba|z|da|k)?sh\b", first):
            return "shell"
        if "node" in first:
            return "js"
    return None


class Scanner:
    def __init__(self, text):
        self.text = text
        self.n = len(text)
        self.found = []
        self.newlines = [k for k, ch in enumerate(text) if ch == "\n"]

    def line_of(self, i):
        return bisect.bisect_left(self.newlines, i) + 1

    def add(self, start, end):
        first = self.line_of(start)
        last = self.line_of(max(start, end - 1))
        body = self.text[start:end]
        self.found.append((first, last, body.split("\n", 1)[0].rstrip("\r"), start, end))

    def eol(self, i):
        j = self.text.find("\n", i)
        return self.n if j < 0 else j

    def skip_quoted(self, i, quote, escape="\\", multiline=False, doubled=False):
        i += len(quote)
        while i < self.n:
            c = self.text[i]
            if escape and c == escape:
                i += 2
                continue
            if self.text.startswith(quote, i):
                if doubled and self.text.startswith(quote + quote, i):
                    i += 2 * len(quote)
                    continue
                return i + len(quote)
            if c == "\n" and not multiline:
                return i
            i += 1
        return self.n


JSX_TEXT_BEFORE = re.compile(r"(<[A-Za-z][\w.:-]*(\s[^<>]*)?>|</[\w.:-]*>|<>)[^{}<>;]*$")
REGEX_PRECEDERS = set("(,=:[!&|?{};+-*%<>~^")
REGEX_KEYWORDS = {"return", "typeof", "instanceof", "in", "of", "new", "delete", "void", "throw", "case", "do", "else", "yield", "await"}


def scan_c_family(text, lang):
    jsx = lang == "jsx"
    if jsx:
        lang = "js"
    s = Scanner(text)
    i = 0
    prev_sig = ""
    prev_word = ""
    template_stack = []

    def scan_template(i):
        while i < s.n:
            c = text[i]
            if c == "\\":
                i += 2
                continue
            if c == "`":
                return i + 1, False
            if text.startswith("${", i):
                return i + 2, True
            i += 1
        return s.n, False

    while i < s.n:
        c = text[i]
        if jsx and c == "/" and text[i + 1:i + 2] in ("/", "*"):
            line_start = text.rfind("\n", 0, i) + 1
            if JSX_TEXT_BEFORE.search(text[line_start:i]):
                i += 2
                continue
        if lang != "css" and text.startswith("//", i):
            j = s.eol(i)
            s.add(i, j)
            i = j
            continue
        if text.startswith("/*", i):
            j = text.find("*/", i + 2)
            j = s.n if j < 0 else j + 2
            s.add(i, j)
            i = j
            continue
        if lang == "cs":
            m = re.match(r'(\$+)?("{3,})', text[i:i + 12])
            if m and (m.group(1) or text.startswith('"""', i)):
                quotes = m.group(2)
                end = text.find(quotes, i + m.end())
                i = s.n if end < 0 else end + len(quotes)
                prev_sig = '"'
                continue
            m = re.match(r'(\$@|@\$|@)"', text[i:i + 3])
            if m:
                i = s.skip_quoted(i + len(m.group(1)), '"', escape=None, multiline=True, doubled=True)
                prev_sig = '"'
                continue
        if c in "\"'":
            i = s.skip_quoted(i, c)
            prev_sig = c
            continue
        if c == "`" and lang == "go":
            end = text.find("`", i + 1)
            i = s.n if end < 0 else end + 1
            prev_sig = c
            continue
        if c == "`" and lang == "js":
            i, interp = scan_template(i + 1)
            if interp:
                template_stack.append(0)
            prev_sig = "`"
            continue
        if lang == "js" and template_stack:
            if c == "{":
                template_stack[-1] += 1
            elif c == "}":
                if template_stack[-1] == 0:
                    template_stack.pop()
                    i, interp = scan_template(i + 1)
                    if interp:
                        template_stack.append(0)
                    prev_sig = "`"
                    continue
                template_stack[-1] -= 1
        if c == "/" and lang == "js" and (prev_sig == "" or prev_sig in REGEX_PRECEDERS or prev_word in REGEX_KEYWORDS):
            j = i + 1
            in_class = False
            while j < s.n and text[j] != "\n":
                ch = text[j]
                if ch == "\\":
                    j += 2
                    continue
                if ch == "[":
                    in_class = True
                elif ch == "]":
                    in_class = False
                elif ch == "/" and not in_class:
                    break
                j += 1
            if j < s.n and text[j] == "/":
                i = j + 1
                prev_sig = "/r"
                prev_word = ""
                continue
        if c.isalnum() or c in "_$":
            m = re.match(r"[\w$]+", text[i:])
            prev_word = m.group(0)
            prev_sig = "w"
            i += m.end()
            continue
        if not c.isspace():
            prev_sig = c
            prev_word = ""
        i += 1
    return s.found


def scan_python_like(text, lang):
    s = Scanner(text)
    i = 0
    while i < s.n:
        c = text[i]
        if c == "#":
            j = s.eol(i)
            s.add(i, j)
            i = j
            continue
        if c in "\"'":
            if text.startswith(c * 3, i):
                i = s.skip_quoted(i, c * 3, multiline=True)
            else:
                i = s.skip_quoted(i, c)
            continue
        i += 1
    return s.found


def word_start(text, i, extra=";&|()"):
    return i == 0 or text[i - 1] in " \t\r\n" or text[i - 1] in extra


def scan_shell(text, lang):
    s = Scanner(text)
    i = 0
    pending_heredocs = []
    while i < s.n:
        c = text[i]
        if c == "\n" and pending_heredocs:
            i += 1
            for strip_tabs, delim in pending_heredocs:
                while i < s.n:
                    j = s.eol(i)
                    line = text[i:j].rstrip("\r")
                    i = min(j + 1, s.n)
                    if (line.lstrip("\t") if strip_tabs else line) == delim:
                        break
            pending_heredocs = []
            continue
        if c == "#" and word_start(text, i):
            j = s.eol(i)
            s.add(i, j)
            i = j
            continue
        if c == "\\":
            i += 2
            continue
        if c == "'":
            end = text.find("'", i + 1)
            i = s.n if end < 0 else end + 1
            continue
        if c == '"':
            i = s.skip_quoted(i, '"', multiline=True)
            continue
        if text.startswith("<<", i) and not text.startswith("<<<", i):
            m = re.match(r"<<(-?)[ \t]*(['\"]?)([\w.-]+)\2", text[i:])
            if m:
                pending_heredocs.append((m.group(1) == "-", m.group(3)))
                i += m.end()
                continue
        i += 1
    return s.found


def scan_powershell(text, lang):
    s = Scanner(text)
    i = 0
    while i < s.n:
        c = text[i]
        if text.startswith("<#", i):
            j = text.find("#>", i + 2)
            j = s.n if j < 0 else j + 2
            s.add(i, j)
            i = j
            continue
        if c == "#" and word_start(text, i, ";(){}|,="):
            j = s.eol(i)
            s.add(i, j)
            i = j
            continue
        if c == "`":
            i += 2
            continue
        if text.startswith('@"', i) or text.startswith("@'", i):
            q = text[i + 1]
            m = re.compile(r"^[ \t]*" + q + "@", re.M).search(text, i + 2)
            i = s.n if not m else m.end()
            continue
        if c == "'":
            i = s.skip_quoted(i, "'", escape=None, multiline=True, doubled=True)
            continue
        if c == '"':
            i = s.skip_quoted(i, '"', escape="`", multiline=True, doubled=True)
            continue
        i += 1
    return s.found


def scan_yaml(text, lang):
    s = Scanner(text)
    pos = 0
    for raw in text.split("\n"):
        i = 0
        quote = None
        while i < len(raw):
            c = raw[i]
            if quote:
                if c == "\\" and quote == '"':
                    i += 2
                    continue
                if c == quote:
                    quote = None
            elif c in "\"'" and (i == 0 or raw[i - 1] in " \t:[{,-"):
                quote = c
            elif c == "#" and (i == 0 or raw[i - 1] in " \t"):
                s.add(pos + i, pos + len(raw))
                break
            i += 1
        pos += len(raw) + 1
    return s.found


def scan_line_prefix(text, prefixes):
    s = Scanner(text)
    pos = 0
    for raw in text.split("\n"):
        stripped = raw.lstrip()
        if any(re.match(p, stripped, re.I) for p in prefixes):
            s.add(pos + len(raw) - len(stripped), pos + len(raw))
        pos += len(raw) + 1
    return s.found


def scan_ini(text, lang):
    return scan_line_prefix(text, (r"#", r";"))


def scan_batch(text, lang):
    return scan_line_prefix(text, (r"@?rem(\s|$)", r"::"))


def scan_xml(text, lang):
    s = Scanner(text)
    i = 0
    while i < s.n:
        if text.startswith("<![CDATA[", i):
            j = text.find("]]>", i)
            i = s.n if j < 0 else j + 3
            continue
        if text.startswith("<!--", i):
            j = text.find("-->", i + 4)
            j = s.n if j < 0 else j + 3
            s.add(i, j)
            i = j
            continue
        i += 1
    return s.found


def scan_semicolon(text, lang):
    s = Scanner(text)
    i = 0
    line_start = True
    while i < s.n:
        c = text[i]
        if line_start and text.startswith("/*", i):
            m = re.compile(r"^[ \t]*\*/", re.M).search(text, i + 2)
            j = s.n if not m else m.end()
            s.add(i, j)
            i = j
            continue
        if c == ";" and (i == 0 or text[i - 1] in " \t\n"):
            j = s.eol(i)
            s.add(i, j)
            i = j
            continue
        if c == '"' and lang == "ahk":
            i = s.skip_quoted(i, '"', escape="`")
            continue
        if c == "\n":
            line_start = True
        elif not c.isspace():
            line_start = False
        i += 1
    return s.found


def scan_lua(text, lang):
    s = Scanner(text)
    i = 0
    while i < s.n:
        c = text[i]
        m = re.match(r"--\[(=*)\[", text[i:i + 64])
        if m:
            close = "]" + m.group(1) + "]"
            j = text.find(close, i + m.end())
            j = s.n if j < 0 else j + len(close)
            s.add(i, j)
            i = j
            continue
        if text.startswith("--", i):
            j = s.eol(i)
            s.add(i, j)
            i = j
            continue
        m = re.match(r"\[(=*)\[", text[i:i + 64])
        if m:
            close = "]" + m.group(1) + "]"
            j = text.find(close, i + m.end())
            i = s.n if j < 0 else j + len(close)
            continue
        if c in "\"'":
            i = s.skip_quoted(i, c)
            continue
        i += 1
    return s.found


def scan_sql(text, lang):
    s = Scanner(text)
    i = 0
    while i < s.n:
        c = text[i]
        if text.startswith("--", i):
            j = s.eol(i)
            s.add(i, j)
            i = j
            continue
        if text.startswith("/*", i):
            j = text.find("*/", i + 2)
            j = s.n if j < 0 else j + 2
            s.add(i, j)
            i = j
            continue
        if c in "'\"":
            i = s.skip_quoted(i, c, escape=None, multiline=True, doubled=True)
            continue
        i += 1
    return s.found


SCANNERS = {
    "c": scan_c_family, "cs": scan_c_family, "js": scan_c_family, "jsx": scan_c_family, "go": scan_c_family, "css": scan_c_family,
    "py": scan_python_like, "shell": scan_shell, "powershell": scan_powershell, "yaml": scan_yaml,
    "ini": scan_ini, "batch": scan_batch, "xml": scan_xml, "ahk": scan_semicolon, "mirc": scan_semicolon,
    "lua": scan_lua, "sql": scan_sql,
}

COMPILED_DIRECTIVES = tuple((name, re.compile(pattern), limit) for name, pattern, limit in DIRECTIVES)


def directive_name(comment_text, line):
    body = comment_text.strip()
    for name, pattern, limit in COMPILED_DIRECTIVES:
        if limit is not None and line > limit:
            continue
        if pattern.fullmatch(body):
            return name
    return None


def is_exempt(path):
    path = path.replace("\\", "/")
    for pattern in GLOBAL_EXEMPT_PATHS + EXEMPT_PATHS:
        if fnmatch.fnmatchcase(path, pattern):
            return True
    return False


def find_comments(path, text):
    lang = language_for(path, text)
    if lang is None:
        return []
    results = []
    for first, last, body, start, end in SCANNERS[lang](text, lang):
        if directive_name(body, first):
            continue
        results.append((first, last, body, start, end))
    return results


def run_git(args, cwd=None):
    return subprocess.run(["git"] + args, check=True, capture_output=True, cwd=cwd).stdout


def added_lines(base, head, cwd=None):
    out = run_git(["diff", "-U0", "--no-color", "--no-ext-diff", "--diff-filter=AMRC", "-M", base + "..." + head], cwd).decode("utf-8", "replace")
    files = {}
    current = None
    for line in out.split("\n"):
        if line.startswith("+++ "):
            target = line[4:]
            current = None if target == "/dev/null" else target[2:] if target.startswith("b/") else target
            if current is not None:
                files.setdefault(current, set())
            continue
        if current is None:
            continue
        m = re.match(r"@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", line)
        if m:
            start = int(m.group(1))
            count = 1 if m.group(2) is None else int(m.group(2))
            files[current].update(range(start, start + count))
    return files


def read_blob(head, path, cwd=None):
    try:
        data = run_git(["show", head + ":" + path], cwd)
    except subprocess.CalledProcessError:
        return None
    if b"\0" in data:
        return None
    return data.decode("utf-8", "replace")


def check_diff(base, head, cwd=None):
    violations = []
    for path, lines in sorted(added_lines(base, head, cwd).items()):
        if not lines or is_exempt(path):
            continue
        text = read_blob(head, path, cwd)
        if text is None:
            continue
        for first, last, body, _start, _end in find_comments(path, text):
            hit = sorted(n for n in range(first, last + 1) if n in lines)
            if hit:
                violations.append((path, hit[0], body.strip()))
    return violations


def scan_tree(paths):
    total = 0
    for root in paths:
        walk = [root] if os.path.isfile(root) else (os.path.join(d, f) for d, _, fs in os.walk(root) for f in fs)
        for full in sorted(walk):
            rel = os.path.relpath(full).replace(os.sep, "/")
            if is_exempt(rel):
                continue
            try:
                with open(full, "rb") as handle:
                    data = handle.read()
            except OSError:
                continue
            if b"\0" in data:
                continue
            for first, _last, body, _s, _e in find_comments(rel, data.decode("utf-8", "replace")):
                total += 1
                print("%s:%d: %s" % (rel, first, body.strip()[:120]))
    return total


SELF_TEST_CASES = (
    ("a.py", "x = 1  # note\n", [1]),
    ("a.py", "#!/usr/bin/env python3\nx = '#not' + \"#no\"\n", []),
    ("a.py", "# -*- coding: utf-8 -*-\nimport os  # noqa: F401\ny = f(x)  # type: ignore[arg-type]\n", []),
    ("a.py", "s = '''\n# inside string\n'''\n", []),
    ("a.py", "x = 1  # noqa because reasons\n", [1]),
    ("a.gd", "@tool\nextends Node\n@export var color := \"#ff0000\"\n", []),
    ("a.gd", "func _ready():\n\tpass ## doc\n", [2]),
    ("a.cs", "#region Things\n#if DEBUG\nint x = 1;\n#endif\n#endregion\n#nullable enable\n", []),
    ("a.cs", "var u = \"http://x\";\nvar v = @\"C:\\a\\\"\"//no\"\"\";\nint y; // hi\n", [3]),
    ("a.cs", "/// <summary>Doc</summary>\nint z;\n/* block\n   more */\n", [1, 3, 4]),
    ("a.cs", "var r = \"\"\"\n// raw\n\"\"\";\nvar i = $\"{a}//x\";\n", []),
    ("a.ts", "const re = /\\/\\//g;\nconst u = 'http://a';\nconst t = `${a}//b${`//c`}`;\n", []),
    ("a.ts", "const x = 1; // trailing\n/** jsdoc */\n", [1, 2]),
    ("a.ts", "// eslint-disable-next-line react-hooks/exhaustive-deps\n// @ts-expect-error\n/// <reference types=\"vite/client\" />\n", []),
    ("a.ts", "// eslint-disable-next-line no-x -- because\n", [1]),
    ("a.ts", "// @ts-ignore\n// @ts-nocheck why\n", [1, 2]),
    ("a.ts", "const d = a / b; const e = c / d; // x\n", [1]),
    ("a.tsx", "const a = <span> // {tag}</span>;\nconst b = <div>{/* real */}</div>;\nconst c = x > y; // real\n", [2, 3]),
    ("a.css", "a { background: url(http://x/y.png); }\n/* c */\n", [2]),
    ("a.go", "s := `//raw`\n// c\n", [2]),
    ("a.sh", "#!/bin/bash\necho \"#x\" '$#' ${#arr[@]} a#b\ncat <<EOF\n# heredoc\nEOF\nls # c\n", [6]),
    ("a.sh", "# shellcheck disable=SC2086\n", []),
    ("a.ps1", "#Requires -Version 5.1\n$a = \"#x\"\n<# help\n#>\n$b = 1 # c\n$h = @\"\n# not\n\"@\n", [3, 4, 5]),
    ("a.yml", "# yaml-language-server: $schema=x\nname: \"a #b\"\nrun: echo hi # c\nurl: http://a#b\n", [3]),
    ("a.yml", "key: value\n# full line\n", [2]),
    ("a.toml", "a = \"#x\"\nb = 1 # c\n", [2]),
    ("a.ini", "[s]\nk=v;x\n; c\n", [3]),
    ("a.cfg", "# c\nKey=Value\n", [1]),
    ("a.xml", "<a><![CDATA[<!-- no -->]]></a>\n<!-- yes -->\n", [2]),
    ("a.npc", "<x>\n<!-- multi\nline -->\n</x>\n", [2, 3]),
    ("a.ahk", "#SingleInstance Force\n#Requires AutoHotkey v2.0\nx := 1 ; c\nMsgBox \"a;b\"\n/*\nblock\n*/\n", [3, 5, 6, 7]),
    ("a.mrc", "alias x { echo -a hi }\n; c\n", [2]),
    ("a.lua", "local s = \"--no\"\n-- c\n--[[ block\n]]\nlocal l = [[--no]]\n", [2, 3, 4]),
    ("a.sql", "SELECT '--no' FROM t; -- c\n", [1]),
    ("a.bat", "@echo off\nrem c\n:: c\nREM c\necho rem\n", [2, 3, 4]),
    ("a.md", "# Heading\n", []),
)


DIFF_TEST_BASE = {
    "keep.py": "x = 1  # old comment stays untouched\n",
    "edit.ts": "export const a = 1;\n",
}
DIFF_TEST_HEAD = {
    "keep.py": "x = 1  # old comment stays untouched\ny = 2\n",
    "edit.ts": "export const a = 1;\n// added comment\nexport const b = '//not';\n",
    "new.sh": "#!/bin/sh\n# shellcheck disable=SC2086\necho hi # added\n",
    "node_modules/x/index.js": "// vendored\n",
}
DIFF_TEST_EXPECTED = [("edit.ts", 2), ("new.sh", 3)]


def diff_self_test():
    with tempfile.TemporaryDirectory() as repo:
        ident = ["-c", "user.name=self-test", "-c", "user.email=self-test@localhost", "-c", "commit.gpgsign=false"]
        run_git(["init", "-q", "-b", "base"], repo)
        for files, label in ((DIFF_TEST_BASE, "base"), (DIFF_TEST_HEAD, "head")):
            if label == "head":
                run_git(["checkout", "-q", "-b", "head"], repo)
            for name, body in files.items():
                full = os.path.join(repo, name)
                os.makedirs(os.path.dirname(full), exist_ok=True)
                with open(full, "w", encoding="utf-8", newline="\n") as handle:
                    handle.write(body)
            run_git(["add", "-A"], repo)
            run_git(ident + ["commit", "-q", "-m", label], repo)
        got = [(path, line) for path, line, _body in check_diff("base", "head", repo)]
    if got != DIFF_TEST_EXPECTED:
        print("FAIL diff mode: expected %s got %s" % (DIFF_TEST_EXPECTED, got))
        return 1
    return 0


def self_test():
    failures = diff_self_test()
    for path, text, expected in SELF_TEST_CASES:
        got = sorted({n for first, last, _b, _s, _e in find_comments(path, text) for n in range(first, last + 1)})
        if got != expected:
            failures += 1
            print("FAIL %s %r: expected %s got %s" % (path, text, expected, got))
    exempt_cases = (("node_modules/x/a.js", True), (".vercel/output/a.mjs", True), ("src/a.ts", False), ("README.md", True))
    for path, expected in exempt_cases:
        if is_exempt(path) != expected:
            failures += 1
            print("FAIL exempt %s: expected %s" % (path, expected))
    for path in EXEMPT_PATHS:
        if not is_exempt(path.rstrip("*").rstrip("/") + ("/x" if path.endswith("/*") else "")):
            failures += 1
            print("FAIL exempt list entry %s does not match itself" % path)
    print("self-test: %d cases, %d failures" % (1 + len(SELF_TEST_CASES) + len(exempt_cases) + len(EXEMPT_PATHS), failures))
    return failures == 0


def main():
    parser = argparse.ArgumentParser(description="Fail when a change adds code comments.")
    parser.add_argument("--base", default="origin/main")
    parser.add_argument("--head", default="HEAD")
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--scan", nargs="*", metavar="PATH")
    args = parser.parse_args()
    if args.self_test:
        return 0 if self_test() else 1
    if args.scan is not None:
        total = scan_tree(args.scan or ["."])
        print("%d comment(s) found" % total)
        return 0
    try:
        run_git(["rev-parse", "--verify", "--quiet", args.base + "^{commit}"])
    except subprocess.CalledProcessError:
        print("Base %s not found. Fetch full history (fetch-depth: 0) first." % args.base)
        return 2
    violations = check_diff(args.base, args.head)
    for path, line, body in violations:
        print("::error file=%s,line=%d::Added comment is not allowed: %s" % (path, line, body[:160]))
    if violations:
        print("%d added comment(s). Remove them; see CONTRIBUTING.md for the allowed machine directives." % len(violations))
        return 1
    print("No added comments between %s and %s." % (args.base, args.head))
    return 0


if __name__ == "__main__":
    sys.exit(main())
