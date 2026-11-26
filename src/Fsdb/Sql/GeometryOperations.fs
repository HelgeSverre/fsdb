module Fsdb.GeometryOperations

open System
open System.Buffers.Binary
open NetTopologySuite.IO
open NetTopologySuite.Operation.Buffer
open NetTopologySuite.Operation.Overlay
open NetTopologySuite.Operation.OverlayNG
open NetTopologySuite.Operation.RelateNG
open Fsdb.Collections
open Fsdb.SpatialReferenceSystems
open Fsdb.Value

[<RequireQualifiedAccess>]
type internal OverlayKind =
    | Intersection
    | Union
    | Difference
    | SymmetricDifference

[<RequireQualifiedAccess>]
type internal BufferStrategy =
    | EndRound of pointsPerCircle: float
    | EndFlat
    | JoinRound of pointsPerCircle: float
    | JoinMiter of pointsPerCircle: float
    | PointCircle of pointsPerCircle: float
    | PointSquare

[<RequireQualifiedAccess>]
type internal BufferError =
    | InvalidStrategy
    | OperationFailed of detail: string

let private defaultPointsPerCircle = 32.0
let private strategyCodeLength = sizeof<int32>
let private encodedStrategyLength = strategyCodeLength + sizeof<double>

let private radians degrees = degrees * Math.PI / 180.0
let private mysqlRadians degrees = degrees * geographicRadiansPerDegree

let internal planarPointDistance (firstX: float, firstY: float) (secondX: float, secondY: float) =
    let deltaX, deltaY = secondX - firstX, secondY - firstY
    sqrt (deltaX * deltaX + deltaY * deltaY)

let private sphericalPointDistance radius toRadians (firstLatitude, firstLongitude) (secondLatitude, secondLongitude) =
    let firstLatitude, secondLatitude = toRadians firstLatitude, toRadians secondLatitude
    let firstLongitude, secondLongitude = toRadians firstLongitude, toRadians secondLongitude
    let latitudeDelta = secondLatitude - firstLatitude
    let longitudeDelta = secondLongitude - firstLongitude
    let haversine angle = sin (angle / 2.0) ** 2.0

    let centralHaversine =
        haversine latitudeDelta
        + cos firstLatitude * cos secondLatitude * haversine longitudeDelta

    let centralAngle = 2.0 * asin (sqrt (min 1.0 centralHaversine))
    radius * centralAngle

let internal minimumPointSetDistance pointDistance firstPoints secondPoints =
    let nearer current candidate =
        match current with
        | None -> Some candidate
        | Some distance -> Some(min distance candidate)

    firstPoints
    |> List.fold (fun minimum first ->
        secondPoints
        |> List.fold (fun minimum second -> pointDistance first second |> nearer minimum) minimum) None

let internal sphericalPointSetDistance radius toRadians firstPoints secondPoints =
    minimumPointSetDistance (sphericalPointDistance radius toRadians) firstPoints secondPoints

/// Boost's first-order Andoyer inverse consumes radians. Cross-track Newton
/// points must stay in radians here; a degree round trip changes edge distances.
let private geographicInverseDistanceRadians preserveTinyMeridians referenceSystem first second =
    match referenceSystem.SemiMajorAxis, referenceSystem.InverseFlattening with
    | Some semiMajorAxis, Some inverseFlattening ->
        let flattening = 1.0 / inverseFlattening
        let firstLatitude, firstLongitude = first
        let secondLatitude, secondLongitude = second
        let longitudeDelta = secondLongitude - firstLongitude
        let sinFirst, cosFirst = sin firstLatitude, cos firstLatitude
        let sinSecond, cosSecond = sin secondLatitude, cos secondLatitude

        let cosineDistance =
            Math.FusedMultiplyAdd(sinFirst, sinSecond,
                                  cosFirst * cosSecond * cos longitudeDelta)
            |> max -1.0
            |> min 1.0

        let tinyMeridionalDistance =
            if preserveTinyMeridians && cosineDistance = 1.0 && firstLongitude = secondLongitude && firstLatitude <> secondLatitude then
                let eccentricitySquared = flattening * (2.0 - flattening)
                let middleLatitude = (firstLatitude + secondLatitude) / 2.0
                let scale = 1.0 - eccentricitySquared * sin middleLatitude ** 2.0
                let meridianRadius = semiMajorAxis * (1.0 - eccentricitySquared) / (scale * sqrt scale)
                Some(meridianRadius * abs (secondLatitude - firstLatitude))
            else None

        let angularDistance = acos cosineDistance
        let sineDistance = sin angularDistance
        let squaredDifference = (sinFirst - sinSecond) ** 2.0
        let squaredSum = (sinFirst + sinSecond) ** 2.0
        let oneMinusCosine = 1.0 - cosineDistance
        let onePlusCosine = 1.0 + cosineDistance

        let firstCorrection =
            if abs oneMinusCosine < 1e-15 then
                0.0
            else
                (angularDistance + 3.0 * sineDistance) / oneMinusCosine

        let secondCorrection =
            if abs onePlusCosine < 1e-15 then
                0.0
            else
                (angularDistance - 3.0 * sineDistance) / onePlusCosine

        let flatteningCorrection =
            -flattening / 4.0 * (firstCorrection * squaredDifference + secondCorrection * squaredSum)

        tinyMeridionalDistance
        |> Option.defaultValue (semiMajorAxis * (angularDistance + flatteningCorrection))
    | _ -> invalidArg (nameof referenceSystem) "a geographic reference system requires an ellipsoid"

let private geographicPointDistanceRadians = geographicInverseDistanceRadians true

/// MySQL converts each geographic coordinate with its SRS angular unit before
/// calling Boost's Andoyer strategy.
let internal geographicPointDistance referenceSystem (firstLatitude, firstLongitude) (secondLatitude, secondLongitude) =
    geographicPointDistanceRadians referenceSystem
        (mysqlRadians firstLatitude, mysqlRadians firstLongitude)
        (mysqlRadians secondLatitude, mysqlRadians secondLongitude)

/// Boost's default geographic line interpolation uses Andoyer for the initial
/// azimuth and first-order Thomas for the direct point. Keep this paired with
/// geographicPointDistance, which supplies the distance along each segment.
let private geographicSegmentAzimuthsFromRadians flattening latitude1 latitude2 longitudeDelta =
    let sin1, cos1 = sin latitude1, cos latitude1
    let sin2, cos2 = sin latitude2, cos latitude2
    let sinLongitude, cosLongitude = sin longitudeDelta, cos longitudeDelta
    // Boost's optimized Andoyer inverse contracts this sum. Near a polar
    // vertex the extra bit keeps a short, nonzero segment's azimuth intact.
    let cosineDistance = max -1.0 (min 1.0 (Math.FusedMultiplyAdd(sin1, sin2, cos1 * cos2 * cosLongitude)))
    let angularDistance = acos cosineDistance
    let sineDistance = sin angularDistance

    if abs sineDistance < 1e-15 then
        if cosineDistance >= 0.0 then 0.0, 0.0
        elif abs (sin1 - 1.0) > 1e-15 then 0.0, Math.PI
        else Math.PI, 0.0
    else
        let firstAngle, firstAdjustment =
            if abs cos2 < 1e-15 then
                (if sin2 < 0.0 then Math.PI else 0.0), 0.0
            else
                // Boost's multiply/subtract contracts to one rounding step on
                // our native oracle; the sign matters for nearly coincident points.
                let denominator = Math.FusedMultiplyAdd(cos1, sin2 / cos2, -(sin1 * cosLongitude))
                let angle = atan2 sinLongitude denominator
                angle, flattening / 2.0 * cos1 * cos1 * sin (2.0 * angle)

        let secondAngle, secondAdjustment =
            if abs cos1 < 1e-15 then
                (if sin1 < 0.0 then Math.PI else 0.0), 0.0
            else
                let denominator = Math.FusedMultiplyAdd(cos2, sin1 / cos1, -(sin2 * cosLongitude))
                let angle = atan2 sinLongitude denominator
                angle, flattening / 2.0 * cos2 * cos2 * sin (2.0 * angle)

        let clamp angle correction azimuth =
            if angle >= 0.0 then
                if correction >= 0.0 then max 0.0 azimuth else min Math.PI azimuth
            elif correction <= 0.0 then min 0.0 azimuth
            else max -Math.PI azimuth
        let firstCorrection = secondAdjustment * angularDistance / sineDistance - firstAdjustment
        let reverseCorrection =
            -firstAdjustment * angularDistance / sineDistance + secondAdjustment
        let forward = clamp firstAngle firstCorrection (firstAngle - firstCorrection)
        let reverse =
            (if secondAngle >= 0.0 then Math.PI - secondAngle else -Math.PI - secondAngle)
            - reverseCorrection
            |> clamp secondAngle reverseCorrection
        forward, reverse

let private geographicSegmentAzimuthsRadians flattening (latitude1, firstLongitude) (latitude2, secondLongitude) =
    geographicSegmentAzimuthsFromRadians flattening latitude1 latitude2 (secondLongitude - firstLongitude)

let private geographicSegmentAzimuths flattening (firstLatitude, firstLongitude) (secondLatitude, secondLongitude) =
    geographicSegmentAzimuthsFromRadians flattening
        (radians firstLatitude) (radians secondLatitude) (radians (secondLongitude - firstLongitude))

let private geographicSegmentAzimuth flattening first second =
    geographicSegmentAzimuths flattening first second |> fst

// The cross-track strategy already holds SRS-radian coordinates; keep them
// through azimuth and direct-point calculation to match MySQL's arithmetic.
let private geographicPointAlongRadianEndpoints referenceSystem first second distance =
    match referenceSystem.SemiMajorAxis, referenceSystem.InverseFlattening with
    | Some semiMajorAxis, Some inverseFlattening ->
        let flattening = 1.0 / inverseFlattening
        let firstLatitude, firstLongitude = first
        let azimuth = geographicSegmentAzimuthsRadians flattening first second |> fst
        let latitude1, azimuth12, flipped =
            let latitude = firstLatitude
            if azimuth > Math.PI / 2.0 then -latitude, Math.PI - azimuth, true
            elif azimuth < -Math.PI / 2.0 then -latitude, -Math.PI - azimuth, true
            else latitude, azimuth, false

        let reducedLatitude = atan ((1.0 - flattening) * tan latitude1)
        let sinReduced, cosReduced = sin reducedLatitude, cos reducedLatitude
        let sinAzimuth, cosAzimuth = sin azimuth12, cos azimuth12
        let m = cosReduced * sinAzimuth
        let n = cosReduced * cosAzimuth
        let c1 = flattening * m
        let c2 = flattening * (1.0 - m * m) / 4.0
        let divisor = 1.0 - 2.0 * c2 - c1 * m
        let p = c2 / divisor
        let sinTheta0 = sin (acos m)
        let cosSigma1 = if abs sinTheta0 < 1e-15 then 1.0 else max -1.0 (min 1.0 (sinReduced / sinTheta0))
        let sigma1 = acos cosSigma1
        let d = distance / (semiMajorAxis * divisor)
        let u = 2.0 * (sigma1 - d)
        let cosineD, sineD = cos d, sin d
        let cosineU, sineU = cos u, sin u
        let v = cosineU * cosineD - sineU * sineD
        let deltaSigma = d - 2.0 * p * v * (1.0 - 2.0 * p * cosineU) * sineD
        let sinDelta, cosDelta = sin deltaSigma, cos deltaSigma
        let reverseAzimuth = atan2 m (n * cosDelta - sinReduced * sinDelta)
        let reverseAzimuth =
            if not flipped then reverseAzimuth
            elif abs reverseAzimuth < 1e-15 then if azimuth >= 0.0 then Math.PI else -Math.PI
            elif reverseAzimuth > 0.0 then Math.PI - reverseAzimuth
            else -Math.PI - reverseAzimuth

        let longitudeDelta =
            atan2 (sinDelta * sinAzimuth) (cosReduced * cosDelta - sinReduced * sinDelta * cosAzimuth)
            - c1 * deltaSigma
        let longitude = firstLongitude + longitudeDelta
        let longitude =
            if longitude > Math.PI then longitude - 2.0 * Math.PI
            elif longitude <= -Math.PI then longitude + 2.0 * Math.PI
            else longitude

        let tangentReducedLatitude =
            if abs m >= 1e-15 then
                (sinReduced * cosDelta + n * sinDelta) * sin reverseAzimuth / m
            else
                let sigma2 = (2.0 * sigma1 - deltaSigma) - sigma1
                cos sigma2 / abs (sin sigma2)
        let latitude = atan (tangentReducedLatitude / (1.0 - flattening))
        let latitude = if flipped then -latitude else latitude
        latitude, longitude
    | _ -> invalidArg (nameof referenceSystem) "a geographic reference system requires an ellipsoid"

// Public point-along callers supply degrees but consume a radian result.
let private geographicPointAlongRadians referenceSystem (firstLatitude, firstLongitude) (secondLatitude, secondLongitude) distance =
    geographicPointAlongRadianEndpoints referenceSystem
        (radians firstLatitude, radians firstLongitude)
        (radians secondLatitude, radians secondLongitude) distance

let internal geographicPointAlong referenceSystem first second distance =
    let latitude, longitude = geographicPointAlongRadians referenceSystem first second distance
    latitude * 180.0 / Math.PI, longitude * 180.0 / Math.PI

let private geographicPointsAwayFrom flattening origin toward query =
    let segmentAzimuth = geographicSegmentAzimuth flattening origin toward
    let queryAzimuth = geographicSegmentAzimuth flattening origin query
    cos (queryAzimuth - segmentAzimuth) < 0.0

// MySQL uses Boost's geographic cross-track strategy for point-to-segment
// distance. Start with a spherical projection, then refine the along-track
// position using Andoyer inverse quantities and Thomas direct points.
let private geographicCrossTrackDistance referenceSystem point start endpoint =
    let major = referenceSystem.SemiMajorAxis.Value
    let flattening = 1.0 / referenceSystem.InverseFlattening.Value
    let minor = major * (1.0 - flattening)
    let radius = (2.0 * major + minor) / 3.0
    let lat1, lon1 = start
    let lat2, lon2 = endpoint
    let lat3, lon3 = point
    let la1, lo1 = mysqlRadians lat1, mysqlRadians lon1
    let la2, lo2 = mysqlRadians lat2, mysqlRadians lon2
    let la3, lo3 = mysqlRadians lat3, mysqlRadians lon3
    let sphericalDistance firstLat firstLon secondLat secondLon =
        let h = sin ((secondLat - firstLat) / 2.0) ** 2.0
                + cos firstLat * cos secondLat * sin ((secondLon - firstLon) / 2.0) ** 2.0
        2.0 * radius * asin (sqrt (min 1.0 h))
    let sphericalAzimuth firstLat firstLon secondLat secondLon =
        let delta = secondLon - firstLon
        atan2 (sin delta * cos secondLat)
            (cos firstLat * sin secondLat - sin firstLat * cos secondLat * cos delta)
    let a12Spherical = sphericalAzimuth la1 lo1 la2 lo2
    let a13Spherical = sphericalAzimuth la1 lo1 la3 lo3
    let s13 = sphericalDistance la1 lo1 la3 lo3
    let s34 = radius * asin (max -1.0 (min 1.0 (sin (s13 / radius) * sin (a13Spherical - a12Spherical))))
    let fraction = max -1.0 (min 1.0 (cos (s13 / radius) / cos (s34 / radius)))
    let along = radius * acos fraction
    let angular = along / radius
    let projectedLatitude = asin (sin la1 * cos angular + cos la1 * sin angular * cos a12Spherical)
    let projectedLongitude =
        lo1 + atan2 (sin a12Spherical * sin angular * cos la1)
                        (cos angular - sin la1 * sin projectedLatitude)
    let mutable position = geographicPointDistanceRadians referenceSystem
                               (la1, lo1) (projectedLatitude, projectedLongitude)
    let mutable best = Double.PositiveInfinity
    let mutable previous = -1.0
    let mutable iteration = 0
    let mutable keepGoing = true
    while keepGoing && iteration < 100 do
        let onLatitude, onLongitude =
            geographicPointAlongRadianEndpoints referenceSystem (la1, lo1) (la2, lo2) position
        let distance = geographicPointDistanceRadians referenceSystem
                           (onLatitude, onLongitude)
                           (la3, lo3)
        let forward, reverse = geographicSegmentAzimuthsRadians flattening
                                    (onLatitude, onLongitude) (la3, lo3)
        let towardEnd = geographicSegmentAzimuthsRadians flattening
                            (onLatitude, onLongitude) (la2, lo2) |> fst
        let angle = forward - towardEnd
        let sinLat1, cosLat1 = sin onLatitude, cos onLatitude
        let sinLat2, cosLat2 = sin la3, cos la3
        let oneMinus = 1.0 - flattening
        let sinBet1, unnormalizedCosBet1 = oneMinus * sinLat1, cosLat1
        let sinBet2, unnormalizedCosBet2 = oneMinus * sinLat2, cosLat2
        let normalize x y =
            let norm = sqrt (x * x + y * y)
            x / norm, y / norm
        let sinBet1, cosBet1 = normalize sinBet1 unnormalizedCosBet1
        let sinBet2, cosBet2 = normalize sinBet2 unnormalizedCosBet2
        let sinSig1, cosSig1 = normalize sinBet1 (cos forward * cosBet1)
        let sinSig2, cosSig2 = normalize sinBet2 (cos reverse * cosBet2)
        let sinAlp0 = sin forward * cosBet1
        let cosAlp0Squared = 1.0 - sinAlp0 * sinAlp0
        let sigma = atan2 (cosSig1 * sinSig2 - sinSig1 * cosSig2)
                          (cosSig1 * cosSig2 + sinSig1 * sinSig2)
        let j12 = cosAlp0Squared * flattening
                  * (sigma - ((2.0 * cosSig2 * sinSig2) - (2.0 * cosSig1 * sinSig1)) / 2.0)
        let eccentricity = flattening * (2.0 - flattening)
        let ep2 = eccentricity / (oneMinus * oneMinus)
        let dn1 = sqrt (1.0 + ep2 * sinBet1 * sinBet1)
        let dn2 = sqrt (1.0 + ep2 * sinBet2 * sinBet2)
        // Keep Boost's grouping: the nearly equal terms can cancel to a
        // sub-metre reduced length near a geodesic endpoint.
        let reduced =
            (dn2 * (cosSig1 * sinSig2) - dn1 * (sinSig1 * cosSig2)
             - cosSig1 * cosSig2 * j12) * minor
        let scale =
            let cosSigma = cosSig1 * cosSig2 + sinSig1 * sinSig2
            let t = ep2 * (cosBet1 - cosBet2) * (cosBet1 + cosBet2) / (dn1 + dn2)
            cosSigma + (t * sinSig2 - cosSig2 * j12) * sinSig1 / dn1
        let mutable derivative = if reduced = 0.0 then 0.0 else scale / reduced * sin angle
        let mutable normalized = angle
        if normalized < -1.25 * Math.PI then normalized <- normalized + 1.5 * Math.PI
        elif normalized > 1.25 * Math.PI then
            derivative <- -derivative
            normalized <- -normalized + 1.5 * Math.PI
        elif normalized < 0.0 && normalized > -0.75 * Math.PI then
            derivative <- -derivative
            normalized <- -normalized - Math.PI / 2.0
        else normalized <- normalized - Math.PI / 2.0
        if distance < best then best <- distance
        keepGoing <- previous < 0.0 || (distance < previous && normalized <> 0.0 && derivative <> 0.0)
        if keepGoing && derivative <> 0.0 then position <- position - normalized / derivative
        previous <- distance
        iteration <- iteration + 1
    let endpointDistance =
        min (geographicPointDistance referenceSystem point start)
            (geographicPointDistance referenceSystem point endpoint)
    if Double.IsFinite best then min best endpointDistance else endpointDistance

let private geographicPointSegmentDistance referenceSystem point start endpoint =
    // Boost orders endpoints by longitude, then starts same-longitude
    // meridian segments at the latitude with smaller absolute value.
    let start, endpoint =
        let startLatitude, startLongitude = start
        let endLatitude, endLongitude = endpoint
        if startLongitude > endLongitude
           || (startLongitude = endLongitude && abs startLatitude > abs endLatitude) then
            endpoint, start
        else start, endpoint
    let pointDistance = geographicPointDistance referenceSystem
    let length = pointDistance start endpoint
    let startDistance = pointDistance point start
    let endDistance = pointDistance point endpoint
    let endpointDistance = min startDistance endDistance

    if length = 0.0 || endpointDistance = 0.0 then endpointDistance
    else
        let flattening = 1.0 / referenceSystem.InverseFlattening.Value
        if geographicPointsAwayFrom flattening start endpoint point then startDistance
        elif geographicPointsAwayFrom flattening endpoint start point then endDistance
        else
            let _, pointLongitude = point
            let startLatitude, _ = start
            let endLatitude, _ = endpoint
            if startLatitude = 0.0 && endLatitude = 0.0 then
                pointDistance point (0.0, pointLongitude)
            else
                geographicCrossTrackDistance referenceSystem point start endpoint

let internal geographicPointLineDistance referenceSystem point lines =
    lines
    |> List.collect List.pairwise
    |> List.fold (fun minimum (start, endpoint) ->
        let distance = geographicPointSegmentDistance referenceSystem point start endpoint
        Some(match minimum with None -> distance | Some current -> min current distance)) None

[<RequireQualifiedAccess>]
type internal GeographicPointLinePosition =
    | Outside
    | Interior
    | Boundary

let internal geographicPointLinePosition referenceSystem point lines =
    let flattening = 1.0 / referenceSystem.InverseFlattening.Value
    let azimuthAligned start endpoint query =
        let segmentAzimuth = geographicSegmentAzimuth flattening start endpoint
        let queryAzimuth = geographicSegmentAzimuth flattening start query
        let difference = queryAzimuth - segmentAzimuth
        let difference =
            if difference > Math.PI then difference - 2.0 * Math.PI
            elif difference <= -Math.PI then difference + 2.0 * Math.PI
            else difference
        // Boost's geographic side strategy compares azimuths at machine precision.
        let machineEpsilon = Math.BitIncrement(1.0) - 1.0
        abs difference <= machineEpsilon
    let onSegment start endpoint =
        point <> start && point <> endpoint
        && azimuthAligned start endpoint point
        && azimuthAligned endpoint start point
    let onInterior =
        lines
        |> List.exists (List.pairwise >> List.exists (fun (start, endpoint) -> onSegment start endpoint))
    if onInterior then GeographicPointLinePosition.Interior
    elif lines |> List.exists (List.contains point) then
        let endpointCount =
            lines
            |> List.sumBy (function
                | [] -> 0
                | line ->
                    (if List.head line = point then 1 else 0)
                    + (if List.last line = point then 1 else 0))
        if endpointCount % 2 = 0 then GeographicPointLinePosition.Interior
        else GeographicPointLinePosition.Boundary
    else GeographicPointLinePosition.Outside

let private sampleGeographicSegment referenceSystem start endpoint =
    let length = geographicPointDistance referenceSystem start endpoint
    let steps = max 1 (min 256 (int (ceil (length / 25000.0))))
    Array.init (steps + 1) (fun index ->
        if index = 0 then start
        elif index = steps then endpoint
        else geographicPointAlong referenceSystem start endpoint (length * float index / float steps))

let private boostGeoEqual first second =
    abs (first - second) <= (Math.BitIncrement(1.0) - 1.0) * max 1.0 (max (abs first) (abs second))

let private normalizedLongitude delta =
    let value = delta % 360.0
    if value > 180.0 then value - 360.0
    elif value <= -180.0 then value + 360.0
    else value

let private longitudeDistance first second = normalizedLongitude (second - first)
let private longitudeEqual first second = boostGeoEqual (longitudeDistance first second) 0.0

let private normalizedLongitudeRadians delta =
    let period = 2.0 * Math.PI
    let value = delta % period
    if value > Math.PI then value - period
    elif value <= -Math.PI then value + period
    else value

let private longitudeDistanceRadians first second = normalizedLongitudeRadians (second - first)
let private longitudeEqualRadians first second = boostGeoEqual (longitudeDistanceRadians first second) 0.0

// MySQL corrects WKB geographic rings with Boost's ellipsoidal area strategy.
// This retains its Andoyer spherical term and prime-meridian correction for
// ring direction; the ellipsoidal series correction is not needed for the
// audited direction corpus but remains a near-degenerate edge to validate.
let private geographicRingAreaSign referenceSystem ring =
    let flattening = referenceSystem.InverseFlattening |> Option.map (fun value -> 1.0 / value) |> Option.defaultValue 0.0
    let shortSegmentThreshold =
        referenceSystem.SemiMajorAxis
        |> Option.map (fun major -> (2.0 * major + major * (1.0 - flattening)) / 3.0 / 638.0)
        |> Option.defaultValue 0.0
    let crossesPrimeMeridian firstLongitude secondLongitude =
        let delta = normalizedLongitude (secondLongitude - firstLongitude)
        if boostGeoEqual (abs delta) 180.0 then true
        else
            let positive longitude =
                let value = longitude % 360.0
                if value < 0.0 then value + 360.0 else value
            let first, second = positive firstLongitude, positive secondLongitude
            max first second > 180.0 && min first second < 180.0 && abs (first - second) > 180.0
    let sum, crossings =
        ring
        |> List.pairwise
        |> List.fold (fun (sum, crossings) ((firstLatitude, firstLongitude), (secondLatitude, secondLongitude)) ->
            let first = firstLatitude, firstLongitude
            let second = secondLatitude, secondLongitude
            let longitudeDelta = normalizedLongitude (secondLongitude - firstLongitude)
            if boostGeoEqual firstLongitude secondLongitude then sum, crossings
            else
                let crossings = crossings + (if crossesPrimeMeridian firstLongitude secondLongitude then 1 else 0)
                let term =
                    if boostGeoEqual (abs longitudeDelta) 180.0 then Math.PI
                    elif boostGeoEqual firstLatitude 0.0 && boostGeoEqual secondLatitude 0.0 then 0.0
                    elif not (boostGeoEqual (abs firstLatitude) 90.0 || boostGeoEqual (abs secondLatitude) 90.0)
                         && geographicPointDistance referenceSystem first second < shortSegmentThreshold then
                        let firstTangent = tan (radians firstLatitude / 2.0)
                        let secondTangent = tan (radians secondLatitude / 2.0)
                        2.0 * atan ((firstTangent + secondTangent) / (1.0 + firstTangent * secondTangent)
                                    * tan (radians longitudeDelta / 2.0))
                    else
                        let forward, reverse = geographicSegmentAzimuths flattening first second
                        reverse - forward
                sum + term, crossings) (0.0, 0)
    let area =
        if crossings % 2 = 0 then sum
        else
            let polar = 2.0 * Math.PI * float (1 + crossings / 2) - abs sum
            if sum > 0.0 then -polar else polar
    // Boost's counterclockwise polygon trait negates the ring strategy result.
    -area

let private geographicRingContains referenceSystem includeBoundary point ring =
    let flattening = referenceSystem.InverseFlattening |> Option.map (fun value -> 1.0 / value) |> Option.defaultValue 0.0
    let area = geographicRingAreaSign referenceSystem ring
    // Boost corrects polygon winding by role: exterior and hole rings have
    // opposite traversal directions even when their input WKT does not.
    let ring = if (area < 0.0) <> includeBoundary then List.rev ring else ring
    // Boost traverses a counterclockwise polygon's stored exterior ring in
    // reverse when applying its point-in-ring winding strategy.
    let windingRing = ring |> List.rev |> List.map (fun (latitude, longitude) -> mysqlRadians latitude, mysqlRadians longitude)
    let pointLatitude, pointLongitude =
        let latitude, longitude = point
        mysqlRadians latitude, mysqlRadians longitude
    let windingPoint = pointLatitude, pointLongitude
    let mutable count = 0
    let mutable southCount = 0
    let mutable rawCount = 0
    let mutable antipodalCount = 0
    let mutable touches = false
    let side first second query =
        if query = first || query = second || first = second then 0
        else
            let queryAzimuth = geographicSegmentAzimuthsRadians flattening first query |> fst
            let segmentAzimuth = geographicSegmentAzimuthsRadians flattening first second |> fst
            let difference =
                let value = (queryAzimuth - segmentAzimuth + Math.PI) % (2.0 * Math.PI)
                let value = if value < 0.0 then value + 2.0 * Math.PI else value
                value - Math.PI
            if boostGeoEqual difference 0.0 || boostGeoEqual (abs difference) Math.PI then 0
            elif difference > 0.0 then -1
            else 1
    for ((firstLatitude, firstLongitude), (secondLatitude, secondLongitude)) in List.pairwise windingRing do
        let first = firstLatitude, firstLongitude
        let second = secondLatitude, secondLongitude
        if not touches then
            let firstStrict = longitudeEqualRadians firstLongitude pointLongitude
            let secondStrict = longitudeEqualRadians secondLongitude pointLongitude
            let antipodalLongitude = pointLongitude + (if pointLongitude <= 0.0 then Math.PI else -Math.PI)
            let firstAnti = longitudeEqualRadians firstLongitude antipodalLongitude
            let secondAnti = longitudeEqualRadians secondLongitude antipodalLongitude
            let mutable firstEqual = firstStrict || firstAnti
            let mutable secondEqual = secondStrict || secondAnti
            let antipodalSegment = boostGeoEqual (longitudeDistanceRadians firstLongitude secondLongitude) Math.PI
            if antipodalSegment then
                let equal = firstEqual || secondEqual || boostGeoEqual (abs pointLatitude) (Math.PI / 2.0)
                firstEqual <- equal
                secondEqual <- equal

            let mutable handled = false
            if longitudeDistanceRadians secondLongitude pointLongitude = 0.0 && boostGeoEqual (abs firstLatitude) (Math.PI / 2.0) then
                touches <- if firstLatitude < 0.0 then secondLatitude > pointLatitude else secondLatitude < pointLatitude
                handled <- touches
            if not handled && longitudeDistanceRadians firstLongitude pointLongitude = 0.0 && boostGeoEqual (abs secondLatitude) (Math.PI / 2.0) then
                touches <- if secondLatitude < 0.0 then firstLatitude > pointLatitude else firstLatitude < pointLatitude
                handled <- touches

            if not handled && firstEqual && secondEqual then
                if not antipodalSegment then
                    if min firstLatitude secondLatitude <= pointLatitude && pointLatitude <= max firstLatitude secondLatitude then
                        touches <- not firstAnti || not secondAnti
                elif firstLatitude + secondLatitude >= 0.0 then
                    touches <- (firstStrict && firstLatitude <= pointLatitude)
                               || (secondStrict && secondLatitude <= pointLatitude)
                               || boostGeoEqual pointLatitude (Math.PI / 2.0)
                else
                    touches <- (firstStrict && firstLatitude >= pointLatitude)
                               || (secondStrict && secondLatitude >= pointLatitude)
                               || boostGeoEqual pointLatitude (-Math.PI / 2.0)
                handled <- true

            if not handled then
                let firstPole = boostGeoEqual (abs firstLatitude) (Math.PI / 2.0)
                let secondPole = boostGeoEqual (abs secondLatitude) (Math.PI / 2.0)
                let delta = longitudeDistanceRadians firstLongitude secondLongitude
                let firstToPoint = longitudeDistanceRadians firstLongitude pointLongitude
                let direction = if delta < 0.0 then -1 else 1
                let crossings, isAnti =
                    if firstPole && secondPole then 0, false
                    elif antipodalSegment then (if firstToPoint < 0.0 then -2 else 2), false
                    elif firstEqual || secondEqual then
                        direction, longitudeEqualRadians (pointLongitude + Math.PI) (if firstEqual then firstLongitude else secondLongitude)
                    elif Math.Sign(firstToPoint) = Math.Sign(delta) && abs firstToPoint < abs delta then
                        2 * direction, false
                    else
                        let firstToAntipode = longitudeDistanceRadians firstLongitude (pointLongitude + Math.PI)
                        if Math.Sign(firstToAntipode) = Math.Sign(delta) && abs firstToAntipode < abs delta then
                            2 * direction, true
                        else 0, false

                if crossings <> 0 then
                    if isAnti then
                        southCount <- southCount - crossings
                        antipodalCount <- antipodalCount - crossings
                    else
                        let segmentSide =
                            if abs crossings = 1 then
                                let endpoint = if firstEqual then first else second
                                let latitude, longitude = endpoint
                                if boostGeoEqual pointLatitude latitude then 0
                                else
                                    let otherLongitude = normalizedLongitudeRadians (longitude + float direction * Math.PI / 180.0)
                                    side endpoint (latitude, otherLongitude) windingPoint
                            elif antipodalSegment then
                                Math.Sign(crossings) * (if firstLatitude + secondLatitude >= 0.0 then -1 else 1)
                            else side first second windingPoint
                        if segmentSide = 0 then touches <- true
                        else
                            if segmentSide * crossings > 0 then count <- count + crossings
                            rawCount <- rawCount + crossings

    if touches then includeBoundary
    elif rawCount <> 0 && antipodalCount <> 0 && rawCount > 0 then count + southCount <> 0
    else count <> 0

[<RequireQualifiedAccess>]
type internal GeographicPointPolygonPosition =
    | Outside of float option
    | Interior
    | ExteriorBoundary
    | HoleBoundary
    | HoleMeridianBoundary

let internal geographicPointPolygonPosition referenceSystem point polygons =
    let pointLineDistance ring = geographicPointLineDistance referenceSystem point [ ring ]
    let nearGeodesicBoundary ring distance =
        distance |> Option.exists (fun value -> value < 0.00001)
        && geographicPointLinePosition referenceSystem point [ ring ]
           <> GeographicPointLinePosition.Outside
    let onMeridian ring =
        let latitude, longitude = point
        ring
        |> List.pairwise
        |> List.exists (fun ((startLatitude, startLongitude), (endLatitude, endLongitude)) ->
            startLongitude = endLongitude
            && longitude = startLongitude
            && latitude >= min startLatitude endLatitude
            && latitude <= max startLatitude endLatitude)
    let polygonPosition rings =
        match rings with
        | [] -> GeographicPointPolygonPosition.Outside None
        | exterior :: holes ->
            let exteriorDistance = pointLineDistance exterior
            let holeDistances = holes |> List.map (fun hole -> hole, pointLineDistance hole)
            let boundaryDistance =
                (Option.toList exteriorDistance) @ (holeDistances |> List.choose snd)
                |> function
                    | [] -> None
                    | distances -> Some(List.min distances)
            match exteriorDistance with
            | _ when onMeridian exterior || nearGeodesicBoundary exterior exteriorDistance ->
                GeographicPointPolygonPosition.ExteriorBoundary
            // A meridian hole edge is an actual geodesic boundary. Coordinate-
            // straight samples on other hole edges may instead lie in the fill.
            | _ when List.exists onMeridian holes ->
                GeographicPointPolygonPosition.HoleMeridianBoundary
            // Andoyer distance can round to zero beside a hole corner. Use
            // geodesic segment membership before treating it as boundary.
            | _ when holeDistances
                     |> List.exists (fun (hole, distance) -> nearGeodesicBoundary hole distance) ->
                GeographicPointPolygonPosition.HoleBoundary
            | _ when geographicRingContains referenceSystem false point exterior
                     && not (holes |> List.exists (geographicRingContains referenceSystem true point)) ->
                GeographicPointPolygonPosition.Interior
            | _ -> GeographicPointPolygonPosition.Outside boundaryDistance
    let positions = polygons |> List.map polygonPosition
    if positions |> List.contains GeographicPointPolygonPosition.Interior then
        GeographicPointPolygonPosition.Interior
    elif positions |> List.contains GeographicPointPolygonPosition.HoleBoundary then
        GeographicPointPolygonPosition.HoleBoundary
    elif positions |> List.contains GeographicPointPolygonPosition.HoleMeridianBoundary then
        GeographicPointPolygonPosition.HoleMeridianBoundary
    elif positions |> List.contains GeographicPointPolygonPosition.ExteriorBoundary then
        GeographicPointPolygonPosition.ExteriorBoundary
    else
        positions
        |> List.choose (function GeographicPointPolygonPosition.Outside distance -> distance | _ -> None)
        |> function
            | [] -> GeographicPointPolygonPosition.Outside None
            | distances -> GeographicPointPolygonPosition.Outside(Some(List.min distances))

let internal geographicPointPolygonDistance referenceSystem point polygons =
    match geographicPointPolygonPosition referenceSystem point polygons with
    | GeographicPointPolygonPosition.Outside distance -> distance
    | _ -> Some 0.0

let private geographicLineSegments lines = lines |> List.collect List.pairwise

let private geographicPointOnLineSegment referenceSystem point (start, endpoint) =
    geographicPointLinePosition referenceSystem point [ [ start; endpoint ] ]
    <> GeographicPointLinePosition.Outside

let private geographicSegmentsShareEndpoint (firstStart, firstEnd) (secondStart, secondEnd) =
    firstStart = secondStart || firstStart = secondEnd
    || firstEnd = secondStart || firstEnd = secondEnd

let private tryGeographicSegmentCrossing referenceSystem first second =
    let firstStart, firstEnd = first
    let secondStart, secondEnd = second
    if geographicSegmentsShareEndpoint first second then None
    else
        let major = referenceSystem.SemiMajorAxis.Value
        let minor = major * (1.0 - 1.0 / referenceSystem.InverseFlattening.Value)
        let flattening = 1.0 - minor / major
        let normalized = { referenceSystem with SemiMajorAxis = Some 1.0; InverseFlattening = Some(1.0 / flattening) }
        let endpoints start endpoint =
            let radians (latitude, longitude) = mysqlRadians latitude, mysqlRadians longitude
            let start, endpoint = if fst start > fst endpoint then endpoint, start else start, endpoint
            radians start, radians endpoint
        let a, b = endpoints firstStart firstEnd
        let c, d = endpoints secondStart secondEnd
        let azimuth start endpoint = geographicSegmentAzimuthsRadians flattening start endpoint |> fst
        let firstAzimuth, secondAzimuth = azimuth a b, azimuth c d
        let side baseline start point =
            let delta = normalizedLongitudeRadians (azimuth start point - baseline)
            if boostGeoEqual delta 0.0 || boostGeoEqual (abs delta) Math.PI then 0
            else Math.Sign delta
        if side firstAzimuth a c * side firstAzimuth a d >= 0
           || side secondAzimuth c a * side secondAzimuth c b >= 0 then None
        else
            GeographicIntersection.intersect flattening a b firstAzimuth c d secondAzimuth
            |> Option.filter (fun point ->
                let inside start endpoint baseline =
                    let distance = geographicPointDistanceRadians normalized start point
                    let length = geographicPointDistanceRadians normalized start endpoint
                    // The side tests establish a proper crossing. The
                    // inverse can round its distance to an endpoint.
                    abs (normalizedLongitudeRadians (azimuth start point - baseline)) <= Math.PI / 2.0
                    && distance <= length
                inside a b firstAzimuth && inside c d secondAzimuth)

let private geographicSegmentsProperlyCross referenceSystem first second =
    tryGeographicSegmentCrossing referenceSystem first second |> Option.isSome

let private geographicSharedSegmentEndpoints referenceSystem ((a, b) as first) ((c, d) as second) =
    [ a; b; c; d ]
    |> List.distinct
    |> List.filter (fun point ->
        geographicPointOnLineSegment referenceSystem point first
        && geographicPointOnLineSegment referenceSystem point second)

let private geographicRingWithoutRepeatedNeighbors ring =
    ring
    |> List.fold (fun points point ->
        match points with
        | previous :: _ when previous = point -> points
        | _ -> point :: points) []
    |> List.rev

let private geographicSimpleRingIsValid referenceSystem ring =
    let vertices = ring |> List.take (max 0 (List.length ring - 1))
    if List.length ring < 4
       || List.head ring <> List.last ring
       || (vertices |> Seq.distinct |> Seq.truncate 3 |> Seq.length) < 3 then
        false
    else
        let segments = ring |> List.pairwise |> List.toArray
        let last = segments.Length - 1
        let pointPosition point (start, endpoint) =
            geographicPointLinePosition referenceSystem point [ [ start; endpoint ] ]
        let invalidPair firstIndex secondIndex =
            let (a, b) as first = segments.[firstIndex]
            let (c, d) as second = segments.[secondIndex]
            let adjacent = secondIndex = firstIndex + 1 || (firstIndex = 0 && secondIndex = last)
            let invalidContact point segment =
                match pointPosition point segment with
                | GeographicPointLinePosition.Outside -> false
                | GeographicPointLinePosition.Interior -> true
                | GeographicPointLinePosition.Boundary -> not adjacent
            geographicSegmentsProperlyCross referenceSystem first second
            || invalidContact a second
            || invalidContact b second
            || invalidContact c first
            || invalidContact d first
        seq {
            for first in 0 .. last do
                for second in first + 1 .. last do
                    yield first, second
        }
        |> Seq.exists (fun (first, second) -> invalidPair first second)
        |> not

let private tryValidateGeographicRing referenceSystem ring =
    // Equal neighbors are allowed. Drop their zero-length edges before either
    // testing adjacency or comparing boundaries.
    let ring = geographicRingWithoutRepeatedNeighbors ring
    if geographicSimpleRingIsValid referenceSystem ring then Some ring else None

let private tryPrepareGeographicParts prepare parts =
    let rec collect prepared = function
        | [] -> Some(List.rev prepared)
        | part :: rest ->
            match prepare part with
            | Some part -> collect (part :: prepared) rest
            | None -> None
    collect [] parts

/// Bearings from the contact toward the preceding and following CCW neighbors.
let private geographicRingDirections referenceSystem point ring =
    let flattening = 1.0 / referenceSystem.InverseFlattening.Value
    let radians (latitude, longitude) = mysqlRadians latitude, mysqlRadians longitude
    let bearing other = geographicSegmentAzimuthsRadians flattening (radians point) (radians other) |> fst
    let segments = List.pairwise ring
    let incoming = segments |> List.tryPick (fun (a, b) -> if b = point && a <> point then Some(bearing a) else None)
    let outgoing = segments |> List.tryPick (fun (a, b) -> if a = point && b <> point then Some(bearing b) else None)
    match incoming, outgoing with
    | Some previous, Some next -> Some(previous, next)
    | _ ->
        segments
        |> List.tryPick (fun ((a, b) as segment) ->
            if a <> b && geographicPointOnLineSegment referenceSystem point segment then Some(bearing a, bearing b)
            else None)

let private geographicBearingInside (previous, next) bearing =
    let clockwise first second =
        let angle = normalizedLongitudeRadians (second - first)
        if angle < 0.0 then angle + 2.0 * Math.PI else angle
    let offset, extent = clockwise previous bearing, clockwise previous next
    offset > 0.0 && offset < extent
    && not (boostGeoEqual offset 0.0 || boostGeoEqual offset extent)

// A contact can be a crossing at a vertex even when no two open segments cross.
let private geographicRingContactIsInvalid referenceSystem first second point =
    match geographicRingDirections referenceSystem point first, geographicRingDirections referenceSystem point second with
    | Some(a, b), Some(c, d) ->
        let equalBearing first second = boostGeoEqual (normalizedLongitudeRadians (first - second)) 0.0
        let coincident = [ a; b ] |> List.exists (fun first -> [ c; d ] |> List.exists (equalBearing first))
        let order = [ a, 0; b, 0; c, 1; d, 1 ] |> List.sortBy fst |> List.map snd
        let alternating = (order @ [ List.head order ]) |> List.pairwise |> List.forall (fun (a, b) -> a <> b)
        coincident || alternating
    | _ -> true

let private geographicRingContacts referenceSystem first second =
    Seq.allPairs (List.pairwise first) (List.pairwise second)
    |> Seq.fold (fun contacts (firstSegment, secondSegment) ->
        contacts
        |> Option.bind (fun contacts ->
            if geographicSegmentsProperlyCross referenceSystem firstSegment secondSegment then None
            else
                let a, b = firstSegment
                let c, d = secondSegment
                let common =
                    [ a, secondSegment; b, secondSegment; c, firstSegment; d, firstSegment ]
                    |> List.choose (fun (point, segment) ->
                        if geographicPointOnLineSegment referenceSystem point segment then Some point else None)
                    |> Set.ofList
                // Two distinct common points imply a shared boundary interval.
                if Set.count common > 1 then None else Some(Set.union contacts common))) (Some Set.empty)
    |> Option.filter (fun contacts ->
        contacts |> Set.forall (geographicRingContactIsInvalid referenceSystem first second >> not))

[<RequireQualifiedAccess>]
type private GeographicRingRole =
    | Shell
    | Hole

let private geographicRingInside referenceSystem role inner outer =
    let onBoundary point = List.pairwise outer |> List.exists (geographicPointOnLineSegment referenceSystem point)
    match inner |> List.tryFind (onBoundary >> not) with
    | Some point -> geographicRingContains referenceSystem (role = GeographicRingRole.Hole) point outer
    | None ->
        // If all vertices touch, the outgoing edge's local side decides the
        // region. Vertex-only containment misses an inscribed component.
        inner
        |> List.exists (fun point ->
            match geographicRingDirections referenceSystem point inner, geographicRingDirections referenceSystem point outer with
            | Some(_, outgoing), Some directions -> geographicBearingInside directions outgoing
            | _ -> false)

let private geographicPolygonInteriorIsConnected ringCount contacts =
    let contactRings =
        contacts
        |> List.fold (fun result (first, second, points) ->
            points
            |> Set.fold (fun result point ->
                result |> Map.change point (fun rings ->
                    rings |> Option.defaultValue Set.empty |> Set.add first |> Set.add second |> Some)) result) Map.empty
    let parents = Array.init (ringCount + Map.count contactRings) id
    let root node =
        let mutable current = node
        while parents.[current] <> current do
            parents.[current] <- parents.[parents.[current]]
            current <- parents.[current]
        current
    let mutable connected = true
    // Ring nodes and contact-point nodes form a bipartite graph. Several rings
    // meeting at one point form a star, not an interior-disconnecting cycle.
    for offset, (_, rings) in contactRings |> Map.toList |> List.indexed do
        for ring in rings do
            let first, second = root ring, root (ringCount + offset)
            if first = second then connected <- false
            else parents.[first] <- second
    connected

type private ValidGeographicPolygon =
    { Shell: (float * float) list
      Holes: (float * float) list list }

let private tryValidateGeographicPolygon referenceSystem rings =
    match tryPrepareGeographicParts (tryValidateGeographicRing referenceSystem) rings with
    | Some(shell :: holes) ->
        let counterclockwise ring =
            if geographicRingAreaSign referenceSystem ring < 0.0 then List.rev ring else ring
        let polygon = { Shell = counterclockwise shell; Holes = List.map counterclockwise holes }
        let rings = List.toArray (polygon.Shell :: polygon.Holes)
        let contacts =
            seq {
                for first in 0 .. rings.Length - 1 do
                    for second in first + 1 .. rings.Length - 1 do
                        yield first, second
            }
            |> Seq.fold (fun contacts (first, second) ->
                contacts
                |> Option.bind (fun contacts ->
                    geographicRingContacts referenceSystem rings.[first] rings.[second]
                    |> Option.map (fun points -> (first, second, points) :: contacts))) (Some [])
        contacts
        |> Option.bind (fun contacts ->
            let inside = geographicRingInside referenceSystem GeographicRingRole.Shell
            let holesInside = polygon.Holes |> List.forall (fun hole -> inside hole polygon.Shell)
            let holesSeparate =
                contacts |> List.forall (fun (first, second, _) ->
                    first = 0
                    || (not (inside rings.[first] rings.[second]) && not (inside rings.[second] rings.[first])))
            if holesInside && holesSeparate && geographicPolygonInteriorIsConnected rings.Length contacts then Some polygon
            else None)
    | _ -> None

let private geographicMultiPolygonIsValid referenceSystem polygons =
    // Polar holes use their hole winding role, which can differ from the
    // same ring interpreted as a standalone polygon.
    let insideFill ring polygon =
        geographicRingInside referenceSystem GeographicRingRole.Shell ring polygon.Shell
        && not (polygon.Holes |> List.exists (geographicRingInside referenceSystem GeographicRingRole.Hole ring))
    let disjoint first second =
        let secondRings = second.Shell :: second.Holes
        let boundariesSeparate =
            first.Shell :: first.Holes |> List.forall (fun a ->
                secondRings |> List.forall (fun b -> geographicRingContacts referenceSystem a b |> Option.isSome))
        boundariesSeparate
        && not (insideFill first.Shell second)
        && not (insideFill second.Shell first)
    let rec allDisjoint = function
        | [] -> true
        | first :: rest -> List.forall (disjoint first) rest && allDisjoint rest
    not (List.isEmpty polygons)
    && (tryPrepareGeographicParts (tryValidateGeographicPolygon referenceSystem) polygons |> Option.exists allDisjoint)

let rec internal geographicValidity referenceSystem (geometry: Geometry) =
    match geometry.Shape with
    | GPolygon rings -> tryValidateGeographicPolygon referenceSystem rings |> Option.isSome
    | GMultiPolygon polygons -> geographicMultiPolygonIsValid referenceSystem polygons
    | GGeometryCollection geometries -> geometries |> List.forall (geographicValidity referenceSystem)
    | _ -> geometryIsValidPlanar geometry

let private geographicLineCovers referenceSystem covering covered =
    let coveredPoints = covered |> List.collect id |> List.distinct
    let onLine point =
        covering
        |> List.exists (fun line ->
            line
            |> List.pairwise
            |> List.exists (geographicPointOnLineSegment referenceSystem point))
    let nearLine point =
        onLine point
        || (geographicPointLineDistance referenceSystem point covering
            |> Option.exists (fun distance -> distance < 0.01))
    let segmentCovered (start, endpoint) =
        if not (onLine start && onLine endpoint) then false
        else
            let length = geographicPointDistance referenceSystem start endpoint
            [ 0.25; 0.5; 0.75 ]
            |> List.forall (fun fraction ->
                geographicPointAlong referenceSystem start endpoint (length * fraction)
                |> nearLine)
    match coveredPoints with
    | [ point ] -> geographicPointLinePosition referenceSystem point covering = GeographicPointLinePosition.Interior
    | [] -> false
    | _ -> covered |> geographicLineSegments |> List.forall segmentCovered

type internal GeographicLineTopology =
    { Crosses: bool
      Touches: bool
      Overlaps: bool
      FirstContainsSecond: bool
      SecondContainsFirst: bool }

let internal geographicLineLineTopology referenceSystem firstLines secondLines =
    let firstSegments = geographicLineSegments firstLines
    let secondSegments = geographicLineSegments secondLines
    let sharedEndpoints = geographicSharedSegmentEndpoints referenceSystem
    let pairs =
        [ for first in firstSegments do
            for second in secondSegments do
                first, second ]
    let position point lines = geographicPointLinePosition referenceSystem point lines
    // Two distinct points shared by the same geodesic segments form an interval,
    // which changes the relation from a point crossing or touch.
    let commonInterval =
        pairs |> List.exists (fun (first, second) -> List.length (sharedEndpoints first second) >= 2)
    let pointLike lines = lines |> List.collect id |> List.distinct |> List.length = 1
    let firstContainsSecond, secondContainsFirst =
        (if commonInterval || pointLike secondLines then geographicLineCovers referenceSystem firstLines secondLines else false),
        (if commonInterval || pointLike firstLines then geographicLineCovers referenceSystem secondLines firstLines else false)
    let overlaps = commonInterval && not (firstContainsSecond || secondContainsFirst)
    // A shared vertex can lie in the interior of both complete lines.
    let crosses =
        pairs
        |> List.exists (fun (first, second) ->
            let contacts = sharedEndpoints first second
            geographicSegmentsProperlyCross referenceSystem first second
            || (List.length contacts < 2
                && (contacts
                    |> List.exists (fun point ->
                        position point firstLines = GeographicPointLinePosition.Interior
                        && position point secondLines = GeographicPointLinePosition.Interior))))
    let hasBoundaryContact =
        pairs
        |> List.exists (fun (first, second) ->
            sharedEndpoints first second
            |> List.exists (fun point ->
                position point firstLines = GeographicPointLinePosition.Boundary
                || position point secondLines = GeographicPointLinePosition.Boundary))
    { Crosses = crosses
      Touches = hasBoundaryContact && not crosses && not commonInterval
      Overlaps = overlaps && not crosses
      FirstContainsSecond = firstContainsSecond
      SecondContainsFirst = secondContainsFirst }

let internal geographicLineLineDistance referenceSystem firstLines secondLines =
    let firstSegments = geographicLineSegments firstLines
    let secondSegments = geographicLineSegments secondLines

    let segmentDistance (firstStart, firstEnd) (secondStart, secondEnd) =
        let first, second = (firstStart, firstEnd), (secondStart, secondEnd)
        let endpointContact =
            geographicSegmentsShareEndpoint first second
            || geographicPointOnLineSegment referenceSystem firstStart second
            || geographicPointOnLineSegment referenceSystem firstEnd second
            || geographicPointOnLineSegment referenceSystem secondStart first
            || geographicPointOnLineSegment referenceSystem secondEnd first
        // Endpoint contact and overlap remain zero even when Andoyer rounds
        // an identical-point or collinear distance above zero.
        if endpointContact || geographicSegmentsProperlyCross referenceSystem first second then 0.0
        else
            [ geographicPointSegmentDistance referenceSystem firstStart secondStart secondEnd
              geographicPointSegmentDistance referenceSystem firstEnd secondStart secondEnd
              geographicPointSegmentDistance referenceSystem secondStart firstStart firstEnd
              geographicPointSegmentDistance referenceSystem secondEnd firstStart firstEnd ]
            |> List.min

    firstSegments
    |> List.fold (fun minimum first ->
        secondSegments
        |> List.fold (fun minimum second ->
            let distance = segmentDistance first second
            Some(match minimum with None -> distance | Some current -> min current distance)) minimum) None

let internal geographicLinePolygonDistance referenceSystem lines polygons =
    let endpoints = lines |> List.collect id
    let distances =
        polygons
        |> List.collect (fun rings ->
            let endpointDistances =
                endpoints
                |> List.choose (fun point -> geographicPointPolygonDistance referenceSystem point [ rings ])
            let boundaryDistance = geographicLineLineDistance referenceSystem lines rings
            endpointDistances @ Option.toList boundaryDistance)
    match distances with
    | [] -> None
    | _ -> Some(List.min distances)

type internal GeographicLinePolygonTopology =
    { Intersects: bool
      Crosses: bool
      Touches: bool
      LineWithinPolygon: bool }

/// A candidate event where the interpolated line crosses a polygon edge in
/// coordinate space. The fraction and point can be reused by a geographic
/// overlay; the relation check currently classifies the intervals between
/// these candidates.
type internal GeographicBoundaryEvent =
    { Fraction: float
      Point: float * float
      Boundary: (float * float) * (float * float) }

let internal geographicBoundaryEvents referenceSystem boundarySegments (start, endpoint) =
    let length = geographicPointDistance referenceSystem start endpoint
    let crossingEvent ((cx, cy), (dx, dy) as boundary) =
        let sx, sy = dx - cx, dy - cy
        let side (px, py) = sx * (py - cy) - sy * (px - cx)
        let firstSide, lastSide = side start, side endpoint
        let _, startLongitude = start
        let _, endLongitude = endpoint
        // A wrapped longitude needs its own unwrapped coordinate frame.
        if abs (endLongitude - startLongitude) > 180.0
           || abs sy > 180.0
           || firstSide * lastSide >= 0.0 then None
        else
            let mutable lower = 0.0
            let mutable upper = 1.0
            for _ in 1 .. 48 do
                let middle = (lower + upper) / 2.0
                let point = geographicPointAlong referenceSystem start endpoint (length * middle)
                if (side point > 0.0) = (firstSide > 0.0) then lower <- middle
                else upper <- middle
            let fraction = (lower + upper) / 2.0
            let point = geographicPointAlong referenceSystem start endpoint (length * fraction)
            let px, py = point
            let along = ((px - cx) * sx + (py - cy) * sy) / (sx * sx + sy * sy)
            if along > 0.0 && along < 1.0 then
                Some { Fraction = fraction; Point = point; Boundary = boundary }
            else None
    boundarySegments |> List.choose crossingEvent |> List.sortBy _.Fraction

let internal geographicLinePolygonTopology referenceSystem lines polygons =
    let boundarySegments = polygons |> List.collect id |> List.collect List.pairwise
    let exteriorBoundarySegments = polygons |> List.choose List.tryHead |> List.collect List.pairwise
    let lineSegments = lines |> List.collect List.pairwise
    let coordinateOverlap (start, endpoint) (boundaryStart, boundaryEnd) =
        let x, y = start
        let endX, endY = endpoint
        let dx, dy = endX - x, endY - y
        let lengthSquared = dx * dx + dy * dy
        let cross (px, py) = dx * (py - y) - dy * (px - x)
        if lengthSquared = 0.0 then None
        elif abs (cross boundaryStart) > 1e-12 * max 1.0 lengthSquared
             || abs (cross boundaryEnd) > 1e-12 * max 1.0 lengthSquared then None
        else
            let position (px, py) = ((px - x) * dx + (py - y) * dy) / lengthSquared
            let first, second = position boundaryStart, position boundaryEnd
            let lower, upper = max 0.0 (min first second), min 1.0 (max first second)
            if lower < upper then Some(lower, upper) else None
    let liesOnBoundary (start, endpoint) =
        boundarySegments
        |> List.exists (fun segment ->
            geographicPointOnLineSegment referenceSystem start segment
            && geographicPointOnLineSegment referenceSystem endpoint segment)
    let boundaryOnly = not lineSegments.IsEmpty && (lineSegments |> List.forall liesOnBoundary)
    let positions =
        lines
        |> List.collect (fun line ->
            line
            |> List.pairwise
            |> List.collect (fun (start, endpoint) ->
                let segment = start, endpoint
                let sharedIntervals =
                    exteriorBoundarySegments |> List.choose (coordinateOverlap segment)
                let samples = sampleGeographicSegment referenceSystem start endpoint
                samples
                |> Array.mapi (fun index point ->
                    let fraction = float index / float (samples.Length - 1)
                    // The common coordinate interval remains boundary contact even
                    // when sampling the longer ellipsoidal arc drifts off the edge.
                    if sharedIntervals
                       |> List.exists (fun (lower, upper) -> fraction >= lower && fraction <= upper) then
                        GeographicPointPolygonPosition.ExteriorBoundary
                    else
                        geographicPointPolygonPosition referenceSystem point polygons)
                |> Array.toList))
    // Candidate boundary events split the line for interval classification.
    let intervalPositions =
        lineSegments
        |> List.collect (fun (start, endpoint) ->
            let length = geographicPointDistance referenceSystem start endpoint
            let fractions =
                geographicBoundaryEvents referenceSystem boundarySegments (start, endpoint)
                |> List.map _.Fraction
            if fractions.IsEmpty then []
            else
                let cuts = 0.0 :: (fractions @ [ 1.0 ])
                cuts
                |> List.pairwise
                |> List.choose (fun (lower, upper) ->
                    if lower >= upper then None
                    else
                        let midpoint = geographicPointAlong referenceSystem start endpoint (length * (lower + upper) / 2.0)
                        Some(geographicPointPolygonPosition referenceSystem midpoint polygons)))
    let allPositions = positions @ intervalPositions
    let hasInterior =
        allPositions |> List.contains GeographicPointPolygonPosition.Interior
    let hasOutside =
        allPositions
        |> List.exists (function GeographicPointPolygonPosition.Outside _ -> true | _ -> false)
    let hasContact =
        allPositions
        |> List.exists (function GeographicPointPolygonPosition.Outside _ -> false | _ -> true)
    let intersects =
        hasContact
        && geographicLinePolygonDistance referenceSystem lines polygons = Some 0.0
    if boundaryOnly then
        { Intersects = true
          Crosses = false
          Touches = true
          LineWithinPolygon = false }
    else
        { Intersects = intersects
          Crosses = intersects && hasInterior && hasOutside
          Touches = intersects && not hasInterior
          LineWithinPolygon = hasInterior && not hasOutside }

let internal geographicPolygonPolygonDistance referenceSystem firstPolygons secondPolygons =
    let firstRings = firstPolygons |> List.collect id
    let secondRings = secondPolygons |> List.collect id
    match geographicLinePolygonDistance referenceSystem firstRings secondPolygons,
          geographicLinePolygonDistance referenceSystem secondRings firstPolygons with
    | Some first, Some second -> Some(min first second)
    | Some distance, None
    | None, Some distance -> Some distance
    | None, None -> None

type internal GeographicPolygonTopology =
    { Touches: bool
      Overlaps: bool
      FirstContainsSecond: bool
      SecondContainsFirst: bool }

let internal geographicPolygonPolygonTopology referenceSystem firstPolygons secondPolygons =
    let boundaryPositions source target =
        let targetRings =
            target
            |> List.collect (function
                | [] -> []
                | exterior :: holes ->
                    (GeographicPointPolygonPosition.ExteriorBoundary, exterior)
                    :: (holes |> List.map (fun hole -> GeographicPointPolygonPosition.HoleBoundary, hole)))
        let sharedBoundary start endpoint =
            targetRings
            |> List.tryPick (fun (boundary, ring) ->
                ring
                |> List.pairwise
                |> List.tryPick (fun segment ->
                    if geographicPointOnLineSegment referenceSystem start segment
                       && geographicPointOnLineSegment referenceSystem endpoint segment then Some boundary
                    else None))
        let classify point =
            let position = geographicPointPolygonPosition referenceSystem point target
            match position with
            | GeographicPointPolygonPosition.ExteriorBoundary
            | GeographicPointPolygonPosition.HoleBoundary
            | GeographicPointPolygonPosition.HoleMeridianBoundary -> position
            | _ ->
                // ponytail: Thomas interpolation can move a shared edge sample by about 0.1 m.
                // Keep the fallback at 0.2 m until boundary intersection no longer uses samples.
                let nearRing ring =
                    geographicPointLineDistance referenceSystem point [ ring ]
                    |> Option.exists (fun distance -> distance < 0.2)
                targetRings
                |> List.tryPick (fun (boundary, ring) -> if nearRing ring then Some boundary else None)
                |> Option.defaultValue position
        source
        |> List.collect id
        |> List.collect (fun ring ->
            ring
            |> List.pairwise
            |> List.collect (fun (start, endpoint) ->
                let boundary = sharedBoundary start endpoint
                sampleGeographicSegment referenceSystem start endpoint
                |> Array.toList
                |> List.map (fun point -> boundary |> Option.defaultWith (fun () -> classify point))))
    let firstInSecond = boundaryPositions firstPolygons secondPolygons
    let secondInFirst = boundaryPositions secondPolygons firstPolygons
    let hasInterior positions =
        positions |> List.contains GeographicPointPolygonPosition.Interior
    let hasOutside positions =
        positions |> List.exists (function GeographicPointPolygonPosition.Outside _ -> true | _ -> false)
    let allBoundary positions =
        not (List.isEmpty positions)
        && (positions
            |> List.forall (function
                | GeographicPointPolygonPosition.ExteriorBoundary
                | GeographicPointPolygonPosition.HoleBoundary
                | GeographicPointPolygonPosition.HoleMeridianBoundary -> true
                | _ -> false))
    let sameBoundary = allBoundary firstInSecond && allBoundary secondInFirst
    let firstContainsSecond = not (hasOutside secondInFirst) && (hasInterior secondInFirst || sameBoundary)
    let secondContainsFirst = not (hasOutside firstInSecond) && (hasInterior firstInSecond || sameBoundary)
    let intersects = geographicPolygonPolygonDistance referenceSystem firstPolygons secondPolygons = Some 0.0
    let overlaps =
        intersects && not firstContainsSecond && not secondContainsFirst
        && (hasInterior firstInSecond || hasInterior secondInFirst)
    { Touches = intersects && not overlaps && not firstContainsSecond && not secondContainsFirst
      Overlaps = overlaps
      FirstContainsSecond = firstContainsSecond
      SecondContainsFirst = secondContainsFirst }

let internal lineworkLength (segmentDistance: float * float -> float * float -> float) =
    let length (points: (float * float) list) =
        points
        |> List.pairwise
        |> List.sumBy (fun (first, second) -> segmentDistance first second)

    function
    | GLineString points -> Some(length points)
    | GMultiLineString lines -> Some(lines |> List.sumBy length)
    | _ -> None

let internal interpolateLinePoints
    (segmentDistance: float * float -> float * float -> float)
    (pointAlong: float * float -> float * float -> float -> float -> float * float)
    spacing
    multiple
    (points: (float * float) list)
    =
    match points with
    | [] -> []
    | first :: rest when spacing <= 0.0 -> [ first ]
    | first :: rest ->
        let result = ResizeArray<float * float>()
        let mutable previous = first
        let mutable start = first
        let mutable previousDistance = 0.0
        let mutable nextDistance = spacing

        for endpoint in rest do
            let currentDistance = previousDistance + segmentDistance previous endpoint
            while currentDistance >= nextDistance && (multiple || result.Count = 0) do
                let remainingDistance = currentDistance - previousDistance
                let fraction = (nextDistance - previousDistance) / remainingDistance
                let point = pointAlong start endpoint fraction remainingDistance
                result.Add point
                start <- point
                previousDistance <- nextDistance
                nextDistance <- nextDistance + spacing
            previousDistance <- currentDistance
            previous <- endpoint
            start <- endpoint

        if result.Count = 0 then result.Add previous
        List.ofSeq result

let private strategyCodeAndPoints = function
    | BufferStrategy.EndRound points -> 1, points
    | BufferStrategy.EndFlat -> 2, 0.0
    | BufferStrategy.JoinRound points -> 3, points
    | BufferStrategy.JoinMiter points -> 4, points
    | BufferStrategy.PointCircle points -> 5, points
    | BufferStrategy.PointSquare -> 6, 0.0

let internal encodeBufferStrategy strategy =
    let code, points = strategyCodeAndPoints strategy
    let bytes = Array.zeroCreate<byte> encodedStrategyLength
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, strategyCodeLength), code)
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(strategyCodeLength, sizeof<double>), BitConverter.DoubleToInt64Bits points)
    bytes

let internal tryDecodeBufferStrategy (bytes: byte[]) =
    if bytes.Length <> encodedStrategyLength then
        None
    else
        let code = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, strategyCodeLength))

        let points =
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(strategyCodeLength, sizeof<double>))
            |> BitConverter.Int64BitsToDouble
        let positive = Double.IsFinite points && points > 0.0
        let validPoints = positive && points <= float Limits.maxPointsInGeometryLimit

        match code with
        | 1 when validPoints -> Some(BufferStrategy.EndRound points)
        | 2 when points = 0.0 -> Some BufferStrategy.EndFlat
        | 3 when validPoints -> Some(BufferStrategy.JoinRound points)
        | 4 when positive -> Some(BufferStrategy.JoinMiter points)
        | 5 when validPoints -> Some(BufferStrategy.PointCircle points)
        | 6 when points = 0.0 -> Some BufferStrategy.PointSquare
        | _ -> None

let private toNts (geometry: Geometry) =
    let value = WKBReader().Read(geometryToWkb geometry)
    value.SRID <- geometry.Srid
    value

let internal geometryCrossesPlanar first second =
    let first, second = toNts first, toNts second
    if first.IsEmpty || second.IsEmpty then None
    else
        match int first.Dimension, int second.Dimension with
        | 0, 1 | 0, 2 | 1, 1 | 1, 2 -> Some(RelateNG.Relate(first, second, RelatePredicate.Crosses()))
        | _ -> None

let internal geometryOverlapsPlanar first second =
    let first, second = toNts first, toNts second
    if first.IsEmpty || second.IsEmpty || first.Dimension <> second.Dimension then None
    else Some(RelateNG.Relate(first, second, RelatePredicate.Overlaps()))

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

let internal overlay kind (first: Geometry) (second: Geometry) =
    let spatialFunction =
        match kind with
        | OverlayKind.Intersection -> SpatialFunction.Intersection
        | OverlayKind.Union -> SpatialFunction.Union
        | OverlayKind.Difference -> SpatialFunction.Difference
        | OverlayKind.SymmetricDifference -> SpatialFunction.SymDifference

    attempt first.Srid (fun () -> OverlayNGRobust.Overlay(toNts first, toNts second, spatialFunction))

let private geographicPointRadians (latitude, longitude) =
    mysqlRadians latitude, mysqlRadians longitude

let private equalGeographicCoordinates (latitude, longitude) (otherLatitude, otherLongitude) =
    boostGeoEqual latitude otherLatitude && boostGeoEqual longitude otherLongitude

// Points compare in radians. A cell is wider than the relative comparison
// tolerance anywhere in the geographic coordinate domain.
let private geographicPointIndex () =
    let cellWidth = 8.0 * (Math.BitIncrement(1.0) - 1.0)
    let cells = System.Collections.Generic.Dictionary<int64 * int64, ResizeArray<float * float>>()
    let cell coordinate = int64 (floor (coordinate / cellWidth))

    let contains point =
        let latitude, longitude = geographicPointRadians point
        let x, y = cell latitude, cell longitude
        let mutable found = false
        for i in x - 1L .. x + 1L do
            for j in y - 1L .. y + 1L do
                if not found then
                    match cells.TryGetValue((i, j)) with
                    | true, candidates ->
                        found <- candidates |> Seq.exists (equalGeographicCoordinates (latitude, longitude))
                    | _ -> ()

        found

    let add point =
        let latitude, longitude = geographicPointRadians point
        let key = cell latitude, cell longitude
        match cells.TryGetValue key with
        | true, candidates -> candidates.Add((latitude, longitude))
        | _ -> cells.Add(key, ResizeArray([ latitude, longitude ]))

    contains, add

let private distinctGeographicPoints points =
    let contains, add = geographicPointIndex ()

    points
    |> List.filter (fun point ->
        if contains point then false
        else
            add point
            true)

let private geographicPointResult points =
    match distinctGeographicPoints points with
    | [] -> GEmpty
    | [ (latitude, longitude) ] -> GPoint(latitude, longitude)
    | points -> GMultiPoint points

let internal geographicPointMembers = function
    | GPoint(latitude, longitude) -> Some [ latitude, longitude ]
    | GMultiPoint points -> Some points
    | _ -> None

let internal geographicPointMembership = function
    | [] -> fun _ -> false
    | [ point ] -> geographicPointRadians >> equalGeographicCoordinates (geographicPointRadians point)
    | points ->
        let contains, add = geographicPointIndex ()
        points |> List.distinct |> List.iter add
        contains

let private orderGeographicRelationPoints comparePoints (points: (float * float) array) =
    let insertionThreshold = 16
    let less first second = comparePoints first second < 0
    let swap first second =
        let value = points[first]
        points[first] <- points[second]
        points[second] <- value

    let median first middle last =
        if less points[first] points[middle] then
            if less points[middle] points[last] then middle
            elif less points[first] points[last] then last
            else first
        elif less points[first] points[last] then first
        elif less points[middle] points[last] then last
        else middle

    let partition first endExclusive =
        swap first (median (first + 1) (first + (endExclusive - first) / 2) (endExclusive - 1))
        let pivot = points[first]
        let mutable left = first + 1
        let mutable right = endExclusive - 1
        while left < right do
            while left < endExclusive && less points[left] pivot do left <- left + 1
            while right > first && less pivot points[right] do right <- right - 1
            if left < right then
                swap left right
                left <- left + 1
                right <- right - 1

        if left = right && less points[left] pivot then left + 1 else left

    let rec orderPartitions first endExclusive depth =
        if endExclusive - first > insertionThreshold then
            if depth = 0 then
                let exactOrder =
                    System.Collections.Generic.Comparer<_>.Create(fun (latitude, longitude) (otherLatitude, otherLongitude) ->
                        Operators.compare (longitude, latitude) (otherLongitude, otherLatitude))

                Array.Sort(points, first, endExclusive - first, exactOrder)
            else
                let split = partition first endExclusive
                orderPartitions split endExclusive (depth - 1)
                orderPartitions first split (depth - 1)

    orderPartitions 0 points.Length (2 * System.Numerics.BitOperations.Log2(uint32 points.Length))

    for index in 1 .. points.Length - 1 do
        let point = points[index]
        if index < insertionThreshold && less point points[0] then
            Array.Copy(points, 0, points, 1, index)
            points[0] <- point
        else
            let mutable target = index
            while target > 0 && less point points[target - 1] do
                points[target] <- points[target - 1]
                target <- target - 1

            points[target] <- point

// MySQL's lower-bound search depends on the ordering of near-equal points
// because the coordinate tolerance is not transitive. Match the oracle's
// partition and insertion order, with bounds and an exact depth fallback.
let internal geographicOrderedPointMembership points =
    let compareCoordinate first second =
        if boostGeoEqual first second then 0 else Operators.compare first second

    let comparePoints (latitude, longitude) (otherLatitude, otherLongitude) =
        match compareCoordinate longitude otherLongitude with
        | 0 -> compareCoordinate latitude otherLatitude
        | order -> order

    let ordered = points |> List.map geographicPointRadians |> List.toArray
    orderGeographicRelationPoints comparePoints ordered

    fun point ->
        let target = geographicPointRadians point
        let mutable lower = 0
        let mutable upper = ordered.Length
        while lower < upper do
            let middle = lower + (upper - lower) / 2
            if comparePoints ordered[middle] target < 0 then lower <- middle + 1
            else upper <- middle

        lower < ordered.Length && comparePoints target ordered[lower] >= 0

let private overlayGeographicPoints kind firstShape (first: (float * float) list) secondShape (second: (float * float) list) =
    // MySQL dispatches a Point before a MultiPoint for the commutative
    // operations, and walks the smaller MultiPoint in an intersection.
    let left, right =
        match firstShape, secondShape with
        | GMultiPoint _, GPoint _ -> (secondShape, second), (firstShape, first)
        | _ -> (firstShape, first), (secondShape, second)

    let membership shape otherShape other =
        match shape, otherShape with
        | GMultiPoint _, GMultiPoint _ -> geographicOrderedPointMembership other
        | _ -> geographicPointMembership other

    let except (shape, points) (otherShape, other) =
        points |> List.filter (membership shape otherShape other >> not)

    let intersection (shape, points) (otherShape, other) =
        if List.length points <= List.length other then points |> List.filter (membership shape otherShape other)
        else other |> List.filter (membership otherShape shape points)

    let union leftOperand rightOperand = snd leftOperand @ except rightOperand leftOperand

    let points =
        match kind with
        | OverlayKind.Intersection -> intersection left right
        | OverlayKind.Difference -> except (firstShape, first) (secondShape, second)
        | OverlayKind.Union -> union left right
        | OverlayKind.SymmetricDifference ->
            let included = union left right
            let shared = intersection left right
            except (GMultiPoint included, included) (GMultiPoint shared, shared)

    geographicPointResult points

let private orientGeographicPolygonRings referenceSystem =
    let orient hole ring =
        let area = geographicRingAreaSign referenceSystem ring
        if (hole && area > 0.0) || (not hole && area < 0.0) then List.rev ring else ring

    List.mapi (fun index ring -> orient (index > 0) ring)

let private orientGeographicPolygons referenceSystem =
    let polygon = orientGeographicPolygonRings referenceSystem

    function
    | GPolygon rings -> GPolygon(polygon rings)
    | GMultiPolygon polygons -> GMultiPolygon(List.map polygon polygons)
    | shape -> shape

let private narrowGeographicOverlayShape = function
    | GMultiPolygon [ rings ] -> GPolygon rings
    | GMultiLineString [ line ] -> GLineString line
    | GMultiPoint points -> geographicPointResult points
    | shape -> shape

let private equatorialSegment ((startLatitude, _), (endLatitude, _)) =
    startLatitude = 0.0 && endLatitude = 0.0

let private supportedGeographicOverlaySegment ((startLatitude, startLongitude), (endLatitude, endLongitude)) =
    abs startLatitude < 90.0 && abs endLatitude < 90.0
    && not (boostGeoEqual (abs (longitudeDistance startLongitude endLongitude)) 180.0)

let private tryGeographicCrossingPoint mapLongitude referenceSystem first second =
    tryGeographicSegmentCrossing referenceSystem first second
    |> Option.map (fun (latitude, longitude) -> latitude / mysqlRadians 1.0, mapLongitude longitude / mysqlRadians 1.0)

let private geographicAxisOffset equatorial (firstLatitude, firstLongitude) (secondLatitude, secondLongitude) =
    if equatorial then longitudeDistanceRadians (mysqlRadians firstLongitude) (mysqlRadians secondLongitude)
    else mysqlRadians secondLatitude - mysqlRadians firstLatitude

let private nearGeographicAxisSpan referenceSystem equatorial ((a, b) as first) ((c, d) as second) =
    let offset = geographicAxisOffset equatorial
    let unitEllipsoid = { referenceSystem with SemiMajorAxis = Some 1.0 }
    let distance left right =
        geographicInverseDistanceRadians false unitEllipsoid (geographicPointRadians left) (geographicPointRadians right)

    // Measure both segments from the shorter segment's origin. Switching to
    // the longer segment can round a nearby endpoint against a different cut.
    let start, endpoint =
        let segment = if distance a b <= distance c d then first else second
        if not equatorial && fst (fst segment) > fst (snd segment) then snd segment, fst segment
        else segment

    let length = distance start endpoint
    let project point =
        let magnitude = distance start point
        let signed = if offset start point * offset start endpoint < 0.0 then -magnitude else magnitude
        if boostGeoEqual signed 0.0 || boostGeoEqual (offset start point) 0.0 then 0.0
        elif boostGeoEqual signed length || boostGeoEqual (offset endpoint point) 0.0 then length
        else signed

    let position value start endpoint =
        if boostGeoEqual value start || boostGeoEqual value endpoint then GeographicPointLinePosition.Boundary
        elif min start endpoint < value && value < max start endpoint then GeographicPointLinePosition.Interior
        else GeographicPointLinePosition.Outside

    let a1, a2, b1, b2 = project a, project b, project c, project d
    let covered value low high = position value low high <> GeographicPointLinePosition.Outside
    let interior value low high = position value low high = GeographicPointLinePosition.Interior

    // A rounded endpoint tie belongs to the first operand.
    [ if covered a1 b1 b2 then yield a
      if interior b1 a1 a2 then yield c
      if covered a2 b1 b2 then yield b
      if interior b2 a1 a2 then yield d ]
    |> List.truncate 2

let private tryNearGeographicAxisIntersection referenceSystem ((a, b) as first) ((c, d) as second) =
    let equatorial = equatorialSegment first && equatorialSegment second
    let sharedMeridian = snd a = snd b && snd a = snd c && snd a = snd d
    if not (equatorial || sharedMeridian) then None
    else
        let offset = geographicAxisOffset equatorial
        let epsilon = Math.BitIncrement 1.0 - 1.0
        // An inverse based on acos can round a close endpoint to zero distance.
        // Keep the ordinary segment path outside this rounding region.
        let near = 2.0 * sqrt epsilon
        let length segment = abs (offset (fst segment) (snd segment))
        let touches firstPoint firstAway secondPoint secondAway =
            let gap = offset firstPoint secondPoint
            if gap = 0.0 || abs gap > near || abs gap >= min (length first) (length second) then None
            elif offset firstPoint firstAway * offset secondPoint secondAway > 0.0 then
                Some(nearGeographicAxisSpan referenceSystem equatorial first second)
            else
                let separated = gap * offset secondPoint secondAway > 0.0
                // Boost tests the expanded longitude envelope before finding
                // a common endpoint. Meridians keep their latitude ordering.
                let margin = 1000.0 * epsilon * max 1.0 (abs (mysqlRadians (snd firstPoint)))
                if separated && (not equatorial || abs gap > margin) then Some []
                else
                    let inverse left right =
                        geographicInverseDistanceRadians false referenceSystem (geographicPointRadians left) (geographicPointRadians right)
                        / referenceSystem.SemiMajorAxis.Value
                    let segment, join, query =
                        if inverse a b <= inverse c d then first, firstPoint, secondPoint
                        else second, secondPoint, firstPoint
                    let start, endpoint =
                        let start, endpoint = segment
                        if not equatorial && fst start > fst endpoint then endpoint, start else start, endpoint
                    let distance = inverse start endpoint
                    let projected =
                        let magnitude = inverse start query
                        if offset start query * offset start endpoint < 0.0 then -magnitude else magnitude
                    let joiningStart = join = start
                    let boundary = if joiningStart then 0.0 else distance
                    if boostGeoEqual projected boundary then Some [ firstPoint ]
                    elif (if joiningStart then projected > boundary else projected < boundary) then Some [ firstPoint; secondPoint ]
                    else Some []

        [ a, b, c, d; a, b, d, c; b, a, c, d; b, a, d, c ]
        |> List.tryPick (fun (firstPoint, firstAway, secondPoint, secondAway) -> touches firstPoint firstAway secondPoint secondAway)

type private GeographicLinePiece =
    { Start: float * float
      End: float * float
      CoveringComponents: int list
      StartsSegment: bool }

let private partitionGeographicLineSegment referenceSystem pointContacts otherSegments ((start, endpoint) as segment) =
    let equatorial = equatorialSegment segment
    let distance =
        if equatorial || snd start = snd endpoint then
            // Coordinate order remains distinct when the inverse rounds equal.
            geographicAxisOffset equatorial start >> abs
        else geographicPointDistance referenceSystem start
    let includeContacts = Option.isSome pointContacts
    let initialContacts =
        pointContacts
        |> Option.defaultValue []
        |> List.filter (fun point -> geographicPointOnLineSegment referenceSystem point segment)

    let contacts, intervals =
        otherSegments
        |> List.fold (fun (contacts, intervals) (componentIndex, other) ->
            let points =
                tryNearGeographicAxisIntersection referenceSystem segment other
                |> Option.defaultWith (fun () -> geographicSharedSegmentEndpoints referenceSystem segment other)
            match points with
            | [ point ] -> (if includeContacts then point :: contacts else contacts), intervals
            | [] when includeContacts ->
                let crossing = tryGeographicCrossingPoint normalizedLongitudeRadians referenceSystem segment other
                Option.toList crossing @ contacts, intervals
            | [] -> contacts, intervals
            | points ->
                let ordered = List.sortBy distance points
                contacts, (List.head ordered, List.last ordered, componentIndex) :: intervals) (initialContacts, [])

    let pieces =
        if List.isEmpty intervals then
            [ { Start = start; End = endpoint; CoveringComponents = []; StartsSegment = true } ]
        else
            let covered = intervals |> List.map (fun (first, last, componentIndex) -> distance first, distance last, componentIndex)
            [ yield start
              yield endpoint
              for first, last, _ in intervals do
                  yield first
                  yield last ]
            |> List.distinct
            |> List.map (fun point -> distance point, point)
            |> List.sortBy fst
            |> List.pairwise
            |> List.mapi (fun index ((from, first), (until, last)) ->
                let covering =
                    covered |> List.choose (fun (lower, upper, componentIndex) ->
                        if lower <= from && until <= upper then Some componentIndex else None)
                { Start = first; End = last; CoveringComponents = covering; StartsSegment = index = 0 })

    pieces, List.sortBy distance contacts

let private geographicLineFragments referenceSystem retainShared pieces =
    let finish current complete =
        match current with
        | [] -> complete
        | _ -> List.rev current :: complete

    let current, complete, _ =
        pieces
        |> List.fold (fun (current, complete, previous) piece ->
            let covering = piece.CoveringComponents
            if (not covering.IsEmpty) <> retainShared then [], finish current complete, []
            else
                let continues =
                    not retainShared
                    || List.exists (fun componentIndex -> List.contains componentIndex previous) covering
                    || (match current with
                        | _ :: previousVertex :: _
                            when piece.StartsSegment && List.min covering < List.max previous ->
                            // At a source turn, contacts on an earlier
                            // component can continue the current fragment.
                            not (geographicPointOnLineSegment referenceSystem piece.Start (previousVertex, piece.End))
                        | _ -> false)
                match current, continues with
                | [], _ | _, false -> [ piece.End; piece.Start ], finish current complete, covering
                | _ :: rest, true ->
                    // Keep source vertices and cuts between components that
                    // meet only at a point. Drop other introduced vertices.
                    let vertices = if piece.StartsSegment then current else rest
                    piece.End :: vertices, complete, covering) ([], [], [])

    finish current complete |> List.rev

let private distinctComponents equal members =
    members
    |> List.fold (fun retained item ->
        if List.exists (equal item) retained then retained else item :: retained) []
    |> List.rev

let private distinctGeographicLines referenceSystem lines =
    let meridianExtent = function
        | (latitude, longitude) :: rest when rest |> List.forall (fun (_, lon) -> lon = longitude) ->
            let lower, upper =
                rest |> List.fold (fun (lower, upper) (lat, _) -> min lower lat, max upper lat) (latitude, latitude)
            Some(longitude, mysqlRadians lower, mysqlRadians upper)
        | _ -> None

    let sameExtent first second =
        match first, second with
        | Some(lon, lower, upper), Some(otherLon, otherLower, otherUpper) when lon = otherLon ->
            // An azimuth comparison can mistake a very short meridional
            // fragment for the longer line sharing one of its endpoints.
            boostGeoEqual lower otherLower && boostGeoEqual upper otherUpper
        | _ -> true

    lines
    |> List.map (fun line -> line, meridianExtent line)
    |> distinctComponents (fun (first, firstExtent) (second, secondExtent) ->
        first = second
        || first = List.rev second
        || (sameExtent firstExtent secondExtent
            && geographicLineCovers referenceSystem [ first ] [ second ]
            && geographicLineCovers referenceSystem [ second ] [ first ]))
    |> List.map fst

let private tryGeographicLineOverlay referenceSystem kind srid first second =
    let usableSegments line =
        line |> List.pairwise |> List.filter (fun (start, endpoint) -> start <> endpoint)

    let prepare lines =
        lines |> List.map (fun line -> line, usableSegments line)

    let firstParts, secondParts = prepare first, prepare second
    let indexedSegments parts =
        parts
        |> List.mapi (fun componentIndex (_, segments) -> List.map (fun segment -> componentIndex, segment) segments)
        |> List.concat

    let firstSegments, secondSegments = indexedSegments firstParts, indexedSegments secondParts
    if List.exists List.isEmpty first || List.exists List.isEmpty second
       || not (List.forall (snd >> supportedGeographicOverlaySegment) firstSegments
               && List.forall (snd >> supportedGeographicOverlaySegment) secondSegments) then None
    else
        let coversPoint parts point =
            parts |> List.exists (fun (line, segments) ->
                if List.isEmpty segments then geographicPointMembership [ List.head line ] point
                else geographicPointLinePosition referenceSystem point [ line ] <> GeographicPointLinePosition.Outside)

        let difference otherParts otherSegments (line, segments) =
            if List.isEmpty segments then
                if coversPoint otherParts (List.head line) then [] else [ line ]
            else
                segments
                |> List.collect (partitionGeographicLineSegment referenceSystem None otherSegments >> fst)
                |> geographicLineFragments referenceSystem false

        let lineResult lines =
            match distinctGeographicLines referenceSystem lines with
            | [] -> GEmpty
            | [ line ] -> GLineString line
            | lines -> GMultiLineString lines

        let subtractFirst () = firstParts |> List.collect (difference secondParts secondSegments)
        let subtractSecond () = secondParts |> List.collect (difference firstParts firstSegments)

        let hasInteriorOccurrence point segments =
            (segments |> List.pairwise |> List.exists (fun ((_, vertex), _) -> vertex = point))
            || (segments |> List.exists (fun ((start, endpoint) as segment) ->
                point <> start && point <> endpoint && geographicPointOnLineSegment referenceSystem point segment))

        match kind with
        | OverlayKind.Union ->
            // A repeated point absorbs a component whose traversal
            // visits that point in its interior.
            let repeatedPoints =
                firstParts |> List.choose (function line, [] when List.length line > 2 -> Some(List.head line) | _ -> None)
            let remaining =
                let fragments = subtractSecond ()
                if repeatedPoints.IsEmpty then fragments
                else
                    fragments |> List.filter (fun line ->
                        let segments = usableSegments line
                        repeatedPoints |> List.forall (fun point -> not (hasInteriorOccurrence point segments)))
            first @ remaining |> lineResult |> Some
        | OverlayKind.Difference -> subtractFirst () |> lineResult |> Some
        | OverlayKind.SymmetricDifference -> subtractFirst () @ subtractSecond () |> lineResult |> Some
        | OverlayKind.Intersection ->
            let otherPoints = secondParts |> List.choose (function line, [] -> Some line.Head | _ -> None)
            let intersectPart (line, segments) =
                match segments with
                | [] ->
                    let point = List.head line
                    [], (if coversPoint secondParts point then [ point ] else [])
                | _ when secondSegments.IsEmpty ->
                    [], List.filter (coversPoint [ line, segments ]) otherPoints
                | _ ->
                    let partitioned = segments |> List.map (partitionGeographicLineSegment referenceSystem (Some otherPoints) secondSegments)
                    let pieces = List.collect fst partitioned
                    let shared = geographicLineFragments referenceSystem true pieces
                    // A completely covered source cannot contribute an
                    // isolated crossing, even if the computed point drifts
                    // off the shared geodesic by a rounding error.
                    let contacts =
                        if pieces |> List.forall (fun piece -> not piece.CoveringComponents.IsEmpty) then []
                        else List.collect snd partitioned
                    shared, contacts

            let intersections = List.map intersectPart firstParts
            let shared = List.collect fst intersections
            let contacts =
                intersections
                |> List.collect snd
                |> List.filter (fun point -> geographicPointLinePosition referenceSystem point shared = GeographicPointLinePosition.Outside)
                |> geographicPointResult

            match contacts, lineResult shared with
            | GEmpty, result | result, GEmpty -> Some result
            | points, lines -> Some(GGeometryCollection [ { Srid = srid; Shape = points }; { Srid = srid; Shape = lines } ])

type private GeographicAreaLinePiece =
    { Start: float * float
      End: float * float
      Covered: bool
      StartsSegment: bool }

let private geographicOverlaySections segments =
    let direction ((firstLatitude, firstLongitude), (secondLatitude, secondLongitude)) =
        let longitudeDelta = longitudeDistanceRadians (mysqlRadians firstLongitude) (mysqlRadians secondLongitude)
        sign longitudeDelta, sign (secondLatitude - firstLatitude)

    // Boost splits a turn-search section at a direction change or after
    // eleven edges. Longitude follows the short arc at the antimeridian.
    let rec collect previous slots current sections = function
        | [] -> List.rev (List.rev current :: sections)
        | segment :: rest ->
            let next = direction segment
            if next = previous && slots > 0 then
                collect previous (slots - 1) (segment :: current) sections rest
            else
                collect next 10 [ segment ] (List.rev current :: sections) rest

    match segments with
    | [] -> []
    | first :: rest -> collect (direction first) 10 [ first ] [] rest

[<Struct>]
type private GeographicBounds =
    { West: float
      East: float
      South: float
      North: float }

let private encloseGeographicBounds largerGap mergeIntervalEnd first rest =
    let half = Math.PI
    let period = 2.0 * half
    let intervals box =
        let width = box.East - box.West
        let west =
            if box.West < -half || box.West > half then normalizedLongitudeRadians box.West
            else box.West
        let east = if west = box.West then box.East else west + width
        if width >= period then [ -half, half ]
        elif east > half then [ west, half; -half, east - period ]
        else [ west, east ]

    let combined =
        rest |> List.fold (fun bounds next ->
            { West = min bounds.West next.West
              East = max bounds.East next.East
              South = min bounds.South next.South
              North = max bounds.North next.North }) first

    // The enclosing arc excludes the largest uncovered longitude gap.
    let west, east =
        let west, east = combined.West, combined.East
        if -half <= west && east <= half && east - west <= half then west, east
        else
            match first :: rest |> List.collect intervals |> List.sortBy fst with
            | [] -> west, east
            | (west, firstEast) :: rest ->
                let east, gap, left, right =
                    rest |> List.fold (fun (east, gap, left, right) (start, finish) ->
                        let nextEast = mergeIntervalEnd east finish
                        if largerGap (start - east) gap then nextEast, start - east, east, start
                        else nextEast, gap, left, right) (firstEast, 0.0, west, west)
                let wrapGap = period + west - east
                if gap > wrapGap && not (boostGeoEqual gap wrapGap) then right, left + period
                else west, east
    { combined with West = west; East = east }

let private mergeGeographicBounds first second =
    encloseGeographicBounds (>) max first [ second ]

let private geographicEdgeBounds flattening (first, second) =
    let half = Math.PI
    let quarter = half / 2.0
    let period = 2.0 * half
    let first = geographicPointRadians first
    let second = geographicPointRadians second
    let (latitude1, west), (latitude2, east) =
        if snd first <= snd second then first, second else second, first
    let (latitude1, west), (latitude2, east) =
        if east - west > half then (latitude2, east), (latitude1, west + period)
        else (latitude1, west), (latitude2, east)

    let south, north = min latitude1 latitude2, max latitude1 latitude2
    let south, north =
        if west = east then south, north
        else
            let firstAzimuth, lastAzimuth =
                geographicSegmentAzimuthsRadians flattening (latitude1, west) (latitude2, east)
            if min firstAzimuth lastAzimuth < quarter && quarter < max firstAzimuth lastAzimuth then
                // Clairaut's relation gives the latitude of the geodesic vertex.
                let azimuth = if latitude1 <= latitude2 then firstAzimuth else lastAzimuth
                let axisRatio = 1.0 - flattening
                let reducedLatitude = atan (axisRatio * tan south)
                let reducedVertex = acos (min 1.0 (abs (cos reducedLatitude * sin azimuth)))
                let vertex = atan (tan reducedVertex / axisRatio)
                if south + north < 0.0 then min south (-vertex), north
                else south, max north vertex
            else south, north
    { West = west; East = east; South = south; North = north }

let private geographicSectionBounds flattening edges =
    let bounds = edges |> List.map (geographicEdgeBounds flattening) |> List.reduce mergeGeographicBounds
    let margin value = 1000.0 * (Math.BitIncrement 1.0 - 1.0) * max 1.0 (abs value)
    { West = bounds.West - margin bounds.West
      East = bounds.East + margin bounds.East
      South = bounds.South - margin bounds.South
      North = bounds.North + margin bounds.North }

let private geographicPartitionThreshold = 16

let private normalizeSpheroidalLongitude longitude =
    let halfTurn = Math.PI
    let period = 2.0 * halfTurn
    // Box strategies normalize the signed difference around the antimeridian
    // before translating it. The evaluation order affects rounded edge ties.
    if boostGeoEqual (abs longitude) halfTurn then halfTurn
    elif longitude > halfTurn then
        let normalized = (longitude + halfTurn) % period - halfTurn
        if boostGeoEqual normalized (-halfTurn) then halfTurn else normalized
    elif longitude < -halfTurn then (longitude - halfTurn) % period + halfTurn
    else longitude

let private geographicBoundsLongitudeOffset first second =
    let period = 2.0 * Math.PI
    let signed = normalizeSpheroidalLongitude (second - first)
    if signed < 0.0 then signed + period else signed

let private geographicBoundsIntersect a b =
    let period = 2.0 * Math.PI
    let width = a.East - a.West
    let otherWidth = b.East - b.West
    let shifted = a.West + geographicBoundsLongitudeOffset a.West b.West
    let wrappedEnd = shifted - period + otherWidth
    let westEnd = if abs (wrappedEnd - b.East) < Math.PI then b.East else wrappedEnd
    b.South <= a.North && a.South <= b.North
    && (width >= period || otherWidth >= period || shifted <= a.East || a.West <= westEnd)

let private visitGeographicSectionPairs visit first second =
    let bounds sections = sections |> List.map snd |> List.reduce mergeGeographicBounds
    let divide lower upper sections =
        let lowerHits, remaining = List.partition (snd >> geographicBoundsIntersect lower) sections
        let spanning, lowerOnly = List.partition (snd >> geographicBoundsIntersect upper) lowerHits
        lowerOnly, List.filter (snd >> geographicBoundsIntersect upper) remaining, spanning
    let visitPairs a b =
        for firstIndex, _ in a do
            for secondIndex, _ in b do
                visit firstIndex secondIndex
    let many sections = List.length sections >= geographicPartitionThreshold

    let rec descend depth byLongitude box a b =
        let lower, upper =
            if byLongitude then
                let middle = (box.West + box.East) / 2.0
                { box with East = middle }, { box with West = middle }
            else
                let middle = (box.South + box.North) / 2.0
                { box with North = middle }, { box with South = middle }
        let leftA, rightA, spanningA = divide lower upper a
        let leftB, rightB, spanningB = divide lower upper b
        let deeper box = descend (depth + 1) (not byLongitude) box
        let canDescend x y = depth < 100 && many x && many y

        // Within a partition, sections that straddle the midpoint are
        // visited before pairs confined to either half.
        if canDescend spanningA spanningB then deeper (bounds (spanningA @ spanningB)) spanningA spanningB
        else visitPairs spanningA spanningB

        if canDescend leftB rightB && many spanningA then
            let spanning = bounds spanningA
            deeper spanning spanningA leftB
            deeper spanning spanningA rightB
        else
            visitPairs spanningA leftB
            visitPairs spanningA rightB

        if canDescend leftA rightA && many spanningB then
            let spanning = bounds spanningB
            deeper spanning leftA spanningB
            deeper spanning rightA spanningB
        else
            visitPairs leftA spanningB
            visitPairs rightA spanningB

        if canDescend leftA leftB then deeper lower leftA leftB
        else visitPairs leftA leftB
        if canDescend rightA rightB then deeper upper rightA rightB
        else visitPairs rightA rightB

    descend 0 true (bounds (first @ second)) first second

let private geographicPartitionContacts referenceSystem lineSections boundarySections contacts =
    if List.length lineSections <= geographicPartitionThreshold
       || List.length boundarySections <= geographicPartitionThreshold
       || List.forall List.isEmpty contacts then
        contacts |> List.collect (List.collect snd)
    else
        let pairs =
            contacts
            |> List.mapi (fun line -> List.map (fun (boundary, points) -> struct (line, boundary), points))
            |> List.concat
        let remaining = Collections.Generic.Dictionary<struct (int * int), (float * float) list>()
        for key, points in pairs do remaining.Add(key, points)
        let ordered = ResizeArray()
        let visit first second =
            let key = struct (first, second)
            match remaining.TryGetValue key with
            | true, points ->
                ordered.AddRange points
                remaining.Remove key |> ignore
            | _ -> ()
        let flattening = referenceSystem.InverseFlattening |> Option.map (fun value -> 1.0 / value) |> Option.defaultValue 0.0
        let index sections = List.mapi (fun index section -> index, geographicSectionBounds flattening section) sections
        visitGeographicSectionPairs visit (index lineSections) (index boundarySections)
        // Keep any borderline contact the clipping pass found outside the section boxes.
        for struct (line, boundary), _ in pairs do visit line boundary
        List.ofSeq ordered

let private geographicAreaLinePieces referenceSystem includePoints polygons boundarySegments ((start, endpoint) as segment) =
    let covered point =
        match geographicPointPolygonPosition referenceSystem point polygons with
        | GeographicPointPolygonPosition.Outside _ -> false
        | _ -> true

    if start = endpoint then
        [ { Start = start; End = endpoint; Covered = covered start; StartsSegment = true } ], []
    else
        let distance = geographicPointDistance referenceSystem start
        // MySQL retains the solver's longitude in area overlay results,
        // even when a cut just beyond the antimeridian uses an extra turn.
        let crossing = tryGeographicCrossingPoint id referenceSystem segment

        let cuts, contacts, spans =
            boundarySegments
            |> List.fold (fun (cuts, contacts, spans) (section, ((a, b) as boundary)) ->
                let addContacts = function
                    | [] -> contacts
                    | points when includePoints -> (section, points) :: contacts
                    | _ -> contacts

                match geographicSharedSegmentEndpoints referenceSystem segment boundary with
                | [] ->
                    // The linear overlay traverses the polygon in the
                    // opposite direction to the point overlay. Equal-latitude
                    // edges can yield two distinct Sjöberg solutions.
                    let cut = crossing (b, a)
                    let point =
                        if not includePoints then []
                        elif fst a <> fst b then Option.toList cut
                        else crossing boundary |> Option.toList
                    Option.toList cut @ cuts, addContacts point, spans
                | [ point ] -> point :: cuts, addContacts [ point ], spans
                | points ->
                    let positions = List.map distance points
                    let turnPoints =
                        if points = [ a; b ] && (points |> List.forall (fun point -> point <> start && point <> endpoint)) then
                            // The collinear turn belongs to the destination of
                            // the polygon edge. Its start belongs to the incoming edge.
                            [ b ]
                        else points
                    points @ cuts, addContacts turnPoints, (List.min positions, List.max positions) :: spans) ([], [], [])

        let cuts = distinctGeographicPoints (start :: endpoint :: cuts) |> List.sortBy distance
        let pieces =
            cuts
            |> List.map (fun point -> distance point, point)
            |> List.pairwise
            |> List.mapi (fun index ((lower, first), (upper, second)) ->
                let alongBoundary = spans |> List.exists (fun (from, until) -> from <= lower && upper <= until)
                let isCovered =
                    alongBoundary
                    || covered (geographicPointAlong referenceSystem start endpoint ((lower + upper) / 2.0))
                { Start = first; End = second; Covered = isCovered; StartsSegment = index = 0 })
        pieces, List.rev contacts

let private geographicAreaLineFragments retainCovered pieces =
    let finish current finished =
        if List.isEmpty current then finished else List.rev current :: finished

    let current, finished =
        pieces
        |> List.fold (fun (current, finished) piece ->
            if piece.Covered <> retainCovered then [], finish current finished
            else
                match current with
                | [] -> [ piece.End; piece.Start ], finished
                | _ :: rest ->
                    // Preserve source vertices. A cut with the same coverage
                    // on both sides does not introduce a new vertex.
                    piece.End :: (if piece.StartsSegment then current else rest), finished) ([], [])

    finish current finished |> List.rev

let private defaultGeographicFlattening = 1.0 - 6356752.3142451793 / 6378137.0

let private geographicSegmentCoversPoint flattening
    (((latitude1, longitude1) as start), ((latitude2, longitude2) as endpoint))
    ((latitude, longitude) as query) =
    let length = longitudeDistanceRadians longitude1 longitude2
    let offset = longitudeDistanceRadians longitude1 longitude
    let sameStart = boostGeoEqual offset 0.0
    let sameEnd = boostGeoEqual (longitudeDistanceRadians longitude2 longitude) 0.0
    match sameStart, sameEnd with
    | true, true -> min latitude1 latitude2 <= latitude && latitude <= max latitude1 latitude2
    | true, false -> boostGeoEqual latitude latitude1
    | false, true -> boostGeoEqual latitude latitude2
    | false, false when offset * length > 0.0 && abs offset < abs length ->
        let azimuth point = geographicSegmentAzimuthsRadians flattening start point |> fst
        let delta = normalizedLongitudeRadians (azimuth query - azimuth endpoint)
        boostGeoEqual delta 0.0 || boostGeoEqual (abs delta) Math.PI
    | _ -> false

let private geographicOverlayLineCoverage lines =
    // MySQL subtracts the clipped linework using Boost's default spheroid.
    let segments = lines |> List.collect (List.map geographicPointRadians >> List.pairwise)
    fun point ->
        let query = geographicPointRadians point
        segments |> List.exists (fun segment -> geographicSegmentCoversPoint defaultGeographicFlattening segment query)

let private geographicMbr referenceSystem shape =
    let half = Math.PI
    let pole = half / 2.0
    let major = referenceSystem.SemiMajorAxis.Value
    let minor = major * (1.0 - 1.0 / referenceSystem.InverseFlattening.Value)
    let flattening = (major - minor) / major

    let pointBounds point =
        let latitude, longitude = geographicPointRadians point
        let longitude = if boostGeoEqual (abs latitude) pole then 0.0 else normalizedLongitudeRadians longitude
        { West = longitude; East = longitude; South = latitude; North = latitude }

    let normalizeBox box =
        if boostGeoEqual (box.East - box.West) (2.0 * half) then
            { box with West = -half; East = half }
        else
            let west = normalizeSpheroidalLongitude box.West
            let east = normalizeSpheroidalLongitude box.East
            { box with West = west; East = if east < west then east + 2.0 * half else east }

    let extendLineBox first second =
        // The new edge is already normalized. The sweep visits a meridian's
        // end after a wrapped interval's, and a new wrapped edge ends last
        // when the two intervals start at the same seam.
        let first = normalizeBox first
        let bothWrap = first.East > half && second.East > half
        let mergeEnd a b =
            if not (boostGeoEqual a b) then max a b
            elif bothWrap then a
            else b
        encloseGeographicBounds (>) mergeEnd second [ first ]

    let lineBounds points =
        match points with
        | [] -> None
        | [ point ] -> Some(pointBounds point)
        | _ ->
            points |> List.pairwise |> List.map (geographicEdgeBounds flattening)
            |> List.reduce extendLineBox |> Some

    let union largerGap = function
        | [] -> None
        | first :: rest as boxes ->
            let longitudes =
                boxes |> List.filter (fun box ->
                    not (boostGeoEqual box.South pole || boostGeoEqual box.North (-pole)))
            let west, east =
                match longitudes with
                | [] -> 0.0, 0.0
                | box :: remaining ->
                    let bounds = encloseGeographicBounds largerGap max box remaining
                    bounds.West, bounds.East
            let south, north = rest |> List.fold (fun (south, north) box -> min south box.South, max north box.North) (first.South, first.North)
            Some { West = west; East = east; South = south; North = north }

    let polygonBounds rings =
        match rings with
        | ring :: _ when not (List.isEmpty ring) ->
            lineBounds ring |> Option.map (fun bounds ->
                if bounds.East - bounds.West < 2.0 * half then bounds
                else
                    // A full-longitude exterior can also enclose a pole.
                    let exterior = orientGeographicPolygonRings referenceSystem [ ring ] |> List.head
                    let sweep =
                        exterior |> List.pairwise
                        |> List.sumBy (fun ((_, first), (_, second)) -> longitudeDistance first second)
                    if sweep > 180.0 then { bounds with North = pole }
                    elif sweep < -180.0 then { bounds with South = -pole }
                    else bounds)
        | _ -> rings |> List.choose lineBounds |> union (>)

    let rec envelope = function
        | GEmpty -> None
        | GPoint(latitude, longitude) -> Some(pointBounds (latitude, longitude))
        | GMultiPoint points ->
            let largerGap first second = first > second && not (boostGeoEqual first second)
            points |> List.map pointBounds |> union largerGap
        | GLineString points -> lineBounds points
        | GMultiLineString lines -> lines |> List.choose lineBounds |> union (>)
        | GPolygon rings -> polygonBounds rings
        | GMultiPolygon polygons -> polygons |> List.choose polygonBounds |> union (>)
        | GGeometryCollection members -> members |> List.choose (fun geometry -> envelope geometry.Shape) |> union (>)

    envelope shape

let private mbrBoundsEqual first second =
    boostGeoEqual first.MinX second.MinX && boostGeoEqual first.MaxX second.MaxX
    && boostGeoEqual first.MinY second.MinY && boostGeoEqual first.MaxY second.MaxY

let private geographicBoundsAsMbr box =
    { MinX = box.West; MaxX = box.East; MinY = box.South; MaxY = box.North }

let private geographicLongitudeCovers outer inner =
    let period = 2.0 * Math.PI
    let outerWidth = outer.East - outer.West
    let innerWidth = inner.East - inner.West
    (outer.West <= inner.West && inner.East <= outer.East)
    || outerWidth >= period
    || (innerWidth <= outerWidth
        && outer.West + geographicBoundsLongitudeOffset outer.West inner.West + innerWidth <= outer.East)

let private geographicBoundsCovers outer inner =
    outer.South <= inner.South && inner.North <= outer.North && geographicLongitudeCovers outer inner

let private geographicBoundsContains outer inner =
    let period = 2.0 * Math.PI
    let longitudeWidth box = box.East - box.West
    let longitudeOffset = geographicBoundsLongitudeOffset outer.West

    let point box = box.South, box.West
    let farCorner box = box.North, box.East
    let longitudeInside isInside longitude =
        longitudeWidth outer >= period
        || isInside outer.West longitude outer.East
        || isInside outer.West (outer.West + longitudeOffset longitude) outer.East
    let within lower value upper = lower < value && value < upper
    let covered lower value upper = lower <= value && value <= upper
    let inBox isInside (latitude, longitude) =
        isInside outer.South latitude outer.North && longitudeInside isInside longitude
    let isPoint box = point box = farCorner box
    let isLine box = (box.West = box.East) <> (box.South = box.North)
    let onOuterLine = geographicSegmentCoversPoint defaultGeographicFlattening (point outer, farCorner outer)

    if mbrBoundsEqual (geographicBoundsAsMbr outer) (geographicBoundsAsMbr inner) then true
    elif isPoint outer then false
    elif isLine outer then
        if isPoint inner then
            let latitude, longitude = point inner
            let isEndpoint (endpointLatitude, endpointLongitude) =
                boostGeoEqual latitude endpointLatitude
                && boostGeoEqual longitude (normalizedLongitudeRadians endpointLongitude)
            not (isEndpoint (point outer) || isEndpoint (farCorner outer)) && onOuterLine (latitude, longitude)
        else isLine inner && onOuterLine (point inner) && onOuterLine (farCorner inner)
    elif isPoint inner then inBox within (point inner)
    elif isLine inner then
        // A line along the edge has no point in the box's interior.
        let alongEdge =
            (inner.West = inner.East && (inner.West = outer.West || inner.West = outer.East))
            || (inner.South = inner.North && (inner.South = outer.South || inner.South = outer.North))
        not alongEdge && inBox covered (point inner) && inBox covered (farCorner inner)
    else geographicBoundsCovers outer inner

[<RequireQualifiedAccess>]
type internal MbrRelation =
    | Contains
    | Within
    | Covers
    | CoveredBy
    | Intersects
    | Disjoint
    | Equals
    | Overlaps
    | Touches

let private planarMbrContains outer inner =
    let contains minimum maximum innerMinimum innerMaximum =
        if minimum = maximum then
            innerMinimum = innerMaximum && minimum = innerMinimum
        elif innerMinimum = innerMaximum then
            minimum < innerMinimum && innerMinimum < maximum
        else
            minimum <= innerMinimum && innerMaximum <= maximum

    mbrBoundsEqual outer inner
    || (contains outer.MinX outer.MaxX inner.MinX inner.MaxX
        && contains outer.MinY outer.MaxY inner.MinY inner.MaxY)

let private planarMbrCovers outer inner =
    outer.MinX <= inner.MinX && inner.MaxX <= outer.MaxX
    && outer.MinY <= inner.MinY && inner.MaxY <= outer.MaxY

let private planarMbrIntersects first second =
    first.MinX <= second.MaxX && second.MinX <= first.MaxX
    && first.MinY <= second.MaxY && second.MinY <= first.MaxY

let private mbrIsPoint box = box.MinX = box.MaxX && box.MinY = box.MaxY
let private mbrIsLine box = (box.MinX = box.MaxX) <> (box.MinY = box.MaxY)

let private planarLineMbrInteriorsMeet first second =
    let interiorsMeet low high otherLow otherHigh =
        (low = high && otherLow = otherHigh && low = otherLow)
        || (low < otherHigh && otherLow < high)
    interiorsMeet first.MinX first.MaxX second.MinX second.MaxX
    && interiorsMeet first.MinY first.MaxY second.MinY second.MaxY

let private mbrOverlaps lineOverlaps first second =
    if mbrIsPoint first || mbrIsPoint second then false
    elif mbrIsLine first && mbrIsLine second then lineOverlaps first second
    else
        first.MinX < second.MaxX && second.MinX < first.MaxX
        && first.MinY < second.MaxY && second.MinY < first.MaxY
        && not (planarMbrCovers first second || planarMbrCovers second first)

let private mbrTouches lineTouches first second =
    let atEndpoint point line =
        (point.MinX = line.MinX && point.MinY = line.MinY)
        || (point.MinX = line.MaxX && point.MinY = line.MaxY)

    if mbrIsPoint first && mbrIsPoint second then false
    elif mbrIsPoint first && mbrIsLine second then atEndpoint first second
    elif mbrIsLine first && mbrIsPoint second then atEndpoint second first
    elif mbrIsLine first && mbrIsLine second then lineTouches first second
    else
        planarMbrIntersects first second
        && (first.MaxX = second.MinX || second.MaxX = first.MinX
            || first.MaxY = second.MinY || second.MaxY = first.MinY)

let private planarMbrRelation relation first second =
    match relation with
    | MbrRelation.Contains -> planarMbrContains first second
    | MbrRelation.Within -> planarMbrContains second first
    | MbrRelation.Covers -> planarMbrCovers first second
    | MbrRelation.CoveredBy -> planarMbrCovers second first
    | MbrRelation.Intersects -> planarMbrIntersects first second
    | MbrRelation.Disjoint -> not (planarMbrIntersects first second)
    | MbrRelation.Equals -> mbrBoundsEqual first second
    | MbrRelation.Overlaps ->
        mbrOverlaps (fun a b -> planarLineMbrInteriorsMeet a b && not (planarMbrCovers a b || planarMbrCovers b a)) first second
    | MbrRelation.Touches ->
        mbrTouches (fun a b -> planarMbrIntersects a b && not (planarLineMbrInteriorsMeet a b)) first second

let private defaultMbrSpheroid = SpatialReferenceSystems.tryFind 4326 |> Option.get

let private geographicMbrRelation referenceSystem relation first second =
    let lineTopology referenceSystem a b =
        let line box =
            [ [ box.MinY / geographicRadiansPerDegree, box.MinX / geographicRadiansPerDegree
                box.MaxY / geographicRadiansPerDegree, box.MaxX / geographicRadiansPerDegree ] ]
        geographicLineLineTopology referenceSystem (line a) (line b)

    match relation with
    | MbrRelation.Contains -> geographicBoundsContains first second
    | MbrRelation.Within -> geographicBoundsContains second first
    | MbrRelation.Covers -> geographicBoundsCovers first second
    | MbrRelation.CoveredBy -> geographicBoundsCovers second first
    | MbrRelation.Intersects -> geographicBoundsIntersect first second
    | MbrRelation.Disjoint -> not (geographicBoundsIntersect first second)
    | MbrRelation.Equals -> mbrBoundsEqual (geographicBoundsAsMbr first) (geographicBoundsAsMbr second)
    // MySQL's box overlap and touch strategies compare the stored intervals,
    // while their line special cases compare actual geodesic segments.
    | MbrRelation.Overlaps ->
        let overlaps a b =
            let topology = lineTopology defaultMbrSpheroid a b
            topology.Overlaps || topology.Crosses
        mbrOverlaps overlaps (geographicBoundsAsMbr first) (geographicBoundsAsMbr second)
    | MbrRelation.Touches ->
        mbrTouches (fun a b -> (lineTopology referenceSystem a b).Touches) (geographicBoundsAsMbr first) (geographicBoundsAsMbr second)

let internal relateMbr referenceSystem relation first second =
    let compare envelope predicate =
        match envelope first, envelope second with
        | Some a, Some b -> Some(predicate relation a b)
        | None, None when relation = MbrRelation.Equals -> Some true
        | _ when relation = MbrRelation.Equals -> Some false
        | _ -> None

    if referenceSystem.SemiMajorAxis.IsSome then
        compare (fun geometry -> geographicMbr referenceSystem geometry.Shape) (geographicMbrRelation referenceSystem)
    else
        compare geometryBounds planarMbrRelation

let private tryGeographicLineAreaOverlay referenceSystem kind srid lines polygons area =
    let sources = List.map List.pairwise lines
    let polygons = List.map (orientGeographicPolygonRings referenceSystem) polygons
    let rings = polygons |> List.collect id |> List.map List.pairwise
    let boundary = List.concat rings
    let nondegenerate = List.filter (fun (first, second) -> first <> second)
    if List.exists List.isEmpty sources
       || not (List.forall supportedGeographicOverlaySegment (nondegenerate (List.concat sources))
               && List.forall supportedGeographicOverlaySegment (nondegenerate boundary)) then None
    else
        let compose = function
            | [] -> GEmpty
            | [ shape ] -> shape
            | shapes -> GGeometryCollection(shapes |> List.map (fun shape -> { Srid = srid; Shape = shape }))

        let includePoints = kind = OverlayKind.Intersection
        let boundarySections = if includePoints then List.collect geographicOverlaySections rings else [ boundary ]
        let boundary =
            boundarySections
            |> List.mapi (fun section edges -> nondegenerate edges |> List.map (fun edge -> section, edge))
            |> List.concat
        let partitionSegment = geographicAreaLinePieces referenceSystem includePoints polygons boundary
        let sourceSections =
            sources |> List.map (fun segments -> if includePoints then geographicOverlaySections segments else [ segments ])
        let partitionLine sections =
            let partitioned = sections |> List.map (List.map partitionSegment)
            let pieces = partitioned |> List.collect (List.collect fst)
            let contacts =
                // Within each line section, Boost visits polygon sections
                // before advancing to the next pair of sections.
                partitioned |> List.map (fun section ->
                    section
                    |> List.collect snd
                    |> List.groupBy fst
                    |> List.sortBy fst
                    |> List.map (fun (boundary, groups) -> boundary, List.collect snd groups))
            pieces, contacts

        let partitioned = sourceSections |> List.map partitionLine
        let fragments retainCovered =
            partitioned
            |> List.collect (fst >> geographicAreaLineFragments retainCovered)
            |> distinctGeographicLines referenceSystem

        match kind with
        | OverlayKind.Intersection ->
            let inside = fragments true
            let covered = geographicOverlayLineCoverage inside
            let points =
                partitioned
                |> List.collect snd
                |> geographicPartitionContacts referenceSystem (List.concat sourceSections) boundarySections
                |> distinctGeographicPoints
                |> List.choose (fun (latitude, longitude) ->
                    if covered (latitude, longitude) then None
                    else Some(GPoint(latitude, longitude)))
            Some(compose (List.map GLineString inside @ points))
        | OverlayKind.Difference ->
            match fragments false with
            | [] -> Some GEmpty
            | [ line ] -> Some(GLineString line)
            | lines -> Some(GMultiLineString lines)
        | OverlayKind.Union | OverlayKind.SymmetricDifference ->
            let oriented =
                match area with
                | GPolygon _ -> GPolygon(List.head polygons)
                | _ -> GMultiPolygon polygons |> narrowGeographicOverlayShape
            match fragments false with
            | [] -> Some oriented
            | outside ->
                let areas =
                    match kind, oriented with
                    | OverlayKind.Union, GMultiPolygon polygons -> List.map GPolygon polygons
                    | _ -> [ oriented ]
                Some(compose (areas @ List.map GLineString outside))

let private geographicCoveredPoint referenceSystem =
    let onLinework lines point =
        geographicPointLinePosition referenceSystem point lines <> GeographicPointLinePosition.Outside

    let inArea polygons point =
        match geographicPointPolygonPosition referenceSystem point polygons with
        | GeographicPointPolygonPosition.Outside _ -> false
        | _ -> true

    function
    | GLineString line -> Some(onLinework [ line ])
    | GMultiLineString lines -> Some(onLinework lines)
    | GPolygon polygon -> Some(inArea [ polygon ])
    | GMultiPolygon polygons -> Some(inArea polygons)
    | _ -> None

let rec private tryGeographicPointTree = function
    | GEmpty -> Some []
    | GGeometryCollection members ->
        members
        |> List.map (fun memberGeometry -> tryGeographicPointTree memberGeometry.Shape)
        |> tryAllSome
        |> Option.map List.concat
    | shape -> geographicPointMembers shape

let private cleanGeographicPointTree shape =
    let rec removeDuplicates = function
        | GMultiPoint points -> GMultiPoint(distinctGeographicPoints points)
        | GGeometryCollection members ->
            let addMember retained (memberGeometry: Geometry) =
                let cleaned = removeDuplicates memberGeometry.Shape
                match tryGeographicPointTree cleaned with
                | Some (_ :: _ as points) ->
                    let contains = geographicOrderedPointMembership points
                    let duplicate =
                        retained
                        |> List.exists (fun (_, other, containsOther) ->
                            List.forall contains other && List.forall containsOther points)

                    if duplicate then retained
                    else ({ memberGeometry with Shape = cleaned }, points, contains) :: retained
                | _ -> retained

            members
            |> List.fold addMember []
            |> List.rev
            |> List.map (fun (memberGeometry, _, _) -> memberGeometry)
            |> GGeometryCollection
        | shape -> shape

    let rec narrow = function
        | GGeometryCollection [] -> GEmpty
        | GGeometryCollection [ memberGeometry ] -> narrow memberGeometry.Shape
        | shape -> narrowGeographicOverlayShape shape

    shape |> removeDuplicates |> narrow

let internal tryGeographicOverlay referenceSystem kind (first: Geometry) (second: Geometry) =
    let result shape = { Srid = first.Srid; Shape = shape }
    let orient = orientGeographicPolygons referenceSystem
    let normalize = orient >> narrowGeographicOverlayShape
    let isCollection = function GGeometryCollection _ -> true | _ -> false
    let (|Linework|_|) = function
        | GLineString line -> Some [ line ]
        | GMultiLineString lines -> Some lines
        | _ -> None
    let (|Area|_|) = function
        | GPolygon polygon -> Some [ polygon ]
        | GMultiPolygon polygons -> Some polygons
        | _ -> None

    let mixed pointShape pointsFirst points other =
        geographicCoveredPoint referenceSystem other
        |> Option.map (fun covered ->
            match kind with
            | OverlayKind.Intersection -> points |> List.filter covered |> geographicPointResult
            | OverlayKind.Difference when pointsFirst -> points |> List.filter (covered >> not) |> geographicPointResult
            | OverlayKind.Difference -> normalize other
            | OverlayKind.Union
            | OverlayKind.SymmetricDifference ->
                let outside = points |> List.filter (covered >> not) |> geographicPointResult
                let areaOrLine = orient other
                if outside = GEmpty then narrowGeographicOverlayShape areaOrLine
                elif isCollection pointShape && kind = OverlayKind.SymmetricDifference then
                    let grouped = geographicPointMembers outside |> Option.map GMultiPoint
                    let components = narrowGeographicOverlayShape areaOrLine :: Option.toList grouped
                    GGeometryCollection(List.map result components)
                else
                    let components =
                        // Union expands multipart members of a mixed result.
                        // Symmetric difference keeps the higher-dimensional part.
                        match kind, areaOrLine with
                        | OverlayKind.Union, GMultiLineString lines -> List.map GLineString lines
                        | OverlayKind.Union, GMultiPolygon polygons -> List.map GPolygon polygons
                        | _, shape -> [ shape ]
                    let points =
                        match outside with
                        | GMultiPoint members -> members |> List.map (fun (latitude, longitude) -> GPoint(latitude, longitude))
                        | shape -> [ shape ]
                    GGeometryCollection(List.map result (components @ points)))

    let collectionOverlay left right =
        let leftCollection = isCollection left
        let rightCollection = isCollection right

        let removeRepeatedComponents = function
            | GMultiLineString lines ->
                GMultiLineString(distinctGeographicLines referenceSystem lines) |> narrowGeographicOverlayShape
            | GMultiPolygon polygons ->
                let equal first second =
                    if first = second then true
                    else
                        let topology = geographicPolygonPolygonTopology referenceSystem [ first ] [ second ]
                        topology.FirstContainsSecond && topology.SecondContainsFirst

                GMultiPolygon(distinctComponents equal polygons) |> narrowGeographicOverlayShape
            | shape -> shape

        let withNonpoint pointShape pointsFirst points other =
            match kind, other, points with
            // Collection union can dissolve the members of a multipolygon.
            | OverlayKind.Union, GMultiPolygon (_ :: _ :: _), _ -> None
            | OverlayKind.Intersection, _, [] -> Some GEmpty
            | OverlayKind.Difference, _, [] when pointsFirst -> Some GEmpty
            | _, _, [] -> Some(normalize (removeRepeatedComponents other))
            | OverlayKind.Intersection, _, _
            | OverlayKind.Difference, _, _ when kind = OverlayKind.Intersection || pointsFirst ->
                mixed pointShape pointsFirst (distinctGeographicPoints points) other
            | _ ->
                mixed pointShape pointsFirst (distinctGeographicPoints points) (removeRepeatedComponents other)

        let rec subtractPointTree shape other =
            match shape, other with
            | GEmpty, _ -> GEmpty
            | _, GEmpty -> narrowGeographicOverlayShape shape
            | _, GGeometryCollection members ->
                let start =
                    match geographicPointMembers shape with
                    | Some points -> GMultiPoint points
                    | None -> shape

                members |> List.fold (fun remaining memberGeometry -> subtractPointTree remaining memberGeometry.Shape) start
                |> narrowGeographicOverlayShape
            | _ ->
                match geographicPointMembers shape, geographicPointMembers other with
                | Some points, Some otherPoints ->
                    overlayGeographicPoints OverlayKind.Difference shape points other otherPoints
                | _ -> shape

        match tryGeographicPointTree left, tryGeographicPointTree right with
        | Some [], Some _ when kind = OverlayKind.SymmetricDifference && rightCollection ->
            Some(cleanGeographicPointTree right)
        | Some [], Some rightPoints ->
            match kind with
            | OverlayKind.Intersection | OverlayKind.Difference -> Some GEmpty
            | _ -> Some(geographicPointResult rightPoints)
        | Some leftPoints, Some [] ->
            if kind = OverlayKind.Intersection then Some GEmpty
            else Some(geographicPointResult leftPoints)
        | Some leftPoints, Some rightPoints when kind = OverlayKind.Union ->
            // Flatten both inputs before deduplication so the first
            // representative is selected across the complete input order.
            Some(geographicPointResult (if leftCollection then leftPoints @ rightPoints else rightPoints @ leftPoints))
        | Some leftPoints, Some rightPoints ->
            let leftPoints = if leftCollection then distinctGeographicPoints leftPoints else leftPoints
            let rightPoints = if rightCollection then distinctGeographicPoints rightPoints else rightPoints
            let leftShape = if leftCollection then GMultiPoint leftPoints else left
            let rightShape = if rightCollection then GMultiPoint rightPoints else right
            match kind with
            | OverlayKind.Difference when rightCollection -> Some(subtractPointTree leftShape right)
            | OverlayKind.Intersection when rightCollection ->
                Some(overlayGeographicPoints kind rightShape rightPoints leftShape leftPoints)
            | OverlayKind.SymmetricDifference when leftCollection && not rightCollection ->
                Some(overlayGeographicPoints kind rightShape rightPoints leftShape leftPoints)
            | OverlayKind.SymmetricDifference when leftCollection && rightCollection ->
                // The right result has already been narrowed when the left points are applied.
                Some(overlayGeographicPoints kind leftShape leftPoints (geographicPointResult rightPoints) rightPoints)
            | _ -> Some(overlayGeographicPoints kind leftShape leftPoints rightShape rightPoints)
        | Some points, None when leftCollection && not rightCollection -> withNonpoint left true points right
        | None, Some points when rightCollection && not leftCollection -> withNonpoint right false points left
        | _ -> None

    let shape =
        match first.Shape, second.Shape with
        | GEmpty, other
        | other, GEmpty ->
            match kind with
            | OverlayKind.Intersection -> Some GEmpty
            | OverlayKind.Difference when first.Shape = GEmpty -> Some GEmpty
            | _ ->
                match other with
                | GGeometryCollection _ -> collectionOverlay first.Shape second.Shape
                | _ -> Some(normalize other)
        | (GGeometryCollection _ as left), right
        | left, (GGeometryCollection _ as right) -> collectionOverlay left right
        | GLineString left, GLineString right ->
            tryGeographicLineOverlay referenceSystem kind first.Srid [ left ] [ right ]
        | GLineString left, GMultiLineString right ->
            tryGeographicLineOverlay referenceSystem kind first.Srid [ left ] right
        | GMultiLineString left, GLineString right ->
            if kind = OverlayKind.Difference then
                tryGeographicLineOverlay referenceSystem kind first.Srid left [ right ]
            else
                tryGeographicLineOverlay referenceSystem kind first.Srid [ right ] left
        | GMultiLineString left, GMultiLineString right ->
            tryGeographicLineOverlay referenceSystem kind first.Srid left right
        | (Area _ as area), Linework _ when kind = OverlayKind.Difference -> Some(normalize area)
        | Linework lines, (Area polygons as area)
        | (Area polygons as area), Linework lines ->
            tryGeographicLineAreaOverlay referenceSystem kind first.Srid lines polygons area
        | left, right ->
            match geographicPointMembers left, geographicPointMembers right with
            | Some points, Some other -> Some(overlayGeographicPoints kind left points right other)
            | Some points, None -> mixed left true points right
            | None, Some points -> mixed right false points left
            | _ -> None

    shape |> Option.map result

type private BufferStrategyCategory =
    | End
    | Join
    | Point

let private strategyCategory = function
    | BufferStrategy.EndRound _
    | BufferStrategy.EndFlat -> End
    | BufferStrategy.JoinRound _
    | BufferStrategy.JoinMiter _ -> Join
    | BufferStrategy.PointCircle _
    | BufferStrategy.PointSquare -> Point

let rec private geometryContents = function
    | GEmpty -> false, false, false
    | GPoint _
    | GMultiPoint _ -> true, false, false
    | GLineString _
    | GMultiLineString _ -> false, true, false
    | GPolygon _
    | GMultiPolygon _ -> false, false, true
    | GGeometryCollection geometries ->
        geometries
        |> List.map (fun geometry -> geometryContents geometry.Shape)
        |> List.fold (fun (hasPoint, hasLine, hasArea) (point, line, area) -> hasPoint || point, hasLine || line, hasArea || area) (false, false, false)

let private roundSegments = function
    | BufferStrategy.EndRound points
    | BufferStrategy.JoinRound points
    | BufferStrategy.PointCircle points -> Some(max 3 (int points))
    | BufferStrategy.EndFlat
    | BufferStrategy.JoinMiter _
    | BufferStrategy.PointSquare -> None

type private BufferConfiguration =
    { Point: BufferStrategy
      Join: BufferStrategy
      End: BufferStrategy
      HasPoint: bool
      HasLine: bool
      HasArea: bool }

let private configureBuffer geometry strategies =
    let categories = strategies |> List.map strategyCategory
    let hasPoint, hasLine, hasArea = geometryContents geometry.Shape
    let hasLinearOrArea = hasLine || hasArea

    let applicable =
        strategies
        |> List.forall (function
            | BufferStrategy.PointCircle _
            | BufferStrategy.PointSquare -> hasPoint
            | _ -> hasLinearOrArea)

    if not applicable || categories.Length <> (categories |> List.distinct |> List.length) then
        Error BufferError.InvalidStrategy
    else
        let tryFind category = strategies |> List.tryFind (strategyCategory >> (=) category)

        Ok
            { Point = tryFind Point |> Option.defaultValue (BufferStrategy.PointCircle defaultPointsPerCircle)
              Join = tryFind Join |> Option.defaultValue (BufferStrategy.JoinRound defaultPointsPerCircle)
              End = tryFind End |> Option.defaultValue (BufferStrategy.EndRound defaultPointsPerCircle)
              HasPoint = hasPoint
              HasLine = hasLine
              HasArea = hasArea }

let private tryNtsParameters configuration =
    let relevantRoundSegments =
        [ if configuration.HasPoint then roundSegments configuration.Point
          if configuration.HasLine || configuration.HasArea then roundSegments configuration.Join
          if configuration.HasLine then roundSegments configuration.End ]
        |> List.choose id

    let quadrants = relevantRoundSegments |> List.map (fun segments -> segments / 4, segments % 4)

    match
        configuration.HasPoint && (configuration.HasLine || configuration.HasArea),
        configuration.Join,
        quadrants |> List.map fst |> List.distinct,
        quadrants |> List.forall (snd >> (=) 0)
    with
    | true, _, _, _ -> None
    | _, BufferStrategy.JoinMiter _, _, _ -> None
    | _, _, _ :: _ :: _, _
    | _, _, _, false -> None
    | _, _, values, true ->
        let parameters = BufferParameters()
        parameters.QuadrantSegments <- values |> List.tryHead |> Option.defaultValue 8

        parameters.EndCapStyle <-
            match configuration.End, configuration.Point with
            | BufferStrategy.EndFlat, _ -> EndCapStyle.Flat
            | _, BufferStrategy.PointSquare -> EndCapStyle.Square
            | _ -> EndCapStyle.Round

        parameters.JoinStyle <- JoinStyle.Round

        Some parameters

let private closeRing points =
    match points with
    | [] -> []
    | first :: _ when List.last points = first -> points
    | first :: _ -> points @ [ first ]

let private polygon srid points =
    { Srid = srid
      Shape = GPolygon [ closeRing points ] }
    |> toNts

let private union geometries =
    match geometries with
    | [] -> None
    | first :: rest ->
        rest
        |> List.fold (fun current geometry -> OverlayNGRobust.Overlay(current, geometry, SpatialFunction.Union)) first
        |> Some

let private circle srid segments distance (x, y) =
    let segments = max 3 segments

    [ 0 .. segments - 1 ]
    |> List.map (fun index ->
        Limits.checkQueryCancellation index
        let angle = 2.0 * Math.PI * float index / float segments
        x + distance * Math.Cos angle, y + distance * Math.Sin angle)
    |> polygon srid

let private square srid distance (x, y) =
    [ x - distance, y - distance
      x + distance, y - distance
      x + distance, y + distance
      x - distance, y + distance ]
    |> polygon srid

type private JoinGeometry =
    { Vertex: float * float
      PreviousDirection: float * float
      NextDirection: float * float
      PreviousOffset: float * float
      NextOffset: float * float
      ArcStart: float * float
      ArcEnd: float * float
      StartAngle: float
      EndAngle: float }

let private tryJoinGeometry distance previous vertex next =
    let x, y = vertex
    let px, py = previous
    let nx, ny = next
    let previousDx, previousDy = x - px, y - py
    let nextDx, nextDy = nx - x, ny - y
    let previousLength = Math.Sqrt(previousDx * previousDx + previousDy * previousDy)
    let nextLength = Math.Sqrt(nextDx * nextDx + nextDy * nextDy)
    let previousAngle = Math.Atan2(previousDy, previousDx)
    let nextAngle = Math.Atan2(nextDy, nextDx)
    let cross = previousDx * nextDy - previousDy * nextDx

    if cross = 0.0 || previousLength = 0.0 || nextLength = 0.0 then
        None
    else
        let left dx dy length = x - distance * dy / length, y + distance * dx / length
        let right dx dy length = x + distance * dy / length, y - distance * dx / length

        let previousOffset, nextOffset, startAngle, endAngle, startPoint, endPoint =
            if cross < 0.0 then
                let previousOffset = left previousDx previousDy previousLength
                let nextOffset = left nextDx nextDy nextLength

                previousOffset,
                nextOffset,
                previousAngle + Math.PI / 2.0,
                nextAngle + Math.PI / 2.0,
                previousOffset,
                nextOffset
            else
                let previousOffset = right previousDx previousDy previousLength
                let nextOffset = right nextDx nextDy nextLength

                previousOffset,
                nextOffset,
                nextAngle - Math.PI / 2.0,
                previousAngle - Math.PI / 2.0,
                nextOffset,
                previousOffset

        let mutable endAngle = endAngle

        while endAngle > startAngle do
            endAngle <- endAngle - 2.0 * Math.PI

        Some
            { Vertex = vertex
              PreviousDirection = previousDx, previousDy
              NextDirection = nextDx, nextDy
              PreviousOffset = previousOffset
              NextOffset = nextOffset
              ArcStart = startPoint
              ArcEnd = endPoint
              StartAngle = startAngle
              EndAngle = endAngle }

let private roundJoin srid pointsPerCircle distance previous vertex next =
    tryJoinGeometry distance previous vertex next
    |> Option.map (fun join ->
        let x, y = join.Vertex
        let difference = join.StartAngle - join.EndAngle
        let segments = max 4 (int pointsPerCircle)
        let steps = max 1 (int (Math.Ceiling(float segments * difference / (2.0 * Math.PI))))

        [ 1 .. steps - 1 ]
        |> List.map (fun index ->
            Limits.checkQueryCancellation index
            let angle = join.StartAngle - difference * float index / float steps
            x + distance * Math.Cos angle, y + distance * Math.Sin angle)
        |> fun middle -> polygon srid (join.Vertex :: join.ArcStart :: middle @ [ join.ArcEnd ]))

let private miterJoin srid limit distance previous vertex next =
    let cross (ax, ay) (bx, by) = ax * by - ay * bx

    tryJoinGeometry distance previous vertex next
    |> Option.bind (fun join ->
        let px, py = join.PreviousOffset
        let nx, ny = join.NextOffset
        let denominator = cross join.PreviousDirection join.NextDirection

        if denominator = 0.0 then
            None
        else
            let t = cross (nx - px, ny - py) join.NextDirection / denominator
            let dx, dy = join.PreviousDirection
            let intersection = px + t * dx, py + t * dy
            let vx, vy = join.Vertex
            let ix, iy = intersection
            let miterDx, miterDy = ix - vx, iy - vy
            let miterDistance = Math.Sqrt(miterDx * miterDx + miterDy * miterDy)
            let maximumDistance = max 1.0 limit * distance

            let tip =
                if miterDistance > maximumDistance then
                    let scale = maximumDistance / miterDistance
                    vx + miterDx * scale, vy + miterDy * scale
                else
                    intersection

            Some(polygon srid [ join.Vertex; join.ArcStart; tip; join.ArcEnd ]))

let private roundEnd srid pointsPerCircle distance penultimate ultimate =
    let ux, uy = ultimate
    let px, py = penultimate
    let dx, dy = ux - px, uy - py
    let length = Math.Sqrt(dx * dx + dy * dy)
    let points = max 4 (int pointsPerCircle)
    let startAngle = Math.Atan2(py - uy, px - ux) - Math.PI / 2.0
    let step = 2.0 * Math.PI / float points
    let left = ux - distance * dy / length, uy + distance * dx / length
    let right = ux + distance * dy / length, uy - distance * dx / length

    let middle =
        [ 1 .. points / 2 - (if points % 2 = 0 then 1 else 0) ]
        |> List.map (fun index ->
            Limits.checkQueryCancellation index
            let angle = startAngle - float index * step
            ux + distance * Math.Cos angle, uy + distance * Math.Sin angle)

    polygon srid (ultimate :: left :: middle @ [ right ])

let private bevelCore distance geometry =
    let parameters = BufferParameters()
    parameters.EndCapStyle <- EndCapStyle.Flat
    parameters.JoinStyle <- JoinStyle.Bevel

    BufferOp.Buffer(geometry, distance, parameters)

let private nondegenerateLineBuffer configuration srid distance closed points =
    let source =
        { Srid = srid
          Shape = GLineString points }
        |> toNts

    let core = bevelCore distance source
    let distinctPoints = if closed then points |> List.take (points.Length - 1) else points

    let joins =
        let joinAt previous vertex next =
            match configuration.Join with
            | BufferStrategy.JoinRound resolution -> roundJoin srid resolution distance previous vertex next
            | BufferStrategy.JoinMiter limit -> miterJoin srid limit distance previous vertex next
            | _ -> None

        if closed then
            let values = List.toArray distinctPoints

            [ 0 .. values.Length - 1 ]
            |> List.choose (fun index ->
                joinAt
                    values.[(index + values.Length - 1) % values.Length]
                    values.[index]
                    values.[(index + 1) % values.Length])
        else
            points
            |> List.windowed 3
            |> List.choose (function
                | [ previous; vertex; next ] -> joinAt previous vertex next
                | _ -> None)

    let ends =
        match closed, configuration.End, points with
        | false, BufferStrategy.EndRound resolution, first :: second :: _ ->
            let penultimate, last = points.[points.Length - 2], List.last points
            [ roundEnd srid resolution distance second first
              roundEnd srid resolution distance penultimate last ]
        | _ -> []

    union (core :: joins @ ends) |> Option.defaultValue core

let private customLineBuffer configuration srid distance closed points =
    let prependDistinct points point =
        match points with
        | head :: _ when head = point -> points
        | _ -> point :: points

    let points =
        points
        |> List.fold prependDistinct []
        |> List.rev
        |> fun values -> if closed then closeRing values else values

    match points with
    | [ point ] -> circle srid (int defaultPointsPerCircle) distance point
    | _ -> nondegenerateLineBuffer configuration srid distance closed points

let rec private customBuffer configuration distance (geometry: Geometry) =
    let srid = geometry.Srid

    let rec bufferShape shape =
        match shape with
        | GEmpty -> Some(toNts { geometry with Shape = GEmpty })
        | GPoint(x, y) ->
            match configuration.Point with
            | BufferStrategy.PointCircle resolution -> Some(circle srid (int resolution) distance (x, y))
            | BufferStrategy.PointSquare -> Some(square srid distance (x, y))
            | _ -> None
        | GMultiPoint points ->
            points
            |> List.map (fun point ->
                match configuration.Point with
                | BufferStrategy.PointCircle resolution -> circle srid (int resolution) distance point
                | BufferStrategy.PointSquare -> square srid distance point
                | _ -> invalidOp "invalid point buffer strategy")
            |> union
        | GLineString points -> Some(customLineBuffer configuration srid distance false points)
        | GMultiLineString lines -> lines |> List.map (customLineBuffer configuration srid distance false) |> union
        | GPolygon rings ->
            let original = toNts { geometry with Shape = GPolygon rings }

            rings
            |> List.map (customLineBuffer configuration srid (abs distance) true)
            |> union
            |> Option.map (fun boundary ->
                let operation = if distance > 0.0 then SpatialFunction.Union else SpatialFunction.Difference
                OverlayNGRobust.Overlay(original, boundary, operation))
        | GMultiPolygon polygons ->
            polygons
            |> List.choose (fun rings -> bufferShape (GPolygon rings))
            |> union
        | GGeometryCollection geometries ->
            geometries
            |> List.choose (fun item -> customBuffer configuration distance item |> Result.toOption)
            |> union

    match bufferShape geometry.Shape with
    | Some result -> Ok result
    | None -> Error "the result cannot be represented as a MySQL geometry"

let internal buffer strategies distance (geometry: Geometry) =
    configureBuffer geometry strategies
    |> Result.bind (fun configuration ->
        attempt geometry.Srid (fun () ->
            match tryNtsParameters configuration with
            | Some parameters -> BufferOp.Buffer(toNts geometry, distance, parameters)
            | None ->
                match customBuffer configuration distance geometry with
                | Ok result -> result
                | Error detail -> invalidArg "geometry" detail)
        |> Result.mapError BufferError.OperationFailed)
