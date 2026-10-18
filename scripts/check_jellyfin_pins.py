#!/usr/bin/env python3
"""Checks that each release names the Jellyfin it is built against, and that its unit tests run on that Jellyfin.

The manifest's targetAbi is the abi= typed into each release's case branch in scripts/publish_dist.sh, while the
build takes its Jellyfin from the plugin's csproj, and nothing tied the two together. For every branch that sets
project= and abi=, this checks that:

- abi is the plugin's Jellyfin.Controller version plus ".0" (12.1.0 needs 12.1.0.0), reading
  $(JellyfinPackageVersion) for the -p:JellyfinTarget the branch's target_args=(...) builds with, or the project's
  default when target_args is empty;
- Jellyfin.Model is at the same version as Jellyfin.Controller;
- each tests/*/*.Tests.csproj that references the plugin pins that same version, so its unit tests run on the
  Jellyfin the release is built against;
- each test project that compiles in a directory of code the plugin also compiles in (as tests/MedicShared.Tests
  does shared/Medic.Shared, which Medic Picks and Medic Profiles both ship) pins that same version too, and the
  plugins that compile in the same directory build against the same Jellyfin.

It also checks that the script builds "$project" with "${target_args[@]}" and passes --abi "$abi" to
scripts/update_manifest.py, so the values it reads are the ones a release uses.

  python3 scripts/check_jellyfin_pins.py [repository]

The unit test job runs it through scripts/tests/test_check_jellyfin_pins.py. Prints a line for each release that
matches and, to stderr, each thing that doesn't, then exits 1 if there is any. It reads only plain single-word
assignments in the script, and the plain MSBuild the projects use ($(Name), and conditions like
'$(Name)' == 'value'), and reports anything else rather than guess.
"""
import argparse
import itertools
import os
import pathlib
import re
import shlex
import sys
import xml.etree.ElementTree as ET
from typing import NamedTuple

PACKAGES = ("Jellyfin.Controller", "Jellyfin.Model")
# Jellyfin's packages carry the server's three-number version. A pattern like 12.1.* would follow every new patch.
EXACT = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+")


class Unreadable(Exception):
    """Something the check can't read, so it can't vouch for that release."""


SCRIPT = "scripts/publish_dist.sh"
# What the script has to do with each branch's values, so the ones read here are the ones a release uses.
USES = (
    (r'dotnet\s+publish\s+"?\$\{?project\b', 'doesn\'t run dotnet publish "$project"'),
    (r'"\$\{target_args\[@\]\}"', 'doesn\'t pass "${target_args[@]}" to dotnet publish'),
    (r'--abi\s+"?\$\{?abi\b', 'doesn\'t pass --abi "$abi" to scripts/update_manifest.py'),
)


class Release(NamedTuple):
    """One case branch of the release script."""
    name: str
    abi: str
    project: str
    properties: dict[str, str]  # the -p properties target_args gives dotnet publish, by lowercase name


def words(raw: str) -> list[str]:
    """The words of a shell value, which must not need the shell to expand anything."""
    try:
        split = shlex.split(raw, comments=True)
    except ValueError:
        raise Unreadable(f"can't read {raw}") from None
    if any("$" in word or "`" in word for word in split):
        raise Unreadable(f"can't read {raw}")
    return split


def branches(text: str) -> dict[str, dict[str, str]]:
    """The assignments in each branch of the script's case statement, by branch name."""
    found: dict[str, dict[str, str]] = {}
    current: dict[str, str] | None = None
    inside = False
    for line in text.splitlines():
        stripped = line.strip()
        if re.match(r"case\s.*\sin$", stripped):
            inside = True
        elif stripped == "esac":
            inside = False
        elif inside:
            m = re.fullmatch(r"([\w|-]+)\)", stripped)
            if m:
                current = found.setdefault(m[1], {})
            elif stripped == ";;":
                current = None
            elif current is not None:
                m = re.fullmatch(r"([A-Za-z_]\w*)=(.*)", stripped)
                if m:
                    current[m[1]] = m[2]
    return {name: values for name, values in found.items() if "project" in values or "abi" in values}


def release(name: str, values: dict[str, str]) -> Release:
    """A case branch's abi, project and the -p properties its target_args set."""
    single: dict[str, str] = {}
    for key in ("project", "abi"):
        if key not in values:
            raise Unreadable(f"sets no {key}=")
        value = words(values[key])
        if len(value) != 1:
            raise Unreadable(f"can't read {key}={values[key]}")
        single[key] = value[0]
    raw = values.get("target_args", "()")
    m = re.fullmatch(r"\((.*)\)", raw)
    if not m:
        raise Unreadable(f"can't read target_args={raw}")
    try:
        args = words(m[1])
    except Unreadable:
        raise Unreadable(f"can't read target_args={raw}") from None
    properties: dict[str, str] = {}
    for word in args:
        p = re.fullmatch(r"(?:--?|/)p(?:roperty)?:([^=]+)=(.*)", word)
        if p:
            properties[p[1].lower()] = p[2]
    return Release(name, single["abi"], single["project"], properties)


def local(element: ET.Element) -> str:
    """An element's tag without the XML namespace, which an MSBuild file may or may not declare."""
    return element.tag.rpartition("}")[2]


def expand(text: str, properties: dict[str, str]) -> str:
    """$(Name) filled in as MSBuild does, a property that isn't set being empty."""
    expanded = re.sub(r"\$\(([A-Za-z_][\w.-]*)\)", lambda m: properties.get(m[1].lower(), ""), text)
    if re.search(r"[$@%]\(", expanded):
        raise Unreadable(f"can't read {text}")
    return expanded


def holds(element: ET.Element, properties: dict[str, str]) -> bool:
    """Whether an element's Condition is true, comparing without case as MSBuild does."""
    condition = element.get("Condition")
    if condition is None:
        return True
    m = re.fullmatch(r"\s*'([^']*)'\s*(==|!=)\s*'([^']*)'\s*", condition)
    if not m:
        raise Unreadable(f"can't read the condition {condition}")
    return (expand(m[1], properties).lower() == expand(m[3], properties).lower()) == (m[2] == "==")


def source_directory(project: pathlib.Path, include: str) -> pathlib.Path:
    """The directory a Compile item takes its files from: the part of the path before any wildcard."""
    parts = pathlib.PurePosixPath(include.replace("\\", "/")).parts
    plain = list(itertools.takewhile(lambda part: not any(c in part for c in "*?"), parts))
    if len(plain) == len(parts):
        plain = plain[:-1]
    return project.parent.joinpath(*plain).resolve()


class Project(NamedTuple):
    """What the check reads from a .csproj."""
    packages: dict[str, str]  # Jellyfin package name to the version it references
    references: list[pathlib.Path]  # the projects it references
    compiles: set[pathlib.Path]  # the directories its Compile items take source files from


def evaluate(path: pathlib.Path, name: str, global_properties: dict[str, str]) -> Project:
    """The Jellyfin package versions a project references, the projects it references and the source it compiles in."""
    try:
        project = ET.parse(path).getroot()
        properties = dict(global_properties)
        # Every property before any item, as MSBuild evaluates them.
        for group in project:
            if local(group) != "PropertyGroup" or not holds(group, properties):
                continue
            for prop in group:
                # A property given on the command line wins over the project's, as in MSBuild.
                if local(prop).lower() not in global_properties and holds(prop, properties):
                    properties[local(prop).lower()] = expand(prop.text or "", properties)

        packages: dict[str, str] = {}
        references: list[pathlib.Path] = []
        compiles: set[pathlib.Path] = set()
        for group in project:
            if local(group) != "ItemGroup":
                continue
            for item in group:
                kind, include = local(item), item.get("Include", "")
                package = next((p for p in PACKAGES if p.lower() == include.lower()), None)
                if kind == "PackageReference" and package and holds(group, properties) and holds(item, properties):
                    version = item.get("Version") or next((v.text or "" for v in item if local(v) == "Version"), "")
                    packages[package] = expand(version, properties)
                elif kind == "ProjectReference" and holds(group, properties) and holds(item, properties):
                    references.append((path.parent / expand(include, properties).replace("\\", "/")).resolve())
                elif kind == "Compile" and include and holds(group, properties) and holds(item, properties):
                    compiles.update(source_directory(path, part.strip())
                                    for part in expand(include, properties).split(";") if part.strip())
    except OSError as e:
        raise Unreadable(f"{name}: {e.strerror}") from None
    except (ET.ParseError, Unreadable) as e:
        raise Unreadable(f"{name}: {e}") from None
    return Project(packages, references, compiles)


def check(root: pathlib.Path) -> tuple[list[str], list[str]]:
    """One line for each release that matches or test project left out, and one for each problem."""
    root = root.resolve()
    report: list[str] = []
    problems: list[str] = []

    def name(path: pathlib.Path) -> str:
        return pathlib.Path(os.path.relpath(path, root)).as_posix()

    tests: dict[pathlib.Path, Project] = {}
    for path in sorted(root.glob("tests/*/*.Tests.csproj")):
        try:
            tests[path] = evaluate(path, name(path), {})
        except Unreadable as e:
            problems.append(str(e))

    releases: list[Release] = []
    try:
        text = (root / SCRIPT).read_text(encoding="utf-8")
    except OSError as e:
        problems.append(f"{SCRIPT}: {e.strerror}, so there is nothing to check")
        text = ""
    found = branches(text)
    if text and not found:
        problems.append(f"{SCRIPT} has no case branch that sets project= and abi=, so there is nothing to check")
    if found:
        problems.extend(f"{SCRIPT} {problem}" for pattern, problem in USES if not re.search(pattern, text))
    for release_name, values in found.items():
        try:
            releases.append(release(release_name, values))
        except Unreadable as e:
            problems.append(f"{release_name}: {e}")

    compared: set[pathlib.Path] = set()
    # Each directory of source a released plugin compiles in, with the plugins and the Jellyfin they build against.
    compiled_by: dict[pathlib.Path, dict[pathlib.Path, str]] = {}
    for each in releases:
        plugin = (root / each.project).resolve()
        try:
            built = evaluate(plugin, each.project, each.properties)
        except Unreadable as e:
            problems.append(f"{each.name}: {e}")
            continue
        abi, packages = each.abi, built.packages
        tested = [test for test, project in tests.items() if plugin in project.references]
        # Test projects that compile in code this plugin compiles in too, and the directory each shares with it.
        sharing = {test: min(project.compiles & built.compiles) for test, project in tests.items()
                   if test not in tested and project.compiles & built.compiles}
        compared.update(tested, sharing)

        found: list[str] = []
        version = packages.get("Jellyfin.Controller")
        if version is None:
            found.append(f"{name(plugin)} doesn't reference Jellyfin.Controller")
        elif not EXACT.fullmatch(version):
            found.append(f"{name(plugin)} builds against Jellyfin.Controller '{version}', not an exact version"
                         " like 12.1.0")
        else:
            model = packages.get("Jellyfin.Model", version)
            if model != version:
                found.append(f"{name(plugin)} builds against Jellyfin.Controller {version}"
                             f" but Jellyfin.Model {model}")
            if abi != version + ".0":
                found.append(f"abi is {abi}, but {name(plugin)} builds against Jellyfin.Controller {version},"
                             f" so it should be {version}.0")
            for test in tested:
                for package, pinned in tests[test].packages.items():
                    if pinned != version:
                        found.append(f"{name(test)} has {package} {pinned}, but {name(plugin)}, which it tests,"
                                     f" builds against {version}")
            for test, directory in sharing.items():
                for package, pinned in tests[test].packages.items():
                    if pinned != version:
                        found.append(f"{name(test)} has {package} {pinned}, but {name(plugin)}, which compiles"
                                     f" the {name(directory)} it tests, builds against {version}")
            for directory in built.compiles:
                compiled_by.setdefault(directory, {})[plugin] = version

        if found:
            problems.extend(f"{each.name}: {problem}" for problem in found)
        else:
            files = [name(plugin), *sorted(name(path) for path in (*tested, *sharing))]
            listed = files[0] if len(files) == 1 else ", ".join(files[:-1]) + " and " + files[-1]
            report.append(f"{each.name}: abi {abi} matches Jellyfin {version} in {listed}")

    for directory, plugins in sorted(compiled_by.items()):
        if len(set(plugins.values())) > 1:
            (first, version), *others = sorted(plugins.items())
            rest = [f"{name(plugin)}, which builds against {other}" for plugin, other in others]
            builds = rest[0] if len(rest) == 1 else ", ".join(rest[:-1]) + " and " + rest[-1]
            problems.append(f"{name(directory)} is compiled into {name(first)}, which builds against Jellyfin"
                            f" {version}, and {builds}")

    for test in tests:
        if test not in compared:
            report.append(f"{name(test)} references none of the released plugins, so its Jellyfin isn't compared")
    return report, problems


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("repository", nargs="?", type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[1])
    args = ap.parse_args()

    report, problems = check(args.repository)
    for line in report:
        print(line)
    if problems:
        sys.exit("\n".join(problems))


if __name__ == "__main__":
    main()
