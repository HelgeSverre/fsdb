namespace Fsdb.Torture

[<RequireQualifiedAccess>]
module TypeMatrix =
    let capabilities =
        [| "t_tiny_int"
           "t_bool"
           "t_small_int"
           "t_medium_int"
           "t_int"
           "t_big_int"
           "t_bit"
           "t_char"
           "t_varchar"
           "t_tiny_text"
           "t_text"
           "t_medium_text"
           "t_long_text"
           "t_binary"
           "t_var_binary"
           "t_tiny_blob"
           "t_blob"
           "t_medium_blob"
           "t_long_blob"
           "t_enum"
           "t_set"
           "t_decimal"
           "t_double"
           "t_float"
           "t_date"
           "t_date_time"
           "t_timestamp"
           "t_time"
           "t_year"
           "t_json"
           "t_geometry" |]

    let columns =
        "c_tiny, c_bool, c_small, c_medium, c_int, c_big, c_bit, c_char, c_varchar, c_tinytext, c_text, c_mediumtext, c_longtext, "
        + "c_binary, c_varbinary, c_tinyblob, c_blob, c_mediumblob, c_longblob, c_enum, c_set, c_decimal, c_double, c_float, "
        + "c_date, c_datetime, c_timestamp, c_time, c_year, c_json, c_geometry"

    let createTable table =
        sprintf
            """CREATE TABLE %s (
                id INT PRIMARY KEY,
                c_tiny TINYINT, c_bool BOOLEAN, c_small SMALLINT,
                c_medium MEDIUMINT, c_int INT, c_big BIGINT, c_bit BIT(9),
                c_char CHAR(4), c_varchar VARCHAR(10), c_tinytext TINYTEXT,
                c_text TEXT, c_mediumtext MEDIUMTEXT, c_longtext LONGTEXT,
                c_binary BINARY(4), c_varbinary VARBINARY(4), c_tinyblob TINYBLOB,
                c_blob BLOB, c_mediumblob MEDIUMBLOB, c_longblob LONGBLOB,
                c_enum ENUM('red','blue'), c_set SET('a','b'),
                c_decimal DECIMAL(10,3), c_double DOUBLE, c_float FLOAT,
                c_date DATE, c_datetime DATETIME(6), c_timestamp TIMESTAMP(6) NULL,
                c_time TIME(6), c_year YEAR, c_json JSON,
                c_geometry GEOMETRY NOT NULL SRID 0
            )"""
            table

    let insert table =
        sprintf
            """INSERT INTO %s VALUES (
                1, -8, TRUE, -32000, 8000000, -2000000000, 9000000000,
                b'100000001', 'ab', 'blå', 'tiny', 'text', 'medium', 'long',
                X'00FF', X'0102', X'03', X'0405', X'0607', X'0809',
                'blue', 'a,b', 12345.678, 1.25, -2.5, '2024-02-29',
                '2024-02-29 12:34:56.123456', '2024-02-29 12:34:56.654321',
                '-34:20:30.123456', 2024, JSON_OBJECT('b', 2, 'a', 1),
                ST_GeomFromText('POINT(1 2)', 0)
            )"""
            table

    let selectColumns table selectedColumns parameter =
        sprintf "SELECT %s FROM %s WHERE id = %s" selectedColumns table parameter

    let select table parameter = selectColumns table columns parameter
