SET @a = ST_GeomFromText('LINESTRING(0 0,2 0,4 0)'),
    @b = ST_GeomFromText('LINESTRING(0 0,1 1,4 0)'),
    @short = ST_GeomFromText('LINESTRING(0 0,4 0)'),
    @reverse = ST_GeomFromText('LINESTRING(4 0,2 0,0 0)'),
    @duplicate = ST_GeomFromText('LINESTRING(0 0,0 0,2 0,4 0)'),
    @point = ST_GeomFromText('POINT(0 0)'),
    @empty = ST_GeomFromText('GEOMETRYCOLLECTION(GEOMETRYCOLLECTION EMPTY)'),
    @collection = ST_GeomFromText('GEOMETRYCOLLECTION(POINT(0 0))');
SET @g = ST_GeomFromText('LINESTRING(0 0,0 1)',4326),
    @h = ST_GeomFromText('LINESTRING(0 0,1 1)',4326),
    @coincident = ST_GeomFromText('LINESTRING(-89 120,40 120)',4326),
    @gp = ST_GeomFromText('POINT(-89 120)',4326),
    @tiny_start = ST_GeomFromText('LINESTRING(0 0,0 0)',4326),
    @tiny_end = ST_GeomFromText('LINESTRING(0 0,0.00000001 0)',4326),
    @ge = ST_GeomFromText('GEOMETRYCOLLECTION EMPTY',4326),
    @x = ST_GeomFromText('LINESTRING(0 0,0 1000)',3857),
    @y = ST_GeomFromText('LINESTRING(0 0,1000 1000)',3857),
    @xp = ST_GeomFromText('POINT(0 0)',3857);

SELECT ST_FrechetDistance(@a,@b), ST_FrechetDistance(@a,@short),
       ST_FrechetDistance(@short,@a), ST_FrechetDistance(@a,@reverse),
       ST_FrechetDistance(@a,@duplicate);
SELECT ROUND(ST_FrechetDistance(@g,@h),6),
       ROUND(ST_FrechetDistance(@g,@h,'kilometre'),6),
       ST_FrechetDistance(@x,@y), ST_FrechetDistance(@x,@y,'kilometre');
SELECT ST_FrechetDistance(@coincident,@coincident), ST_Distance(@gp,@gp),
       ROUND(ST_FrechetDistance(@tiny_start,@tiny_end),12);
SELECT ST_FrechetDistance(
    X'E61000000102000000020000000000000000005E4000000000000044400000000000405E400000000000804440',
    ST_GeomFromText('LINESTRING(40 120,41 121)',4326));
SELECT ST_FrechetDistance(@empty,@point,'bogus'), ST_FrechetDistance('x',NULL),
       ST_FrechetDistance(@a,@a,NULL);

SELECT ST_FrechetDistance(@ge,@a);
SELECT ST_FrechetDistance(@a,@point,NULL);
SELECT ST_FrechetDistance(@gp,@g,'bogus');
SELECT ST_FrechetDistance(@xp,@x);
SELECT ST_FrechetDistance(@collection,@point);
SELECT ST_FrechetDistance(@a,@a,'bogus');
SELECT ST_FrechetDistance(@g,@h,'bogus');
SELECT ST_FrechetDistance('x',@a,NULL);
SELECT ST_FrechetDistance(
    X'3F420F0001020000000200000000000000000000000000000000000000000000000000F03F0000000000000000',
    @a);
SELECT ST_FrechetDistance(ST_SwapXY(ST_GeomFromText('LINESTRING(10 20,10 150)',4326)),@g);
SELECT ST_FrechetDistance(ST_GeomFromText('LINESTRING(0 0,1e200 0)'),@a,'bogus');
SELECT ST_FrechetDistance(@a);

PREPARE distance_query FROM 'SELECT ST_FrechetDistance(?,?)';
EXECUTE distance_query USING @a,@b;
DEALLOCATE PREPARE distance_query;
SET @unit = 'kilometre';
PREPARE distance_with_unit FROM 'SELECT ROUND(ST_FrechetDistance(?,?,?),6)';
EXECUTE distance_with_unit USING @g,@h,@unit;
DEALLOCATE PREPARE distance_with_unit;
PREPARE invalid_distance_query FROM 'SELECT ST_FrechetDistance(?)';
