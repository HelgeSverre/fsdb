<?php

define('ABSPATH', __DIR__ . '/src/');
define('WP_DEFAULT_THEME', 'default');
define('WP_DEBUG', true);
define('DB_NAME', 'fsdb_wordpress');
define('DB_USER', 'root');
define('DB_PASSWORD', '');
define('DB_HOST', 'fsdb:3306');
define('DB_CHARSET', 'utf8mb4');
define('DB_COLLATE', '');
define('AUTH_KEY', 'fsdb-smoke-auth-key');
define('SECURE_AUTH_KEY', 'fsdb-smoke-secure-auth-key');
define('LOGGED_IN_KEY', 'fsdb-smoke-logged-in-key');
define('NONCE_KEY', 'fsdb-smoke-nonce-key');
define('AUTH_SALT', 'fsdb-smoke-auth-salt');
define('SECURE_AUTH_SALT', 'fsdb-smoke-secure-auth-salt');
define('LOGGED_IN_SALT', 'fsdb-smoke-logged-in-salt');
define('NONCE_SALT', 'fsdb-smoke-nonce-salt');

$table_prefix = 'wptests_';

define('WP_TESTS_DOMAIN', 'example.org');
define('WP_TESTS_EMAIL', 'admin@example.org');
define('WP_TESTS_TITLE', 'fsdb smoke');
define('WP_PHP_BINARY', 'php');
define('WPLANG', '');
