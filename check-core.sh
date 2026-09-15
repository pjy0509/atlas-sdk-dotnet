#!/bin/sh
# The parity gate: the netstandard2.0 SDK builds, the net8 console exercises
# queue/adoption/transport/links against an in-process listener, and the
# written envelopes are checked by tools/check-golden.sh.
set -e
cd "$(dirname "$0")"

OUT="${TMPDIR:-/tmp}/atlas-dotnet-parity"
rm -rf "$OUT"
mkdir -p "$OUT"

dotnet build tools/ParityCheck/ParityCheck.csproj -c Release --nologo -v quiet
dotnet run --project tools/ParityCheck -c Release --no-build -- "$OUT/envelopes"

sh tools/check-golden.sh "$OUT/envelopes" atlas-dotnet windows campaign_id
