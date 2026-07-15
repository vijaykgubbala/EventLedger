#!/usr/bin/env bash
# PostToolUse hook for Write|Edit|MultiEdit: runs `dotnet format` on the
# touched file if it's a C# file inside a project. Never blocks the agent —
# every exit path is 0, even on failure, since formatting is a courtesy,
# not a gate.

input="$(cat)"

# Extract "file_path" from the hook's JSON payload without requiring jq.
file_path=$(printf '%s' "$input" | grep -o '"file_path"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | sed -E 's/.*:[[:space:]]*"(.*)"$/\1/')

[[ -z "${file_path:-}" ]] && exit 0
[[ "$file_path" != *.cs ]] && exit 0
[[ ! -f "$file_path" ]] && exit 0

command -v dotnet >/dev/null 2>&1 || exit 0

# Walk up from the file's directory to find the nearest .csproj or .sln.
search_dir="$(dirname "$file_path")"
project_file=""
while :; do
  found="$(find "$search_dir" -maxdepth 1 \( -name '*.csproj' -o -name '*.sln' \) 2>/dev/null | head -1)"
  if [[ -n "$found" ]]; then
    project_file="$found"
    break
  fi
  parent="$(dirname "$search_dir")"
  [[ "$parent" == "$search_dir" ]] && break
  search_dir="$parent"
done

[[ -z "$project_file" ]] && exit 0

dotnet format "$project_file" --include "$file_path" >/dev/null 2>&1

exit 0
