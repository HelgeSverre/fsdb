#!/usr/bin/env bash
set -euo pipefail

cd /opt/nextcloud
mkdir -p data
cp tests/redis.config.php config/
cp tests/preseed-config.php config/config.php
sed -i "s/'host' => 'localhost'/'host' => 'redis'/" config/redis.config.php

NC_setup_create_db_user=false ./occ maintenance:install --verbose \
    --database=mysql \
    --database-name=fsdb_nextcloud \
    --database-host=fsdb \
    --database-port=3306 \
    --database-user=root \
    --database-pass='' \
    --admin-user=admin \
    --admin-pass=admin

php -f tests/enable_all.php
composer run --timeout=0 test:db -- --log-junit /tmp/nextcloud-junit.xml
