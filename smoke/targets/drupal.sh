#!/usr/bin/env bash
set -euo pipefail

cd /opt/drupal
mkdir -p sites/simpletest sites/default/files/simpletest
export SIMPLETEST_DB='mysql://root@fsdb:3306/fsdb?module=mysql'

vendor/bin/phpunit --configuration core/phpunit.xml.dist \
    core/modules/mysql/tests/src/Kernel/mysql/ConnectionTest.php
vendor/bin/phpunit --configuration core/phpunit.xml.dist \
    core/modules/mysql/tests/src/Kernel/mysql/SchemaTest.php
