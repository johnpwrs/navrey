#!/usr/bin/env python3
"""Offline tests for combat_movement_melee.py - no live client needed.

    python3 -m unittest discover -s .claude/skills/uo-combat-loop -p 'test_*.py' -v

Everything worth testing here is pure: the point pickers, `is_aggro`, the transition table, and
`Looter.step` all take data rather than a `Client`. `Mobile`/`Item` wrap a plain dict, so fixtures
cost nothing. The one place a stub is needed is the looter, which does call out to the client - so
there is a fake with recorded calls and scripted listings.
"""

import argparse
import os
import sys
import unittest
from types import SimpleNamespace

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", ".."))
sys.path.insert(0, os.path.join(HERE, "..", "..", "..", "cli"))

import importlib.util

spec = importlib.util.spec_from_file_location(
    "melee", os.path.join(HERE, "combat_movement_melee.py"))
melee = importlib.util.module_from_spec(spec)
# Register before exec: @dataclass resolves annotations via sys.modules[cls.__module__], and an
# unregistered module makes that lookup return None. Nothing to do with the script itself.
sys.modules["melee"] = melee
spec.loader.exec_module(melee)

from uo.avoidance import is_aggro, parse_term         # noqa: E402
from uo.entities import Item, Mobile                  # noqa: E402


def mob(serial="0x1", name="target", x=0, y=0, notoriety="Criminal",
        war=False, known=True, **extra):
    return Mobile({"serial": serial, "name": name, "x": x, "y": y, "z": 0,
                   "notoriety": notoriety, "inWarMode": war, "knownMonster": known,
                   "distance": max(abs(x), abs(y)), **extra})


def corpse(serial="0xC1", name="a corpse", x=0, y=0):
    return Item({"serial": serial, "name": name, "x": x, "y": y, "z": 0,
                 "distance": max(abs(x), abs(y)), "isCorpse": True, "isContainer": True})


def thing(serial, name="gold", container=False):
    return Item({"serial": serial, "name": name, "isContainer": container})


def make_args(**over):
    """Defaults straight from the real parser, so a flag rename breaks these tests loudly."""
    ns = melee.ap.parse_args([])
    for k, v in over.items():
        setattr(ns, k, v)
    return ns


def make_threats(me=(0, 0), hp_pct=100.0, mobiles=(), corpses=(), avoided=(), now=100.0,
                 aggro_range=12):
    mobiles = list(mobiles)
    return melee.Threats(
        now=now, me=me, hp_pct=hp_pct, mobiles=mobiles,
        threatening=[m for m in mobiles if melee.is_threat(m)],
        avoided=list(avoided),
        aggro={m.serial for m in mobiles if is_aggro(m, me, aggro_range)},
        corpses=list(corpses),
    )


def make_brain(state=melee.KILL, escape=None, looter=None, **over):
    st = melee.Brain(mover=None, looter=looter or FakeLooter(), avoid=[], include=[])
    st.state = state
    st.escape = escape
    for k, v in over.items():
        setattr(st, k, v)
    return st


class FakeLooter:
    def __init__(self, work=False):
        self.done, self.unreachable, self._work = set(), set(), work

    def has_work(self):
        return self._work


# ------------------------------------------------------------------------------------------
class TestIsAggro(unittest.TestCase):
    """War mode says a mobile is fighting *something*; distance is what makes it about us."""

    def test_war_mode_far_is_someone_elses_fight(self):
        self.assertFalse(is_aggro(mob(x=20, war=True), (0, 0), 12))

    def test_war_mode_adjacent_is_us(self):
        self.assertTrue(is_aggro(mob(x=1, war=True), (0, 0), 12))

    def test_war_mode_at_the_boundary_counts(self):
        self.assertTrue(is_aggro(mob(x=12, war=True), (0, 0), 12))

    def test_not_in_war_mode_is_never_aggro(self):
        self.assertFalse(is_aggro(mob(x=1, war=False), (0, 0), 12))


# ------------------------------------------------------------------------------------------
class TestEscapePoint(unittest.TestCase):
    def test_maximin_beats_away_from_nearest(self):
        # Threats north and south, the north one marginally closer. Running "away from the
        # nearest" goes south, straight into the other. Maximin has to go east or west.
        point, direction = melee.pick_escape_point((0, 0), [(0, -3), (0, 4)], 15)
        self.assertEqual(direction[1], 0, f"fled along y into the second threat: {direction}")
        self.assertIn(direction[0], (-1, 1))
        self.assertEqual(max(abs(point[0]), abs(point[1])), 15)

    def test_picks_the_one_open_side(self):
        ring = [d for d in melee._COMPASS if d != (1, 0)]
        threats = [(d[0] * 2, d[1] * 2) for d in ring]
        _, direction = melee.pick_escape_point((0, 0), threats, 15)
        self.assertEqual(direction, (1, 0))

    def test_single_threat_runs_directly_away(self):
        _, direction = melee.pick_escape_point((0, 0), [(0, 5)], 15)
        self.assertEqual(direction, (0, -1))

    def test_all_directions_excluded_still_returns_one(self):
        point, direction = melee.pick_escape_point(
            (0, 0), [(0, 5)], 15, frozenset(melee._COMPASS))
        self.assertIn(direction, melee._COMPASS)
        self.assertIsNotNone(point)

    def test_excluded_direction_is_not_chosen(self):
        _, direction = melee.pick_escape_point((0, 0), [(0, 5)], 15, frozenset({(0, -1)}))
        self.assertNotEqual(direction, (0, -1))

    def test_no_threats_does_not_crash(self):
        point, direction = melee.pick_escape_point((0, 0), [], 15)
        self.assertIn(direction, melee._COMPASS)
        self.assertIsNotNone(point)


# ------------------------------------------------------------------------------------------
class TestPursuitPoint(unittest.TestCase):
    def test_no_zigzag_on_a_shallow_diagonal(self):
        # The regression: a unit-sign scaler applies the full chebyshev magnitude to both axes,
        # overshoots the short axis, then flips its sign on the next call once the character has
        # walked past it. Target 8 east, 1 north; neither aim point may overshoot y.
        first = melee.pick_pursuit_point((0, 0), (8, -1), 0)
        second = melee.pick_pursuit_point((3, 0), (8, -1), 0)
        self.assertEqual(first, (8, -1))
        self.assertEqual(second, (8, -1))
        for point in (first, second):
            self.assertGreaterEqual(point[1], -1, "overshot the short axis")

    def test_straight_line_when_nothing_to_avoid(self):
        self.assertEqual(melee.pick_pursuit_point((0, 0), (5, 0), 0), (5, 0))

    def test_stop_distance_holds_short(self):
        self.assertEqual(melee.pick_pursuit_point((0, 0), (10, 0), 2), (8, 0))

    def test_routes_around_an_avoided_mobile(self):
        point = melee.pick_pursuit_point((0, 0), (10, 0), 0, [((10, 0), 5)])
        self.assertIsNotNone(point)
        self.assertGreaterEqual(melee.chebyshev(point, (10, 0)), 5)

    def test_an_idle_avoided_mobile_blocks_far_less_ground(self):
        """The same mobile, the same spot - only the berth differs. At the aggro'd berth the
        straight line is refused; at the idle berth it is fine."""
        self.assertNotEqual(melee.pick_pursuit_point((0, 0), (6, 0), 0, [((9, 0), 5)]), (6, 0))
        self.assertEqual(melee.pick_pursuit_point((0, 0), (6, 0), 0, [((9, 0), 2)]), (6, 0))

    def test_returns_none_when_nothing_clears(self):
        # Ringed by avoided mobiles at every candidate: hold position rather than walk into one.
        avoid = [((d[0] * 5, d[1] * 5), 5) for d in melee._COMPASS] + [((5, 0), 5)]
        self.assertIsNone(melee.pick_pursuit_point((0, 0), (5, 0), 0, avoid))


# ------------------------------------------------------------------------------------------
class TestStaleTargetPosition(unittest.TestCase):
    """After an escape, `lastAttack` still names the mobile we fled and still reports the last
    position the client saw. Trusting that made the target read as locatable forever, so
    `lost_since` was never set, `--lost-after` never armed, and the script pursued a remembered
    spot 25 tiles away - walking back toward what it had just fled."""

    class _UO:
        def __init__(self, mobs, last):
            self._mobs, self.last_attack = mobs, last

        def mobiles(self):
            return self._mobs

    def _target(self, x, y, exists=True):
        from types import SimpleNamespace
        return SimpleNamespace(exists=exists, pos=(x, y, 0))

    def test_a_visible_mobile_is_always_trusted(self):
        uo = self._UO([mob(serial="0xT", x=5)], self._target(5, 0))
        self.assertEqual(melee.locate(uo, "0xT", (0, 0)), (5, 0))

    def test_a_remembered_position_inside_world_range_is_trusted(self):
        uo = self._UO([], self._target(10, 0))
        self.assertEqual(melee.locate(uo, "0xT", (0, 0)), (10, 0))

    def test_a_remembered_position_beyond_world_range_is_not(self):
        uo = self._UO([], self._target(25, 0))
        self.assertIsNone(melee.locate(uo, "0xT", (0, 0)),
                          "pursued a position the world file could not corroborate")

    def test_without_a_reference_point_behaviour_is_unchanged(self):
        uo = self._UO([], self._target(25, 0))
        self.assertEqual(melee.locate(uo, "0xT"), (25, 0))


class TestGivingUpSticks(unittest.TestCase):
    def test_a_lost_target_is_blacklisted_so_it_is_not_re_adopted(self):
        """Clearing st.current alone did nothing: lastAttack still named the mobile, so the next
        iteration re-adopted it and restarted the timer - a loop that never acquired anything."""
        uo, mover = FakeUO(), FakeMover()
        from types import SimpleNamespace
        uo.last_attack = SimpleNamespace(is_set=True, serial="0xGONE", name="a target",
                                         exists=False, pos=None)
        st = make_brain()
        st.mover, st.current, st.lost_since = mover, "0xGONE", 0.0
        st.last_pos = None
        melee.do_kill(uo, make_args(), make_threats(now=100.0), st)
        self.assertGreater(st.blacklist.get("0xGONE", 0.0), 100.0,
                           "gave up without blacklisting - it will be re-adopted next tick")


class TestDecideState(unittest.TestCase):
    def test_low_hp_escapes(self):
        state, reason = melee.decide_state(make_args(), make_threats(hp_pct=40), make_brain())
        self.assertEqual((state, reason), (melee.ESCAPE, "hp"))

    def test_hp_preempts_a_running_avoid_escape(self):
        st = make_brain(escape=melee.Escape("avoid-near", 15, 0.0, (0, 0)))
        state, reason = melee.decide_state(make_args(), make_threats(hp_pct=40), st)
        self.assertEqual((state, reason), (melee.ESCAPE, "hp"))

    def test_avoided_aggro_escapes_from_well_outside_avoid_range(self):
        hostile = mob(serial="0xS", name="a hostile", x=11, war=True)
        t = make_threats(mobiles=[hostile], avoided=[hostile])
        state, reason = melee.decide_state(make_args(), t, make_brain())
        self.assertEqual((state, reason), (melee.ESCAPE, "avoid-aggro"))

    def test_avoided_merely_close_escapes_without_a_hit(self):
        hostile = mob(serial="0xS", name="a hostile", x=2, war=False)
        t = make_threats(mobiles=[hostile], avoided=[hostile])
        state, reason = melee.decide_state(make_args(), t, make_brain())
        self.assertEqual((state, reason), (melee.ESCAPE, "avoid-near"))

    def test_an_idle_avoided_mobile_further_out_is_tolerated(self):
        """It is not fighting us and it is not underfoot - no reason to abandon the fight. This is
        the whole point of --avoid-idle-range being smaller than --avoid-range."""
        hostile = mob(serial="0xS", name="a hostile", x=4, war=False)
        t = make_threats(mobiles=[hostile], avoided=[hostile])
        state, _ = melee.decide_state(make_args(), t, make_brain())
        self.assertEqual(state, melee.KILL)

    def test_but_the_same_mobile_aggrod_still_escapes_from_far_off(self):
        hostile = mob(serial="0xS", name="a hostile", x=11, war=True)
        t = make_threats(mobiles=[hostile], avoided=[hostile])
        state, reason = melee.decide_state(make_args(), t, make_brain())
        self.assertEqual((state, reason), (melee.ESCAPE, "avoid-aggro"))

    def test_keepaway_gives_aggrod_and_idle_different_berths(self):
        angry = mob(serial="0xA", name="a hostile", x=3, war=True)
        calm = mob(serial="0xB", name="a hostile", x=-3, war=False)
        t = make_threats(mobiles=[angry, calm], avoided=[angry, calm])
        berths = dict(t.keepaway(5, 2))
        self.assertEqual(berths[(3, 0)], 5)
        self.assertEqual(berths[(-3, 0)], 2)

    def test_swarm_count_threats_within_two_tiles_is_a_swarm(self):
        crowd = [mob(serial=f"0x{i}", x=1, y=i - 1) for i in range(make_args().swarm_count)]
        state, reason = melee.decide_state(
            make_args(), make_threats(mobiles=crowd, hp_pct=60), make_brain())
        self.assertEqual((state, reason), (melee.ESCAPE, "swarm"))

    def test_one_short_of_swarm_count_is_not(self):
        crowd = [mob(serial=f"0x{i}", x=1, y=i - 1) for i in range(make_args().swarm_count - 1)]
        state, _ = melee.decide_state(
            make_args(), make_threats(mobiles=crowd, hp_pct=60), make_brain())
        self.assertEqual(state, melee.KILL)

    def test_a_swarm_at_full_health_is_fought_not_fled(self):
        """Being surrounded at full HP is a busy fight, not an emergency - fleeing it throws away
        kills and covers ground for nothing."""
        crowd = [mob(serial=f"0x{i}", x=1, y=i - 1) for i in range(3)]
        state, _ = melee.decide_state(
            make_args(), make_threats(mobiles=crowd, hp_pct=100), make_brain())
        self.assertEqual(state, melee.KILL)

    def test_the_same_swarm_is_fled_once_it_is_costing_health(self):
        crowd = [mob(serial=f"0x{i}", x=1, y=i - 1) for i in range(3)]
        state, reason = melee.decide_state(
            make_args(swarm_count=3), make_threats(mobiles=crowd, hp_pct=60), make_brain())
        self.assertEqual((state, reason), (melee.ESCAPE, "swarm"))

    def test_the_swarm_hp_gate_is_configurable(self):
        crowd = [mob(serial=f"0x{i}", x=1, y=i - 1) for i in range(3)]
        state, _ = melee.decide_state(
            make_args(swarm_count=3, swarm_hp_pct=50),
            make_threats(mobiles=crowd, hp_pct=80), make_brain())
        self.assertEqual(state, melee.KILL, "fled a swarm above the configured gate")

    def test_three_rabbits_are_not_a_swarm(self):
        # The old version counted swarms off `hostile`, which includes Gray wildlife.
        bunnies = [mob(serial=f"0x{i}", name="a rabbit", x=1, y=i - 1,
                       notoriety="Gray", known=False) for i in range(3)]
        state, _ = melee.decide_state(make_args(), make_threats(mobiles=bunnies), make_brain())
        self.assertEqual(state, melee.KILL)

    def test_escape_latches_while_still_pursued(self):
        chaser = mob(x=9)
        st = make_brain(state=melee.ESCAPE,
                        escape=melee.Escape("avoid-near", 10, 0.0, (0, 0), origin=(-20, 0)))
        state, _ = melee.decide_state(make_args(), make_threats(mobiles=[chaser]), st)
        self.assertEqual(state, melee.ESCAPE, "resumed with a threat inside --pursuit-range")

    def test_a_swarm_escape_ends_once_the_pack_is_broken(self):
        """A swarm break only has to string the pack out; a straggler in view does not hold it."""
        chaser = mob(x=9)
        st = make_brain(state=melee.ESCAPE,
                        escape=melee.Escape("swarm", 6, 0.0, (0, 0), origin=(-20, 0)))
        state, _ = melee.decide_state(make_args(), make_threats(mobiles=[chaser]), st)
        self.assertEqual(state, melee.KILL)

    def test_hp_escape_does_not_resume_until_healed(self):
        st = make_brain(state=melee.ESCAPE,
                        escape=melee.Escape("hp", 25, 0.0, (0, 0), origin=(0, 0)))
        state, _ = melee.decide_state(make_args(), make_threats(me=(30, 0), hp_pct=60), st)
        self.assertEqual(state, melee.ESCAPE, "resumed at 60% with --resume-hp-pct 85")

    def test_hp_escape_resumes_once_healed_and_clear(self):
        st = make_brain(state=melee.ESCAPE,
                        escape=melee.Escape("hp", 25, 0.0, (0, 0), origin=(0, 0)))
        state, _ = melee.decide_state(make_args(), make_threats(me=(30, 0), hp_pct=90), st)
        self.assertEqual(state, melee.KILL)

    def test_escape_will_not_end_before_it_has_actually_fled(self):
        """Clearance alone saturates: the world file only carries 18 tiles, so an empty screen
        reads as 999 and a 25-tile flee could 'succeed' having barely moved. Observed live as
        escape -> kill -> escape every few seconds while the character went nowhere."""
        e = melee.Escape("avoid-aggro", 25, 0.0, (0, 0), origin=(0, 0))
        st = make_brain(state=melee.ESCAPE, escape=e)
        state, _ = melee.decide_state(make_args(), make_threats(me=(3, 0), now=100.0), st)
        self.assertEqual(state, melee.ESCAPE, "resumed after fleeing 3 tiles of a 25-tile escape")

    def test_escape_ends_once_it_has_both_fled_and_cleared(self):
        e = melee.Escape("avoid-aggro", 25, 0.0, (0, 0), origin=(0, 0))
        st = make_brain(state=melee.ESCAPE, escape=e)
        state, _ = melee.decide_state(make_args(), make_threats(me=(30, 0), now=100.0), st)
        self.assertEqual(state, melee.KILL)

    def test_escape_does_not_resume_inside_the_dwell(self):
        """Observed live: kill -> escape -> kill -> escape a second apart, nine times a minute,
        while a stalker loitered on the --aggro-range boundary. Fought nothing, fled nowhere."""
        st = make_brain(state=melee.ESCAPE,
                        escape=melee.Escape("avoid-aggro", 15, 99.0, (0, 0)))
        state, _ = melee.decide_state(make_args(), make_threats(now=100.0), st)
        self.assertEqual(state, melee.ESCAPE, "resumed 1s into an escape")

    def test_escape_resumes_after_the_dwell_when_clear(self):
        st = make_brain(state=melee.ESCAPE,
                        escape=melee.Escape("avoid-aggro", 15, 90.0, (0, 0), origin=(0, 0)))
        state, _ = melee.decide_state(make_args(), make_threats(me=(20, 0), now=100.0), st)
        self.assertEqual(state, melee.KILL)

    def test_resume_needs_more_clearance_than_the_trigger_took(self):
        """The avoided mobile is outside --aggro-range (12), so it is no longer 'aggro'd' - but it
        is inside the hysteresis band, so going back now just re-triggers."""
        hostile = mob(serial="0xS", name="a hostile", x=14, war=True)
        st = make_brain(state=melee.ESCAPE,
                        escape=melee.Escape("avoid-aggro", 15, 0.0, (0, 0)))
        t = make_threats(mobiles=[hostile], avoided=[hostile], now=100.0)
        state, _ = melee.decide_state(make_args(), t, st)
        self.assertEqual(state, melee.ESCAPE)

    def test_loot_when_there_is_work_and_it_is_quiet(self):
        st = make_brain(looter=FakeLooter(work=True))
        state, _ = melee.decide_state(make_args(), make_threats(), st)
        self.assertEqual(state, melee.LOOT)

    def test_how_many_are_hitting_us_does_not_change_the_loot_decision(self):
        """LOOT defends without moving; what KILL adds is pursuit, and pursuit loses the corpse.

        Measured live: `opened - a plain corpse at depth 0`, `loot -> kill` a second later when
        a second attacker arrived, then `giving up - corpse no longer in view` 65s after that,
        with the inner container never opened. Three corpses in one session.
        """
        for n in (1, 2, 4):
            crowd = [mob(serial=f"0x{i}", x=1, war=True) for i in range(n)]
            st = make_brain(looter=FakeLooter(work=True))
            state, _ = melee.decide_state(make_args(), make_threats(mobiles=crowd), st)
            self.assertEqual(state, melee.LOOT, f"{n} adjacent")

    def test_a_crowd_does_not_end_a_drain_in_progress_either(self):
        crowd = [mob(serial=f"0x{i}", x=1, war=True) for i in range(4)]
        st = make_brain(state=melee.LOOT, looter=FakeLooter(work=True))
        state, _ = melee.decide_state(make_args(), make_threats(mobiles=crowd, now=100.0), st)
        self.assertEqual(state, melee.LOOT)

    def test_but_escape_still_outranks_looting(self):
        """Safety is unchanged - HP, swarm and avoid-aggro all pre-empt a drain."""
        crowd = [mob(serial=f"0x{i}", x=1, war=True) for i in range(4)]
        st = make_brain(state=melee.LOOT, looter=FakeLooter(work=True))
        state, reason = melee.decide_state(
            make_args(flee_hp_pct=50.0), make_threats(mobiles=crowd, hp_pct=20.0, now=100.0), st)
        self.assertEqual(state, melee.ESCAPE)
        self.assertEqual(reason, "hp")

    def test_no_loot_flag_never_enters_loot(self):
        st = make_brain(looter=FakeLooter(work=True))
        state, _ = melee.decide_state(make_args(loot=False), make_threats(), st)
        self.assertEqual(state, melee.KILL)

    def test_escape_suppression_does_not_apply_to_hp(self):
        st = make_brain(escape_suppressed_until=200.0)
        state, reason = melee.decide_state(
            make_args(), make_threats(hp_pct=40, now=100.0), st)
        self.assertEqual((state, reason), (melee.ESCAPE, "hp"))

    def test_escape_suppression_lets_kill_run_when_cornered(self):
        crowd = [mob(serial=f"0x{i}", x=1, y=i - 1) for i in range(3)]
        st = make_brain(escape_suppressed_until=200.0)
        state, _ = melee.decide_state(make_args(), make_threats(mobiles=crowd, now=100.0), st)
        self.assertEqual(state, melee.KILL)


# ------------------------------------------------------------------------------------------
class FakeMover:
    def __init__(self):
        self.aiming_at, self.sent, self.stopped = None, [], 0

    def to(self, point):
        if point is None:
            return
        self.sent.append(point)
        self.aiming_at = point

    def clear(self):
        self.aiming_at = None

    def stop(self):
        self.stopped += 1
        self.aiming_at = None


class FakeUO:
    """Just enough Client for the handlers: nav status, combat, and recorded actions."""

    class _Nav:
        status = "arrived"

    class _Combat:
        since_attacked = 999.0
        last_attacker_name = None

    def __init__(self):
        self.nav, self.combat = self._Nav(), self._Combat()
        self.logs, self.attacks = [], []
        self.last_attack = None      # replaced per-test
        self._mobiles = []
        self.in_combat_now = False

    def in_combat(self, within=0.0):
        return self.in_combat_now

    def mobiles(self, **_):
        return self._mobiles

    def containers(self, **_):
        return []

    def nearest(self, **_):
        return None

    def log(self, msg):
        self.logs.append(msg)

    def war(self, on=None):
        pass

    def attack(self, serial):
        self.attacks.append(serial)


class NoTarget:
    is_set = False


class TestEscapeStopsRunningOnceClear(unittest.TestCase):
    """Regression: an HP escape stays latched until autoheal finishes, but staying latched is not
    a reason to keep walking. Live, one flee that was clear within seconds kept re-aiming a fresh
    25-tile goto for the whole 40s of bandaging and ended 94 tiles from the fight."""

    def run_escape(self, threats, hp_pct, distance=25, me=(40, 0)):
        """`me` defaults to well beyond `distance` from the origin - holding needs both an empty
        screen AND real ground covered, so a test that wants the 'clear' branch must have fled."""
        uo, mover = FakeUO(), FakeMover()
        mover.aiming_at = (99, 99)
        st = make_brain(state=melee.ESCAPE,
                        escape=melee.Escape("hp", distance, 100.0, (0, 0), origin=(0, 0)))
        st.mover = mover
        melee.do_escape(uo, make_args(),
                        make_threats(me=me, mobiles=threats, hp_pct=hp_pct), st)
        return mover

    def test_holds_position_once_it_has_fled_and_the_screen_is_empty(self):
        mover = self.run_escape([], hp_pct=50)
        self.assertEqual(mover.sent, [], "kept fleeing with no threat in view")

    def test_keeps_running_when_the_screen_is_empty_but_it_has_not_fled(self):
        """The bug this exists for: view range is 18 tiles, so an empty screen is not evidence of
        25 tiles of clearance. It used to stop the moment nothing was visible."""
        mover = self.run_escape([], hp_pct=50, me=(3, 0))
        self.assertTrue(mover.sent, "stopped 3 tiles into a 25-tile escape")

    def test_holds_with_a_straggler_at_the_edge_of_view(self):
        """The creep bug: --hp-flee-distance 25 exceeds the 18-tile world file, so a pursuer 16
        tiles back read as 'not clear' forever and the hold un-latched every time it flickered
        into view. Clearance is judged by --pursuit-range, which is observable."""
        mover = self.run_escape([mob(x=56, war=True)], hp_pct=50, me=(40, 0))
        self.assertEqual(mover.sent, [], "crept onward for a threat 16 tiles behind")

    def test_still_runs_while_a_threat_is_inside_the_clearance(self):
        # Mobile positions are absolute, so place it 6 tiles from `me`, not 6 from the origin.
        mover = self.run_escape([mob(x=46, war=True)], hp_pct=50, me=(40, 0))
        self.assertTrue(mover.sent, "stopped fleeing with a threat 6 tiles away")

    def test_cancels_the_walk_in_flight_on_going_clear(self):
        mover = self.run_escape([], hp_pct=50)
        self.assertEqual(mover.stopped, 1)


class TestFleeDistanceOrdering(unittest.TestCase):
    """A swarm break is the cheap escape - it only has to string a pack out. The two that mean
    something is actually going wrong have to run further, or the character wanders the map."""

    def test_defaults_are_ordered(self):
        a = make_args()
        self.assertLess(a.swarm_flee_distance, a.flee_distance)
        self.assertLess(a.swarm_flee_distance, a.aggro_flee_distance)
        self.assertLess(a.swarm_flee_distance, a.hp_flee_distance)
        self.assertLessEqual(a.flee_distance, a.aggro_flee_distance)

    def test_each_reason_picks_its_own_distance(self):
        a = make_args()
        for reason, expected in (("hp", a.hp_flee_distance),
                                 ("avoid-aggro", a.aggro_flee_distance),
                                 ("swarm", a.swarm_flee_distance),
                                 ("avoid-near", a.flee_distance)):
            chosen = (a.hp_flee_distance if reason == "hp"
                      else a.aggro_flee_distance if reason == "avoid-aggro"
                      else a.swarm_flee_distance if reason == "swarm"
                      else a.flee_distance)
            self.assertEqual(chosen, expected, reason)


class TestReturnsToTheFight(unittest.TestCase):
    """Regression: after an escape the character is far from everything, and KILL only acquires
    within --acquire-range - so it healed up, went back to KILL, and idled forever."""

    def run_kill(self, me, anchor, **args_over):
        uo, mover = FakeUO(), FakeMover()
        uo.last_attack = NoTarget()
        st = make_brain()
        st.mover, st.anchor = mover, anchor
        melee.do_kill(uo, make_args(**args_over), make_threats(me=me), st)
        return mover

    def test_walks_back_when_out_of_range(self):
        mover = self.run_kill(me=(0, 0), anchor=(90, 0))
        self.assertTrue(mover.sent, "idled instead of returning to the fight")

    def test_goes_back_even_with_pursuers_in_view(self):
        """Deliberate: the run breaks a pack up, so heading back means meeting them one at a time
        rather than all at once. Standing off instead just stalls the cycle."""
        uo, mover = FakeUO(), FakeMover()
        uo.last_attack = NoTarget()
        st = make_brain()
        st.mover, st.anchor = mover, (90, 0)
        chasers = [mob(serial=f"0x{i}", x=11, y=i) for i in range(4)]
        melee.do_kill(uo, make_args(), make_threats(me=(0, 0), mobiles=chasers), st)
        self.assertTrue(mover.sent, "stalled instead of going back to the fight")

    def test_stays_put_once_back_in_range(self):
        mover = self.run_kill(me=(0, 0), anchor=(5, 0))
        self.assertEqual(mover.sent, [], "kept walking after arriving")

    def test_drops_the_anchor_once_back_and_the_area_is_empty(self):
        """The anchor is the last target's death spot, which is where its corpse lies. Keeping it
        after the area is cleared makes the character walk back to a body, not loot it (already
        drained) and not move on - exactly what was reported."""
        uo, mover = FakeUO(), FakeMover()
        uo.last_attack = NoTarget()
        st = make_brain()
        st.mover, st.anchor = mover, (5, 0)
        melee.do_kill(uo, make_args(), make_threats(me=(0, 0)), st)
        self.assertIsNone(st.anchor, "held on to a cleared-out anchor")

    def test_no_return_flag_disables_it(self):
        mover = self.run_kill(me=(0, 0), anchor=(90, 0), return_to_fight=False)
        self.assertEqual(mover.sent, [])

    def test_only_our_own_kills_are_queued(self):
        """A corpse in view is not work. The queue is fed only by do_kill, on a corpse confirmed
        to belong to something we killed - other people's corpses are their loot."""
        lt = melee.Looter(FakeUO(), make_args())
        self.assertFalse(lt.has_work())
        self.assertFalse(hasattr(lt, "refresh"), "a bulk 'take every corpse in view' path is back")
        lt.enqueue(corpse(serial="0xDEAD"))
        self.assertTrue(lt.has_work())

    def test_no_anchor_yet_does_nothing(self):
        mover = self.run_kill(me=(0, 0), anchor=None)
        self.assertEqual(mover.sent, [])


# ------------------------------------------------------------------------------------------
class FakeClient:
    """Records calls and serves scripted listings. `listings` maps serial -> list of states,
    popped one per `container()` call so a drain can be watched emptying."""

    def __init__(self, listings):
        self.listings = {k: list(v) for k, v in listings.items()}
        self.calls = []

    def _record(self, *call):
        self.calls.append(call)

    def use(self, serial):
        self._record("use", serial)

    def get_item(self, serial):
        self._record("get", serial)

    def container(self, serial, timeout=5.0):
        self._record("container", serial)
        states = self.listings.get(serial, [[]])
        return states.pop(0) if len(states) > 1 else states[0]

    def log(self, msg):
        self._record("log", msg)

    def round_trips(self):
        return [c for c in self.calls if c[0] in ("use", "get", "container")]


class TestLooter(unittest.TestCase):
    def looter(self, listings, **args_over):
        uo = FakeClient(listings)
        lt = melee.Looter(uo, make_args(**args_over))
        return uo, lt

    def drain(self, uo, lt, corpse_item, limit=200):
        """Run steps until the corpse is finished, jumping the clock past each cooldown."""
        lt.enqueue(corpse_item)
        now, results = 0.0, []
        for _ in range(limit):
            results.append(lt.step(now))
            if corpse_item.serial in lt.done or corpse_item.serial in lt.unreachable:
                break
            now = max(now, lt.next_action)
        return results

    def test_waiting_costs_nothing(self):
        uo, lt = self.looter({"0xC1": [[]]})
        lt.enqueue(corpse())
        lt.step(0.0)                       # "picked"
        lt.step(0.0)                       # "opened" - sets a cooldown
        before = len(uo.round_trips())
        self.assertEqual(lt.step(0.1), "waiting")
        self.assertEqual(len(uo.round_trips()), before, "a waiting step hit the server")

    def test_at_most_one_round_trip_per_step(self):
        uo, lt = self.looter({"0xC1": [[thing("0x1")], [thing("0x1")], []]})
        now, seen = 0.0, 0
        for _ in range(20):
            lt.step(now)
            self.assertLessEqual(len(uo.round_trips()) - seen, 1, "a step did two round trips")
            seen = len(uo.round_trips())
            now = max(now, lt.next_action)

    def test_drains_a_flat_corpse(self):
        uo, lt = self.looter({"0xC1": [[thing("0x1", "gold")], []]})
        self.drain(uo, lt, corpse())
        self.assertIn("0xC1", lt.done)
        self.assertIn(("get", "0x1"), uo.calls)

    def test_nested_bag_is_opened_never_taken(self):
        uo, lt = self.looter({
            "0xC1": [[thing("0xB", "a bag", container=True)],
                     [thing("0xB", "a bag", container=True)], []],
            "0xB": [[thing("0x2", "gold")], []],
        })
        self.drain(uo, lt, corpse())
        self.assertIn("0xC1", lt.done)
        self.assertIn(("use", "0xB"), uo.calls)
        self.assertIn(("get", "0x2"), uo.calls)
        self.assertNotIn(("get", "0xB"), uo.calls, "tried to pick up a container")

    def test_get_is_never_called_with_a_container_serial(self):
        uo, lt = self.looter({
            "0xC1": [[thing("0xB", "a bag", container=True)],
                     [thing("0xB", "a bag", container=True)], []],
            "0xB": [[]],
        })
        self.drain(uo, lt, corpse())
        self.assertNotIn("0xB", [c[1] for c in uo.calls if c[0] == "get"])

    def test_bag_at_the_depth_bound_is_drained(self):
        uo, lt = self.looter({}, max_loot_depth=3)
        # Three levels of nesting below the corpse: 1, 2 and 3 all get drained.
        uo.listings = {
            "0xC1": [[thing("0xB1", "bag1", container=True)]] * 2 + [[]],
            "0xB1": [[thing("0xB2", "bag2", container=True)]] * 2 + [[]],
            "0xB2": [[thing("0xB3", "bag3", container=True)]] * 2 + [[]],
            "0xB3": [[thing("0x9", "gold")], []],
        }
        self.drain(uo, lt, corpse())
        self.assertIn(("get", "0x9"), uo.calls, "did not reach the item at depth 3")

    def test_bag_below_the_depth_bound_is_skipped(self):
        uo, lt = self.looter({}, max_loot_depth=2)
        uo.listings = {
            "0xC1": [[thing("0xB1", "bag1", container=True)]] * 2 + [[]],
            "0xB1": [[thing("0xB2", "bag2", container=True)]] * 2 + [[]],
            "0xB2": [[thing("0xB3", "bag3", container=True)]] * 2 + [[]],
            "0xB3": [[thing("0x9", "gold")], []],
        }
        self.drain(uo, lt, corpse())
        self.assertNotIn(("get", "0x9"), uo.calls, "descended past --max-loot-depth")
        self.assertIn("0xB3", lt.gave_up)

    def test_immovable_item_is_abandoned_after_three_gets(self):
        uo, lt = self.looter({"0xC1": [[thing("0x1", "stuck")]]})   # never empties
        self.drain(uo, lt, corpse(), limit=60)
        gets = [c for c in uo.calls if c == ("get", "0x1")]
        self.assertEqual(len(gets), melee.MAX_GET_ATTEMPTS)
        self.assertIn("0x1", lt.gave_up)

    def test_corpse_timeout_gives_up(self):
        uo, lt = self.looter({"0xC1": [[thing("0x1", "stuck")]]}, loot_corpse_timeout=5.0)
        lt.enqueue(corpse())
        lt.step(0.0)
        lt.step(0.0)
        lt.step(100.0)
        self.assertIn("0xC1", lt.unreachable)

    def test_resumes_mid_corpse_without_reopening(self):
        uo, lt = self.looter({"0xC1": [[thing("0x1")], [thing("0x1")], []]})
        lt.enqueue(corpse())
        lt.step(0.0)
        lt.step(0.0)                            # "opened"
        opens = [c for c in uo.calls if c == ("use", "0xC1")]
        lt.step(lt.next_action)                 # a state change in between touches nothing
        self.assertEqual([c for c in uo.calls if c == ("use", "0xC1")], opens,
                         "reopened the corpse after a state change")

    def test_a_looted_corpse_is_never_requeued(self):
        uo, lt = self.looter({"0xC1": [[]]})
        c = corpse()
        self.drain(uo, lt, c)
        self.assertIn("0xC1", lt.done)
        self.assertFalse(lt.enqueue(c))
        self.assertFalse(lt.has_work())


class FakeNav:
    """Minimal Client stand-in for do_escape: records gotos and reports a walk in flight."""

    def __init__(self):
        self.gotos = []
        self.nav_active = True
        self.logs = []

    @property
    def nav(self):
        return SimpleNamespace(active=self.nav_active, status="walking")

    def goto(self, x, y):
        self.gotos.append((x, y))
        self.nav_active = True

    def cancel_walk(self):
        self.nav_active = False

    def log(self, message):
        self.logs.append(message)


class TestAggroPursuitRange(unittest.TestCase):
    """An avoid-aggro escape has to keep running while the chaser is still visible.

    The bug this pins: with one pursuit range for every escape, a chaser that matches the
    character's speed re-closes to just inside 10 tiles, the escape un-latches, runs a little,
    latches again, and repeats forever. Observed live at 57 tiles out and still being chased, and
    it is how a poisoned character died - the flight never ended so the poison never stopped.
    """

    def test_avoid_aggro_uses_the_wider_range(self):
        args = make_args(pursuit_range=10, aggro_pursuit_range=19)
        self.assertEqual(melee.pursuit_range_for(args, "avoid-aggro"), 19)

    def test_other_escapes_keep_the_cheap_range(self):
        args = make_args(pursuit_range=10, aggro_pursuit_range=19)
        for reason in ("hp", "swarm", "avoid-near", None):
            self.assertEqual(melee.pursuit_range_for(args, reason), 10, reason)

    def test_default_is_beyond_the_world_files_view(self):
        """>18 is what makes the rule 'run while anything hostile is in view at all'."""
        self.assertGreater(make_args().aggro_pursuit_range, 18)

    def test_a_chaser_at_twelve_tiles_does_not_end_an_aggro_escape(self):
        """12 tiles is clear of the old 10 but still in view - the case that used to oscillate."""
        args = make_args(pursuit_range=10, aggro_pursuit_range=19, escape_min_dwell=0,
                         aggro_flee_distance=40, aggro_range=12, escape_hysteresis=2)
        # Deliberately not avoid-listed, so `avoided_aggro` cannot be what holds the escape open -
        # the pursuit range has to be the only thing doing it.
        chaser = mob("0xW", "a target", 12, 0, war=False)
        t = make_threats(me=(0, 0), mobiles=[chaser])
        st = SimpleNamespace(
            escape=melee.Escape("avoid-aggro", 40, 0.0, (-60, 0), origin=(-60, 0)))

        self.assertFalse(melee.resumed(args, t, st))

    def test_the_same_chaser_would_have_ended_a_swarm_escape(self):
        """Same distance, different reason: the cheap range is still 10, so 12 tiles is clear."""
        args = make_args(pursuit_range=10, aggro_pursuit_range=19, escape_min_dwell=0,
                         swarm_flee_distance=6, aggro_range=12, escape_hysteresis=2)
        rat = mob("0xR", "a target", 12, 0, war=False)
        t = make_threats(me=(0, 0), mobiles=[rat])
        st = SimpleNamespace(
            escape=melee.Escape("swarm", 6, 0.0, (-60, 0), origin=(-60, 0)))

        self.assertTrue(melee.resumed(args, t, st))


class TestEscapeAimIsLatched(unittest.TestCase):
    """The flee destination must not be recomputed every poll.

    `pick_escape_point` measures from the character's *current* position, so while walking it
    returns a different point every 0.4s iteration. `Mover` only suppresses a re-issue when the
    point is unchanged, so nothing suppressed it: each tick fired a fresh `goto` that cancelled the
    walk in flight, and the flight became a few tiles per leg. With a pursuer circling, the
    away-direction flipped too, so those few tiles alternated and cancelled out.
    """

    def _escaping(self, **over):
        args = make_args(escape_reaim=6.0, escape_min_dwell=0, escape_timeout=999,
                         aggro_flee_distance=25, **over)
        st = melee.Brain(mover=melee.Mover(FakeNav()), looter=None, avoid=[], include=[])
        st.escape = melee.Escape("avoid-aggro", 25, 0.0, (0, 0), origin=(0, 0))
        return args, st

    def test_aim_point_is_kept_while_the_walk_is_in_flight(self):
        args, st = self._escaping()
        uo = st.mover.uo
        chaser = mob("0xC", "a chaser", -3, 0, war=True)

        melee.do_escape(uo, args, make_threats(me=(0, 0), mobiles=[chaser], now=1.0), st)
        first = st.escape.aim_point
        self.assertIsNotNone(first)
        self.assertEqual(len(uo.gotos), 1)

        # Two more polls, character has moved, walk still active - must not re-aim or re-issue.
        for step, now in ((1, 1.4), (2, 1.8)):
            melee.do_escape(uo, args,
                            make_threats(me=(step, 0), mobiles=[chaser], now=now), st)

        self.assertEqual(st.escape.aim_point, first, "destination was recomputed mid-flight")
        self.assertEqual(len(uo.gotos), 1, "goto was re-issued, cancelling the walk in flight")

    def test_it_re_aims_once_the_walk_ends(self):
        args, st = self._escaping()
        uo = st.mover.uo
        chaser = mob("0xC", "a chaser", -3, 0, war=True)

        melee.do_escape(uo, args, make_threats(me=(0, 0), mobiles=[chaser], now=1.0), st)
        uo.nav_active = False                       # arrived / blocked / nopath

        melee.do_escape(uo, args, make_threats(me=(5, 0), mobiles=[chaser], now=1.4), st)

        self.assertEqual(len(uo.gotos), 2)

    def test_it_re_aims_after_escape_reaim_seconds(self):
        """The adaptation path: a pursuer that cuts us off must not be run into forever."""
        args, st = self._escaping()
        uo = st.mover.uo
        chaser = mob("0xC", "a chaser", -3, 0, war=True)

        melee.do_escape(uo, args, make_threats(me=(0, 0), mobiles=[chaser], now=1.0), st)
        melee.do_escape(uo, args, make_threats(me=(5, 0), mobiles=[chaser], now=1.0 + 6.5), st)

        self.assertEqual(len(uo.gotos), 2)
        self.assertAlmostEqual(st.escape.aimed_at, 7.5)


class TestEscapeEscalation(unittest.TestCase):
    """aggro -> run far -> still pursued -> run further, with both loops bounded.

    A chaser that matches the character's speed is never shed by repeating the clearance that
    already failed; the escape just settles into a stable orbit at whatever range the first attempt
    reached. Escalating on each re-engagement is what eventually out-lasts it.
    """

    def _held_escape(self, **over):
        defaults = dict(escape_escalation=15, escape_max_distance=100, escape_max_seconds=120,
                        escape_reaim=6.0, escape_min_dwell=0, escape_timeout=999,
                        aggro_pursuit_range=19)
        defaults.update(over)
        args = make_args(**defaults)
        st = melee.Brain(mover=melee.Mover(FakeNav()), looter=None, avoid=[], include=[])
        st.escape = melee.Escape("avoid-aggro", 25, 0.0, (0, 0), origin=(0, 0), origin_at=0.0)
        st.escape.holding = True          # we thought we had shed it
        return args, st

    def test_target_grows_when_a_pursuer_comes_back(self):
        args, st = self._held_escape()
        chaser = mob("0xC", "a chaser", 8, 0, war=True)

        melee.do_escape(st.mover.uo, args,
                        make_threats(me=(0, 0), mobiles=[chaser], now=10.0), st)

        self.assertEqual(st.escape.distance, 40)      # 25 + 15

    def test_it_keeps_growing_across_re_engagements(self):
        args, st = self._held_escape()
        chaser = mob("0xC", "a chaser", 8, 0, war=True)

        for now in (10.0, 20.0, 30.0):
            st.escape.holding = True
            melee.do_escape(st.mover.uo, args,
                            make_threats(me=(0, 0), mobiles=[chaser], now=now), st)

        self.assertEqual(st.escape.distance, 70)      # 25 + 15*3

    def test_distance_is_capped(self):
        """Guard one: a chaser that never gives up cannot walk us off the edge of the world."""
        # The time bound is lifted here so this isolates the *distance* cap. Left at its default
        # the run would legitimately end partway through the loop, because a give-up now actually
        # releases the escape - see `give_up_escape` and the tests below.
        args, st = self._held_escape(escape_max_distance=50, escape_max_seconds=10_000)
        chaser = mob("0xC", "a chaser", 8, 0, war=True)

        for now in range(10, 200, 10):
            st.escape.holding = True
            melee.do_escape(st.mover.uo, args,
                            make_threats(me=(0, 0), mobiles=[chaser], now=float(now)), st)

        self.assertEqual(st.escape.distance, 50)

    def test_the_flight_gives_up_after_escape_max_seconds(self):
        """Guard two: an unshakeable pursuer must not become an infinite flight."""
        args, st = self._held_escape(escape_max_seconds=60)
        chaser = mob("0xC", "a chaser", 8, 0, war=True)

        melee.do_escape(st.mover.uo, args,
                        make_threats(me=(0, 0), mobiles=[chaser], now=61.0), st)

        self.assertGreater(st.escape_suppressed_until, 61.0,
                           "escape should hand over rather than run forever")

    def test_giving_up_releases_the_latch_so_kill_gets_its_turn(self):
        """The bug this pair of tests exists for.

        Suppression alone only silences *new* escape reasons; `decide_state` rule 5 re-selected the
        escape still sitting on `st.escape`, so the give-up branches logged their intent and
        changed nothing. Cornered, that was a livelock - the character stood still, neither
        fleeing nor fighting, until something killed it.
        """
        args, st = self._held_escape(escape_max_seconds=60)
        chaser = mob("0xC", "a chaser", 8, 0, war=True)
        t = make_threats(me=(0, 0), mobiles=[chaser], now=61.0)

        melee.do_escape(st.mover.uo, args, t, st)

        self.assertIsNone(st.escape, "the give-up must clear the latch, not just suppress")
        st.looter = FakeLooter()          # decide_state consults it once escape is out of the way
        self.assertEqual(melee.decide_state(args, t, st)[0], melee.KILL)

    def test_repeated_give_ups_hand_off_instead_of_cycling(self):
        """Turning around is the first answer; if it keeps failing, it is the orchestrator's call."""
        args, st = self._held_escape(escape_max_seconds=60, escape_giveup_limit=2)
        chaser = mob("0xC", "a chaser", 8, 0, war=True)

        for i in range(2):
            st.escape = melee.Escape("avoid-aggro", 25, 0.0, (0, 0), origin=(0, 0), origin_at=0.0)
            melee.do_escape(st.mover.uo, args,
                            make_threats(me=(0, 0), mobiles=[chaser], now=61.0 + i), st)

        self.assertIsNotNone(st.finish, "second give-up should ask main to hand off")
        self.assertEqual(st.finish[1], 1)
        self.assertIn("handing off", st.finish[0])

    def test_an_hp_escape_that_never_heals_hands_off(self):
        """An HP escape waits on autoheal, which never ends if nothing is healing."""
        args, st = self._held_escape(hp_stall_secs=30.0)
        st.escape = melee.Escape("hp", 25, 0.0, (0, 0), origin=(0, 0), origin_at=0.0)
        chaser = mob("0xC", "a chaser", 8, 0, war=True)

        # HP never improves across the whole window.
        for now in (10.0, 20.0, 30.0, 45.0):
            melee.do_escape(st.mover.uo, args,
                            make_threats(me=(0, 0), hp_pct=30.0, mobiles=[chaser], now=now), st)

        self.assertIsNotNone(st.finish, "a stalled HP escape must hand off, not wait forever")
        self.assertIn("stalled", st.finish[0])

    def test_an_hp_escape_that_is_healing_keeps_waiting(self):
        """Progress restarts the stall clock - a slow heal is not a stall."""
        args, st = self._held_escape(hp_stall_secs=30.0)
        st.escape = melee.Escape("hp", 25, 0.0, (0, 0), origin=(0, 0), origin_at=0.0)
        chaser = mob("0xC", "a chaser", 8, 0, war=True)

        for now, hp in ((10.0, 30.0), (25.0, 40.0), (45.0, 50.0), (60.0, 60.0)):
            melee.do_escape(st.mover.uo, args,
                            make_threats(me=(0, 0), hp_pct=hp, mobiles=[chaser], now=now), st)

        self.assertIsNone(st.finish, "HP is climbing - this is not a stall")

    def test_an_hp_escape_is_exempt_from_the_time_bound(self):
        """Its end condition is autoheal restoring HP, not a clock."""
        args, st = self._held_escape(escape_max_seconds=60)
        st.escape = melee.Escape("hp", 25, 0.0, (0, 0), origin=(0, 0), origin_at=0.0)
        chaser = mob("0xC", "a chaser", 8, 0, war=True)

        melee.do_escape(st.mover.uo, args,
                        make_threats(me=(0, 0), mobiles=[chaser], hp_pct=30.0, now=61.0), st)

        self.assertEqual(st.escape_suppressed_until, 0.0)



class TrackEscapedThreatTests(unittest.TestCase):
    """The escape has to capture where the thing was *while it can still see it*."""

    def test_prefers_the_one_actually_chasing_us(self):
        e = melee.Escape("avoid-aggro", 25, 0.0, (0, 0))
        chaser = mob("0xC", "a chaser", 6, 0, war=True)
        bystander = mob("0xB", "a hostile", 2, 0, war=False)
        t = make_threats(me=(0, 0), mobiles=[chaser, bystander],
                         avoided=[chaser, bystander])

        melee.track_escaped_threat(t, e)

        self.assertEqual(e.threat.name, "a chaser")
        self.assertEqual(e.threat.pos, (6, 0))

    def test_falls_back_to_the_nearest_avoided_when_none_is_aggrod(self):
        e = melee.Escape("avoid-near", 15, 0.0, (0, 0))
        near = mob("0xN", "a lurker", 3, 0)
        far = mob("0xF", "a hostile", 9, 0)
        t = make_threats(me=(0, 0), mobiles=[near, far], avoided=[near, far])

        melee.track_escaped_threat(t, e)

        self.assertEqual((e.threat.name, e.threat.pos), ("a lurker", (3, 0)))

    def test_a_swarm_escape_with_nothing_avoided_records_nothing(self):
        e = melee.Escape("swarm", 6, 0.0, (0, 0))

        melee.track_escaped_threat(t := make_threats(me=(0, 0)), e)

        self.assertIsNone(e.threat.pos)
        self.assertIsNone(t.avoided or None)

    def test_the_last_sighting_of_that_serial_wins(self):
        e = melee.Escape("avoid-aggro", 25, 0.0, (0, 0))
        for x in (4, 7, 11):
            chaser = mob("0xC", "a chaser", x, 0, war=True)
            melee.track_escaped_threat(
                make_threats(me=(0, 0), mobiles=[chaser], avoided=[chaser]), e)

        self.assertEqual(e.threat.pos, (11, 0))

    def test_locks_on_and_ignores_creatures_merely_run_past(self):
        """The trigger is what blocks the return trip; a bystander passed en route is not."""
        e = melee.Escape("avoid-aggro", 25, 0.0, (0, 0))
        trigger = mob("0xT", "a lurker", 3, 0, war=True)
        melee.track_escaped_threat(
            make_threats(me=(0, 0), mobiles=[trigger], avoided=[trigger]), e)

        # Fled 40 tiles and ran past something else entirely; the trigger is out of view.
        bystander = mob("0xB", "a hostile", 41, 0, war=True)
        melee.track_escaped_threat(
            make_threats(me=(40, 0), mobiles=[bystander], avoided=[bystander]), e)

        self.assertEqual((e.threat.serial, e.threat.name), ("0xT", "a lurker"))
        self.assertEqual(e.threat.pos, (3, 0))

    def test_the_locked_creature_still_updates_while_it_chases(self):
        """Locked on the nearest at the moment of the escape, then followed - even once a
        different avoided creature is closer than it is."""
        e = melee.Escape("avoid-aggro", 25, 0.0, (0, 0))
        trigger = mob("0xT", "a lurker", 2, 0, war=True)
        melee.track_escaped_threat(
            make_threats(me=(0, 0), mobiles=[trigger], avoided=[trigger]), e)
        self.assertEqual(e.threat.serial, "0xT")

        for x in (9, 16):
            chaser = mob("0xT", "a lurker", x, 0, war=True)
            nearer = mob("0xB", "a hostile", 1, 1, war=True)
            melee.track_escaped_threat(
                make_threats(me=(0, 0), mobiles=[chaser, nearer], avoided=[chaser, nearer]), e)

        self.assertEqual(e.threat.serial, "0xT")
        self.assertEqual(e.threat.pos, (16, 0))


class HandOffTests(unittest.TestCase):
    """Escaping and then resuming is what walks the character back into the thing it fled."""

    class FakeClient:
        def __init__(self):
            self.logs = []
            self.stopped = None

        def log(self, msg):
            self.logs.append(msg)

        def stop(self, msg, code=0):
            self.stopped = (msg, code)
            return code

    def _escape(self, **over):
        e = melee.Escape("avoid-aggro", 25, 0.0, (0, 0))
        e.threat.name, e.threat.pos = "a chaser", (170, 156)
        for k, v in over.items():
            setattr(e, k, v)
        return e

    def test_reports_both_coordinates_and_stops(self):
        uo = self.FakeClient()
        args = make_args()

        code = melee.hand_off(uo, args, make_threats(me=(130, 150)), self._escape())

        line = "\n".join(uo.logs)
        self.assertIn("HANDOFF", line)
        self.assertIn("a chaser", line)
        self.assertIn("(170, 156)", line)      # where the threat was
        self.assertIn("(130, 150)", line)      # where we are
        self.assertEqual(code, 1)
        self.assertIsNotNone(uo.stopped)

    def test_says_so_rather_than_inventing_a_coordinate(self):
        uo = self.FakeClient()
        args = make_args()

        blank = melee.Escape("avoid-aggro", 25, 0.0, (0, 0))   # never saw anything
        melee.hand_off(uo, args, make_threats(me=(0, 0)), blank)

        self.assertIn("unknown", "\n".join(uo.logs))




class HpOverrideKeepsTheAvoidEscapesMemoryTests(unittest.TestCase):
    """HP pre-empting an avoid escape must not forget what it was running from or that clearing
    means handing off - otherwise it resumes KILL straight back toward the creature it fled."""

    def _upgraded(self):
        uo, args = HandOffTests.FakeClient(), make_args()
        st = make_brain(state=melee.ESCAPE,
                        escape=melee.Escape("avoid-aggro", 40, 0.0, (0, 0), origin=(0, 0)))
        st.escape.threat.saw("0xC", "a chaser", (3, 0), 1.0)
        st.escape.excluded.add((0, -1))
        melee.update_escape(uo, args, make_threats(me=(10, 0), hp_pct=30.0, now=5.0),
                            st, melee.ESCAPE, "hp")
        return uo, args, st

    def test_keeps_threat_memory_and_exclusions(self):
        _, _, st = self._upgraded()
        self.assertEqual(st.escape.reason, "hp")
        self.assertEqual(st.escape.threat.serial, "0xC")
        self.assertIn((0, -1), st.escape.excluded)
        self.assertEqual(st.escape.origin, (0, 0))
        self.assertEqual(st.escape.distance, 40)     # never downgraded

    def test_still_hands_off_when_it_clears(self):
        uo, args, st = self._upgraded()
        code = melee.update_escape(uo, args, make_threats(me=(60, 0), now=99.0), st,
                                   melee.KILL, None)
        self.assertEqual(code, 1)
        self.assertIsNotNone(uo.stopped)
        self.assertTrue(any("HANDOFF" in l for l in uo.logs))

    def test_a_plain_hp_escape_resumes_without_handoff(self):
        uo, args = HandOffTests.FakeClient(), make_args()
        st = make_brain(state=melee.ESCAPE, escape=melee.Escape("hp", 25, 0.0, (0, 0)))
        code = melee.update_escape(uo, args, make_threats(me=(60, 0), now=99.0), st,
                                   melee.KILL, None)
        self.assertIsNone(code)
        self.assertIsNone(st.escape)
        self.assertIsNone(uo.stopped)


class EscapeDoesNotEndTooEarlyTests(unittest.TestCase):
    """The three gates between "the screen went quiet" and "hand the decision back".

    All three come from one death: an avoid escape from a stalker cleared the instant the mobile
    crossed the 18-tile edge of the world file, handed off, every mover stood down, and the
    stalker - which had never stopped following - walked back in and killed a stationary character
    at 43% HP before the orchestrator could react.
    """

    def _clear_of_everything(self, **over):
        """Distances and dwell all satisfied, so only the gate under test can hold the escape."""
        defaults = dict(escape_min_dwell=0, aggro_flee_distance=40, pursuit_range=10,
                        aggro_pursuit_range=19, aggro_range=12, escape_hysteresis=2,
                        resume_hp_pct=80.0, threat_clear_secs=15.0)
        defaults.update(over)
        args = make_args(**defaults)
        e = melee.Escape("avoid-aggro", 40, 0.0, (-60, 0), origin=(-60, 0))
        return args, e

    def test_out_of_view_is_not_yet_lost(self):
        args, e = self._clear_of_everything()
        e.threat.saw("0xS", "a chaser", (0, 0), 100.0)       # seen 5s ago
        st = SimpleNamespace(escape=e)

        self.assertFalse(melee.resumed(args, make_threats(me=(0, 0), now=105.0), st))

    def test_and_is_lost_once_the_window_passes(self):
        args, e = self._clear_of_everything()
        e.threat.saw("0xS", "a chaser", (0, 0), 100.0)
        st = SimpleNamespace(escape=e)

        self.assertTrue(melee.resumed(args, make_threats(me=(0, 0), now=116.0), st))

    def test_the_window_is_measured_from_the_last_sighting_not_the_escape_start(self):
        """A pursuer flickering in and out of view resets the clock rather than accruing credit."""
        args, e = self._clear_of_everything()
        e.threat.saw("0xS", "a chaser", (0, 0), 100.0)
        st = SimpleNamespace(escape=e)

        e.threat.seen_at = 100.0
        self.assertTrue(melee.resumed(args, make_threats(me=(0, 0), now=116.0), st))
        e.threat.seen_at = 114.0                              # seen again at the view edge
        self.assertFalse(melee.resumed(args, make_threats(me=(0, 0), now=116.0), st))

    def test_a_hurt_character_keeps_escaping_rather_than_stopping_to_ask(self):
        """Handing off stops every mover; doing that at low HP is how the character died."""
        args, e = self._clear_of_everything()
        e.threat.seen_at = 0.0                                # nothing ever seen - window moot
        st = SimpleNamespace(escape=e)

        self.assertFalse(
            melee.resumed(args, make_threats(me=(0, 0), hp_pct=43.0, now=200.0), st))
        self.assertTrue(
            melee.resumed(args, make_threats(me=(0, 0), hp_pct=85.0, now=200.0), st))


class KeepsMovingWhileConfirmingTests(unittest.TestCase):
    """During the confirmation window the character runs; it does not stand still and scan."""

    def _args(self, **over):
        defaults = dict(escape_escalation=15, escape_max_distance=100, escape_max_seconds=120,
                        escape_reaim=6.0, escape_min_dwell=0, escape_timeout=999,
                        pursuit_range=10, aggro_pursuit_range=19, threat_clear_secs=15.0)
        defaults.update(over)
        return make_args(**defaults)

    def _brain(self, seen_at):
        st = melee.Brain(mover=melee.Mover(FakeNav()), looter=None, avoid=[], include=[])
        st.escape = melee.Escape("avoid-aggro", 25, 0.0, (0, 0), origin=(0, 0), origin_at=0.0)
        st.escape.threat.saw("0xS", "a chaser", (0, 0), seen_at)
        return st

    def test_does_not_latch_holding_while_the_window_is_open(self):
        args = self._args()
        st = self._brain(seen_at=100.0)                       # 5s ago

        melee.do_escape(st.mover.uo, args,
                        make_threats(me=(60, 0), now=105.0), st)   # screen empty, 60 tiles flown

        self.assertFalse(st.escape.holding)

    def test_holds_once_the_window_has_passed(self):
        args = self._args()
        st = self._brain(seen_at=100.0)

        melee.do_escape(st.mover.uo, args,
                        make_threats(me=(60, 0), now=116.0), st)

        self.assertTrue(st.escape.holding)




class DoesNotReturnToAnAnchorAnAvoidedCreatureIsOnTests(unittest.TestCase):
    """Returning to the anchor is right after a swarm break, and wrong once something we refuse
    to fight has walked into it.

    Measured live: `escape (swarm) clear - resuming`, then `returning to (138,148)` twice, then
    `kill -> escape (avoid-aggro)` - the character walked 13 tiles back into a pursuer that had
    been visible at 12 tiles the whole time. Dropping the anchor when the avoid escape *starts* is
    too late; by then it is already standing next to the thing.
    """

    class FakeClient:
        def __init__(self):
            self.logs = []
            self.stopped = None

        def log(self, msg):
            self.logs.append(msg)

        def stop(self, reason, code=0):
            self.stopped = (reason, code)
            return code

        def in_combat(self, within=0.0):
            return False

        def war(self, *_a, **_k):
            pass

        def attack(self, *_a, **_k):
            pass

        @property
        def combat(self):
            return SimpleNamespace(since_attacked=999.0, last_attacker_name=None)

        def nearest(self, **_k):
            return None

        @property
        def last_attack(self):
            return SimpleNamespace(is_set=False, serial=None, exists=False)

    def _args(self, **over):
        defaults = dict(acquire_range=12, return_to_fight=True,
                        attack_interval=1.0, avoid_range=5, avoid_idle_range=2,
                        avoid_memory_secs=90.0, avoid_memory_range=15)
        defaults.update(over)
        return make_args(**defaults)

    def _brain(self, anchor=(100, 100)):
        st = melee.Brain(mover=melee.Mover(FakeNav()), looter=FakeLooter(),
                         avoid=[], include=[parse_term("any:target")])
        st.anchor = anchor
        return st

    def test_abandons_the_anchor(self):
        uo, args, st = self.FakeClient(), self._args(), self._brain(anchor=(100, 100))
        st.avoid_seen["0xR"] = ("a pursuer", (104, 103), 100.0)

        melee.do_kill(uo, args, make_threats(me=(0, 0), now=140.0), st)

        self.assertIsNone(st.anchor)
        self.assertTrue(any("not returning to" in l for l in uo.logs), uo.logs)

    def test_keeps_an_anchor_far_from_the_sighting(self):
        uo, args, st = self.FakeClient(), self._args(), self._brain(anchor=(100, 100))
        st.avoid_seen["0xR"] = ("a pursuer", (140, 140), 100.0)

        melee.do_kill(uo, args, make_threats(me=(0, 0), now=140.0), st)

        self.assertEqual(st.anchor, (100, 100))

    def test_a_stale_sighting_stops_counting(self):
        """It wanders - a minutes-old position is not where it is now."""
        uo, args, st = self.FakeClient(), self._args(), self._brain(anchor=(100, 100))
        st.avoid_seen["0xR"] = ("a pursuer", (104, 103), 100.0)

        melee.do_kill(uo, args, make_threats(me=(0, 0), now=100.0 + 91.0), st)

        self.assertEqual(st.anchor, (100, 100))




class EmptyGroundExitTests(unittest.TestCase):
    """Empty ground is the one thing the reflex can see that the orchestrator cannot.

    The reflex keeps a running list of what it wants to fight (`Brain.wanted`, maintained by
    `update_wanted` every iteration). When that list is empty and nothing is engaged, it hands
    off at once - there is no combat grace and no empty timer to wait out, because every second
    of that was a second standing still (measured 2026-09-11 at a fenced zombie pen).
    """

    class FakeClient:
        def __init__(self):
            self.logs = []
            self.stopped = None

        def log(self, msg):
            self.logs.append(msg)

        def stop(self, reason, code=0):
            self.stopped = (reason, code)
            return code

        def war(self, *_a, **_k):
            pass

        def attack(self, *_a, **_k):
            pass

        @property
        def combat(self):
            return SimpleNamespace(since_attacked=999.0, last_attacker_name=None)

        def nearest(self, **_k):
            return None

        @property
        def last_attack(self):
            return SimpleNamespace(is_set=False, serial=None, exists=False)

    def _args(self, **over):
        defaults = dict(acquire_range=12, return_to_fight=True,
                        attack_interval=1.0, avoid_range=5, avoid_idle_range=2,
                        avoid_memory_secs=90.0, avoid_memory_range=15)
        defaults.update(over)
        return make_args(**defaults)

    def _brain(self):
        st = melee.Brain(mover=melee.Mover(FakeNav()), looter=FakeLooter(),
                         avoid=[], include=[parse_term("any:target")])
        st.anchor = None
        return st

    def _tick(self, uo, args, st, t):
        """One main-loop iteration's worth: refresh the wanted list, then dispatch KILL."""
        melee.update_wanted(uo, args, t, st)
        melee.do_kill(uo, args, t, st)

    def test_empty_view_hands_off_at_once(self):
        uo, args, st = self.FakeClient(), self._args(), self._brain()

        self._tick(uo, args, st, make_threats(me=(0, 0), now=100.0))

        self.assertIsNotNone(st.finish, "waited instead of handing off on an empty list")
        self.assertIn("nothing left to fight within", st.finish[0])

    def test_wanted_list_tracks_what_is_fightable_in_range(self):
        uo, args, st = self.FakeClient(), self._args(), self._brain()
        a, b = mob("0xA", "a target", 3, 0), mob("0xB", "a target", -4, 2)
        far = mob("0xF", "a target", 16, 0)                 # beyond acquire range
        hostile = mob("0xS", "a hostile", 2, 2)             # not on the allowlist

        melee.update_wanted(uo, args, make_threats(me=(0, 0), mobiles=[a, b, far, hostile],
                                                   now=100.0), st)

        self.assertEqual(set(st.wanted), {"0xA", "0xB"})
        self.assertTrue(any(m.startswith("wanted: +a target 0xA") for m in uo.logs))

    def test_a_kill_leaves_the_list_and_the_last_one_hands_off(self):
        uo, args, st = self.FakeClient(), self._args(), self._brain()
        a, b = mob("0xA", "a target", 3, 0), mob("0xB", "a target", -4, 2)

        self._tick(uo, args, st, make_threats(me=(0, 0), mobiles=[a, b], now=100.0))
        self.assertIsNone(st.finish)
        self._tick(uo, args, st, make_threats(me=(0, 0), mobiles=[b], now=101.0))   # a died
        self.assertEqual(set(st.wanted), {"0xB"})
        self.assertIsNone(st.finish)
        self.assertTrue(any("-a target 0xA gone" in m for m in uo.logs))

        self._tick(uo, args, st, make_threats(me=(0, 0), mobiles=[], now=102.0))    # b died
        self.assertEqual(st.wanted, {})
        self.assertIsNotNone(st.finish, "the last kill should hand off on the next iteration")

    def test_an_unreachable_target_leaves_the_list_and_is_reported(self):
        """The fenced-pen case: struck out on nopaths, blacklisted, and the moment the last one
        is, the run hands off saying the spawn cannot be walked to."""
        uo, args, st = self.FakeClient(), self._args(), self._brain()
        z = mob("0xZ", "a target", 6, 0)
        t = make_threats(me=(0, 0), mobiles=[z], now=100.0)

        self._tick(uo, args, st, t)
        self.assertIn("0xZ", st.wanted)
        st.drop_unreachable("0xZ", (6, 0), 100.0)

        self._tick(uo, args, st, make_threats(me=(0, 0), mobiles=[z], now=101.0))

        self.assertEqual(st.wanted, {})
        self.assertTrue(any("-a target 0xZ unreachable" in m for m in uo.logs))
        self.assertIsNotNone(st.finish)
        self.assertIn("UNREACHABLE", st.finish[0])
        self.assertEqual(st.finish[1], 1)

    def test_an_avoided_creature_standing_there_does_not_count_as_something_to_fight(self):
        """Otherwise the character parks beside the one thing it has decided never to attack."""
        uo, args, st = self.FakeClient(), self._args(), self._brain()   # include=["target"]
        hostile = mob("0xS", "a hostile", 3, 0)                             # not on the allowlist

        self._tick(uo, args, st, make_threats(me=(0, 0), mobiles=[hostile], avoided=[hostile],
                                              now=100.0))

        self.assertEqual(st.wanted, {})
        self.assertIsNotNone(st.finish)
        self.assertIn("avoided creature", st.finish[0])

    def test_something_we_would_fight_keeps_us_here(self):
        uo, args, st = self.FakeClient(), self._args(), self._brain()
        skel = mob("0xK", "a target", 5, 0)             # inside --acquire-range

        self._tick(uo, args, st, make_threats(me=(0, 0), mobiles=[skel], now=100.0))
        self._tick(uo, args, st, make_threats(me=(0, 0), mobiles=[skel], now=131.0))

        self.assertIsNone(st.finish)

    def test_a_target_outside_acquire_range_hands_off_and_says_where_it_is(self):
        """Ground that needs walking to is still ground this reflex cannot use.

        Acquisition never looks past --acquire-range and there is no anchor to walk back to, so
        counting a target at 16 tiles as "not empty" left the character standing still forever -
        observed live at a graveyard, a skeleton at 15 tiles and no report of any kind. Walking
        somewhere new is the orchestrator's call, so the handoff carries the distance it needs.
        """
        uo, args, st = self.FakeClient(), self._args(acquire_range=12), self._brain()
        far = mob("0xS", "a target", 16, 0)

        self._tick(uo, args, st, make_threats(me=(0, 0), mobiles=[far], now=100.0))

        self.assertIsNotNone(st.finish)
        self.assertIn("beyond acquire range", st.finish[0])
        self.assertIn("(16, 0)", st.finish[0])
        self.assertIn("16 tiles", st.finish[0])

    def test_a_wanted_target_that_wanders_out_of_range_is_dropped(self):
        uo, args, st = self.FakeClient(), self._args(acquire_range=12), self._brain()
        m = mob("0xM", "a target", 5, 0)

        self._tick(uo, args, st, make_threats(me=(0, 0), mobiles=[m], now=100.0))
        self.assertIn("0xM", st.wanted)
        m2 = mob("0xM", "a target", 15, 0)
        self._tick(uo, args, st, make_threats(me=(0, 0), mobiles=[m2], now=101.0))

        self.assertEqual(st.wanted, {})
        self.assertIsNotNone(st.finish)

    def test_optional_grace_delays_the_handoff(self):
        uo, args, st = self.FakeClient(), self._args(empty_exit_secs=10.0), self._brain()

        self._tick(uo, args, st, make_threats(me=(0, 0), now=100.0))
        self.assertIsNone(st.finish)
        self._tick(uo, args, st, make_threats(me=(0, 0), now=105.0))
        self.assertIsNone(st.finish)
        self._tick(uo, args, st, make_threats(me=(0, 0), now=111.0))
        self.assertIsNotNone(st.finish)

    def test_default_grace_is_zero(self):
        self.assertEqual(make_args().empty_exit_secs, 0.0)



if __name__ == "__main__":
    unittest.main(verbosity=2)
