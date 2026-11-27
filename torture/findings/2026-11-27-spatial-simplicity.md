# ST_IsSimple in Cartesian and geographic reference systems

MySQL 8.4.11 returns a Boolean for simplicity. A self-crossing line
returns `0`, while a bow-tie polygon without consecutive repeated
positions can return `1`. A multipolygon with overlapping components
can also return `1`. The same polygons as separate geometry collection
members return `0` because their interiors intersect.

Fsdb now checks consecutive positions for lines and polygon rings,
duplicate positions for multipoints, and both self-intersections and
member contacts for multilines. Two open lines can share endpoints.
A closed line's endpoint belongs to its interior for this check.
Geographic lines use geodesic segment contacts, including dateline
crossings. Collection checks account for nested linear boundaries and
the point members that survive union with their collection's lines and
polygons. In the audited nested cases, an uncovered point member can
make MySQL reject an otherwise boundary-only contact.

The SQL fixture matched 26 result rows and six errors in memory and on
disk. It covers `NULL`, malformed data, unknown SRIDs, and geographic
coordinate ranges. Prepared metadata reports a geometry parameter and
a `LONGLONG` result with display width 1. [SQL fixture](2026-11-27-spatial-simplicity.sql)

Two 4,500-case runs, using seeds 1917 and 7312, matched all results
across SRIDs 0, 3857, and 4326. The runs covered multipoints, lines,
multilines, polygons in multipart inputs, mixed and nested collections,
and dateline lines. The existing validity corpus also matched all 5,845
checks after sharing its geodesic ring logic. [Geographic validity corpus](2026-11-17-geographic-polygon-validity.md)

See the [MySQL property functions](https://dev.mysql.com/doc/refman/8.4/en/gis-general-property-functions.html)
and [MySQL's collection simplicity implementation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/is_simple.cc#L98-L132).
