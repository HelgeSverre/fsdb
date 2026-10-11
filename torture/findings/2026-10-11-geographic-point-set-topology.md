# Geographic point-set topology

Status: implemented for geographic `POINT` and `MULTIPOINT` pairs in
`ST_INTERSECTS`, `ST_DISJOINT`, `ST_CONTAINS`, `ST_WITHIN`, `ST_EQUALS`, and
`ST_TOUCHES`. Point/polygon topology is also covered; topology involving
geographic lines or polygon pairs remains open.

On MySQL 8.4.11, EPSG 4326 `POINT(1 0)` intersects and is within
`MULTIPOINT((0 0),(1 0))`; the multipoint contains that point. A missing
`POINT(2 0)` is disjoint. Reordering multipoint members preserves `ST_EQUALS`.
`ST_TOUCHES` between a point and multipoint returns `NULL`, as it does for
the existing point-only planar rule. MySQL also reports `0,1,NULL,1` for
`ST_DISJOINT`, `ST_WITHIN`, `ST_TOUCHES`, and `ST_EQUALS` on a geographic point
compared with itself; the former refusal test now pins those values. The
focused regression expects `1,1,1,1,1,0,NULL` for the matching, reordered,
missing, and touches cases.

Fsdb previously refused every nonzero-SRID topology predicate with error
1235. The point-set path now checks equal SRIDs and uses the existing
coordinate-membership predicates. It still rejects geographic line/polygon
shapes, whose topology depends on curved edges rather than planar segments.

Oracle example:

```sql
SELECT
 ST_Intersects(ST_GeomFromText('POINT(1 0)',4326),
               ST_GeomFromText('MULTIPOINT((0 0),(1 0))',4326)),
 ST_Contains(ST_GeomFromText('MULTIPOINT((0 0),(1 0))',4326),
             ST_GeomFromText('POINT(1 0)',4326)),
 ST_Touches(ST_GeomFromText('POINT(1 0)',4326),
            ST_GeomFromText('MULTIPOINT((0 0),(1 0))',4326));
```
