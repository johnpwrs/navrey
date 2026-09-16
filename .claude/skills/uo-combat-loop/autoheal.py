#!/usr/bin/env python3
"""Watch player HP and bandage automatically whenever hurt.

Run it in the background beside whatever else is driving the character:

    python3 .claude/skills/uo-combat-loop/autoheal.py &
    python3 .claude/skills/uo-combat-loop/autoheal.py --threshold 75 --verbose
    python3 .claude/skills/uo-combat-loop/autoheal.py --replace     # restart a running one

Only one copy runs at a time - @script holds a lock under this file's name and a second launch
exits with code 3 rather than starting. Two healers is not a harmless duplicate: both see the same
low HP, both start a bandage, and the second `use` answers the target cursor the first one opened,
so a bandage is spent for nothing and the cursor is left dangling. Use --replace to hand over.

Stops on its own when the character dies (hand off to uo-death-recovery), when the backpack runs
out of bandages, or when the client goes away.

This is a *script*, not part of the `uo` library: "heal whenever I drop below 90%" is one opinion
about how to play, and the library deliberately holds none. The library gives it state, actions,
events, and the loop scaffolding; the policy below is all that is left.

It replaces auto_healer.sh, which had three problems this does not:

  It shelled out to `navrey status` and `navrey inv` every iteration - a .NET process spawn each
  time, 50-100ms of pure overhead per poll. State is read from the file here, at ~4us.

  Its bandage-completion grep listed 5 of the 9 messages, missing all three poison cures and one
  failure, so a poison cure never tripped the early break and it burned the full 15s timeout.
  The library owns one canonical set.

  It scraped the bandage serial with `grep -oE '0x[0-9A-Fa-f]+ clean bandage'` while `inv` prints
  bare-bracketed serials, so it likely never matched at all.
"""

from __future__ import annotations

import argparse
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "..", "cli"))

from uo import Event, script  # noqa: E402

ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
ap.add_argument("--threshold", type=int, default=90,
                help="bandage below this %% of max HP (default 90)")
ap.add_argument("--poll", type=float, default=0.3,
                help="seconds between HP checks (default 0.3)")
ap.add_argument("--bandage", default="bandage",
                help="name to match in the backpack (default 'bandage')")


@script(ap)
def main(uo, args):
    uo.log(f"threshold {args.threshold}% of max HP")

    for _ in uo.every(args.poll):
        # One snapshot, so hp and max cannot come from two different moments.
        with uo.frozen():
            hp, max_hp = uo.hp, uo.max_hp

        if max_hp <= 0 or hp * 100 >= max_hp * args.threshold:
            continue

        bandage = uo.find(args.bandage)

        if bandage is None:
            # An empty backpack list means "not opened yet" at least as often as "nothing there",
            # so ask before concluding we are out.
            if not uo.backpack_opened:
                uo.log("backpack not open yet - opening")
                uo.open_backpack()
                continue

            return uo.stop(f"HP {hp}/{max_hp} low but NO BANDAGES LEFT - restock and relaunch")

        uo.log(f"HP {hp}/{max_hp} below {args.threshold}% - applying {bandage.name}")

        # use_on waits for the server's target cursor before answering it. Firing `use` and
        # `target self` back to back loses the race: the cursor arrives ~33ms later and the
        # answer is discarded with "nothing is asking for a target".
        if not uo.use_on(bandage, "self"):
            uo.log("no target cursor after using the bandage - retrying")
            continue

        event = uo.wait_for([Event.BANDAGE_DONE, Event.BANDAGE_FAILED], timeout=20)

        if event is None:
            uo.log("no bandage result in 20s - carrying on")
        elif args.verbose or event.kind is Event.BANDAGE_FAILED:
            uo.log(f"{event.kind.value}: {event.text}")


if __name__ == "__main__":
    sys.exit(main())
