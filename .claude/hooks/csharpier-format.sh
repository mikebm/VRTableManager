#!/usr/bin/env bash
# PostToolUse hook: tidy an edited .cs/.csproj file.
#   .cs     -> add required braces (IDE0011 via dotnet format), then CSharpier
#   .csproj -> CSharpier
# Reads the tool-call JSON on stdin and pulls out tool_input.file_path (no jq needed).
# JSON-escaped Windows backslashes are turned into single forward slashes.
f=$(sed -n 's/.*"file_path"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n1 | tr -s '\\' '/')
case "$f" in
  *.cs|*.csproj) ;;
  *) exit 0 ;;
esac
cd "${CLAUDE_PROJECT_DIR:-$(dirname "$0")/../..}" || exit 0

# dotnet format's --include silently ignores absolute C:/ paths, so make it repo-relative.
root=$(cygpath -m "$PWD" 2>/dev/null || pwd)
f=${f#"$root"/}

if [[ "$f" == *.cs ]]; then
  # Nearest project file above the edited file.
  d=$(dirname "$f")
  while [[ -n "$d" && "$d" != "." && "$d" != "/" ]]; do
    proj=$(ls "$d"/*.csproj 2>/dev/null | head -n1)
    [[ -n "$proj" ]] && break
    parent=$(dirname "$d"); [[ "$parent" == "$d" ]] && break; d=$parent
  done
  if [[ -n "$proj" ]]; then
    dotnet format style "$proj" --include "$f" --diagnostics IDE0011 --severity info
  fi
fi

dotnet csharpier format "$f"
