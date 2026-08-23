#!/usr/bin/env bash
set -euo pipefail

FEED_URL="https://nuget.sliekens.dev/v3/index.json"
API_KEY="bab45b99091a6db7281261ee51f5270899be4118208227ef15750283ca81a470"

# Optional: add a prerelease suffix to avoid colliding with the official release.
# Leave empty for the default version (13.3.1).
VERSION_SUFFIX="preview.1"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUTPUT_DIR="$REPO_ROOT/artifacts/packages"

PROJECTS=(
  "src/CommunityToolkit.Aspire.Hosting.GlitchTip/CommunityToolkit.Aspire.Hosting.GlitchTip.csproj"
  "src/CommunityToolkit.Aspire.GlitchTip/CommunityToolkit.Aspire.GlitchTip.csproj"
)

rm -rf "$OUTPUT_DIR"

PACK_ARGS=(--configuration Release --output "$OUTPUT_DIR")
if [[ -n "$VERSION_SUFFIX" ]]; then
  PACK_ARGS+=(--version-suffix "$VERSION_SUFFIX")
fi

for PROJECT in "${PROJECTS[@]}"; do
  echo "Packing $PROJECT..."
  dotnet pack "$REPO_ROOT/$PROJECT" "${PACK_ARGS[@]}"
done

echo "Pushing packages to $FEED_URL..."
dotnet nuget push "$OUTPUT_DIR/*.nupkg" --source "$FEED_URL" --api-key "$API_KEY"

echo "Done."
