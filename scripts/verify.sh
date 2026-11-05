#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet restore AskVest.slnx --locked-mode
dotnet build AskVest.slnx -c Release --no-restore
dotnet test AskVest.slnx -c Release --no-build
dotnet list AskVest.slnx package --vulnerable --include-transitive
