#!/usr/bin/env bash
# Capture the running ClassicUO window (that window only, never the whole screen).
#
#   window_shot.sh [outfile]
#
# Prints the path it wrote. The window id changes every client run, so it is looked up each call.
set -euo pipefail

out="${1:-/tmp/cuoshot.png}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# pyobjc/Quartz is not installed and System Events is refused ("not allowed assistive access"),
# so CGWindowList is read through swift, which needs no permission at all.
id="$(swift "$here/winlist.swift" | awk -F'\t' 'tolower($2) ~ /cuo/ {print $1; exit}')"

if [ -z "$id" ]; then
    echo "No ClassicUO window found - is the client running with a window (not --headless)?" >&2
    exit 1
fi

screencapture -x -o -l "$id" "$out"
echo "$out"
