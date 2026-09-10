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
      InverseFlattening: float option }

let private wgs84Definition =
    "GEOGCS[\"WGS 84\",DATUM[\"World Geodetic System 1984\",SPHEROID[\"WGS 84\",6378137,298.257223563,AUTHORITY[\"EPSG\",\"7030\"]],AUTHORITY[\"EPSG\",\"6326\"]],PRIMEM[\"Greenwich\",0,AUTHORITY[\"EPSG\",\"8901\"]],UNIT[\"degree\",0.017453292519943278,AUTHORITY[\"EPSG\",\"9122\"]],AXIS[\"Lat\",NORTH],AXIS[\"Lon\",EAST],AUTHORITY[\"EPSG\",\"4326\"]]"

let internal all =
    [ { Name = ""
        Srid = 0
        Organization = None
        OrganizationCoordinateSystemId = None
        Definition = ""
        Description = None
        AxisOrder = LongitudeLatitude
        SemiMajorAxis = None
        InverseFlattening = None }
      { Name = "WGS 84"
        Srid = 4326
        Organization = Some "EPSG"
        OrganizationCoordinateSystemId = Some 4326
        Definition = wgs84Definition
        Description = None
        AxisOrder = LatitudeLongitude
        SemiMajorAxis = Some 6378137.0
        InverseFlattening = Some 298.257223563 } ]

let internal tryFind srid = all |> List.tryFind (fun referenceSystem -> referenceSystem.Srid = srid)

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

and private mapGeometry transform (geometry: Geometry) =
    { geometry with Shape = mapShape transform geometry.Shape }

let internal withAxisOrder axisOrder (geometry: Geometry) =
    match axisOrder with
    | LatitudeLongitude -> geometry
    | LongitudeLatitude -> mapGeometry (fun (longitude, latitude) -> latitude, longitude) geometry

let rec private coordinates = function
    | GEmpty -> []
    | GPoint(x, y) -> [ x, y ]
    | GLineString points
    | GMultiPoint points -> points
    | GPolygon rings
    | GMultiLineString rings -> List.concat rings
    | GMultiPolygon polygons -> polygons |> List.collect List.concat
    | GGeometryCollection geometries -> geometries |> List.collect (fun geometry -> coordinates geometry.Shape)

let internal tryCoordinateDomainError (geometry: Geometry) =
    coordinates geometry.Shape
    |> List.tryPick (fun (latitude, longitude) ->
        if latitude < -90.0 || latitude > 90.0 then
            Some(LatitudeOutOfRange latitude)
        elif longitude <= -180.0 || longitude > 180.0 then
            Some(LongitudeOutOfRange longitude)
        else
            None)
