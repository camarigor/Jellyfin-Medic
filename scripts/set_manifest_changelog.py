#!/usr/bin/env python3
"""Replaces one released version's changelog in manifest.json, leaving everything else as it is.

Used by the Refresh release notes workflow:

  python3 scripts/set_manifest_changelog.py --guid 8d1f5c2e-... --version 1.0.20.0 --changelog-file CHANGES.md
"""
import argparse
import json
import pathlib
import sys


def set_changelog(manifest: list, guid: str, version: str, changelog: str) -> bool:
    """Sets the changelog; False when the manifest has no such plugin version."""
    for plugin in manifest:
        if plugin.get("guid", "").lower() != guid.lower():
            continue
        for entry in plugin.get("versions", []):
            if entry.get("version") == version:
                entry["changelog"] = changelog
                return True
    return False


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest", default="manifest.json")
    ap.add_argument("--guid", required=True)
    ap.add_argument("--version", required=True)
    ap.add_argument("--changelog-file", required=True)
    args = ap.parse_args()

    path = pathlib.Path(args.manifest)
    manifest = json.loads(path.read_text(encoding="utf-8"))
    changelog = pathlib.Path(args.changelog_file).read_text(encoding="utf-8").strip()
    if not set_changelog(manifest, args.guid, args.version, changelog):
        sys.exit(f"{args.manifest} has no version {args.version} of {args.guid}")
    path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"manifest.json: changelog of {args.version} updated")


if __name__ == "__main__":
    main()
