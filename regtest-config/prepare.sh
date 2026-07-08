#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

config=regtest-config/boltz.conf

cp regtest/data/backend/boltz.conf "$config"
git apply regtest-config/boltz.conf.patch
