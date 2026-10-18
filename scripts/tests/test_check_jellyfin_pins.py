"""Tests for scripts/check_jellyfin_pins.py: python3 -m unittest discover -s scripts/tests"""
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))

import check_jellyfin_pins as pins  # noqa: E402

REPOSITORY = pathlib.Path(__file__).resolve().parents[2]

# The release script and its case branches, shaped like the repository's scripts/publish_dist.sh.
PUBLISH = """\
#!/usr/bin/env bash
set -euo pipefail
plugin=${{1:-}}
case "$plugin" in
{branches}
    *)
        echo "Unknown plugin '$plugin'" >&2
        exit 2
        ;;
esac
dotnet publish "$project" -c Release -o "$work/out" "${{target_args[@]}}" \\
    -p:Version="$short" > "$work/build.log"
python3 scripts/update_manifest.py --manifest "$dist/manifest.json" \\
    --guid "$guid" --version "$version" --abi "$abi"
"""

BRANCH = """\
    {name})
        project='{project}'
        dll='Plugin.dll'
        abi='{abi}'
        description="A plugin's description."
        target_args=({target_args})
        ;;"""


def publish(*branches: dict[str, str]) -> str:
    """A publish_dist.sh with one case branch for each release."""
    return PUBLISH.format(branches="\n".join(BRANCH.format(**branch) for branch in branches))


def branch(name="plugin", project="Plugin/Plugin.csproj", abi="12.0.0.0", target="12") -> dict[str, str]:
    return {"name": name, "project": project, "abi": abi,
            "target_args": f"-p:JellyfinTarget={target}" if target else ""}


PLUGIN = """\
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <JellyfinTarget Condition="'$(JellyfinTarget)' == ''">12</JellyfinTarget>
  </PropertyGroup>
  <PropertyGroup Condition="'$(JellyfinTarget)' == '12'">
    <JellyfinPackageVersion>12.0.0</JellyfinPackageVersion>
  </PropertyGroup>
  <PropertyGroup Condition="'$(JellyfinTarget)' == '10.11'">
    <JellyfinPackageVersion>10.11.11</JellyfinPackageVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Jellyfin.Controller" Version="$(JellyfinPackageVersion)" ExcludeAssets="runtime" />
    <PackageReference Include="Jellyfin.Model" Version="{model}" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
"""

TESTS = """\
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="Jellyfin.Controller" Version="{tests}" />
    <PackageReference Include="Jellyfin.Model" Version="{tests}" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../Plugin/Plugin.csproj" />
  </ItemGroup>
</Project>
"""


class CheckTests(unittest.TestCase):
    def empty(self) -> pathlib.Path:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        return pathlib.Path(directory.name)

    def write(self, root: pathlib.Path, files: dict[str, str]) -> None:
        for path, text in files.items():
            (root / path).parent.mkdir(parents=True, exist_ok=True)
            (root / path).write_text(text, encoding="utf-8")

    def repository(self, abi="12.0.0.0", target="12", model="$(JellyfinPackageVersion)",
                   tests="12.0.0") -> pathlib.Path:
        root = self.empty()
        self.write(root, {
            "scripts/publish_dist.sh": publish(branch(abi=abi, target=target)),
            "Plugin/Plugin.csproj": PLUGIN.format(model=model),
            "tests/Plugin.Tests/Plugin.Tests.csproj": TESTS.format(tests=tests),
        })
        return root

    def test_matching_versions(self):
        self.assertEqual(pins.check(self.repository()), (
            ["plugin: abi 12.0.0.0 matches Jellyfin 12.0.0 in Plugin/Plugin.csproj"
             " and tests/Plugin.Tests/Plugin.Tests.csproj"],
            [],
        ))

    def test_abi_must_be_the_jellyfin_the_plugin_builds_against(self):
        _, problems = pins.check(self.repository(abi="12.1.0.0"))
        self.assertEqual(problems, ["plugin: abi is 12.1.0.0, but Plugin/Plugin.csproj builds"
                                    " against Jellyfin.Controller 12.0.0, so it should be 12.0.0.0"])

    def test_the_releases_jellyfin_target_picks_the_version(self):
        _, problems = pins.check(self.repository(abi="10.11.11.0", target="10.11", tests="10.11.11"))
        self.assertEqual(problems, [])
        _, problems = pins.check(self.repository(abi="12.0.0.0", target="10.11", tests="10.11.11"))
        self.assertEqual(problems, ["plugin: abi is 12.0.0.0, but Plugin/Plugin.csproj builds"
                                    " against Jellyfin.Controller 10.11.11, so it should be 10.11.11.0"])

    def test_empty_target_args_build_the_projects_default(self):
        self.assertEqual(pins.check(self.repository(target=""))[1], [])
        _, problems = pins.check(self.repository(abi="10.11.11.0", target="", tests="10.11.11"))
        self.assertIn("plugin: abi is 10.11.11.0, but Plugin/Plugin.csproj builds against Jellyfin.Controller"
                      " 12.0.0, so it should be 12.0.0.0", problems)

    def test_controller_and_model_must_match(self):
        _, problems = pins.check(self.repository(model="12.1.0"))
        self.assertEqual(problems, ["plugin: Plugin/Plugin.csproj builds against Jellyfin.Controller"
                                    " 12.0.0 but Jellyfin.Model 12.1.0"])

    def test_unit_tests_must_pin_the_same_version(self):
        _, problems = pins.check(self.repository(tests="12.0.*"))
        self.assertEqual(problems, [
            "plugin: tests/Plugin.Tests/Plugin.Tests.csproj has Jellyfin.Controller 12.0.*, but"
            " Plugin/Plugin.csproj, which it tests, builds against 12.0.0",
            "plugin: tests/Plugin.Tests/Plugin.Tests.csproj has Jellyfin.Model 12.0.*, but"
            " Plugin/Plugin.csproj, which it tests, builds against 12.0.0",
        ])

    def test_what_it_cannot_read_is_a_problem(self):
        root = self.repository()
        plugin = root / "Plugin/Plugin.csproj"
        text = plugin.read_text(encoding="utf-8").replace("'$(JellyfinTarget)' == '12'", "Exists('jellyfin.props')")
        plugin.write_text(text, encoding="utf-8")
        self.assertEqual(pins.check(root)[1], ["plugin: Plugin/Plugin.csproj: can't read the condition"
                                               " Exists('jellyfin.props')"])

    def test_a_missing_release_script_is_a_problem(self):
        self.assertEqual(pins.check(self.empty()), (
            [], ["scripts/publish_dist.sh: No such file or directory, so there is nothing to check"]))

    def test_a_release_script_without_releases_is_a_problem(self):
        root = self.empty()
        self.write(root, {"scripts/publish_dist.sh": publish()})
        self.assertEqual(pins.check(root), (
            [], ["scripts/publish_dist.sh has no case branch that sets project= and abi=,"
                 " so there is nothing to check"]))

    def test_a_release_without_an_abi_is_a_problem(self):
        root = self.repository()
        script = root / "scripts/publish_dist.sh"
        script.write_text(script.read_text(encoding="utf-8").replace("        abi='12.0.0.0'\n", ""),
                          encoding="utf-8")
        self.assertEqual(pins.check(root)[1], ["plugin: sets no abi="])

    def test_target_args_it_cannot_read_are_a_problem(self):
        root = self.repository()
        script = root / "scripts/publish_dist.sh"
        script.write_text(script.read_text(encoding="utf-8").replace("target_args=(", "target_args=($extra "),
                          encoding="utf-8")
        self.assertEqual(pins.check(root)[1], ["plugin: can't read target_args=($extra -p:JellyfinTarget=12)"])

    def test_the_script_must_build_and_publish_what_each_release_sets(self):
        for used in ('dotnet publish "$project"', '"${target_args[@]}"', '--abi "$abi"'):
            with self.subTest(used=used):
                root = self.repository()
                script = root / "scripts/publish_dist.sh"
                script.write_text(script.read_text(encoding="utf-8").replace(used, ""), encoding="utf-8")
                self.assertEqual(len(pins.check(root)[1]), 1)
                self.assertIn(used, pins.check(root)[1][0])

    def shared_code(self, other_target="12", shared_tests="12.0.0") -> pathlib.Path:
        """The repository with a second released plugin, Other, and shared/Lib, which both plugins compile in
        and tests/Lib.Tests tests on its own, as Medic Picks, Medic Profiles and tests/MedicShared.Tests do."""
        root = self.repository()
        compile_lib = '  <ItemGroup>\n    <Compile Include="..\\shared\\Lib\\*.cs" />\n  </ItemGroup>\n</Project>'
        plugin = PLUGIN.format(model="$(JellyfinPackageVersion)").replace("</Project>", compile_lib)
        self.write(root, {
            "Plugin/Plugin.csproj": plugin,
            "Other/Other.csproj": plugin,
            "scripts/publish_dist.sh": publish(
                branch(),
                branch(name="other", project="Other/Other.csproj",
                       abi="10.11.11.0" if other_target == "10.11" else "12.0.0.0", target=other_target)),
            "tests/Lib.Tests/Lib.Tests.csproj": TESTS.format(tests=shared_tests).replace(
                '<ProjectReference Include="../../Plugin/Plugin.csproj" />',
                '<Compile Include="../../shared/Lib/*.cs" />'),
        })
        return root

    def test_shared_code_tests_are_compared_with_the_plugins_that_compile_it(self):
        self.assertEqual(pins.check(self.shared_code()), (
            ["plugin: abi 12.0.0.0 matches Jellyfin 12.0.0 in Plugin/Plugin.csproj,"
             " tests/Lib.Tests/Lib.Tests.csproj and tests/Plugin.Tests/Plugin.Tests.csproj",
             "other: abi 12.0.0.0 matches Jellyfin 12.0.0 in Other/Other.csproj"
             " and tests/Lib.Tests/Lib.Tests.csproj"],
            [],
        ))

    def test_shared_code_tests_must_pin_the_same_version(self):
        _, problems = pins.check(self.shared_code(shared_tests="12.1.0"))
        self.assertEqual(problems, [
            f"{release}: tests/Lib.Tests/Lib.Tests.csproj has {package} 12.1.0, but {plugin}, which compiles"
            " the shared/Lib it tests, builds against 12.0.0"
            for release, plugin in (("plugin", "Plugin/Plugin.csproj"), ("other", "Other/Other.csproj"))
            for package in pins.PACKAGES
        ])

    def test_plugins_that_compile_the_same_code_must_agree(self):
        _, problems = pins.check(self.shared_code(other_target="10.11"))
        self.assertIn("shared/Lib is compiled into Other/Other.csproj, which builds against Jellyfin 10.11.11,"
                      " and Plugin/Plugin.csproj, which builds against 12.0.0", problems)


# The unit test job runs this, which is what checks the repository on every pull request.
class RepositoryTests(unittest.TestCase):
    def test_releases_and_unit_tests_agree_on_jellyfin(self):
        report, problems = pins.check(REPOSITORY)
        self.assertEqual(problems, [])
        self.assertEqual([line.split(":")[0] for line in report if " matches " in line],
                         ["medic", "picks", "profiles"])

    def test_the_shared_code_tests_are_compared(self):
        report, _ = pins.check(REPOSITORY)
        self.assertEqual([line for line in report if "MedicShared.Tests" in line], [
            "picks: abi 12.0.0.0 matches Jellyfin 12.0.0 in"
            " Jellyfin.Plugin.MedicPicks/Jellyfin.Plugin.MedicPicks.csproj,"
            " tests/MedicPicks.Tests/MedicPicks.Tests.csproj and tests/MedicShared.Tests/MedicShared.Tests.csproj",
            "profiles: abi 12.0.0.0 matches Jellyfin 12.0.0 in"
            " Jellyfin.Plugin.MedicProfiles/Jellyfin.Plugin.MedicProfiles.csproj,"
            " tests/MedicProfiles.Tests/MedicProfiles.Tests.csproj and"
            " tests/MedicShared.Tests/MedicShared.Tests.csproj",
        ])


if __name__ == "__main__":
    unittest.main()
