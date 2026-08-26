#!/usr/bin/env bash
set -euo pipefail

cd /opt/rails
bundle exec ruby -rmysql2 -e '
  db = Mysql2::Client.new(host: "fsdb", port: 3306, username: "root")
  db.query("CREATE DATABASE IF NOT EXISTS activerecord_unittest")
  db.query("CREATE DATABASE IF NOT EXISTS activerecord_unittest2")
'

cd activerecord
ARCONFIG=/usr/local/share/fsdb-rails.yml ARCONN=mysql2 \
  bundle exec ruby -Itest test/cases/adapters/mysql2/mysql2_adapter_test.rb \
    -n /test_configure_connection_sets_default_wait_timeout/
