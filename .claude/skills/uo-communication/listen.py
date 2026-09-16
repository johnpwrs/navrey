#!/usr/bin/env python3
"""Watch for people talking to us, and surface only the messages worth answering.

    python3 .claude/skills/uo-communication/listen.py            # backgrounded, like any reflex
    python3 .claude/skills/uo-communication/listen.py --range 5 --ambient

This is a *listener*, not a talker. It never sends speech. It decides which of the chatter in
`/tmp/cuolog` is plausibly addressed to us and prints one clean line per qualifying message; the
replying is a judgment call and stays with whoever is driving. Pair it with the Monitor tool and
answer with `say`:

    echo "say Well met, Randall." >> /tmp/cuocmd

WHAT QUALIFIES

Two independent triggers:

  named       the message mentions our character's name, at any distance
  proximity   the speaker is a mobile within --range (default 5) tiles

Everything else is ambient - worth noticing, not worth answering - and is dropped unless
`--ambient` is passed.

WHY THE PARSING IS FUSSY

The log is a single stream of narration, and "speech" has no dedicated marker in it. Ordinary
speech is emitted as a bare `Speaker: text` (Narrator.cs, the `default` case), which is exactly
the shape of several things that are *not* speech:

  Swing: Homie -> a creature          combat narration
  [LABEL] a creature: a creature      an object label, not a sentence anyone said
  21:44:02 [combat_movement_melee] …  another script's own log line, which also contains ": "

So this matches by exclusion rather than by pattern, and each exclusion below is one of those.
`uo.events.classify()` is no help here - it maps `[LABEL]`/`[YELL]`/`[whisper]` to SPEECH but
leaves bare `Speaker: text` as OTHER, which is the common case.

WHO COUNTS AS A PERSON

Not a body-graphic test: non-human player races exist, so `isHuman` would silently ignore them.
What is actually checked is "not a known monster, and named rather than described" - "a creature" is a
description, "randall flagg" is a name. That still cannot separate a player from a vendor, because
the protocol carries no player flag and neither does the client. Speakers are reported with
`person=yes/no` so the call can be made on the line rather than guessed at in here.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
import time
from typing import Dict, Optional, Tuple

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "..", "cli"))

from uo import Client, script                                          # noqa: E402
from uo.entities import Mobile                                         # noqa: E402

# Lines that are narration, machinery or another script's logging - never something a person said.
# Several of these contain ": " and would otherwise parse as a speaker.
NOISE_PREFIXES = (
    "[SYSTEM]", "[LABEL]", "[CMD]", "[CMD-END]", "[CONTAINER]", "[SHOP]", "[NAV]", "[POI]",
    "[SKILL]", "[DEATH]", "[ITEM]", "[TARGET]", "[PERF]", "Swing:", "Damage:", "Walking to",
    # Our own speech, echoed back by the CLI as `Said (say): ...` a beat before the narrator
    # emits the normal `<us>: ...` line. The name-based self-filter below does not catch it - the
    # speaker reads as "Said (say)", not as us - and because a reply often contains our own name,
    # it came back through the "named" trigger as though a stranger had addressed us. Left in, an
    # auto-replying build of this would hold a conversation with itself.
    "Said (",
    # Spell words and any message kind the narrator does not name are not conversation. `[MSG:n]`
    # keeps the raw MessageType visible in the log so a new kind can be identified rather than
    # quietly read as speech.
    "[SPELL]", "[MSG:",
    # Status echoes from Output that happen to be `X: Y` shaped. They never qualify (no speaker
    # mobile, no name match) but they should not reach the qualification step at all.
    "War mode:", "Arrived at", "Attacking ",
)

# `HH:MM:SS [script-name] message` - the shape uo.script's own logger writes into the same file.
SCRIPT_LINE = re.compile(r"^\d{2}:\d{2}:\d{2} \[")

# "a creature", "an animal", "the guard" - a description rather than somebody's name.
ARTICLE = re.compile(r"^(a|an|the)\s", re.IGNORECASE)

# Speakers that are the game talking, not a person. The narrator emits some server messages with
# a speaker name rather than the [SYSTEM] prefix, so the prefix filter alone misses them - seen
# within seconds of first running this: "System: You finish applying the bandages."
NOISE_SPEAKERS = {"system", "?", ""}


def parse_speech(line: str) -> Optional[Tuple[str, str, str]]:
    """`(kind, speaker, text)` for a line somebody actually said, else None."""
    if not line or SCRIPT_LINE.match(line):
        return None
    if line.startswith(NOISE_PREFIXES):
        return None

    kind = "say"

    # Labelled forms first. `[SAY]` and the channel labels come from the packet's MessageType and
    # are unambiguous - no exclusion guessing needed. They only appear on a client built after
    # Narrator.cs started emitting them; an older client still sends ordinary speech bare, which
    # is why the exclusion path below is kept rather than replaced.
    for tag, name in (("[SAY] ", "say"), ("[GUILD] ", "guild"), ("[ALLIANCE] ", "alliance"),
                      ("[PARTY] ", "party"), ("[YELL] ", "yell"), ("[whisper] ", "whisper")):
        if line.startswith(tag):
            return _split(name, line[len(tag):])

    if line.startswith("* "):
        # Emotes are "* Speaker text" with no colon, so they need their own split.
        speaker, _, text = line[2:].partition(" ")
        return ("emote", speaker, text) if speaker and text else None

    return _split(kind, line)


def _split(kind: str, rest: str) -> Optional[Tuple[str, str, str]]:
    speaker, sep, text = rest.partition(": ")

    if not sep or not speaker or not text:
        return None
    # A real name, not a sentence that happened to contain a colon.
    if len(speaker) > 40 or "  " in speaker:
        return None

    return kind, speaker.strip(), text.strip()


def looks_like_person(mob: Optional[Mobile], speaker: str) -> Optional[bool]:
    """True/False for a speaker we can see, None for one we cannot.

    Deliberately does NOT require `is_human`. Non-human player races exist, and other bodies
    besides - a player is not a body graphic. What actually separates a person from scenery here
    is not being a known monster and having a name rather than a description ("a creature" is a
    description; "randall flagg" is a name).
    """
    if mob is None:
        return None
    return bool(not mob.known_monster and not ARTICLE.match(speaker))


ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
ap.add_argument("--range", type=int, default=5,
                help="tiles - a speaker this close is treated as talking to us even without "
                     "naming us (default 5)")
ap.add_argument("--poll", type=float, default=0.3, help="seconds between log reads")
ap.add_argument("--ambient", action="store_true",
                help="also print speech that did NOT qualify, for tuning what gets missed")
ap.add_argument("--transcript", default="/tmp/cuotalk.log",
                help="append every qualifying line here, plus our own replies, so a conversation "
                     "can be read back in order (default /tmp/cuotalk.log)")
ap.add_argument("--also", action="append", default=[], metavar="WORD",
                help="extra word that counts as addressing us (a nickname, a guild tag) - "
                     "repeatable")


@script(ap)
def main(uo, args):
    me = (uo.name or "").strip()
    if not me:
        return uo.stop("no character name in the state file - is the client logged in?")

    # Match on the first word too: people shorten a full name, and shards often show one.
    handles = {me.lower(), me.split()[0].lower()} | {w.lower() for w in args.also}

    uo.log(f"listening as {me} (also answers to {', '.join(sorted(handles - {me.lower()})) or '-'});"
           f" replying to anything naming us, or spoken within {args.range} tiles."
           f" Transcript: {args.transcript}")
    uo.log("this script never speaks - reply with:  echo \"say <text>\" >> /tmp/cuocmd")

    def record(line: str) -> None:
        try:
            with open(args.transcript, "a") as fh:
                fh.write(f"{time.strftime('%H:%M:%S')} {line}\n")
        except OSError:
            pass          # a transcript is a convenience; never let it kill the listener

    # on_ghost="run": being dead is exactly when someone is most likely to be talking to us.
    for _ in uo.every(args.poll, on_ghost="run"):
        events = uo.events.poll()
        if not events:
            continue

        mx, my = uo.xy

        # One world read per batch rather than per line.
        nearby: Dict[str, Mobile] = {}
        for m in uo.mobiles():
            if m.name:
                nearby.setdefault(m.name.lower(), m)

        for event in events:
            parsed = parse_speech(event.text)
            if parsed is None:
                continue

            kind, speaker, text = parsed

            if speaker.lower() in NOISE_SPEAKERS:
                continue
            if speaker.lower() in handles:
                record(f"<{me}> {text}")          # our own line, so the transcript reads in order
                continue

            mob = nearby.get(speaker.lower())
            distance = mob.distance if mob is not None else None
            lowered = text.lower()

            named = any(h in lowered for h in handles)
            close = distance is not None and distance <= args.range

            # Why a message is surfaced depends on the channel it arrived on:
            #
            #   guild/party/alliance  a closed channel - everyone on it is talking to the group,
            #                         and distance is meaningless (they may be across the world).
            #                         Always surfaced; whether it is aimed at *us* is a judgment
            #                         call, which is the reader's, not this script's.
            #   whisper               directed by construction - you whisper *to* someone.
            #   say/yell/emote        open speech, so it needs evidence: our name in the text, or
            #                         the speaker close enough that they plainly mean us.
            if kind in ("guild", "party", "alliance"):
                via = "channel"
            elif kind == "whisper":
                via = "whisper"
            elif named:
                via = "named"
            elif close:
                via = "nearby"
            else:
                if args.ambient:
                    where = f"d={distance}" if distance is not None else "unseen"
                    uo.log(f"[ambient] {kind} from {speaker} ({where}): {text}")
                continue

            person = looks_like_person(mob, speaker)
            at = f"({mob.x},{mob.y})" if mob is not None else "?"
            dist = str(distance) if distance is not None else "?"

            # One line, every fact needed to decide: which channel, who, where they are, where we
            # are, how far apart, whether they look like a person, and why this surfaced.
            uo.log(
                f"[TALK] chan={kind} from=\"{speaker}\" at={at} d={dist} "
                f"me=({mx},{my}) person={'yes' if person else 'no' if person is False else '?'} "
                f"via={via} text=\"{text}\""
            )
            record(f"({kind}) <{speaker}> {text}")


if __name__ == "__main__":
    sys.exit(main())
