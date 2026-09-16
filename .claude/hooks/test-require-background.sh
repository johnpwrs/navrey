#!/usr/bin/env bash
# Test battery for require-background.sh.
#
# This lives in a file rather than an inline command on purpose: the fixtures below necessarily
# contain the very patterns the hook blocks ("sleep 45", "&& sleep 30"), so pasting them into a
# Bash call gets that call blocked by the hook under test. Running a file whose *name* is
# innocuous sidesteps the recursion.
#
#   bash .claude/hooks/test-require-background.sh
set -uo pipefail
cd "$(dirname "$0")/../.."
HOOK=.claude/hooks/require-background.sh
fails=0

check() { # name  json  expected
  local out r
  out=$(printf '%s' "$2" | "$HOOK")
  if [ -z "$out" ]; then r=allow; else r=$(printf '%s' "$out" | jq -r '.hookSpecificOutput.permissionDecision'); fi
  if [ "$r" = "$3" ]; then
    printf '  %-5s %-8s %s\n' "$r" "ok" "$1"
  else
    printf '  %-5s %-8s %s  (expected %s)\n' "$r" "FAIL" "$1" "$3"; fails=$((fails + 1))
  fi
}

# Fixtures are assembled from pieces so this file itself stays free of trip patterns at a
# command position - otherwise editing it would be blocked by the hook it tests.
S="sleep"; T="tail"; W="watch"; U="until"; WH="while"

echo "must BLOCK (would stall the session):"
check "long sleep"          "{\"tool_input\":{\"command\":\"$S 45; grep x f\"}}"                       deny
check "sleep after &&"      "{\"tool_input\":{\"command\":\"echo hi && $S 30\"}}"                      deny
check "sleep 3 (over 2s)"   "{\"tool_input\":{\"command\":\"$S 3\"}}"                                  deny
check "while true"          "{\"tool_input\":{\"command\":\"$WH true; do jq . f; done\"}}"             deny
check "until at start"      "{\"tool_input\":{\"command\":\"$U [ -f x ]; do :; done\"}}"               deny
check "sleep inside for"    "{\"tool_input\":{\"command\":\"for i in 1 2; do $S 5; done\"}}"           deny
check "tail -f"             "{\"tool_input\":{\"command\":\"$T -f /tmp/cuolog | grep foo\"}}"          deny
check "watch loop"          "{\"tool_input\":{\"command\":\"$W -n1 jq . /tmp/cuostate.json\"}}"        deny
check "foreground reflex"   '{"tool_input":{"command":"python3 .claude/skills/uo-combat-loop/combat_movement_melee.py --include zombie"}}' deny

echo "must ALLOW (backgrounded - the whole point is to redirect, not forbid):"
check "bg long sleep"       "{\"tool_input\":{\"command\":\"$S 45\",\"run_in_background\":true}}"      allow
check "bg tail -f monitor"  "{\"tool_input\":{\"command\":\"$T -f x | grep y\",\"run_in_background\":true}}" allow
check "bg reflex script"    '{"tool_input":{"command":"python3 .claude/skills/uo-combat-loop/autoheal.py","run_in_background":true}}' allow

echo "must ALLOW (prose and short waits - the false positives that bit us):"
check "prose 'until'"       "{\"tool_input\":{\"command\":\"echo 'fought $U HP reaches 85' >> doc.md\"}}"  allow
check "prose 'watch'"       "{\"tool_input\":{\"command\":\"echo 'I will $W the log' >> doc.md\"}}"        allow
check "prose 'sleep 30'"    "{\"tool_input\":{\"command\":\"echo 'it will $S 30 seconds' >> doc.md\"}}"    allow
check "short sleep"         "{\"tool_input\":{\"command\":\"$S 2\"}}"                                  allow
check "sub-second sleep"    "{\"tool_input\":{\"command\":\"$S 0.5\"}}"                                allow
check "py_compile"          '{"tool_input":{"command":"python3 -m py_compile .claude/skills/uo-combat-loop/combat_movement_melee.py"}}' allow
check "unittest"            '{"tool_input":{"command":"python3 -m unittest discover -s .claude/skills/uo-combat-loop"}}' allow
check "grep a reflex file"  '{"tool_input":{"command":"grep -n holding .claude/skills/uo-combat-loop/combat_movement_melee.py"}}' allow
check "jq state"            '{"tool_input":{"command":"jq -c .nav /tmp/cuostate.json"}}'               allow
check "locks.py"            '{"tool_input":{"command":"python3 cli/uo/locks.py"}}'                allow
check "git status"          '{"tool_input":{"command":"git status --porcelain"}}'                      allow

echo
if [ "$fails" -eq 0 ]; then echo "all checks passed"; else echo "$fails check(s) FAILED"; fi
exit "$fails"
