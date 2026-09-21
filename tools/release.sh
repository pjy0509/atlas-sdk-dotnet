#!/bin/sh
# Cuts a release on a machine with the .NET SDK:
#
#   sh tools/release.sh            # version from the csproj
#
# Needs NUGET_API_KEY in the environment: a nuget.org API key scoped to
# AppAtlas.Sdk. Never a key that can push anything.
#
# Order matters: gate, pack, push, and only then tag. A tag is a promise that
# a version is out there, so nothing is tagged until nuget.org has taken it.
set -eu
REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO"

PROJECT="src/AppAtlas.Sdk/AppAtlas.Sdk.csproj"
VERSION="$(sed -n 's|.*<Version>\([^<]*\)</Version>.*|\1|p' "$PROJECT" | head -n1)"
[ -n "$VERSION" ] || { echo "no <Version> in $PROJECT" >&2; exit 1; }
[ -n "${NUGET_API_KEY:-}" ] || { echo "no NUGET_API_KEY" >&2; exit 1; }

# Everything that would go into the release must already be committed: the
# package is built from the tree, and the tag will name the commit.
[ -z "$(git status --porcelain --untracked-files=no)" ] || { echo "working tree is dirty; commit first" >&2; exit 1; }
! git rev-parse -q --verify "refs/tags/v$VERSION" >/dev/null || { echo "v$VERSION is already tagged" >&2; exit 1; }

# The guide's install lines name a version. A release that does not match them
# ships instructions nobody can follow, which is how nuget.org stayed at 0.2.0
# while the guide asked for 0.4.0.
for FILE in README.md README.ko.md README.zh.md; do
    OTHERS="$(awk '/^## (Install|설치|安装)/{on=1;next} /^## /{on=0} on' "$FILE" \
        | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | sort -u | grep -vx "$VERSION" || true)"
    [ -z "$OTHERS" ] || { echo "$FILE installs $(echo "$OTHERS" | tr '\n' ' '), not $VERSION" >&2; exit 1; }
done
echo "== the guide installs $VERSION"

echo "== releasing $VERSION from $(git rev-parse --short HEAD)"

echo "== gate"
sh check-core.sh

echo "== pack"
rm -rf build/nupkg
dotnet pack "$PROJECT" -c Release --nologo -o build/nupkg
ls build/nupkg

# The targets that start the SDK without a line of code only work if they ship
# inside the package, and a missing file is silent at pack time.
for ENTRY in "buildTransitive/AppAtlas.Sdk.targets" "build/AppAtlas.Sdk.targets"; do
    unzip -l "build/nupkg/AppAtlas.Sdk.$VERSION.nupkg" | grep -q "$ENTRY" \
        || { echo "the package is missing $ENTRY" >&2; exit 1; }
done
echo "== the package carries its targets"

echo "== push"
dotnet nuget push "build/nupkg/AppAtlas.Sdk.$VERSION.nupkg" \
    --source https://api.nuget.org/v3/index.json --api-key "$NUGET_API_KEY" --skip-duplicate

echo "== tag"
git tag -a "v$VERSION" -m "v$VERSION"
git push origin "v$VERSION"

echo "== $VERSION is on its way to nuget.org"
