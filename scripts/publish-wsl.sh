#!/usr/bin/env bash
set -euo pipefail
sf_source="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
sf_output="${1:-$sf_source/artifacts/win-x64}"
sf_dotnet="${STEAMFUSION_DOTNET:-dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$sf_source"
"$sf_dotnet" run --project tests/SteamFusion.Tests -c Release
for sf_project in SteamFusion.App SteamFusion.Cli; do
    "$sf_dotnet" publish "src/$sf_project" -c Release -r win-x64 --self-contained true -o "$sf_output" --nologo
done
(
    cd integrations/millennium
    npm ci --ignore-scripts
    npm test
    npm run build
)
mkdir -p "$sf_output/Scripts" "$sf_output/Docs"
cp packaging/* "$sf_output/Scripts/"
cp README.md VERIFICATION.md "$sf_output/Docs/"
mkdir -p "$sf_output/Millennium"
cp integrations/millennium/SteamFusion.star "$sf_output/Millennium/"
