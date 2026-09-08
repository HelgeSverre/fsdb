module Fsdb.GeometryOperations

open System
open NetTopologySuite.IO
open NetTopologySuite.Operation.Buffer
open NetTopologySuite.Operation.Overlay
open NetTopologySuite.Operation.OverlayNG
open Fsdb.Value

type OverlayKind =
    | Intersection
    | Union
    | Difference
    | SymmetricDifference

let private toNts (geometry: Geometry) =
    let value = WKBReader().Read(geometryToWkb geometry)
    value.SRID <- geometry.Srid
    value

let private fromNts srid (geometry: NetTopologySuite.Geometries.Geometry) =
    if geometry.IsEmpty then
        Ok { Srid = srid; Shape = GEmpty }
    else
        match geometry |> WKBWriter().Write |> tryGeometryFromWkb srid with
        | Some result -> Ok result
        | None -> Error "the result cannot be represented as a MySQL geometry"

let private attempt srid operation =
    try
        operation () |> fromNts srid
    with
    | :? NetTopologySuite.Geometries.TopologyException as error -> Error error.Message
    | :? ParseException as error -> Error error.Message
    | :? ArgumentException as error -> Error error.Message

let overlay kind (first: Geometry) (second: Geometry) =
    let spatialFunction =
        match kind with
        | Intersection -> SpatialFunction.Intersection
        | Union -> SpatialFunction.Union
        | Difference -> SpatialFunction.Difference
        | SymmetricDifference -> SpatialFunction.SymDifference

    attempt first.Srid (fun () -> OverlayNGRobust.Overlay(toNts first, toNts second, spatialFunction))

let buffer distance (geometry: Geometry) =
    attempt geometry.Srid (fun () -> BufferOp.Buffer(toNts geometry, distance))
