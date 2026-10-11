# Geographic line-to-line distance

Status: implemented for `LINESTRING` and `MULTILINESTRING` pairs in supported
geographic SRSs. Geographic polygon distance remains open.

MySQL 8.4.11 returns zero for crossing, overlapping, and antimeridian-crossing
segments. For disjoint EPSG 4326 lines, it returns 111252.13055897616 metres
between `LINESTRING(0 0,2 0)` and `LINESTRING(0 1,2 1)`.

Endpoint handling matters for long arcs. MySQL returns 1105843.247087976
metres between `LINESTRING(0 0,0 170)` and `LINESTRING(10 20,10 150)`.
The nearest location is an original endpoint, even though geographic line
interpolation can slightly overshoot that endpoint because its direct and
inverse approximations differ. The distance search first checks azimuth
projections and uses the original endpoint when the nearest position lies
outside the segment. Fsdb checks crossings on sampled geographic arcs and
uses endpoint-to-segment distances when they do not cross. Crossings within
the sampling error of a tangent boundary remain unverified. MySQL also
distinguishes a curved-line near miss of
1.617217236128797 metres from a crossing when a second line starts at
latitude 50.10784 versus 50.1078 at longitude 5.

Oracle examples:

```sql
SELECT ST_Distance(
  ST_GeomFromText('LINESTRING(0 0,0 2)',4326),
  ST_GeomFromText('LINESTRING(-1 1,1 1)',4326));

SELECT ST_Distance(
  ST_GeomFromText('LINESTRING(50 0,50 10)',4326),
  ST_GeomFromText('LINESTRING(50.10784 5,51 5)',4326));
```
