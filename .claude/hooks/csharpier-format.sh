#!/usr/bin/env bash
# PostToolUse hook: format an edited .cs/.csproj file with CSharpier.
# Reads the tool-call JSON on stdin and pulls out tool_input.file_path (no jq needed).
# JSON-escaped Windows backslashes are turned into single forward slashes.
f=$(sed -n 's/.*"file_path"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n1 | tr -s '\\' '/')
case "$f" in
  *.cs|*.csproj) ;;
  *) exit 0 ;;
esac
cd "${CLAUDE_PROJECT_DIR:-$(dirname "$0")/../..}" || exit 0
dotnet csharpier format "$f"
