"""Tests for scripts/github_http.py: python3 -m unittest discover -s scripts/tests"""
import pathlib
import sys
import unittest
import urllib.request

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))

import github_http  # noqa: E402


def redirect(to, code=302):
    request = urllib.request.Request("https://api.github.com/repos/a/b", headers={"Authorization": "Bearer token", "Accept": "application/json"})
    return github_http.SameHostAuthorization().redirect_request(request, None, code, "Found", {}, to)


class RedirectTests(unittest.TestCase):
    def test_the_token_goes_on_to_the_same_host(self):
        new = redirect("https://api.github.com/repositories/1", 301)
        self.assertEqual(new.get_header("Authorization"), "Bearer token")

    def test_the_token_is_dropped_for_another_host(self):
        new = redirect("https://objects.example.com/file", 307)
        self.assertIsNone(new.get_header("Authorization"))
        self.assertEqual(new.get_header("Accept"), "application/json")

    def test_the_token_is_dropped_for_plain_http(self):
        self.assertIsNone(redirect("http://api.github.com/repos/a/b").get_header("Authorization"))


if __name__ == "__main__":
    unittest.main()
