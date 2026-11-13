#!/usr/bin/env bash
set -euo pipefail
ROOT="$(dirname "$(realpath "$0")")"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$ROOT/.dotnet}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$ROOT/.nuget}"
GAME_DIR="${GAME_DIR:-/data/SteamLibrary/steamapps/common/Probably Stolen}"
dotnet build "$ROOT/ProbablyStolenMcp.csproj" -c Release -p:GameDir="$GAME_DIR" --nologo
python3 "$ROOT/package.py"
