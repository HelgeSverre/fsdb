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

type private UtmDatum =
    | Wgs84
    | Etrs89
    | Nad83

type private UtmZone =
    { Srid: int
      Name: string
      Datum: UtmDatum
      GeographicDefinition: string
      CentralMeridian: int
      FalseNorthing: int
      InverseFlattening: float }

let private wgs84Definition =
    "GEOGCS[\"WGS 84\",DATUM[\"World Geodetic System 1984\",SPHEROID[\"WGS 84\",6378137,298.257223563,AUTHORITY[\"EPSG\",\"7030\"]],AUTHORITY[\"EPSG\",\"6326\"]],PRIMEM[\"Greenwich\",0,AUTHORITY[\"EPSG\",\"8901\"]],UNIT[\"degree\",0.017453292519943278,AUTHORITY[\"EPSG\",\"9122\"]],AXIS[\"Lat\",NORTH],AXIS[\"Lon\",EAST],AUTHORITY[\"EPSG\",\"4326\"]]"

let private unspecifiedWgs84DatumDefinition =
    "GEOGCS[\"Unknown datum based upon the WGS 84 ellipsoid\",DATUM[\"Not specified (based on WGS 84 ellipsoid)\",SPHEROID[\"WGS 84\",6378137,298.257223563,AUTHORITY[\"EPSG\",\"7030\"]],AUTHORITY[\"EPSG\",\"6030\"]],PRIMEM[\"Greenwich\",0,AUTHORITY[\"EPSG\",\"8901\"]],UNIT[\"degree\",0.017453292519943278,AUTHORITY[\"EPSG\",\"9122\"]],AXIS[\"Lat\",NORTH],AXIS[\"Lon\",EAST],AUTHORITY[\"EPSG\",\"4030\"]]"

let private etrs89Definition =
    "GEOGCS[\"ETRS89\",DATUM[\"European Terrestrial Reference System 1989\",SPHEROID[\"GRS 1980\",6378137,298.257222101,AUTHORITY[\"EPSG\",\"7019\"]],TOWGS84[0,0,0,0,0,0,0],AUTHORITY[\"EPSG\",\"6258\"]],PRIMEM[\"Greenwich\",0,AUTHORITY[\"EPSG\",\"8901\"]],UNIT[\"degree\",0.017453292519943278,AUTHORITY[\"EPSG\",\"9122\"]],AXIS[\"Lat\",NORTH],AXIS[\"Lon\",EAST],AUTHORITY[\"EPSG\",\"4258\"]]"

let private nad83Definition =
    "GEOGCS[\"NAD83\",DATUM[\"North American Datum 1983\",SPHEROID[\"GRS 1980\",6378137,298.257222101,AUTHORITY[\"EPSG\",\"7019\"]],TOWGS84[1,1,-1,0,0,0,0],AUTHORITY[\"EPSG\",\"6269\"]],PRIMEM[\"Greenwich\",0,AUTHORITY[\"EPSG\",\"8901\"]],UNIT[\"degree\",0.017453292519943278,AUTHORITY[\"EPSG\",\"9122\"]],AXIS[\"Lat\",NORTH],AXIS[\"Lon\",EAST],AUTHORITY[\"EPSG\",\"4269\"]]"

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

let private utmDefinition name geographicDefinition centralMeridian falseNorthing srid =
    [ sprintf "PROJCS[\"%s\"," name
      geographicDefinition
      ",PROJECTION[\"Transverse Mercator\",AUTHORITY[\"EPSG\",\"9807\"]]"
      ",PARAMETER[\"Latitude of natural origin\",0,AUTHORITY[\"EPSG\",\"8801\"]]"
      sprintf ",PARAMETER[\"Longitude of natural origin\",%d,AUTHORITY[\"EPSG\",\"8802\"]]" centralMeridian
      ",PARAMETER[\"Scale factor at natural origin\",0.9996,AUTHORITY[\"EPSG\",\"8805\"]]"
      ",PARAMETER[\"False easting\",500000,AUTHORITY[\"EPSG\",\"8806\"]]"
      sprintf ",PARAMETER[\"False northing\",%d,AUTHORITY[\"EPSG\",\"8807\"]]" falseNorthing
      sprintf ",UNIT[\"metre\",1,AUTHORITY[\"EPSG\",\"9001\"]],AXIS[\"E\",EAST],AXIS[\"N\",NORTH],AUTHORITY[\"EPSG\",\"%d\"]]" srid ]
    |> String.concat ""

let private utmZone name datum geographicDefinition inverseFlattening baseSrid hemisphere falseNorthing zone =
    { Srid = baseSrid + zone
      Name = sprintf "%s / UTM zone %d%s" name zone hemisphere
      Datum = datum
      GeographicDefinition = geographicDefinition
      CentralMeridian = zone * 6 - 183
      FalseNorthing = falseNorthing
      InverseFlattening = inverseFlattening }

let private wgs84UtmZones =
    [ for hemisphere, baseSrid, falseNorthing in [ "N", 32600, 0; "S", 32700, 10000000 ] do
        for zone in 1..60 do
            utmZone "WGS 84" Wgs84 wgs84Definition 298.257223563 baseSrid hemisphere falseNorthing zone ]

let private etrs89UtmZones =
    [ for zone in 28..37 -> utmZone "ETRS89" Etrs89 etrs89Definition 298.257222101 25800 "N" 0 zone ]

let private nad83UtmZones =
    [ for zone in 1..23 -> utmZone "NAD83" Nad83 nad83Definition 298.257222101 26900 "N" 0 zone ]

let private utmZones = wgs84UtmZones @ etrs89UtmZones @ nad83UtmZones

let private utmSystems =
    utmZones
    |> List.map (fun zone ->
        { Name = zone.Name
          Srid = zone.Srid
          Organization = Some "EPSG"
          OrganizationCoordinateSystemId = Some zone.Srid
          Definition = utmDefinition zone.Name zone.GeographicDefinition zone.CentralMeridian zone.FalseNorthing zone.Srid
          Description = None
          AxisOrder = LongitudeLatitude
          SemiMajorAxis = None
          InverseFlattening = None
          LinearUnitInMetres = Some 1.0 })

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
      { Name = "Unknown datum based upon the WGS 84 ellipsoid"
        Srid = 4030
        Organization = Some "EPSG"
        OrganizationCoordinateSystemId = Some 4030
        Definition = unspecifiedWgs84DatumDefinition
        Description = None
        AxisOrder = LatitudeLongitude
        SemiMajorAxis = Some 6378137.0
        InverseFlattening = Some 298.257223563
        LinearUnitInMetres = None }
      { Name = "ETRS89"
        Srid = 4258
        Organization = Some "EPSG"
        OrganizationCoordinateSystemId = Some 4258
        Definition = etrs89Definition
        Description = None
        AxisOrder = LatitudeLongitude
        SemiMajorAxis = Some 6378137.0
        InverseFlattening = Some 298.257222101
        LinearUnitInMetres = None }
      { Name = "NAD83"
        Srid = 4269
        Organization = Some "EPSG"
        OrganizationCoordinateSystemId = Some 4269
        Definition = nad83Definition
        Description = None
        AxisOrder = LatitudeLongitude
        SemiMajorAxis = Some 6378137.0
        InverseFlattening = Some 298.257222101
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
    @ utmSystems

let internal tryFind srid = all |> List.tryFind (fun referenceSystem -> referenceSystem.Srid = srid)

let internal lacksTowgs84 srid = srid = 4030

let private earthRadius = 6378137.0
let private antimeridianRoundoff = 1e-9
let private eccentricity =
    let flattening = 1.0 / 298.257223563
    sqrt (flattening * (2.0 - flattening))

let private wrapProjectedLongitude longitude =
    // Preserve floating-point noise at the antimeridian while wrapping full turns.
    if abs longitude > 180.0 + antimeridianRoundoff then Math.IEEERemainder(longitude, 360.0)
    else longitude

let private pseudoMercatorForward (latitude, longitude) =
    if abs latitude >= 90.0 then
        Double.PositiveInfinity, Double.PositiveInfinity
    else
        earthRadius * longitude * Math.PI / 180.0,
        earthRadius * log (tan (Math.PI / 4.0 + latitude * Math.PI / 360.0))

let private pseudoMercatorInverse (x, y) =
    (2.0 * atan (exp (y / earthRadius)) - Math.PI / 2.0) * 180.0 / Math.PI,
    wrapProjectedLongitude (x * 180.0 / (earthRadius * Math.PI))

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
    latitude * 180.0 / Math.PI, wrapProjectedLongitude (x * 180.0 / (earthRadius * Math.PI))

// Fourth-order Krüger series for WGS 84/GRS 1980 Transverse Mercator. The
// omitted terms are below a micrometre inside a UTM zone. Coefficients are from:
// https://proj.org/en/stable/operations/projections/tmerc.html
type private UtmProjection =
    { CentralMeridian: float
      FalseNorthing: float
      Eccentricity: float
      Scale: float
      ForwardCoefficients: float array
      InverseCoefficients: float array }

let private utmProjection centralMeridian inverseFlattening falseNorthing =
    let flattening = 1.0 / inverseFlattening
    let n = flattening / (2.0 - flattening)
    { CentralMeridian = centralMeridian
      FalseNorthing = falseNorthing
      Eccentricity = sqrt (flattening * (2.0 - flattening))
      Scale = 0.9996 * earthRadius / (1.0 + n) * (1.0 + n * n / 4.0 + n ** 4.0 / 64.0)
      ForwardCoefficients =
        [| n / 2.0 - 2.0 * n ** 2.0 / 3.0 + 5.0 * n ** 3.0 / 16.0 + 41.0 * n ** 4.0 / 180.0
           13.0 * n ** 2.0 / 48.0 - 3.0 * n ** 3.0 / 5.0 + 557.0 * n ** 4.0 / 1440.0
           61.0 * n ** 3.0 / 240.0 - 103.0 * n ** 4.0 / 140.0
           49561.0 * n ** 4.0 / 161280.0 |]
      InverseCoefficients =
        [| -n / 2.0 + 2.0 * n ** 2.0 / 3.0 - 37.0 * n ** 3.0 / 96.0 + n ** 4.0 / 360.0
           -(n ** 2.0) / 48.0 - n ** 3.0 / 15.0 + 437.0 * n ** 4.0 / 1440.0
           -17.0 * n ** 3.0 / 480.0 + 37.0 * n ** 4.0 / 840.0
           -4397.0 * n ** 4.0 / 161280.0 |] }

let private utmProjections =
    utmZones
    |> List.map (fun zone ->
        zone.Srid,
        (zone.Datum, utmProjection (float zone.CentralMeridian) zone.InverseFlattening (float zone.FalseNorthing)))
    |> Map.ofList

let private tryUtmProjection srid = Map.tryFind srid utmProjections

let private utmSeries coefficients (xi, eta) =
    coefficients
    |> Array.mapi (fun index coefficient ->
        let harmonic = float (2 * (index + 1))
        coefficient * sin (harmonic * xi) * cosh (harmonic * eta),
        coefficient * cos (harmonic * xi) * sinh (harmonic * eta))
    |> Array.fold (fun (xi, eta) (deltaXi, deltaEta) -> xi + deltaXi, eta + deltaEta) (xi, eta)

let private utmForward projection (latitude, longitude) =
    let phi = latitude * Math.PI / 180.0
    let lambda = Math.IEEERemainder(longitude - projection.CentralMeridian, 360.0) * Math.PI / 180.0
    let e = projection.Eccentricity
    let conformal = atan (sinh (Math.Asinh(tan phi) - e * Math.Atanh(e * sin phi)))
    let xi = atan2 (tan conformal) (cos lambda)
    let eta = Math.Asinh(sin lambda / sqrt (tan conformal ** 2.0 + cos lambda ** 2.0))
    let xi, eta = utmSeries projection.ForwardCoefficients (xi, eta)
    500000.0 + projection.Scale * eta, projection.FalseNorthing + projection.Scale * xi

let private utmInverse projection (x, y) =
    let xi, eta = utmSeries projection.InverseCoefficients ((y - projection.FalseNorthing) / projection.Scale, (x - 500000.0) / projection.Scale)
    let conformal = atan (sin xi / sqrt (sinh eta ** 2.0 + cos xi ** 2.0))
    let longitude = atan2 (sinh eta) (cos xi) * 180.0 / Math.PI + projection.CentralMeridian
    let target = Math.Asinh(tan conformal)
    let rec recoverLatitude iterations latitude =
        if iterations = 0 then latitude
        else
            let e = projection.Eccentricity
            let next = atan (sinh (target + e * Math.Atanh(e * sin latitude)))
            recoverLatitude (iterations - 1) next
    let latitude = recoverLatitude 8 conformal * 180.0 / Math.PI
    latitude, wrapProjectedLongitude longitude

let private cartesianFromGeographic inverseFlattening (latitude, longitude) =
    let latitude = latitude * Math.PI / 180.0
    let longitude = longitude * Math.PI / 180.0
    let flattening = 1.0 / inverseFlattening
    let eccentricitySquared = flattening * (2.0 - flattening)
    let radius = earthRadius / sqrt (1.0 - eccentricitySquared * sin latitude ** 2.0)
    radius * cos latitude * cos longitude,
    radius * cos latitude * sin longitude,
    radius * (1.0 - eccentricitySquared) * sin latitude

let private geographicFromCartesian inverseFlattening (x, y, z) =
    let flattening = 1.0 / inverseFlattening
    let eccentricitySquared = flattening * (2.0 - flattening)
    let horizontal = sqrt (x * x + y * y)
    let rec latitudeAt iterations latitude =
        if iterations = 0 then latitude
        else
            let radius = earthRadius / sqrt (1.0 - eccentricitySquared * sin latitude ** 2.0)
            let next = atan2 (z + eccentricitySquared * radius * sin latitude) horizontal
            latitudeAt (iterations - 1) next
    let latitude = latitudeAt 8 (atan2 z (horizontal * (1.0 - eccentricitySquared)))
    latitude * 180.0 / Math.PI, atan2 y x * 180.0 / Math.PI

let private nad83ToWgs84 point =
    // NAD83's registered TOWGS84 parameters translate geocentric metres by (1, 1, -1).
    let x, y, z = cartesianFromGeographic 298.257222101 point
    geographicFromCartesian 298.257223563 (x + 1.0, y + 1.0, z - 1.0)

let private wgs84ToNad83 point =
    let x, y, z = cartesianFromGeographic 298.257223563 point
    geographicFromCartesian 298.257222101 (x - 1.0, y - 1.0, z + 1.0)

let internal tryCoordinateTransform sourceSrid targetSrid =
    let compose first second point = second (first point)
    let toWgs84 =
        match sourceSrid with
        | 4258 | 4326 -> Some id
        | 4269 -> Some nad83ToWgs84
        | 3857 -> Some pseudoMercatorInverse
        | 3395 -> Some worldMercatorInverse
        | _ ->
            tryUtmProjection sourceSrid
            |> Option.map (fun (datum, projection) ->
                match datum with
                | Nad83 -> compose (utmInverse projection) nad83ToWgs84
                | Wgs84 | Etrs89 -> utmInverse projection)
    let fromWgs84 =
        match targetSrid with
        | 4258 | 4326 -> Some id
        | 4269 -> Some wgs84ToNad83
        | 3857 -> Some pseudoMercatorForward
        | 3395 -> Some worldMercatorForward
        | _ ->
            tryUtmProjection targetSrid
            |> Option.map (fun (datum, projection) ->
                match datum with
                | Nad83 -> compose wgs84ToNad83 (utmForward projection)
                | Wgs84 | Etrs89 -> utmForward projection)
    Option.map2 compose toWgs84 fromWgs84

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

let internal tryTransformedCoordinateDomainError (geometry: Geometry) =
    // MySQL retains tiny projection roundoff beyond either antimeridian.
    coordinates geometry.Shape
    |> List.tryPick (fun point ->
        match tryPointDomainError point with
        | Some(LongitudeOutOfRange longitude) when abs (abs longitude - 180.0) < antimeridianRoundoff -> None
        | error -> error)
