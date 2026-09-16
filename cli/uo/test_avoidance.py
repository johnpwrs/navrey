#!/usr/bin/env python3
"""Tests for avoid/include term matching - name, species and body graphic.

    python3 -m unittest discover -s cli/uo -p 'test_*.py' -v
"""
import argparse
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from uo.avoidance import (  # noqa: E402
    add_avoid_args,
    is_avoided,
    parse_term,
    resolve_avoid_args,
)
from uo.entities import Mobile, Target  # noqa: E402


def mob(name="a target", species=None, graphic=0):
    return Mobile({"serial": "0x1", "name": name, "species": species, "graphic": graphic,
                   "x": 0, "y": 0, "z": 0, "distance": 1, "notoriety": "Criminal"})


def terms(*raw):
    return [parse_term(r) for r in raw]


# The three creatures the whole change is about: an orc that says so, an orc that doesn't, and the
# caster an `--include orc` allowlist is specifically meant to keep away from a no-Resist build.
PLAIN_ORC = mob("an orc", "Orc", 7)
NAMED_ORC = mob("Gruuk", "Orc", 7)
ORCISH_MAGE = mob("an orcish mage", "Orcish Mage", 140)


class BareTermTests(unittest.TestCase):
    """A bare term is a substring of the name OR the species - the default, and the only form
    anything normally needs to write."""

    def test_matches_on_name(self):
        self.assertTrue(is_avoided(PLAIN_ORC, terms("any:orc"), []))

    def test_matches_a_named_monster_on_species(self):
        """The regression this exists for: measured live 2026-08-31, a monster carrying a personal
        name matched nothing, so `--include <type>` treated its own target as an unknown and fled
        it repeatedly with no swing landing."""
        self.assertTrue(is_avoided(NAMED_ORC, terms("any:orc"), []))
        self.assertFalse(is_avoided(NAMED_ORC, [], terms("any:orc")))

    def test_is_case_insensitive_both_ways(self):
        self.assertTrue(is_avoided(NAMED_ORC, terms("any:ORC"), []))
        self.assertTrue(is_avoided(mob("GRUUK", "Orc", 7), terms("any:gruuk"), []))

    def test_unrelated_creature_is_not_matched(self):
        self.assertFalse(is_avoided(mob("a rabbit", "Rabbit", 205), terms("any:orc"), []))


class SpeciesTermTests(unittest.TestCase):
    def test_exact_species_excludes_the_subtype(self):
        """The second documented hole: a bare `orc` substring-matches `Orcish Mage`, so an
        allowlist whose whole point is keeping casters out admits one. `species:` is exact."""
        self.assertFalse(is_avoided(PLAIN_ORC, [], terms("species:orc")))
        self.assertTrue(is_avoided(ORCISH_MAGE, [], terms("species:orc")))

    def test_bare_term_admits_both_which_is_why_the_exact_form_exists(self):
        self.assertFalse(is_avoided(PLAIN_ORC, [], terms("any:orc")))
        self.assertFalse(is_avoided(ORCISH_MAGE, [], terms("any:orc")))

    def test_species_term_ignores_a_matching_name(self):
        self.assertFalse(is_avoided(mob("orc", None, 999), terms("species:orc"), []))


class NameTermTests(unittest.TestCase):
    def test_exact_name_does_not_catch_species_mates(self):
        self.assertTrue(is_avoided(NAMED_ORC, terms("name:gruuk"), []))
        self.assertFalse(is_avoided(PLAIN_ORC, terms("name:gruuk"), []))

    def test_is_exact_not_a_substring(self):
        self.assertFalse(is_avoided(NAMED_ORC, terms("name:gru"), []))


class GraphicTermTests(unittest.TestCase):
    def test_matches_whatever_the_name_is(self):
        for m in (PLAIN_ORC, NAMED_ORC):
            self.assertTrue(is_avoided(m, terms("graphic:7"), []))
            self.assertTrue(is_avoided(m, terms("graphic:0x07"), []))

    def test_distinguishes_the_subtype(self):
        self.assertFalse(is_avoided(ORCISH_MAGE, terms("graphic:7"), []))
        self.assertTrue(is_avoided(ORCISH_MAGE, terms("graphic:140"), []))
        self.assertTrue(is_avoided(ORCISH_MAGE, terms("graphic:0x8C"), []))

    def test_a_bare_number_stays_a_substring(self):
        """A creature name with digits is likelier than someone typing a decimal body id with no
        prefix, so only the unambiguous `0x` / `graphic:` forms mean an id."""
        self.assertFalse(is_avoided(ORCISH_MAGE, terms("any:140"), []))
        self.assertTrue(is_avoided(mob("golem 140", None, 3), terms("any:140"), []))


class MalformedTermTests(unittest.TestCase):
    """A bad term must fail at launch. Silently never matching would, under `--include`, avoid-list
    the entire world - which reads as the reflex refusing to fight, not as a bad argument."""

    def test_a_non_numeric_graphic_raises(self):
        with self.assertRaises(ValueError):
            parse_term("graphic:abc")

    def test_a_bare_term_raises_and_says_what_to_write(self):
        """The whole point of requiring a prefix: an unlabelled term is a guess about which
        identity was meant, and guessing wrong is silent in both directions."""
        for bare in ("orc", "140", "0x8C", "a skeleton"):
            with self.assertRaises(ValueError) as cm:
                parse_term(bare)
            msg = str(cm.exception)
            self.assertIn(f"species:{bare}", msg)
            self.assertIn("graphic:", msg)

    def test_an_empty_or_valueless_term_raises(self):
        for bad in ("", "   ", "species:", "graphic:", "any:"):
            with self.assertRaises(ValueError):
                parse_term(bad)


class TargetTests(unittest.TestCase):
    """A `Target` (state file lastAttack/lastCursorTarget) is matched by the same terms as a `Mobile`,
    so an already-engaged creature can be checked without re-finding it in the world file. It
    carries `species` for the same reason a mobile does - a target named "Gruuk" says nothing
    about what it is."""

    def tgt(self, **over):
        d = {"kind": "entity", "serial": "0x1", "name": "Gruuk", "species": "Orc",
             "graphic": 7, "x": 0, "y": 0, "z": 0, "exists": True}
        d.update(over)
        return Target(d)

    def test_matches_on_every_axis(self):
        t = self.tgt()
        for term in ("any:orc", "species:orc", "name:gruuk", "graphic:7", "graphic:0x07"):
            self.assertTrue(is_avoided(t, terms(term), []), term)

    def test_a_ground_target_has_no_species(self):
        """Terrain and statics have no body graphic, so species is null rather than whatever the
        mob table happens to hold at that id - the `Mobile`-only guard in StateFile.cs."""
        ground = Target({"kind": "static", "serial": None, "name": None, "species": None,
                         "graphic": 3111, "x": 1, "y": 2, "z": 0, "exists": True})
        self.assertFalse(is_avoided(ground, terms("any:orc"), []))
        self.assertTrue(is_avoided(ground, terms("graphic:3111"), []))

    def test_an_unset_target_matches_nothing(self):
        empty = Target({"kind": "none"})
        self.assertFalse(is_avoided(empty, terms("any:orc"), []))

    def test_a_vanished_target_still_matches_on_what_it_was(self):
        """`exists: false` keeps name/species/graphic from the last sighting, which is what lets a
        fight decide whether the thing it lost was avoid-listed."""
        gone = self.tgt(exists=False)
        self.assertTrue(is_avoided(gone, terms("species:orc"), []))


class TermRenderingTests(unittest.TestCase):
    """Both scripts print their term lists in the startup line that says what the run will and
    won't fight. A `Term` that doesn't render is a launch-time crash, not a cosmetic problem -
    which is how it was found, `', '.join(include)` raising TypeError with the client already up."""

    def test_renders_as_it_would_be_typed(self):
        for raw, shown in (("any:orc", "any:orc"), ("species:Orc", "species:orc"),
                           ("name:Gruuk", "name:gruuk"), ("graphic:7", "graphic:7"),
                           ("graphic:0x07", "graphic:7")):
            self.assertEqual(str(parse_term(raw)), shown)

    def test_a_list_of_terms_joins(self):
        self.assertEqual(", ".join(str(t) for t in terms("any:orc", "graphic:0x07")),
                         "any:orc, graphic:7")


class UnresolvedMobileTests(unittest.TestCase):
    """The table stops at graphic 403 and has gaps, so `species` can be null. The two opposite
    defaults for that case are unchanged from when matching was name-only."""

    UNKNOWN = mob("Zzyzx", None, 999)

    def test_permissive_under_avoid(self):
        self.assertFalse(is_avoided(self.UNKNOWN, terms("any:orc"), []))

    def test_conservative_under_include(self):
        self.assertTrue(is_avoided(self.UNKNOWN, [], terms("any:orc")))

    def test_no_lists_at_all_avoids_nothing(self):
        self.assertFalse(is_avoided(self.UNKNOWN, [], []))

    def test_an_empty_name_does_not_match_everything(self):
        """`name` is "" when absent while `species` is None - and `"" in anything` is True, so an
        unguarded substring check would make every term match every unnamed mobile."""
        unnamed = mob("", "Orc", 7)
        self.assertTrue(is_avoided(unnamed, terms("any:orc"), []))
        self.assertFalse(is_avoided(unnamed, terms("any:rabbit"), []))
        self.assertFalse(is_avoided(mob("", None, 999), terms("any:rabbit"), []))


class TargetMatchingTests(unittest.TestCase):
    """The engaged target is a `Target`, not a `Mobile`, and the combat scripts pass it straight to
    `is_avoided` to re-check the thing they are already fighting.

    These cases pin the record with **no** `species` key at all - a ground/static target, or a state
    file written by a client older than the field. `Target.species` must read as "no value" there,
    never raise: reading `.species` off a Target once raised AttributeError and killed the melee
    reflex one frame after it acquired a target, leaving the character standing in melee range with
    nothing swinging or fleeing. `TargetTests` covers the ordinary case where species is present.
    """

    def tgt(self, name="a zombie", graphic=3):
        return Target({"kind": "entity", "serial": "0x1", "name": name, "graphic": graphic,
                       "x": 0, "y": 0, "z": 0, "exists": True})

    def test_a_target_matches_by_name(self):
        self.assertTrue(is_avoided(self.tgt(), terms("any:zombie"), []))
        self.assertFalse(is_avoided(self.tgt(), terms("any:shade"), []))

    def test_a_target_matches_by_graphic(self):
        self.assertTrue(is_avoided(self.tgt(), terms("graphic:3"), []))
        self.assertFalse(is_avoided(self.tgt(), terms("graphic:4"), []))

    def test_a_species_term_does_not_match_a_speciesless_target(self):
        """Absent species must read as "no value", not as a crash and not as a match."""
        self.assertIsNone(self.tgt().species)
        self.assertFalse(is_avoided(self.tgt(), terms("species:zombie"), []))

    def test_include_mode_works_on_a_target(self):
        self.assertFalse(is_avoided(self.tgt(), [], terms("any:zombie")))
        self.assertTrue(is_avoided(self.tgt(), [], terms("any:skeleton")))


class ArgParsingTests(unittest.TestCase):
    def _parse(self, argv):
        ap = argparse.ArgumentParser()
        add_avoid_args(ap)
        return resolve_avoid_args(ap.parse_args(argv))

    def test_the_two_modes_are_mutually_exclusive(self):
        with self.assertRaises(ValueError):
            self._parse(["--avoid", "any:orc", "--include", "any:ratman"])

    def test_terms_are_parsed_once_at_startup(self):
        avoid, include = self._parse(["--avoid", "any:orc", "--avoid", "graphic:7"])
        self.assertEqual([t.kind for t in avoid], ["any", "graphic"])
        self.assertEqual(include, [])

    def test_a_malformed_term_fails_at_parse_time(self):
        with self.assertRaises(ValueError):
            self._parse(["--include", "graphic:abc"])


if __name__ == "__main__":
    unittest.main()
