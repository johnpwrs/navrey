#!/usr/bin/env bash
# PreToolUse / Bash: refuse foreground commands that loop or wait.
#
# Enforces the standing rule from CLAUDE.md - "never block the main thread" - which was previously
# only a written instruction. An inline `sleep 45 && grep ...` or a foreground watcher makes the
# session unresponsive for its whole duration: nothing can be checked, cancelled, or reacted to,
# and a hang leaves nothing to inspect. Measured cost in this repo: a 70-second inline watcher
# during a live fight, while the character failed to approach its target for the entire 70s,
# invisible until someone pointed it out.
#
# Backgrounded commands are always allowed - the point is to redirect, not to forbid.
set -uo pipefail

input=$(cat)

bg=$(printf '%s' "$input" | jq -r '.tool_input.run_in_background // false')
cmd=$(printf '%s' "$input" | jq -r '.tool_input.command // ""')

# A reflex launch, as opposed to merely mentioning one (pkill, grep, --help, a unit test).
REFLEX='python3?[^;&|]*(combat_movement_[a-z]+|autoheal|safe_goto|listen)\.py'
NOT_A_LAUNCH='py_compile|unittest|--help|\bgrep\b|\bsed\b|\bcat\b|\bcp\b|\bwc\b|md5|\bpgrep\b|\bpkill\b|\bps\b'

launches_reflex() {
  printf '%s' "$cmd" | grep -qE "$REFLEX" && ! printf '%s' "$cmd" | grep -qE "$NOT_A_LAUNCH"
}

# `run_in_background: true` is NOT enough on its own. The harness tracks the *Bash invocation*, not
# what it spawns, so `nohup <reflex> &` inside a backgrounded call returns immediately: the tracked
# task completes in seconds while the reflex runs on as a PPID-1 orphan - absent from the
# background-task list, output unstreamed, unstoppable from there. Worse, a reflex hands its
# decision back by *exiting*, and only a tracked exit re-invokes the model; detached, the HANDOFF
# line just lands in a file nobody reads. Measured: three handoffs in a row missed for minutes
# each. This check therefore runs BEFORE the run_in_background early-exit below, which is the hole
# every one of those launches went through.
if launches_reflex; then
  # Ignore redirections (2>&1, >&2) and `&&`; what matters is a bare `&` that detaches.
  norm=$(printf '%s' "$cmd" | sed -e 's/[0-9]*>&[0-9-]*//g' -e 's/&&/ /g')
  if printf '%s' "$norm" | grep -qE '(^|[[:space:]])nohup([[:space:]]|$)|&'; then
    jq -n '{
      hookSpecificOutput: {
        hookEventName: "PreToolUse",
        permissionDecision: "deny",
        permissionDecisionReason: "Blocked: this launches a uo-* reflex detached (nohup and/or a trailing &). run_in_background does not help here - the harness tracks the Bash invocation, not what it spawns, so the reflex becomes a PPID-1 orphan: invisible in the background-task list, output unstreamed, unstoppable from there. It also swallows the handoff - a reflex hands its decision back by exiting, and only a tracked exit re-invokes you. Re-run the reflex in the FOREGROUND of a run_in_background call: drop the nohup, the trailing &, and any `> /tmp/... 2>&1`, and pass run_in_background: true. Use --replace to take over from a copy already running. Do not wrap a reflex in a bash relauncher: that consumes the exit and hides the reason."
      }
    }'
    exit 0
  fi
fi

# run_in_background is a Bash tool parameter, not part of the command string, so the hook has to
# read it from the tool input rather than look for a trailing '&'.
if [ "$bg" = "true" ]; then
  exit 0
fi

# A keyword only counts at a *command position*: start of line, or after a separator (; & | ( `)
# or a do/then. Matching it after any whitespace made the hook fire on ordinary English - the
# word "until" inside a sentence being written into a doc was enough to block the write.
AT_CMD='(^|[;&|(`]|[[:space:]]do|[[:space:]]then)[[:space:]]*'

reason=""
if printf '%s' "$cmd" | grep -qE "${AT_CMD}sleep[[:space:]]+([3-9]|[0-9]{2,})"; then
  reason="a sleep of 3s or more"
elif printf '%s' "$cmd" | grep -qE "${AT_CMD}(while[[:space:]]+(true|:)|until[[:space:]])"; then
  reason="a while/until loop"
elif printf '%s' "$cmd" | grep -qE "${AT_CMD}tail[[:space:]]+-[a-zA-Z]*[fF]"; then
  reason="a 'tail -f', which never exits"
elif printf '%s' "$cmd" | grep -qE "${AT_CMD}watch[[:space:]]"; then
  reason="a 'watch' loop"
elif launches_reflex; then
  reason="a uo-* reflex script, which loops until killed"
fi

[ -z "$reason" ] && exit 0

jq -n --arg r "$reason" '{
  hookSpecificOutput: {
    hookEventName: "PreToolUse",
    permissionDecision: "deny",
    permissionDecisionReason: ("Blocked: this command contains " + $r + ", so it would block the session for its whole duration - nothing could be checked or reacted to while it ran. Re-run the SAME command with run_in_background: true, then use the Monitor tool (or a one-shot read of its output file) to watch it. To wait for a condition, background an `until <check>; do sleep 2; done` loop rather than sleeping inline.")
  }
}'
exit 0
