/// Built-in spatial reference systems and linear units shared by constructors,
/// operations, and the corresponding information-schema tables.
module Fsdb.SpatialReferenceSystems

open System
open Fsdb.Value

type internal AxisOrder =
    | LatitudeLongitude
    | LongitudeLatitude

type internal CoordinateDomainError =
    | LatitudeOutOfRange of float
    | LongitudeOutOfRange of float

type internal SpatialReferenceSystem =
    { Name: string
      Srid: int
      Organization: string option
      OrganizationCoordinateSystemId: int option
      Definition: string
      Description: string option
      AxisOrder: AxisOrder
      SemiMajorAxis: float option
      InverseFlattening: float option
      /// Projected coordinate-unit scale; absent for geographic and unitless SRID 0.
      LinearUnitInMetres: float option }

let private wgs84Definition =
    "GEOGCS[\"WGS 84\",DATUM[\"World Geodetic System 1984\",SPHEROID[\"WGS 84\",6378137,298.257223563,AUTHORITY[\"EPSG\",\"7030\"]],AUTHORITY[\"EPSG\",\"6326\"]],PRIMEM[\"Greenwich\",0,AUTHORITY[\"EPSG\",\"8901\"]],UNIT[\"degree\",0.017453292519943278,AUTHORITY[\"EPSG\",\"9122\"]],AXIS[\"Lat\",NORTH],AXIS[\"Lon\",EAST],AUTHORITY[\"EPSG\",\"4326\"]]"

let private pseudoMercatorDefinition =
    [ "PROJCS[\"WGS 84 / Pseudo-Mercator\","
      wgs84Definition
      ",PROJECTION[\"Popular Visualisation Pseudo Mercator\",AUTHORITY[\"EPSG\",\"1024\"]]"
      ",PARAMETER[\"Latitude of natural origin\",0,AUTHORITY[\"EPSG\",\"8801\"]]"
      ",PARAMETER[\"Longitude of natural origin\",0,AUTHORITY[\"EPSG\",\"8802\"]]"
      ",PARAMETER[\"False easting\",0,AUTHORITY[\"EPSG\",\"8806\"]]"
      ",PARAMETER[\"False northing\",0,AUTHORITY[\"EPSG\",\"8807\"]]"
      ",UNIT[\"metre\",1,AUTHORITY[\"EPSG\",\"9001\"]],AXIS[\"X\",EAST],AXIS[\"Y\",NORTH],AUTHORITY[\"EPSG\",\"3857\"]]" ]
    |> String.concat ""

let private worldMercatorDefinition =
    [ "PROJCS[\"WGS 84 / World Mercator\","
      wgs84Definition
      ",PROJECTION[\"Mercator (variant A)\",AUTHORITY[\"EPSG\",\"9804\"]]"
      ",PARAMETER[\"Latitude of natural origin\",0,AUTHORITY[\"EPSG\",\"8801\"]]"
      ",PARAMETER[\"Longitude of natural origin\",0,AUTHORITY[\"EPSG\",\"8802\"]]"
      ",PARAMETER[\"Scale factor at natural origin\",1,AUTHORITY[\"EPSG\",\"8805\"]]"
      ",PARAMETER[\"False easting\",0,AUTHORITY[\"EPSG\",\"8806\"]]"
      ",PARAMETER[\"False northing\",0,AUTHORITY[\"EPSG\",\"8807\"]]"
      ",UNIT[\"metre\",1,AUTHORITY[\"EPSG\",\"9001\"]],AXIS[\"E\",EAST],AXIS[\"N\",NORTH],AUTHORITY[\"EPSG\",\"3395\"]]" ]
    |> String.concat ""

let internal all =
    [ { Name = ""
        Srid = 0
        Organization = None
        OrganizationCoordinateSystemId = None
        Definition = ""
        Description = None
        AxisOrder = LongitudeLatitude
        SemiMajorAxis = None
        InverseFlattening = None
        LinearUnitInMetres = None }
      { Name = "WGS 84"
        Srid = 4326
        Organization = Some "EPSG"
        OrganizationCoordinateSystemId = Some 4326
        Definition = wgs84Definition
        Description = None
        AxisOrder = LatitudeLongitude
        SemiMajorAxis = Some 6378137.0
        InverseFlattening = Some 298.257223563
        LinearUnitInMetres = None }
      { Name = "WGS 84 / Pseudo-Mercator"
        Srid = 3857
        Organization = Some "EPSG"
        OrganizationCoordinateSystemId = Some 3857
        Definition = pseudoMercatorDefinition
        Description = None
        AxisOrder = LongitudeLatitude
        SemiMajorAxis = None
        InverseFlattening = None
        LinearUnitInMetres = Some 1.0 }
      { Name = "WGS 84 / World Mercator"
        Srid = 3395
        Organization = Some "EPSG"
        OrganizationCoordinateSystemId = Some 3395
        Definition = worldMercatorDefinition
        Description = None
        AxisOrder = LongitudeLatitude
        SemiMajorAxis = None
        InverseFlattening = None
        LinearUnitInMetres = Some 1.0 } ]

let internal tryFind srid = all |> List.tryFind (fun referenceSystem -> referenceSystem.Srid = srid)

let private earthRadius = 6378137.0
let private eccentricity =
    let flattening = 1.0 / 298.257223563
    sqrt (flattening * (2.0 - flattening))

let private pseudoMercatorForward (latitude, longitude) =
    if abs latitude >= 90.0 then
        Double.PositiveInfinity, Double.PositiveInfinity
    else
        earthRadius * longitude * Math.PI / 180.0,
        earthRadius * log (tan (Math.PI / 4.0 + latitude * Math.PI / 360.0))

let private pseudoMercatorInverse (x, y) =
    (2.0 * atan (exp (y / earthRadius)) - Math.PI / 2.0) * 180.0 / Math.PI,
    x * 180.0 / (earthRadius * Math.PI)

let private worldMercatorForward (latitude, longitude) =
    if abs latitude >= 90.0 then
        Double.PositiveInfinity, Double.PositiveInfinity
    else
        let latitudeRadians = latitude * Math.PI / 180.0
        let sine = sin latitudeRadians
        let correction = ((1.0 - eccentricity * sine) / (1.0 + eccentricity * sine)) ** (eccentricity / 2.0)
        earthRadius * longitude * Math.PI / 180.0,
        earthRadius * log (tan (Math.PI / 4.0 + latitudeRadians / 2.0) * correction)

let private worldMercatorInverse (x, y) =
    let t = exp (-y / earthRadius)
    let rec latitudeAt iteration latitude =
        if iteration = 0 then latitude
        else
            let sine = sin latitude
            let correction = ((1.0 - eccentricity * sine) / (1.0 + eccentricity * sine)) ** (eccentricity / 2.0)
            latitudeAt (iteration - 1) (Math.PI / 2.0 - 2.0 * atan (t * correction))
    let latitude = latitudeAt 8 (atan (sinh (y / earthRadius)))
    latitude * 180.0 / Math.PI, x * 180.0 / (earthRadius * Math.PI)

let internal tryCoordinateTransform sourceSrid targetSrid =
    let compose first second point = second (first point)
    match sourceSrid, targetSrid with
    | 4326, 3857 -> Some pseudoMercatorForward
    | 3857, 4326 -> Some pseudoMercatorInverse
    | 4326, 3395 -> Some worldMercatorForward
    | 3395, 4326 -> Some worldMercatorInverse
    | 3857, 3395 -> Some(compose pseudoMercatorInverse worldMercatorForward)
    | 3395, 3857 -> Some(compose worldMercatorInverse pseudoMercatorForward)
    | _ -> None

let internal linearUnits =
    [ "British chain (Benoit 1895 A)", 20.1167824
      "British chain (Benoit 1895 B)", 20.116782494375872
      "British chain (Sears 1922 truncated)", 20.116756
      "British chain (Sears 1922)", 20.116765121552632
      "British foot (1865)", 0.30480083333333335
      "British foot (1936)", 0.3048007491
      "British foot (Benoit 1895 A)", 0.3047997333333333
      "British foot (Benoit 1895 B)", 0.30479973476327077
      "British foot (Sears 1922 truncated)", 0.30479933333333337
      "British foot (Sears 1922)", 0.3047994715386762
      "British link (Benoit 1895 A)", 0.201167824
      "British link (Benoit 1895 B)", 0.2011678249437587
      "British link (Sears 1922 truncated)", 0.20116756
      "British link (Sears 1922)", 0.2011676512155263
      "British yard (Benoit 1895 A)", 0.9143992
      "British yard (Benoit 1895 B)", 0.9143992042898124
      "British yard (Sears 1922 truncated)", 0.914398
      "British yard (Sears 1922)", 0.9143984146160288
      "centimetre", 0.01
      "chain", 20.1168
      "Clarke's chain", 20.1166195164
      "Clarke's foot", 0.3047972654
      "Clarke's link", 0.201166195164
      "Clarke's yard", 0.9143917962
      "fathom", 1.8288
      "foot", 0.3048
      "German legal metre", 1.0000135965
      "Gold Coast foot", 0.3047997101815088
      "Indian foot", 0.30479951024814694
      "Indian foot (1937)", 0.30479841
      "Indian foot (1962)", 0.3047996
      "Indian foot (1975)", 0.3047995
      "Indian yard", 0.9143985307444408
      "Indian yard (1937)", 0.91439523
      "Indian yard (1962)", 0.9143988
      "Indian yard (1975)", 0.9143985
      "kilometre", 1000.0
      "link", 0.201168
      "metre", 1.0
      "millimetre", 0.001
      "nautical mile", 1852.0
      "Statute mile", 1609.344
      "US survey chain", 20.11684023368047
      "US survey foot", 0.30480060960121924
      "US survey link", 0.2011684023368047
      "US survey mile", 1609.3472186944375
      "yard", 0.9144 ]

let internal tryLinearUnit name =
    linearUnits
    |> List.tryPick (fun (registeredName, factor) ->
        if String.Equals(registeredName, name, StringComparison.OrdinalIgnoreCase) then
            Some factor
        else
            None)

let rec private mapShape (transform: float * float -> float * float) shape =
    let mapPoints = List.map transform

    match shape with
    | GEmpty -> GEmpty
    | GPoint(x, y) ->
        let mappedX, mappedY = transform (x, y)
        GPoint(mappedX, mappedY)
    | GLineString points -> GLineString(mapPoints points)
    | GPolygon rings -> GPolygon(List.map mapPoints rings)
    | GMultiPoint points -> GMultiPoint(mapPoints points)
    | GMultiLineString lines -> GMultiLineString(List.map mapPoints lines)
    | GMultiPolygon polygons -> GMultiPolygon(List.map (List.map mapPoints) polygons)
    | GGeometryCollection geometries -> GGeometryCollection(List.map (mapGeometry transform) geometries)

and internal mapGeometry transform (geometry: Geometry) =
    { geometry with Shape = mapShape transform geometry.Shape }

let rec internal withSrid srid (geometry: Geometry) =
    { Srid = srid
      Shape =
        match geometry.Shape with
        | GGeometryCollection children -> GGeometryCollection(List.map (withSrid srid) children)
        | shape -> shape }

let internal withAxisOrder axisOrder (geometry: Geometry) =
    match axisOrder with
    | LatitudeLongitude -> geometry
    | LongitudeLatitude -> mapGeometry (fun (longitude, latitude) -> latitude, longitude) geometry

let rec internal coordinates = function
    | GEmpty -> []
    | GPoint(x, y) -> [ x, y ]
    | GLineString points
    | GMultiPoint points -> points
    | GPolygon rings
    | GMultiLineString rings -> List.concat rings
    | GMultiPolygon polygons -> polygons |> List.collect List.concat
    | GGeometryCollection geometries -> geometries |> List.collect (fun geometry -> coordinates geometry.Shape)

let internal hasNonFiniteCoordinates (geometry: Geometry) =
    coordinates geometry.Shape
    |> List.exists (fun (x, y) -> not (Double.IsFinite x && Double.IsFinite y))

let internal tryPointDomainError (latitude, longitude) =
    if latitude < -90.0 || latitude > 90.0 then
        Some(LatitudeOutOfRange latitude)
    elif longitude <= -180.0 || longitude > 180.0 then
        Some(LongitudeOutOfRange longitude)
    else
        None

let internal tryCoordinateDomainError (geometry: Geometry) =
    coordinates geometry.Shape |> List.tryPick tryPointDomainError
