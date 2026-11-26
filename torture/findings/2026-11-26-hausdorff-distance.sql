SET @a=ST_GeomFromText('LINESTRING(0 0,1 0)'),
    @b=ST_GeomFromText('LINESTRING(0 0,1 0,10 0)'),
    @point=ST_GeomFromText('POINT(0 0)'),
    @near=ST_GeomFromText('MULTIPOINT((0 0),(1 0))'),
    @far=ST_GeomFromText('MULTIPOINT((1 0),(100 0))'),
    @wide=ST_GeomFromText('MULTIPOINT((0 0),(1 0),(10 0))'),
    @multi=ST_GeomFromText('MULTILINESTRING((0 0,1 0),(10 0,11 0))'),
    @one=ST_GeomFromText('MULTILINESTRING((0 0,2 0))'),
    @empty=ST_GeomFromText('GEOMETRYCOLLECTION(GEOMETRYCOLLECTION EMPTY)'),
    @collection=ST_GeomFromText('GEOMETRYCOLLECTION(POINT(0 0))');
SET @geo=ST_GeomFromText('LINESTRING(0 0,0 1)',4326),
    @geoTarget=ST_GeomFromText('LINESTRING(0 0,1 1)',4326),
    @geoMulti=ST_GeomFromText('MULTILINESTRING((0 0,0 1),(1 0,1 1))',4326),
    @geoPoint=ST_GeomFromText('POINT(0 0)',4326),
    @geoPoints=ST_GeomFromText('MULTIPOINT((1 0),(5 0))',4326),
    @projected=ST_GeomFromText('LINESTRING(0 0,1000 0)',3857),
    @projectedTarget=ST_GeomFromText('LINESTRING(0 1000,1000 1000)',3857);

SELECT ST_HausdorffDistance(@a,@b), ST_HausdorffDistance(@b,@a),
       ST_HausdorffDistance(@point,@far), ST_HausdorffDistance(@far,@point),
       ST_HausdorffDistance(@near,@wide), ST_HausdorffDistance(@wide,@near);
SELECT ST_HausdorffDistance(@a,@multi), ST_HausdorffDistance(@multi,@a),
       ST_HausdorffDistance(@multi,@one), ST_HausdorffDistance(@one,@multi),
       ST_HausdorffDistance(@multi,@multi);
SELECT ROUND(ST_HausdorffDistance(@geo,@geoTarget),6),
       ROUND(ST_HausdorffDistance(@geo,@geoMulti),6),
       ROUND(ST_HausdorffDistance(@geoMulti,@geo),6),
       ROUND(ST_HausdorffDistance(@geoPoint,@geoPoints),6),
       ROUND(ST_HausdorffDistance(@geoPoints,@geoPoint,'kilometre'),6);
SELECT ST_HausdorffDistance(@projected,@projectedTarget),
       ST_HausdorffDistance(@projected,@projectedTarget,'kilometre');
SELECT ST_HausdorffDistance(
    X'E61000000102000000020000000000000000005E4000000000000044400000000000405E400000000000804440',
    ST_GeomFromText('LINESTRING(40 120,41 121)',4326));
SELECT ST_HausdorffDistance(@empty,@a,'bogus'), ST_HausdorffDistance('x',NULL),
       ST_HausdorffDistance(@a,@b,NULL);

SELECT ST_HausdorffDistance(@point,@point);
SELECT ST_HausdorffDistance(@a,@near);
SELECT ST_HausdorffDistance(@near,@a);
SELECT ST_HausdorffDistance(@point,@multi);
SELECT ST_HausdorffDistance(@collection,@a);
SELECT ST_HausdorffDistance(@geoPoint,@geo,NULL);
SELECT ST_HausdorffDistance(@geo,@a);
SELECT ST_HausdorffDistance(@a,@b,'bogus');
SELECT ST_HausdorffDistance(@geo,@geoTarget,'bogus');
SELECT ST_HausdorffDistance('x',@a,NULL);
SELECT ST_HausdorffDistance(ST_SwapXY(ST_GeomFromText('LINESTRING(10 20,10 150)',4326)),@geo);
SELECT ST_HausdorffDistance(ST_GeomFromText('LINESTRING(0 0,1e200 0)'),@a,'bogus');
SELECT ST_HausdorffDistance(@a);

PREPARE hd_plain FROM 'SELECT ST_HausdorffDistance(?,?)';
EXECUTE hd_plain USING @b,@a;
DEALLOCATE PREPARE hd_plain;
SET @unit='kilometre';
PREPARE hd_unit FROM 'SELECT ST_HausdorffDistance(?,?,?)';
EXECUTE hd_unit USING @projected,@projectedTarget,@unit;
DEALLOCATE PREPARE hd_unit;
PREPARE hd_invalid FROM 'SELECT ST_HausdorffDistance(?)';
