# Geographic edge topology audit

MySQL 8.4.11 is the oracle for EPSG 4326. A native-server differential probe
compared `ST_Crosses`, `ST_Touches`, `ST_Within`, `ST_Contains`, and
`ST_Intersects` for 337 line/polygon pairs. The corpus varied lines along and
near the edges of two squares and a polygon with a hole, plus a multipart
polygon where an edge overlap precedes an interior crossing. The probe is
[`geographic-edge-oracle.py`](../scripts/geographic-edge-oracle.py).

FSDB previously treated a line that extended past a polygon's exterior edge
as crossing because interpolated geodesic samples drifted from the shared
coordinate edge. A bounded overlap interval now preserves boundary contact
only on the common exterior-edge portion. The multipart case confirms that
the later interior crossing still counts. MySQL also reports a point exactly
on a hole's meridian edge as touching; the point classifier now records that
case separately from other near-hole samples that MySQL treats as filled area.

The current corpus agrees on 335 of 337 line/polygon pairs. The remaining
disagreements are these near-hole lines, for which MySQL reports crossing and
FSDB reports touching:

```sql
SET @area = ST_GeomFromText(
  'POLYGON((0 0,0 4,4 4,4 0,0 0),(1 1,3 1,3 3,1 3,1 1))',4326);
SELECT ST_Crosses(ST_GeomFromText('LINESTRING(1 1.00001,3 1.00001)',4326),@area),
       ST_Touches(ST_GeomFromText('LINESTRING(1 1.00001,3 1.00001)',4326),@area);
```

The same difference occurs at longitude `1.001`. Separate point probes also
find near-corner differences, including `POINT(3 1.00001)` and offsets only
`0.0000001` degree from a hole corner. This audit is a synthetic boundary
corpus, not a general geographic parity claim.

A narrower MySQL 8.4.11 probe shows why classifying the line solely from its
endpoints is insufficient. `POINT(1 1.00001)` is within the polygon, while
`POINT(3 1.00001)` is outside it; the same within/outside split occurs for
`POINT(1 2.999)` and `POINT(3 2.999)`. Yet every tested line from latitude
`1` to `1.000001`, `1.0001`, `1.01`, `1.1`, `2`, or `3` crosses beside longitude
`1.00001` and only touches beside longitude `2.999`. In fsdb, geographic
point-to-hole-edge distance can round to zero for both near-corner endpoints
even though the exact point-on-line check says they are off the edge. Treating
those rounded distances as exact boundary contact explains the first pair of
mismatches; replacing the distance check alone shifts the mismatches to the
opposite edge. A fix needs line/ring topology at the corner, not a wider or
narrower distance tolerance.
