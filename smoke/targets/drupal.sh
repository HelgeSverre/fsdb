#!/usr/bin/env bash
set -euo pipefail

cd /opt/drupal
mkdir -p sites/simpletest sites/default/files/simpletest
export SIMPLETEST_DB='mysql://root@fsdb:3306/fsdb?module=mysql'
export SIMPLETEST_BASE_URL='http://127.0.0.1:8080'
export MINK_DRIVER_ARGS_WEBDRIVER='["chrome", {"browserName":"chrome", "goog:chromeOptions":{"args":["--headless", "--no-sandbox", "--disable-dev-shm-usage"]}}, "http://127.0.0.1:9515"]'

concurrency="${DRUPAL_CONCURRENCY:-8}"
http_workers="${DRUPAL_HTTP_WORKERS:-$((concurrency * 2))}"
artifact_dir="${SMOKE_RUN_ID:+/smoke-results/$SMOKE_RUN_ID}"

if [[ -n "$artifact_dir" ]]; then
    mkdir -p "$artifact_dir"
    results_db="$artifact_dir/drupal-results.sqlite"
    http_log="$artifact_dir/drupal-http.log"
    webdriver_log="$artifact_dir/drupal-webdriver.log"
else
    results_db=/tmp/drupal-results.sqlite
    http_log=/tmp/drupal-http.log
    webdriver_log=/tmp/drupal-webdriver.log
fi

PHP_CLI_SERVER_WORKERS="$http_workers" php -S 0.0.0.0:8080 .ht.router.php >"$http_log" 2>&1 &
http_pid=$!
chromedriver --port=9515 --allowed-ips='' >"$webdriver_log" 2>&1 &
webdriver_pid=$!

cleanup() {
    kill "$http_pid" "$webdriver_pid" 2>/dev/null || true

    if [[ -n "$artifact_dir" ]]; then
        tar -czf "$artifact_dir/drupal-junit.tar.gz" -C /opt/drupal sites/default/files/simpletest || true
        find "$artifact_dir" -maxdepth 1 -type f -exec chmod 0666 {} +
    fi
}

trap cleanup EXIT INT TERM

php core/scripts/run-tests.sh \
    --all \
    --concurrency="$concurrency" \
    --sqlite="$results_db" \
    --dburl="$SIMPLETEST_DB" \
    --url="$SIMPLETEST_BASE_URL" \
    --non-html
