#!/usr/bin/env bash
set -euo pipefail
sf_source="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
sf_output="${1:-$sf_source/artifacts/win-x64}"
sf_dotnet="${STEAMFUSION_DOTNET:-dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$sf_source"
"$sf_dotnet" run --project tests/SteamFusion.Tests -c Release
for sf_project in SteamFusion.App SteamFusion.Cli; do
    "$sf_dotnet" publish "src/$sf_project" -c Release -r win-x64 --self-contained true -o "$sf_output/App" --nologo
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
mkdir -p "$sf_output/App/Millennium"
cp integrations/millennium/SteamFusion.star "$sf_output/App/Millennium/"
cp packaging/START-HERE.txt "$sf_output/开始使用.txt"
rm "$sf_output/Scripts/START-HERE.txt"
# Windows shortcuts contain absolute targets; recreate them after moving a release.
if command -v powershell.exe >/dev/null && command -v wslpath >/dev/null; then
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w "$sf_output/Scripts/Create-Launchers.ps1")"
fi
