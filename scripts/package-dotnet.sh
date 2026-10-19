#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project_root="$(dirname -- "$script_dir")"

package_project() {
  local project_name="$1"
  local artifact_name="$2"
  local output_dir="$project_root/artifacts/$project_name"
  mkdir -p "$output_dir"
  rm -rf -- "$output_dir/publish"
  rm -f -- "$output_dir/$artifact_name.zip"
  dotnet publish "$project_root/src/$project_name/$project_name.csproj" \
    --configuration Release --runtime linux-arm64 --self-contained false \
    -p:RestoreLockedMode=true -p:PublishReadyToRun=false --output "$output_dir/publish"
  (cd "$output_dir/publish" && zip -q -r "$output_dir/$artifact_name.zip" .)
}

package_project OsbbRequests.Bot bot
package_project OsbbRequests.AdminApi admin-api
