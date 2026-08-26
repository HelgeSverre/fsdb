#!/usr/bin/env bash
set -euo pipefail

php -r '$db = new mysqli("fsdb", "root", "", null, 3306); $db->query("CREATE DATABASE IF NOT EXISTS fsdb_wordpress");'

cd /opt/wordpress
vendor/bin/phpunit --configuration phpunit.xml.dist tests/phpunit/tests/db.php
