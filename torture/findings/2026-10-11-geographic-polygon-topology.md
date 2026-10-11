# Geographic polygon topology

MySQL 8.4.11 distinguishes geographic polygon overlap, containment,
touching, and equality. With EPSG 4326's latitude-longitude text order,
let the first polygon be `POLYGON((0 0,0 4,4 4,4 0,0 0))`.

| Second polygon | `Touches` | `Overlaps` | `Contains` | `Equals` |
|---|---:|---:|---:|---:|
| `POLYGON((2 2,2 6,6 6,6 2,2 2))` | 0 | 1 | 0 | 0 |
| `POLYGON((1 1,1 2,2 2,2 1,1 1))` | 0 | 0 | 1 | 0 |
| `POLYGON((1 -2,3 -2,3 0,1 0,1 -2))` | 1 | 0 | 0 | 0 |
| `POLYGON((4 1,4 3,6 3,6 1,4 1))` | 0 | 1 | 0 | 0 |
| `POLYGON((4 4,4 6,6 6,6 4,4 4))` | 1 | 0 | 0 | 0 |
| Identical or reversed ring | 0 | 0 | 1 | 1 |
| Disjoint polygon | 0 | 0 | 0 | 0 |

The apparently adjacent fourth case overlaps because its east-west
geodesic boundary differs from the first polygon's curved edge. A shared
meridian edge in the third case touches. A polygon wholly inside a hole is
disjoint. MySQL also distinguishes identical holes from a polygon with the
same exterior and no hole, and recognizes containment by a multipolygon.

Fsdb samples polygon geodesic boundaries against the opposite polygon,
recognizes exactly shared segments before interpolation, and uses its
existing polygon distance path for contact. Thomas interpolation can move
samples from a shared boundary by about 0.1 m; a bounded 0.2 m fallback
classifies split segments until exact geographic boundary intersection
replaces sampling. The focused Expecto regression and differential
contracts cover the cases above. Overlaps or gaps thinner than this
tolerance, and unsampled excursions between boundary samples, remain
precision limits.
