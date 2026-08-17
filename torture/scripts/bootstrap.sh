#!/usr/bin/env bash
set -euo pipefail

torture_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

cargo install sql-splitter \
  --version 1.21.0 \
  --locked \
  --no-default-features \
  --root "${torture_dir}/.tools"
