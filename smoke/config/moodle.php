<?php

unset($CFG);
global $CFG;
$CFG = new stdClass();
$CFG->dbtype = 'mysqli';
$CFG->dblibrary = 'native';
$CFG->dbhost = 'fsdb';
$CFG->dbname = 'fsdb_moodle';
$CFG->dbuser = 'root';
$CFG->dbpass = '';
$CFG->prefix = 'mdl_';
$CFG->dboptions = [
    'dbpersist' => false,
    'dbsocket' => false,
    'dbport' => 3306,
    'dbcollation' => 'utf8mb4_bin',
    'dbtransactions' => true,
];
$CFG->wwwroot = 'http://moodle.invalid';
$CFG->dataroot = '/opt/moodledata';
$CFG->admin = 'admin';
$CFG->directorypermissions = 02777;
$CFG->phpunit_prefix = 'phpu_';
$CFG->phpunit_dataroot = '/opt/phpu_moodledata';

require_once(__DIR__ . '/lib/setup.php');
