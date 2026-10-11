# WGS 84 UTM zone family

MySQL 8.4.11 has 120 WGS 84 UTM projected SRSs: EPSG 32601–32660 for
northern zones and EPSG 32701–32760 for southern zones. Each zone has a
six-degree central-meridian step, 0.9996 scale, and 500,000 m false easting.
Southern zones add 10,000,000 m false northing. Fsdb now generates the full
catalog family and uses the shared WGS 84 Transverse Mercator projection for
forward and reverse `ST_Transform`.

The MySQL oracle samples include zone 1N at −177° longitude, zone 60N at
177°, zone 34S around Cape Town, and zone 60S at 45°S. The sampled projected
coordinates agree within 1 mm and inverse coordinates within 1e-8 degrees.
The differential contract checks the 120-row count, exact definitions for
every zone, and southern forward and reverse coordinates.

This covers the WGS 84 UTM family, not arbitrary SRS definitions. MySQL's
projection behavior far outside a zone remains unverified.
