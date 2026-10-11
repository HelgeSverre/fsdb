# Geographic `ST_DISTANCE` for multipoints

Status: implemented for point/multipoint pairs in both orders and for two
multipoints. Geographic line and polygon distance remain open.

MySQL 8.4.11 on the digest-pinned `torture/compose.yaml` image reports the
minimum ellipsoidal point-pair distance. For EPSG 4326, the distance from
`MULTIPOINT((0 0),(1 0))` to `POINT(2 0)` is **110573.82008001186 metres**,
the same as `POINT(1 0)` to `POINT(2 0)`. Reversing the operands and replacing
the second point with `MULTIPOINT((2 0),(4 0))` give the same result. With the
`kilometre` unit argument, both closest-pair queries return
**110.57382008001187**.

The focused regression compares each multipoint shape with its nearest
point-pair baseline within 0.00001 units. Fsdb computes these distances with
its existing geographic point-distance strategy and applies the existing unit
conversion. It previously returned error 1235 for every non-point geographic
`ST_DISTANCE` call.

Oracle query:

```sql
SELECT ST_Distance(
  ST_GeomFromText('MULTIPOINT((0 0),(1 0))',4326),
  ST_GeomFromText('POINT(2 0)',4326));
SELECT ST_Distance(
  ST_GeomFromText('POINT(1 0)',4326),
  ST_GeomFromText('POINT(2 0)',4326));
SELECT ST_Distance(
  ST_GeomFromText('MULTIPOINT((0 0),(1 0))',4326),
  ST_GeomFromText('MULTIPOINT((2 0),(4 0))',4326));
```
