#!/usr/bin/env bash
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
self="$(git ls-files --full-name -- "$0" 2>/dev/null || true)"
patterns=(
  'unzip[^|;&]*-P[[:space:]]*[^$[:space:]"'"'"'-]'
  'unzip[^|;&]*-P[[:space:]]*["'"'"'][^$"'"'"']'
  '7z[^|;&]*[[:space:]]-p[^$[:space:]"'"'"'%]'
  '[Pp][Aa][Ss][Ss][Ww][Oo][Rr][Dd][[:space:]]*:[[:space:]]*["'"'"'`]?[A-Za-z0-9!@#%^&*_+=~-]{4,}["'"'"'`]?[[:space:]]*$'
  '[A-Z_]*PASSWORD[[:space:]]*=[[:space:]]*[^$[:space:]"'"'"'{%(<]'
  '[A-Z_]*PASSWORD[[:space:]]*=[[:space:]]*["'"'"'][^$"'"'"'{%<]'
  '[[:space:]]-p'"'"'[^$'"'"']+'"'"''
  '[[:space:]]-p"[^$"]+"'
)
files=()
while IFS= read -r -d '' f; do
  [ "$f" = "$self" ] && continue
  case "$f" in
    *.md|*.txt|*.sh|*.bash|*.ps1|*.psm1|*.bat|*.cmd|*.py|*.yml|*.yaml|*.toml|*.ini|*.cfg|*.conf|*.env|*.env.*|Dockerfile|Makefile) files+=("$f") ;;
  esac
done < <(git ls-files -z)
hits=0
if [ "${#files[@]}" -gt 0 ]; then
  for p in "${patterns[@]}"; do
    if out="$(grep -HnIE -- "$p" "${files[@]}" 2>/dev/null | cut -d: -f1,2)"; then
      [ -n "$out" ] && { printf '%s\n' "$out" | sed 's/$/: password-like literal/'; hits=1; }
    fi
  done
fi
if [ "$hits" -ne 0 ]; then
  echo "check_no_secrets: FAIL" >&2
  exit 1
fi
echo "check_no_secrets: OK (${#files[@]} files scanned)"
