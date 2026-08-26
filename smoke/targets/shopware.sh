#!/usr/bin/env bash
set -euo pipefail

cd /opt/shopware
php -d memory_limit=-1 src/Core/TestBootstrap.php
