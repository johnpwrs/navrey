#!/usr/bin/env python3
"""Offline tests for safe_goto.py - no live client needed.

    python3 -m unittest discover -s .claude/skills/uo-navigation -p 'test_*.py' -v
"""

import importlib.util
import os
import sys
import unittest
from types import SimpleNamespace
from unittest.mock import patch

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "..", "cli"))

spec = importlib.util.spec_from_file_location("safe_goto", os.path.join(HERE, "safe_goto.py"))
sg = importlib.util.module_from_spec(spec)
sys.modules["safe_goto"] = sg
spec.loader.exec_module(sg)

from uo.avoidance import chebyshev, parse_term   # noqa: E402
from uo.entities import Mobile                   # noqa: E402


def mob(serial="0x1", name="a hostile", x=0, y=0, notoriety="Criminal", war=False, known=True):
    return Mobile({"serial": serial, "name": name, "x": x, "y": y, "z": 0,
                   "notoriety": notoriety, "inWarMode": war, "knownMonster": known,
                   "distance": max(abs(x), abs(y))})


class Clock:
    def __init__(self):
        self.t = 0.0

    def monotonic(self):
        return self.t

    def sleep(self, s):
        self.t += s


class FakeUO:
    def __init__(self, me=(0, 0), mobiles=()):
        self.xy, self._mobiles = me, list(mobiles)
        self.ghost, self.gotos, self.logs, self.cancels = False, [], [], 0
        self.nav = SimpleNamespace(status="walking", active=True)
        self.hp, self.max_hp = 50, 50

    def is_live(self):
        return True

    def mobiles(self, **_):
        return self._mobiles

    def goto(self, x, y):
        self.gotos.append((x, y))

    def cancel_walk(self):
        self.cancels += 1

    def log(self, msg):
        self.logs.append(msg)


def args(**over):
    ns = sg.ap.parse_args(["1", "2"])
    for k, v in over.items():
        setattr(ns, k, v)
    return ns


class RetreatTests(unittest.TestCase):
    def test_re_aims_on_the_interval_until_the_timeout(self):
        """One aim per leg was the bug: a 6s leg re-aims at 0, 1.5, 3.0 and 4.5s."""
        uo = FakeUO(me=(5, 0), mobiles=[mob(x=0, y=0)])
        with patch.object(sg, "time", Clock()):
            clear, remaining = sg.retreat(sg.Scanner(uo, [], []), clearance=15, timeout=6.0)
        self.assertFalse(clear)
        self.assertEqual(len(remaining), 1)
        self.assertEqual(len(uo.gotos), 4)

    def test_flee_point_is_measured_in_chebyshev_tiles(self):
        """A diagonal flee used to be scaled by hypot and land short of the clearance."""
        uo = FakeUO(me=(3, 3), mobiles=[mob(x=0, y=0)])
        with patch.object(sg, "time", Clock()):
            sg.retreat(sg.Scanner(uo, [], []), clearance=15, timeout=0.1)
        self.assertEqual(chebyshev(uo.gotos[0], (3, 3)), 15 + sg.FLEE_MARGIN)

    def test_clear_cancels_the_walk_and_returns(self):
        uo = FakeUO(me=(0, 0), mobiles=[])
        with patch.object(sg, "time", Clock()):
            clear, _ = sg.retreat(sg.Scanner(uo, [], []), clearance=15, timeout=6.0)
        self.assertTrue(clear)
        self.assertEqual(uo.cancels, 1)
        self.assertEqual(uo.gotos, [])

    def test_a_ghost_ends_the_leg(self):
        uo = FakeUO(me=(0, 0), mobiles=[mob(x=2)])
        uo.ghost = True
        with patch.object(sg, "time", Clock()):
            clear, _ = sg.retreat(sg.Scanner(uo, [], []), clearance=15, timeout=6.0)
        self.assertFalse(clear)
        self.assertEqual(uo.gotos, [])


class ScannerTests(unittest.TestCase):
    def test_gray_wildlife_is_not_a_threat(self):
        uo = FakeUO(mobiles=[mob(name="a rabbit", x=2, notoriety="Gray", known=False)])
        self.assertEqual(sg.Scanner(uo, [], []).threats(15), [])

    def test_avoided_mobiles_are_tagged_and_sorted_nearest_first(self):
        far, near = mob("0xF", "a wolf", x=9, notoriety="Gray", known=False), mob("0xN", x=3)
        uo = FakeUO(mobiles=[far, near])
        hits = sg.Scanner(uo, [parse_term("name:a wolf")], []).threats(15)
        self.assertEqual([(m.serial, k) for m, k in hits], [("0xN", "HOSTILE"), ("0xF", "AVOID")])

    def test_out_of_range_is_ignored(self):
        uo = FakeUO(mobiles=[mob(x=16)])
        self.assertEqual(sg.Scanner(uo, [], []).threats(15), [])


class StopForTests(unittest.TestCase):
    """Fighting us -> full escape; avoided and idle -> one back-off; otherwise a stop-short."""

    def run_stop(self, threats, **over):
        uo = FakeUO(me=(0, 0), mobiles=[m for m, _ in threats])
        run, calls = {}, []
        with patch.object(sg, "escape", lambda s, a, t: calls.append(("escape", t.label))), \
             patch.object(sg, "back_off", lambda s, a, c, w: calls.append(("back_off", c))):
            code = sg.stop_for(uo, args(**over), sg.Scanner(uo, [], []), run, threats)
        return code, run["outcome"], calls, uo

    def test_a_chaser_gets_the_full_escape_and_a_handoff(self):
        code, outcome, calls, uo = self.run_stop([(mob(name="a chaser", x=8, war=True), "HOSTILE")])
        self.assertEqual(code, 1)
        self.assertEqual(calls, [("escape", "a chaser")])
        self.assertTrue(outcome.startswith("fled a chaser"))
        self.assertTrue(any(l.startswith("HANDOFF") for l in uo.logs))
        self.assertEqual(uo.cancels, 1)

    def test_an_idle_avoided_creature_gets_one_back_off(self):
        code, outcome, calls, _ = self.run_stop([(mob(name="a lurker", x=10), "AVOID")])
        self.assertEqual(code, 1)
        self.assertEqual(calls, [("back_off", args().stop_berth)])
        self.assertTrue(outcome.startswith("blocked by a lurker"))

    def test_an_idle_hostile_is_a_stop_short(self):
        code, outcome, calls, uo = self.run_stop([(mob(name="a zombie", x=10), "HOSTILE")])
        self.assertEqual(code, 1)
        self.assertEqual(calls, [])
        self.assertIn("stopped short", outcome)
        self.assertIn("a zombie at (10,0)", outcome)
        self.assertTrue(any(l.startswith("HOSTILE STOP:") for l in uo.logs))


class ResumeLineTests(unittest.TestCase):
    def test_reports_distance_short(self):
        line = sg.resume_line(FakeUO(me=(0, 0)), "(10,0)", (10, 0), "walking", "x")
        self.assertIn("RESUME: x; phase=walking; at (0,0) hp=50/50", line)
        self.assertTrue(line.endswith("10 tiles short"))

    def test_survives_a_dead_client(self):
        class Dead:
            @property
            def xy(self):
                raise OSError("gone")
        line = sg.resume_line(Dead(), "bank", None, "walking", "x")
        self.assertIn("unknown", line)


if __name__ == "__main__":
    unittest.main(verbosity=2)
