"""Tests for scripts/set_manifest_changelog.py: python3 -m unittest discover -s scripts/tests"""
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))

import set_manifest_changelog as smc  # noqa: E402


class SetChangelogTests(unittest.TestCase):
    def manifest(self):
        return [
            {"guid": "AAA", "versions": [{"version": "1.0.20.0", "changelog": "old", "checksum": "x"},
                                         {"version": "1.0.19.0", "changelog": "older"}]},
            {"guid": "bbb", "versions": [{"version": "1.0.20.0", "changelog": "other plugin"}]},
        ]

    def test_only_that_version_of_that_plugin_changes(self):
        m = self.manifest()
        self.assertTrue(smc.set_changelog(m, "aaa", "1.0.20.0", "new"))
        self.assertEqual(m[0]["versions"][0], {"version": "1.0.20.0", "changelog": "new", "checksum": "x"})
        self.assertEqual(m[0]["versions"][1]["changelog"], "older")
        self.assertEqual(m[1]["versions"][0]["changelog"], "other plugin")

    def test_unknown_version_or_plugin(self):
        self.assertFalse(smc.set_changelog(self.manifest(), "aaa", "9.9.9.0", "new"))
        self.assertFalse(smc.set_changelog(self.manifest(), "ccc", "1.0.20.0", "new"))


if __name__ == "__main__":
    unittest.main()
