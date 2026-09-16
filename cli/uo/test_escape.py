#!/usr/bin/env python3
"""Tests for the escape timing rules and handoff wording both reflexes share.

    python3 -m unittest discover -s cli/uo -p 'test_*.py' -v
"""
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from uo.escape import (  # noqa: E402
    HANDOFF_BERTH,
    THREAT_CLEAR_SECS,
    ThreatMemory,
    escalate,
    handoff_message,
    handoff_summary,
)


class ThreatMemoryTests(unittest.TestCase):
    def test_locks_onto_the_first_serial_offered(self):
        """The trigger is what blocks the return trip; a bystander passed en route is not."""
        t = ThreatMemory()
        t.saw("0xT", "a bogle", (3, 0), 100.0)
        t.saw("0xB", "a shade", (41, 0), 110.0)

        self.assertEqual((t.serial, t.name, t.pos), ("0xT", "a bogle", (3, 0)))

    def test_follows_the_locked_one(self):
        t = ThreatMemory()
        for x, when in ((3, 100.0), (9, 101.0), (16, 102.0)):
            t.saw("0xT", "a bogle", (x, 0), when)

        self.assertEqual((t.pos, t.seen_at), ((16, 0), 102.0))

    def test_out_of_view_is_not_yet_clear(self):
        t = ThreatMemory()
        t.saw("0xS", "a spectre", (5, 0), 100.0)

        self.assertFalse(t.confirmed_clear(105.0))
        self.assertTrue(t.confirmed_clear(100.0 + THREAT_CLEAR_SECS))

    def test_a_reappearance_resets_the_clock(self):
        t = ThreatMemory()
        t.saw("0xS", "a spectre", (5, 0), 100.0)
        self.assertTrue(t.confirmed_clear(116.0))

        t.saw("0xS", "a spectre", (17, 0), 114.0)          # flickered at the view edge
        self.assertFalse(t.confirmed_clear(116.0))

    def test_never_seen_anything_counts_as_clear(self):
        """A swarm or HP escape has no avoided creature at all."""
        self.assertTrue(ThreatMemory().confirmed_clear(0.0))

    def test_says_unknown_rather_than_inventing_a_coordinate(self):
        t = ThreatMemory()
        self.assertEqual(t.label, "an avoided creature")
        self.assertEqual(t.where, "unknown")


class EscalateTests(unittest.TestCase):
    def test_widens_and_caps(self):
        self.assertEqual(escalate(25, 10, 60), 35)
        self.assertEqual(escalate(55, 10, 60), 60)
        self.assertEqual(escalate(60, 10, 60), 60)


class HandoffWordingTests(unittest.TestCase):
    def _threat(self):
        t = ThreatMemory()
        t.saw("0xW", "a wraith", (1373, 1456), 1.0)
        return t

    def test_carries_the_two_coordinates_a_route_needs(self):
        msg = handoff_message(self._threat(), (1298, 1536))

        self.assertIn("a wraith", msg)
        self.assertIn("(1373, 1456)", msg)     # where it was
        self.assertIn("(1298, 1536)", msg)     # where we are

    def test_asks_for_a_small_detour_not_a_journey(self):
        """An earlier wording said "wide berth" and got an 80-tile trek."""
        msg = handoff_message(self._threat(), (0, 0))

        self.assertIn(f"{HANDOFF_BERTH} tiles", msg)
        self.assertIn("do not take a long way round", msg)

    def test_summary_is_one_line_and_names_the_berth(self):
        line = handoff_summary(self._threat())

        self.assertIn("a wraith", line)
        self.assertIn("(1373, 1456)", line)
        self.assertIn(f"{HANDOFF_BERTH} tiles", line)
        self.assertNotIn("\n", line)


if __name__ == "__main__":
    unittest.main(verbosity=2)
