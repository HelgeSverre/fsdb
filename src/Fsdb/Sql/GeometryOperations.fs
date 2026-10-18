module Fsdb.GeometryOperations

open System
open System.Buffers.Binary
open NetTopologySuite.IO
open NetTopologySuite.Operation.Buffer
open NetTopologySuite.Operation.Overlay
open NetTopologySuite.Operation.OverlayNG
open NetTopologySuite.Operation.RelateNG
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
let private mysqlRadians degrees = degrees * 0.017453292519943278

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

/// MySQL delegates geographic point distance to Boost.Geometry's first-order
/// Andoyer strategy; more exact geodesic formulae produce observably different
/// results and therefore are not interchangeable here.
let internal geographicPointDistance referenceSystem first second =
    match referenceSystem.SemiMajorAxis, referenceSystem.InverseFlattening with
    | Some semiMajorAxis, Some inverseFlattening ->
        let flattening = 1.0 / inverseFlattening
        let firstLatitude, firstLongitude = first
        let secondLatitude, secondLongitude = second
        let firstLatitude = radians firstLatitude
        let secondLatitude = radians secondLatitude
        let longitudeDelta = radians (secondLongitude - firstLongitude)
        let sinFirst, cosFirst = sin firstLatitude, cos firstLatitude
        let sinSecond, cosSecond = sin secondLatitude, cos secondLatitude

        let cosineDistance =
            sinFirst * sinSecond + cosFirst * cosSecond * cos longitudeDelta
            |> max -1.0
            |> min 1.0

        let tinyMeridionalDistance =
            if cosineDistance = 1.0 && firstLongitude = secondLongitude && firstLatitude <> secondLatitude then
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

/// Boost's default geographic line interpolation uses Andoyer for the initial
/// azimuth and first-order Thomas for the direct point. Keep this paired with
/// geographicPointDistance, which supplies the distance along each segment.
let private geographicSegmentAzimuthsFromRadians flattening latitude1 latitude2 longitudeDelta =
    let sin1, cos1 = sin latitude1, cos latitude1
    let sin2, cos2 = sin latitude2, cos latitude2
    let sinLongitude, cosLongitude = sin longitudeDelta, cos longitudeDelta
    let cosineDistance = max -1.0 (min 1.0 (sin1 * sin2 + cos1 * cos2 * cosLongitude))
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
                let angle = atan2 sinLongitude (cos1 * sin2 / cos2 - sin1 * cosLongitude)
                angle, flattening / 2.0 * cos1 * cos1 * sin (2.0 * angle)

        let secondAngle, secondAdjustment =
            if abs cos1 < 1e-15 then
                (if sin1 < 0.0 then Math.PI else 0.0), 0.0
            else
                let angle = atan2 sinLongitude (cos2 * sin1 / cos1 - sin2 * cosLongitude)
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

let internal geographicPointAlong referenceSystem first second distance =
    match referenceSystem.SemiMajorAxis, referenceSystem.InverseFlattening with
    | Some semiMajorAxis, Some inverseFlattening ->
        let flattening = 1.0 / inverseFlattening
        let firstLatitude, firstLongitude = first
        let azimuth = geographicSegmentAzimuth flattening first second
        let latitude1, azimuth12, flipped =
            let latitude = radians firstLatitude
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
        let longitude = radians firstLongitude + longitudeDelta
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
        latitude * 180.0 / Math.PI, longitude * 180.0 / Math.PI
    | _ -> invalidArg (nameof referenceSystem) "a geographic reference system requires an ellipsoid"

let private minimumOnSegment iterations length distanceAt =
    let ratio = (sqrt 5.0 - 1.0) / 2.0
    let mutable lower, upper = 0.0, length
    let mutable left = upper - ratio * (upper - lower)
    let mutable right = lower + ratio * (upper - lower)
    let mutable leftDistance = distanceAt left
    let mutable rightDistance = distanceAt right

    for _ in 1 .. iterations do
        if leftDistance <= rightDistance then
            upper <- right
            right <- left
            rightDistance <- leftDistance
            left <- upper - ratio * (upper - lower)
            leftDistance <- distanceAt left
        else
            lower <- left
            left <- right
            leftDistance <- rightDistance
            right <- lower + ratio * (upper - lower)
            rightDistance <- distanceAt right

    min leftDistance rightDistance

let private geographicPointsAwayFrom flattening origin toward query =
    let segmentAzimuth = geographicSegmentAzimuth flattening origin toward
    let queryAzimuth = geographicSegmentAzimuth flattening origin query
    cos (queryAzimuth - segmentAzimuth) < 0.0

let private geographicPointOnSegment referenceSystem flattening start endpoint position =
    let point = geographicPointAlong referenceSystem start endpoint position
    if geographicPointsAwayFrom flattening start endpoint point then start
    elif geographicPointsAwayFrom flattening endpoint start point then endpoint
    else point

let private geographicPointSegmentDistance referenceSystem point start endpoint =
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
            let pointLatitude, pointLongitude = point
            let startLatitude, startLongitude = start
            let endLatitude, endLongitude = endpoint
            if startLatitude = 0.0 && endLatitude = 0.0 then
                pointDistance point (0.0, pointLongitude)
            elif startLongitude = endLongitude then
                pointDistance point (pointLatitude, startLongitude)
            else
                let distanceAt position =
                    geographicPointOnSegment referenceSystem flattening start endpoint position
                    |> pointDistance point

                // A half-earth segment shrinks below 2 mm after 48 golden-section steps.
                min endpointDistance (minimumOnSegment 48 length distanceAt)

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

let private geographicUnitVector (latitude, longitude) =
    let latitude, longitude = radians latitude, radians longitude
    cos latitude * cos longitude, cos latitude * sin longitude, sin latitude

let private dot3 (ax, ay, az) (bx, by, bz) = ax * bx + ay * by + az * bz

let private cross3 (ax, ay, az) (bx, by, bz) =
    ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx

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
    let ring = if geographicRingAreaSign referenceSystem ring < 0.0 then List.rev ring else ring
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

let private geographicArcIntersection (firstStart: float * float) firstEnd secondStart secondEnd =
    let magnitude (x, y, z) = sqrt (x * x + y * y + z * z)
    let angle first second = atan2 (magnitude (cross3 first second)) (dot3 first second)
    let firstStart, firstEnd = geographicUnitVector firstStart, geographicUnitVector firstEnd
    let secondStart, secondEnd = geographicUnitVector secondStart, geographicUnitVector secondEnd
    let intersection = cross3 (cross3 firstStart firstEnd) (cross3 secondStart secondEnd)
    let size = magnitude intersection

    if size < 1e-15 then None
    else
        let x, y, z = intersection
        let candidate = x / size, y / size, z / size
        let onArc start endpoint point =
            abs (angle start point + angle point endpoint - angle start endpoint) < 1e-9
        let crosses point = onArc firstStart firstEnd point && onArc secondStart secondEnd point
        if crosses candidate then Some candidate
        else
            let opposite = -x / size, -y / size, -z / size
            if crosses opposite then Some opposite else None

let private geographicArcsIntersect firstStart firstEnd secondStart secondEnd =
    geographicArcIntersection firstStart firstEnd secondStart secondEnd |> Option.isSome

let private geographicLineSegments lines = lines |> List.collect List.pairwise

let private geographicPointOnLineSegment referenceSystem point (start, endpoint) =
    geographicPointLinePosition referenceSystem point [ [ start; endpoint ] ]
    <> GeographicPointLinePosition.Outside

let private geographicSegmentsProperlyCross referenceSystem (firstStart, firstEnd) (secondStart, secondEnd) =
    let firstArcs = sampleGeographicSegment referenceSystem firstStart firstEnd |> Array.pairwise
    let secondArcs = sampleGeographicSegment referenceSystem secondStart secondEnd |> Array.pairwise
    let nearEndpoint intersection endpoint =
        let x, y, z = intersection
        let otherX, otherY, otherZ = geographicUnitVector endpoint
        (x - otherX) ** 2.0 + (y - otherY) ** 2.0 + (z - otherZ) ** 2.0 < 1e-18
    firstArcs
    |> Array.exists (fun (firstArcStart, firstArcEnd) ->
        secondArcs
        |> Array.exists (fun (secondArcStart, secondArcEnd) ->
            geographicArcIntersection firstArcStart firstArcEnd secondArcStart secondArcEnd
            |> Option.exists (fun intersection ->
                not (nearEndpoint intersection firstStart || nearEndpoint intersection firstEnd
                     || nearEndpoint intersection secondStart || nearEndpoint intersection secondEnd))))

let private geographicSimpleRingIsValid referenceSystem ring =
    // A zero-length edge between equal neighbors is allowed; remove it before
    // deciding which remaining edges are adjacent.
    let ring =
        ring
        |> List.fold (fun points point ->
            match points with
            | previous :: _ when previous = point -> points
            | _ -> point :: points) []
        |> List.rev
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
            if adjacent then
                geographicSegmentsProperlyCross referenceSystem first second
                || pointPosition a second = GeographicPointLinePosition.Interior
                || pointPosition b second = GeographicPointLinePosition.Interior
                || pointPosition c first = GeographicPointLinePosition.Interior
                || pointPosition d first = GeographicPointLinePosition.Interior
            else
                geographicSegmentsProperlyCross referenceSystem first second
                || pointPosition a second <> GeographicPointLinePosition.Outside
                || pointPosition b second <> GeographicPointLinePosition.Outside
                || pointPosition c first <> GeographicPointLinePosition.Outside
                || pointPosition d first <> GeographicPointLinePosition.Outside
        seq {
            for first in 0 .. last do
                for second in first + 1 .. last do
                    yield first, second
        }
        |> Seq.exists (fun (first, second) -> invalidPair first second)
        |> not

/// None preserves the explicit unsupported boundary for geographic polygons
/// with holes and multipolygons until their geodesic containment is validated.
let rec internal geographicValidity referenceSystem (geometry: Geometry) : bool option =
    match geometry.Shape with
    | GPolygon [ ring ] -> Some(geographicSimpleRingIsValid referenceSystem ring)
    | GPolygon _
    | GMultiPolygon _ -> None
    | GGeometryCollection geometries ->
        let results = geometries |> List.map (geographicValidity referenceSystem)
        if results |> List.exists Option.isNone then None
        else Some(results |> List.forall ((=) (Some true)))
    | _ -> Some(geometryIsValidPlanar geometry)

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
    let sharedEndpoints (firstStart, firstEnd) (secondStart, secondEnd) =
        [ firstStart; firstEnd; secondStart; secondEnd ]
        |> List.distinct
        |> List.filter (fun point ->
            geographicPointOnLineSegment referenceSystem point (firstStart, firstEnd)
            && geographicPointOnLineSegment referenceSystem point (secondStart, secondEnd))
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
    let sample (start, endpoint) =
        start, endpoint, sampleGeographicSegment referenceSystem start endpoint |> Array.pairwise
    let firstSegments = firstLines |> List.collect List.pairwise |> List.map sample
    let secondSegments = secondLines |> List.collect List.pairwise |> List.map sample

    let segmentDistance (firstStart, firstEnd, firstArcs) (secondStart, secondEnd, secondArcs) =
        let nearestEndpoint =
            [ geographicPointSegmentDistance referenceSystem firstStart secondStart secondEnd
              geographicPointSegmentDistance referenceSystem firstEnd secondStart secondEnd
              geographicPointSegmentDistance referenceSystem secondStart firstStart firstEnd
              geographicPointSegmentDistance referenceSystem secondEnd firstStart firstEnd ]
            |> List.min

        if nearestEndpoint = 0.0 then 0.0
        else
            let intersects =
                firstArcs
                |> Array.exists (fun (firstArcStart, firstArcEnd) ->
                    secondArcs
                    |> Array.exists (fun (secondArcStart, secondArcEnd) ->
                        geographicArcsIntersect firstArcStart firstArcEnd secondArcStart secondArcEnd))
            if intersects then 0.0 else nearestEndpoint

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
    { Crosses: bool
      Touches: bool
      LineWithinPolygon: bool }

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
    let hasInterior =
        positions
        |> List.exists (function
            | GeographicPointPolygonPosition.Interior -> true
            | _ -> false)
    let hasOutside =
        positions
        |> List.exists (function GeographicPointPolygonPosition.Outside _ -> true | _ -> false)
    let intersects = geographicLinePolygonDistance referenceSystem lines polygons = Some 0.0
    if boundaryOnly then
        { Crosses = false
          Touches = true
          LineWithinPolygon = false }
    else
        { Crosses = intersects && hasInterior && hasOutside
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
