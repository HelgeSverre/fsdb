#!/usr/bin/env bash
set -euo pipefail

php -r '$db = new mysqli("fsdb", "root", "", null, 3306); $db->query("CREATE DATABASE IF NOT EXISTS fsdb_moodle");'
mkdir -p /opt/moodledata /opt/phpu_moodledata
chmod 0777 /opt/moodledata /opt/phpu_moodledata

cd /opt/moodle
php -d memory_limit=512M public/admin/tool/phpunit/cli/init.php --disable-composer
vendor/bin/phpunit --testsuite core_ddl_testsuite
vendor/bin/phpunit --testsuite core_dml_testsuite
