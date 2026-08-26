#!/usr/bin/env bash
set -euo pipefail

cd /opt/gitea
export GITEA_TEST_DATABASE=mysql
export TEST_MYSQL_HOST=fsdb:3306
export TEST_MYSQL_DBNAME=fsdb_test_gitea
export TEST_MYSQL_USERNAME=root
export TEST_MYSQL_PASSWORD=''

make 'test-integration#TestUser'
