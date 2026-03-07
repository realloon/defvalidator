#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-osx-arm64}"
CONFIGURATION="${CONFIGURATION:-Release}"
PROJECT="$ROOT_DIR/src/DefValidator.Cli/DefValidator.Cli.csproj"
PUBLISH_DIR="$ROOT_DIR/src/DefValidator.Cli/bin/$CONFIGURATION/net10.0/$RID/publish"

DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet publish "$PROJECT" \
  -c "$CONFIGURATION" \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=false

printf 'Published single-file executable:\n%s/defvalidator\n' "$PUBLISH_DIR"
