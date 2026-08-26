#!/usr/bin/env bash
set -euo pipefail

cd /opt/ghost/ghost/core
export NODE_ENV=testing-mysql
export TZ=America/New_York
export database__connection__host=fsdb
export database__connection__port=3306
export database__connection__user=root
export database__connection__password=''
export database__connection__database=ghost_testing

/opt/ghost/node_modules/.bin/vitest run \
    --config vitest.config.db.ts \
    --project integration \
    test/integration/models/member-deep-pagination.test.js
