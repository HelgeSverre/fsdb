/// Coordinate transformations and domain checks on SQL geometry values.
module internal Fsdb.GeometryCoordinates

open System
open Fsdb.SpatialReferenceSystems
open Fsdb.Value

type internal CoordinateDomainError =
    | LatitudeOutOfRange of float
    | LongitudeOutOfRange of float

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

/// SQL's SRID-prefixed representation and the internal model have opposite
/// geographic axis order. The same swap converts in either direction.
let internal swapMySqlGeographicAxes (geometry: Geometry) =
    if isGeographic geometry.Srid then withAxisOrder LongitudeLatitude geometry else geometry

let rec private tryPickCoordinate check = function
    | GEmpty -> None
    | GPoint(x, y) -> check (x, y)
    | GLineString points
    | GMultiPoint points -> List.tryPick check points
    | GPolygon rings
    | GMultiLineString rings -> List.tryPick (List.tryPick check) rings
    | GMultiPolygon polygons -> List.tryPick (List.tryPick (List.tryPick check)) polygons
    | GGeometryCollection geometries -> List.tryPick (fun geometry -> tryPickCoordinate check geometry.Shape) geometries

let internal hasNonFiniteCoordinates (geometry: Geometry) =
    geometry.Shape
    |> tryPickCoordinate (fun (x, y) -> if Double.IsFinite x && Double.IsFinite y then None else Some())
    |> Option.isSome

let internal tryPointDomainError (latitude, longitude) =
    if latitude < -90.0 || latitude > 90.0 then
        Some(LatitudeOutOfRange latitude)
    elif longitude <= -180.0 || longitude > 180.0 then
        Some(LongitudeOutOfRange longitude)
    else
        None

let internal tryCoordinateDomainError (geometry: Geometry) =
    tryPickCoordinate tryPointDomainError geometry.Shape

let internal tryGeometryParameterDomainError (geometry: Geometry) =
    if not (isGeographic geometry.Srid) then None
    else
        // MySQL checks incoming geographic operation parameters in radians.
        let check (latitude, longitude) =
            let lon = longitude * geographicRadiansPerDegree
            let lat = latitude * geographicRadiansPerDegree
            let pole = Math.PI / 2.0
            if lon <= -Math.PI || lon > Math.PI then
                Some(LongitudeOutOfRange(lon / geographicRadiansPerDegree))
            elif lat < -pole || lat > pole then
                Some(LatitudeOutOfRange(lat / geographicRadiansPerDegree))
            else None
        tryPickCoordinate check geometry.Shape

let internal tryTransformedCoordinateDomainError (geometry: Geometry) =
    // MySQL retains tiny projection roundoff beyond either antimeridian.
    geometry.Shape
    |> tryPickCoordinate (fun point ->
        match tryPointDomainError point with
        | Some(LongitudeOutOfRange longitude) when abs (abs longitude - 180.0) < antimeridianRoundoff -> None
        | error -> error)
