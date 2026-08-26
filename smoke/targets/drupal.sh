#!/usr/bin/env bash
set -euo pipefail

cd /opt/drupal
mkdir -p sites/simpletest sites/default/files/simpletest
export SIMPLETEST_DB='mysql://root@fsdb:3306/fsdb?module=mysql'
export SIMPLETEST_BASE_URL='http://127.0.0.1:8080'
export MINK_DRIVER_ARGS_WEBDRIVER='["chrome", {"browserName":"chrome", "goog:chromeOptions":{"args":["--headless", "--no-sandbox", "--disable-dev-shm-usage"]}}, "http://127.0.0.1:9515"]'

php -S 0.0.0.0:8080 .ht.router.php >/tmp/drupal-http.log 2>&1 &
http_pid=$!
chromedriver --port=9515 --allowed-ips='' >/tmp/drupal-webdriver.log 2>&1 &
webdriver_pid=$!

cleanup() {
    kill "$http_pid" "$webdriver_pid" 2>/dev/null || true
}

trap cleanup EXIT INT TERM

php core/scripts/run-tests.sh \
    --all \
    --concurrency="${DRUPAL_CONCURRENCY:-8}" \
    --sqlite=/tmp/drupal-results.sqlite \
    --dburl="$SIMPLETEST_DB" \
    --url="$SIMPLETEST_BASE_URL" \
    --non-html
