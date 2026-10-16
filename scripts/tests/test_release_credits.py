"""Tests for scripts/release_credits.py: python3 -m unittest discover -s scripts/tests"""
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))

import release_credits as rc  # noqa: E402


class PluginTests(unittest.TestCase):
    def test_prefix_of_each_plugin(self):
        self.assertEqual(rc.plugin_prefix("v1.0.20"), "v")
        self.assertEqual(rc.plugin_prefix("picks-v1.2.4"), "picks-v")
        self.assertEqual(rc.plugin_prefix("profiles-v1.1.3"), "profiles-v")
        self.assertEqual(rc.plugin_prefix("posters-v1.0.0"), "posters-v")
        with self.assertRaises(ValueError):
            rc.plugin_prefix("nightly")

    def test_previous_tag_is_the_same_plugins_last_lower_version(self):
        tags = ["v1.0.9", "v1.0.10", "v1.0.19", "v1.0.20", "picks-v1.2.4", "profiles-v1.1.2", "profiles-v1.1.3", "junk"]
        self.assertEqual(rc.previous_tag("v1.0.20", tags), "v1.0.19")
        self.assertEqual(rc.previous_tag("v1.0.10", tags), "v1.0.9")
        self.assertEqual(rc.previous_tag("profiles-v1.1.3", tags), "profiles-v1.1.2")
        self.assertIsNone(rc.previous_tag("picks-v1.2.4", tags))

    def test_files_belong_to_their_plugin(self):
        self.assertTrue(rc.belongs_to("v", "src/JellyfinMedic/Api/MedicController.cs"))
        self.assertTrue(rc.belongs_to("v", "CHANGELOG.md"))
        self.assertFalse(rc.belongs_to("v", "Jellyfin.Plugin.MedicPicks/Plugin.cs"))
        self.assertFalse(rc.belongs_to("v", "manifest.json"))
        self.assertTrue(rc.belongs_to("picks-v", "Jellyfin.Plugin.MedicPicks/Plugin.cs"))
        self.assertTrue(rc.belongs_to("picks-v", "tests/MedicPicks.Tests/PickTests.cs"))
        self.assertFalse(rc.belongs_to("picks-v", "src/JellyfinMedic/Plugin.cs"))
        self.assertTrue(rc.belongs_to("profiles-v", "Jellyfin.Plugin.MedicProfiles/Plugin.cs"))
        self.assertFalse(rc.belongs_to("profiles-v", "manifest.json"))
        self.assertTrue(rc.belongs_to("posters-v", "Jellyfin.Plugin.MedicPosters/Plugin.cs"))
        self.assertTrue(rc.belongs_to("posters-v", "tests/MedicPosters.Tests/PosterMakerTests.cs"))
        self.assertFalse(rc.belongs_to("v", "Jellyfin.Plugin.MedicPosters/Plugin.cs"))
        self.assertFalse(rc.belongs_to("profiles-v", "Jellyfin.Plugin.MedicPosters/Plugin.cs"))


class WindowTests(unittest.TestCase):
    def test_merged_after_the_last_release_and_by_this_one(self):
        self.assertTrue(rc.in_window("2026-10-15T08:45:12Z", "2026-10-14T22:30:00Z", "2026-10-15T15:20:19Z"))
        self.assertFalse(rc.in_window("2026-10-14T21:00:00Z", "2026-10-14T22:30:00Z", "2026-10-15T15:20:19Z"))
        self.assertFalse(rc.in_window("2026-10-15T16:00:00Z", "2026-10-14T22:30:00Z", "2026-10-15T15:20:19Z"))

    def test_first_release_and_release_being_made(self):
        self.assertTrue(rc.in_window("2026-01-01T00:00:00Z", None, "9999"))

    def test_unmerged_pull_request_is_never_in(self):
        self.assertFalse(rc.in_window(None, None, "9999"))


class CreditTests(unittest.TestCase):
    PULLS = [
        {"number": 125, "login": "camarigor", "type": "User"},
        {"number": 117, "login": "camarigor", "type": "User"},
        {"number": 126, "login": "p1zzm1ssile", "type": "User"},
        {"number": 131, "login": "dependabot[bot]", "type": "Bot"},
        {"number": 140, "login": "someone", "type": "User"},
    ]

    def test_owner_and_bots_are_not_thanked(self):
        people = rc.contributors(self.PULLS, "P1zzm1ssile")
        self.assertEqual(people, {"camarigor": {117, 125}, "someone": {140}})

    def test_one_contributor(self):
        line = rc.credit_line({"camarigor": {125, 117, 121}})
        self.assertEqual(line, "Thanks @camarigor (#117, #121, #125) for their work on this release.")

    def test_several_contributors(self):
        line = rc.credit_line({"zed": {3}, "amy": {1}, "Bob": {2}})
        self.assertEqual(line, "Thanks @amy (#1), @Bob (#2) and @zed (#3) for their work on this release.")

    def test_already_credited_in_the_changelog(self):
        changes = "- **A fix.** It works now. Thanks @CamarIgor (#117)."
        self.assertEqual(rc.credit_line({"camarigor": {117}}, changes), "")
        self.assertEqual(rc.credit_line({"camarigor": {117}, "amy": {1}}, changes),
                         "Thanks @amy (#1) for their work on this release.")

    def test_nobody_to_thank(self):
        self.assertEqual(rc.credit_line({}), "")


if __name__ == "__main__":
    unittest.main()
