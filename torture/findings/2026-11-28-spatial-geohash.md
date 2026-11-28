# Spatial geohash functions

`ST_GeoHash` accepts longitude, latitude, and a maximum length from 1 to
100. Its two-argument form accepts a point in SRID 0 or 4326. Encoding
stops early when both coordinates are exactly at a cell's center. The
longitude and latitude limits are inclusive.

`ST_LatFromGeoHash` and `ST_LongFromGeoHash` accept uppercase or lowercase
hashes and return the rounded center, with the rounding kept inside the
cell. `ST_PointFromGeoHash` attaches the requested SRID and uses its axis
order for WKT output. The coordinates are decoded in degrees even when
the requested SRID is projected.
[MySQL spatial geohash reference](https://dev.mysql.com/doc/refman/8.4/en/spatial-geohash-functions.html)

```sql
SELECT ST_GeoHash(-0.1,51.5,12);
-- gcpuvxr1jzfd

SELECT ST_GeoHash(22.5,22.5,100);
-- s

SELECT ST_AsText(ST_PointFromGeoHash('U4PRUYDQQVJ',4326));
-- POINT(57.64911 10.40744)
```

The digest-pinned MySQL 8.4.11 comparison covered 300 seeded coordinate
pairs and 300 seeded hashes, including inputs from one to 600 characters.
It compared the encoded text, both decoded coordinates, and the exact
MySQL bytes of the decoded WGS 84 point. The SQL probes cover world
boundaries, length and type checks, NULLs, invalid hashes, unsupported
point-encoding SRIDs, and integer and double conversion warnings.
A separate 300-pair comparison matched the encoded hashes from both
SRID 0 and WGS 84 points.

Prepared parameter and result descriptors matched in `utf8mb4` and
`latin1`. Sixteen binary executions matched values, errors, and warnings,
including a geometry bound as a BLOB and coordinate strings with trailing
text.

Both engines now check argument types during `PREPARE` and before executing
ordinary statements that yield no rows. For example,
`PREPARE s FROM 'SELECT ST_LatFromGeoHash(42)'` returns 3064 during preparation.
An empty hash is valid text at prepare time and returns 1411 on execution.
A bit string can supply the binary point argument to `ST_GeoHash`, and
execution returns 3037 if those bytes cannot hold a geometry.
The pinned MySQL comparison covered 60 preparations and 11 ordinary
statements, including literal, column, scalar-subquery, and placeholder
arguments, empty sources, and short-circuited expressions.

The text and geometry comparison passed in memory and WAL modes. After
reopening the WAL database, the generated hash column still supported its
indexed lookup and the decoded point was readable from the materialized
table.
