#!/usr/bin/env bash
# Publishes one plugin release to the dist branch on Forgejo, which Jellyfin reads as the plugin repository:
#
#   https://forgejo.tinfoilhat.work/camarigor/Jellyfin-Medic/raw/branch/dist/manifest.json
#
# Usage: scripts/publish_dist.sh medic|picks|profiles DIST_DIR
#
# DIST_DIR is a checkout of the dist branch (git worktree add ../medic-dist dist). The plugin is built from this
# checkout at the version its .csproj says, the same way the old GitHub release workflows built it; its zip and a
# manifest entry are written into DIST_DIR and committed there. Pushing dist stays a separate, deliberate step.
set -euo pipefail

BASE_URL='https://forgejo.tinfoilhat.work/camarigor/Jellyfin-Medic/raw/branch'
OWNER='camarigor'

plugin=${1:-}
dist=${2:-}
if [[ -z "$plugin" || -z "$dist" ]]; then
    echo "Usage: $0 medic|picks|profiles DIST_DIR" >&2
    exit 2
fi

root=$(git rev-parse --show-toplevel)
cd "$root"

case "$plugin" in
    medic)
        project='src/JellyfinMedic/JellyfinMedic.csproj'
        dll='JellyfinMedic.dll'
        zip_prefix='JellyfinMedic'
        changelog_file='CHANGELOG.md'
        guid='8d1f5c2e-6b4a-4f7e-9c3d-2a7b5e1f0c94'
        name='Jellyfin Medic'
        abi='12.1.0.0'
        description='Health checks, performance tests and usage-aware task scheduling for your Jellyfin server.'
        overview='Keeps a busy Jellyfin server healthy: checks, tests and smart scheduling.'
        image="$BASE_URL/main/src/JellyfinMedic/thumb.png"
        target_args=()
        ;;
    picks)
        project='Jellyfin.Plugin.MedicPicks/Jellyfin.Plugin.MedicPicks.csproj'
        dll='Jellyfin.Plugin.MedicPicks.dll'
        zip_prefix='MedicPicks'
        changelog_file='Jellyfin.Plugin.MedicPicks/CHANGELOG.md'
        guid='b34ec5d1-b70f-435f-b77d-9b76979b03c7'
        name='Medic Picks'
        abi='12.0.0.0'
        description='Personal picks for every user, from what they watch.'
        overview="A private playlist of picks in every Jellyfin app, plus a My picks page with titles that aren't on the server yet and one-tap Seerr requests."
        image="$BASE_URL/main/Jellyfin.Plugin.MedicPicks/thumb.png"
        target_args=(-p:JellyfinTarget=12)
        ;;
    profiles)
        project='Jellyfin.Plugin.MedicProfiles/Jellyfin.Plugin.MedicProfiles.csproj'
        dll='Jellyfin.Plugin.MedicProfiles.dll'
        zip_prefix='MedicProfiles'
        changelog_file='Jellyfin.Plugin.MedicProfiles/CHANGELOG.md'
        guid='1eb65368-7b2e-4a55-8f2b-b56134f3fe68'
        name='Medic Profiles'
        abi='12.0.0.0'
        description='Sonarr and Radarr from your Jellyfin dashboard: downloads, fixes and profile advice.'
        overview='See what Sonarr and Radarr are downloading, remove, block or import stuck and wrong downloads by hand, and get quality profile advice from what your server actually plays.'
        image="$BASE_URL/main/Jellyfin.Plugin.MedicProfiles/thumb.png"
        target_args=(-p:JellyfinTarget=12)
        ;;
    *)
        echo "Unknown plugin '$plugin': use medic, picks or profiles" >&2
        exit 2
        ;;
esac

# A release has to match a commit: built from uncommitted changes, nobody could rebuild it.
if [[ -n "$(git status --porcelain)" ]]; then
    echo "This checkout has uncommitted changes; commit or stash them first." >&2
    exit 1
fi
if [[ "$(git -C "$dist" rev-parse --abbrev-ref HEAD)" != 'dist' ]]; then
    echo "$dist is not a checkout of the dist branch." >&2
    exit 1
fi

version=$(grep -oP '(?<=<AssemblyVersion>)[0-9.]+(?=</AssemblyVersion>)' "$project")
short=${version%.0}
zip="$zip_prefix-v$short.zip"
if [[ -e "$dist/$zip" ]]; then
    # Jellyfin keeps a checksum per version: a second build under the same number would fail to install.
    echo "$zip is already published; raise the version to publish again." >&2
    exit 1
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

python3 scripts/changelog_section.py "$changelog_file" "$short" > "$work/CHANGES.md" || {
    echo "$changelog_file has no section for $short." >&2
    exit 1
}

dotnet publish "$project" -c Release -o "$work/out" "${target_args[@]}" \
    -p:Version="$short" -p:AssemblyVersion="$version" -p:FileVersion="$version" > "$work/build.log" || {
    tail -20 "$work/build.log" >&2
    exit 1
}

zip -j -q "$dist/$zip" "$work/out/$dll"
checksum=$(md5sum "$dist/$zip" | cut -d' ' -f1)

python3 scripts/update_manifest.py --manifest "$dist/manifest.json" \
    --guid "$guid" --name "$name" --version "$version" --abi "$abi" \
    --url "$BASE_URL/dist/$zip" --checksum "$checksum" --changelog-file "$work/CHANGES.md" \
    --owner "$OWNER" --description "$description" --overview "$overview" --image-url "$image"

git -C "$dist" add "$zip" manifest.json
git -C "$dist" commit -q -m "dist: $name $version" -m "Built from $(git rev-parse --short HEAD)."
echo "Published $name $version ($zip, md5 $checksum) to $dist; push the dist branch to make it live."
