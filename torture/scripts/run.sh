#!/usr/bin/env bash
set -euo pipefail

torture_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="fsdb-torture-${UID}-$$"
compose=(docker compose --project-name "${project}" --file "${torture_dir}/compose.yaml")

command_name="${1:-suite}"
if [[ "${command_name}" == "durability" || "${command_name}" == "check-tools" || "${command_name}" == "--help" || "${command_name}" == "-h" ]]; then
  cd "${torture_dir}"
  exec dotnet run --project src/Fsdb.Torture -- "$@"
fi

if ! command -v docker >/dev/null 2>&1 || ! docker info >/dev/null 2>&1; then
  echo "error: Docker with a reachable daemon is required" >&2
  exit 1
fi

# shellcheck disable=SC2329 # Invoked indirectly by the EXIT trap below.
cleanup() {
  if ! "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1; then
    echo "warning: MySQL cleanup failed for Compose project ${project}" >&2
  fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

"${compose[@]}" up --detach --wait
binding="$("${compose[@]}" port mysql 3306)"
port="${binding##*:}"

cd "${torture_dir}"
set +e
dotnet run --project src/Fsdb.Torture -- "$@" \
  --mysql "Server=127.0.0.1;Port=${port};User ID=root;Password=torture-secret;SslMode=None;AllowPublicKeyRetrieval=True"
status=$?
set -e
exit "${status}"
