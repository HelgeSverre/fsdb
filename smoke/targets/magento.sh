#!/usr/bin/env bash
set -euo pipefail

for _ in $(seq 1 120); do
    if curl --fail --silent --show-error http://opensearch:9200 >/dev/null; then
        break
    fi
    sleep 1
done
curl --fail --silent --show-error http://opensearch:9200 >/dev/null

php -r '$db = new PDO("mysql:host=fsdb;port=3306", "root", ""); $db->exec("CREATE DATABASE IF NOT EXISTS fsdb_magento");'

cd /opt/magento
php -d memory_limit=-1 bin/magento setup:install \
    --base-url=http://localhost/ \
    --db-host=fsdb:3306 \
    --db-name=fsdb_magento \
    --db-user=root \
    --db-password='' \
    --cleanup-database \
    --backend-frontname=admin \
    --admin-firstname=Fs \
    --admin-lastname=Db \
    --admin-email=admin@example.test \
    --admin-user=admin \
    --admin-password='FsdbSmoke-2026!' \
    --language=en_US \
    --currency=USD \
    --timezone=UTC \
    --use-rewrites=0 \
    --session-save=files \
    --search-engine=opensearch \
    --opensearch-host=opensearch \
    --opensearch-port=9200 \
    --opensearch-index-prefix=fsdb_smoke
