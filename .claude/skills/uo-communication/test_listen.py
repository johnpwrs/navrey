#!/usr/bin/env python3
"""Parser tests for listen.py - the exclusions are the whole difficulty, so they are pinned here.

    python3 -m unittest discover -s .claude/skills/uo-communication -p 'test_*.py'
"""

import importlib.util
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "..", "cli"))

spec = importlib.util.spec_from_file_location("listen", os.path.join(HERE, "listen.py"))
listen = importlib.util.module_from_spec(spec)
sys.modules["listen"] = listen
spec.loader.exec_module(listen)

from uo.entities import Mobile                                          # noqa: E402


def mob(name, x=0, y=0, human=True, monster=False):
    return Mobile({"name": name, "x": x, "y": y, "distance": max(abs(x), abs(y)),
                   "isHuman": human, "knownMonster": monster, "serial": "0x1"})


class TestRealSpeech(unittest.TestCase):
    def test_plain_say(self):
        self.assertEqual(listen.parse_speech("randall flagg: well met friend"),
                         ("say", "randall flagg", "well met friend"))

    def test_yell(self):
        self.assertEqual(listen.parse_speech("[YELL] randall flagg: HELP"),
                         ("yell", "randall flagg", "HELP"))

    def test_whisper(self):
        self.assertEqual(listen.parse_speech("[whisper] randall flagg: psst"),
                         ("whisper", "randall flagg", "psst"))

    def test_emote_has_no_colon(self):
        self.assertEqual(listen.parse_speech("* randall flagg waves"),
                         ("emote", "randall", "flagg waves"))

    def test_text_containing_a_colon_survives(self):
        self.assertEqual(listen.parse_speech("randall flagg: note: bring bandages"),
                         ("say", "randall flagg", "note: bring bandages"))


class TestLabelledForms(unittest.TestCase):
    """Emitted by a client built after Narrator.cs learned to print the packet's MessageType.
    These need no exclusion guessing at all - the label IS the evidence."""

    def test_labelled_say(self):
        self.assertEqual(listen.parse_speech("[SAY] randall flagg: hello"),
                         ("say", "randall flagg", "hello"))

    def test_guild_channel(self):
        self.assertEqual(listen.parse_speech("[GUILD] randall flagg: regroup"),
                         ("guild", "randall flagg", "regroup"))

    def test_party_channel(self):
        self.assertEqual(listen.parse_speech("[PARTY] randall flagg: pulling"),
                         ("party", "randall flagg", "pulling"))

    def test_alliance_channel(self):
        self.assertEqual(listen.parse_speech("[ALLIANCE] randall flagg: hail"),
                         ("alliance", "randall flagg", "hail"))

    def test_spell_words_are_not_conversation(self):
        self.assertIsNone(listen.parse_speech("[SPELL] randall flagg: Vas Ort Flam"))

    def test_an_unnamed_message_kind_is_not_read_as_speech(self):
        self.assertIsNone(listen.parse_speech("[MSG:16] a gm: greetings"))

    def test_the_old_bare_form_still_works(self):
        """The running client predates the label, so both shapes must parse until it is rebuilt."""
        self.assertEqual(listen.parse_speech("randall flagg: hello"),
                         ("say", "randall flagg", "hello"))


class TestNoiseSpeakers(unittest.TestCase):
    """The narrator gives some server messages a speaker name instead of the [SYSTEM] prefix, so
    the prefix filter misses them. Caught live within seconds of first running the listener."""

    def test_system_as_a_speaker_name_is_excluded(self):
        parsed = listen.parse_speech("System: You finish applying the bandages.")
        self.assertIsNotNone(parsed, "should still parse as speech-shaped")
        self.assertIn(parsed[1].lower(), listen.NOISE_SPEAKERS,
                      "a speaker named 'System' must be filtered as noise")

    def test_our_own_speech_echo_is_excluded(self):
        """The CLI echoes what we say as `Said (say): ...`, which the name-based self-filter
        misses because the speaker reads as "Said (say)". Since replies often contain our own
        name, it re-entered through the "named" trigger - a self-conversation waiting to happen."""
        self.assertIsNone(listen.parse_speech(
            "Said (say): Well met, Randall. I am Homie - clearing the spawn here."))

    def test_our_own_yell_echo_is_excluded(self):
        self.assertIsNone(listen.parse_speech("Said (yell): HELP"))

    def test_unknown_speaker_is_excluded(self):
        self.assertIn("?", listen.NOISE_SPEAKERS)


class TestNoise(unittest.TestCase):
    """Every one of these has the `X: Y` shape and would parse as speech without an exclusion."""

    def test_combat_swing(self):
        self.assertIsNone(listen.parse_speech("Swing: Homie -> a creature: HIT"))

    def test_damage(self):
        self.assertIsNone(listen.parse_speech("Damage: a creature takes 6"))

    def test_object_label_is_not_conversation(self):
        # The single biggest source of noise in the log - "a creature: a creature", endlessly.
        self.assertIsNone(listen.parse_speech("[LABEL] a creature: a creature"))

    def test_system_message(self):
        self.assertIsNone(listen.parse_speech("[SYSTEM] You have left the protection of the town"))

    def test_another_scripts_log_line(self):
        self.assertIsNone(listen.parse_speech(
            "21:44:02 [combat_movement_melee] melee: reach 1, acquire within 10"))

    def test_container_listing(self):
        self.assertIsNone(listen.parse_speech("[CONTAINER] 0x407BE852 a rotting corpse x3:"))

    def test_command_echo(self):
        self.assertIsNone(listen.parse_speech("[CMD] container 0x407BE852"))

    def test_a_line_with_no_colon(self):
        self.assertIsNone(listen.parse_speech("nothing to see here"))

    def test_empty(self):
        self.assertIsNone(listen.parse_speech(""))


class TestPersonHeuristic(unittest.TestCase):
    def test_a_player_reads_as_a_person(self):
        self.assertTrue(listen.looks_like_person(mob("randall flagg"), "randall flagg"))

    def test_a_gargoyle_player_still_reads_as_a_person(self):
        """A player is not a body graphic. Requiring isHuman would have silently ignored every
        non-human race - and anything else a shard allows."""
        self.assertTrue(listen.looks_like_person(
            mob("stonewing", human=False), "stonewing"))

    def test_a_monster_does_not(self):
        self.assertFalse(listen.looks_like_person(
            mob("a creature", human=False, monster=True), "a creature"))

    def test_a_described_name_does_not_even_if_human(self):
        self.assertFalse(listen.looks_like_person(mob("a beggar"), "a beggar"))

    def test_a_speaker_we_cannot_see_is_unknown_not_false(self):
        self.assertIsNone(listen.looks_like_person(None, "someone offscreen"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
