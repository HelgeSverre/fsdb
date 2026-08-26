#!/usr/bin/env bash
set -euo pipefail

cd /opt/mediawiki
php maintenance/run.php install \
    --server=http://localhost \
    --scriptpath=/w \
    --with-developmentsettings \
    --dbtype=mysql \
    --dbserver=fsdb:3306 \
    --dbname=fsdb_mediawiki \
    --dbuser=root \
    --dbpass='' \
    --pass='FsdbSmokeAdmin-2026!' \
    'fsdb smoke' Admin

composer phpunit:config
PHPUNIT_USE_NORMAL_TABLES=1 composer phpunit -- --filter DatabaseIntegrationTest
