"""Log Swordsmanship (and the rest of the build) plus stats at a fixed cadence.

Skills are not in the state file, so this is the only way to see grind progress over time.
Appends one line per sample to --out and stops once --target base skill is reached.
"""
import argparse
import re
import sys
import time

sys.path.insert(0, "cli")
from uo import script

ap = argparse.ArgumentParser(description=__doc__)
ap.add_argument("--skill", default="Swordsmanship", help="the skill the target applies to")
ap.add_argument("--target", type=float, default=75.0, help="base skill to stop at")
ap.add_argument("--out", default="/tmp/cuoskill.log")
ap.add_argument("--interval", type=float, default=120.0)

TRACKED = ("Swordsmanship", "Tactics", "Anatomy", "Parrying", "Healing", "Resisting Spells")
SKILL_LINE = re.compile(r"\[SKILL\] (.+?)\s+([\d.]+) \(base\s+([\d.]+)\)")


def sample(uo):
    """`skills` is a command, not state - ask for it and parse the reply."""
    out = {}
    for line in uo.call("skills", timeout=15.0):
        m = SKILL_LINE.search(line)
        if m and m.group(1).strip() in TRACKED:
            out[m.group(1).strip()] = float(m.group(3))
    return out


@script(ap)
def main(uo, args):
    first = None
    t0 = time.time()
    for _ in uo.every(args.interval):
        skills = sample(uo)
        cur = skills.get(args.skill)
        if cur is None:
            continue
        if first is None:
            first = cur
        mins = (time.time() - t0) / 60.0
        gained = cur - first
        rate = (gained / mins * 60.0) if mins >= 5 else float("nan")
        parts = " ".join(f"{n.split()[0][:4]}={v:.1f}" for n, v in sorted(skills.items()))
        line = (f"{time.strftime('%H:%M:%S')} {parts} "
                f"| str={uo.state.get('str')} dex={uo.state.get('dex')} "
                f"hp={uo.hp}/{uo.max_hp} gold={uo.gold} "
                f"| +{gained:.1f} in {mins:.0f}m ({rate:.1f}/hr)")
        print(line, flush=True)
        with open(args.out, "a") as fh:
            fh.write(line + "\n")
        if cur >= args.target:
            return uo.stop(f"{args.skill} base {cur:.1f} reached target {args.target}", code=0)


if __name__ == "__main__":
    sys.exit(main())
