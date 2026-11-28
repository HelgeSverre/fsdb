// Adapted from Boost.Geometry's Sjöberg intersection formula (Boost 1.84).
// Copyright (c) 2023 Adam Wulkiewicz, Lodz, Poland.
// Copyright (c) 2016-2019 Oracle and/or its affiliates.
// Distributed under the Boost Software License, Version 1.0; see
// licenses/Boost-1.0.txt.
module internal Fsdb.GeographicIntersection

open System

let private equal a b =
    abs (a - b) <= (Math.BitIncrement(1.0) - 1.0) * max 1.0 (max (abs a) (abs b))

let private normalize angle =
    let value = angle % (2.0 * Math.PI)
    if value > Math.PI then value - 2.0 * Math.PI
    elif value <= -Math.PI then value + 2.0 * Math.PI
    else value

let private difference first second = normalize (second - first)
let private bounded value = max -1.0 (min 1.0 value)
let private halfPi = Math.PI / 2.0

// The initial spherical solution chooses the antipode nearest both segments.
let private sphericalGuess multiplyAdd (lat1, lon1) (latA2, lonA2) (lat2, lon2) (latB2, lonB2) =
    let sinLon1, cosLon1 = sin lon1, cos lon1
    let sinLon2, cosLon2 = sin lon2, cos lon2
    let sinLat1, cosLat1 = sin lat1, cos lat1
    let sinLat2, cosLat2 = sin lat2, cos lat2
    let delta1, delta2 = lonA2 - lon1, lonB2 - lon2
    let divisor1 = multiplyAdd (cosLat1, tan latA2, -(sinLat1 * cos delta1))
    let divisor2 = multiplyAdd (cosLat2, tan latB2, -(sinLat2 * cos delta2))
    let vertical1 = equal (sin delta1) 0.0 || equal divisor1 0.0
    let vertical2 = equal (sin delta2) 0.0 || equal divisor2 0.0
    if vertical1 && vertical2 then None
    else
        let alpha1 = if vertical1 then 0.0 else sin delta1 / divisor1
        let alpha2 = if vertical2 then 0.0 else sin delta2 / divisor2
        let longitude =
            if vertical1 then lon1
            elif vertical2 then lon2
            else
                let t1, t2 = alpha1 * cosLat1, alpha2 * cosLat2
                let product = t1 * t2
                let y = multiplyAdd (product,
                            multiplyAdd (sinLat1 / cosLat1, cosLon1, -(sinLat2 / cosLat2 * cosLon2)),
                            multiplyAdd (t1, sinLon2, -(t2 * sinLon1)))
                let x = multiplyAdd (-product,
                            multiplyAdd (sinLat1 / cosLat1, sinLon1, -(sinLat2 / cosLat2 * sinLon2)),
                            multiplyAdd (t1, cosLon2, -(t2 * cosLon1)))
                atan2 y x
        let opposite = if longitude > 0.0 then longitude - Math.PI else longitude + Math.PI
        let nearest longitude =
            max (min (abs (difference lon1 longitude)) (abs (difference lonA2 longitude)))
                (min (abs (difference lon2 longitude)) (abs (difference lonB2 longitude)))
        let longitude = if nearest opposite < nearest longitude then opposite else longitude
        let alpha, sinLat, cosLat, sinLon, cosLon =
            if abs alpha1 >= abs alpha2 then alpha1, sinLat1, cosLat1, sinLon1, cosLon1
            else alpha2, sinLat2, cosLat2, sinLon2, cosLon2
        let sinDelta = multiplyAdd (sin longitude, cosLon, -(cos longitude * sinLon))
        let cosDelta = multiplyAdd (cos longitude, cosLon, sin longitude * sinLon)
        let tangent = multiplyAdd (alpha * sinLat, cosDelta, sinDelta) / (alpha * cosLat)
        Some(longitude, tangent)

type private Geodesic =
    { Longitude: float
      OneMinusFlattening: float
      EccentricitySquared: float
      Clairaut: float
      ClairautSquared: float
      MaxSinBeta: float
      SinBetaStart: float
      Direction: float
      VertexTangent: float
      StartArc: float }
    member g.IsMeridian = equal g.Clairaut 0.0
    member g.IsEquator = equal g.MaxSinBeta 0.0
    member g.Latitude tangent = atan (tangent / g.OneMinusFlattening)
    member g.BetaInRange sinBeta =
        abs (sinBeta / g.MaxSinBeta) <= 1.0 || equal (abs sinBeta) g.MaxSinBeta
    member g.LongitudeCorrection sinBeta =
        let arc = asin (bounded (sinBeta / g.MaxSinBeta))
        let startArc = asin (g.SinBetaStart / g.MaxSinBeta)
        -g.Clairaut * g.EccentricitySquared * ((arc - startArc) / 2.0)
    member g.VertexCorrection =
        if g.IsMeridian then 0.0
        else g.LongitudeCorrection (float (Math.Sign g.VertexTangent) * g.MaxSinBeta)
    member g.LongitudeOffsets sinBeta checkRange =
        if g.IsMeridian then Some(0.0, g.Direction * Math.PI)
        elif checkRange && not (g.BetaInRange sinBeta) then None
        else
            let correction = g.LongitudeCorrection sinBeta
            Some(g.Direction * (-g.StartArc + correction),
                 g.Direction * (Math.PI - g.StartArc + g.VertexCorrection + (g.VertexCorrection - correction)))
    member g.LongitudeDifference sinBeta tangent =
        if g.IsMeridian then Some 0.0
        else
            g.LongitudeOffsets sinBeta true
            |> Option.map (fun (before, _) -> g.Direction * asin (bounded (tangent / g.VertexTangent)) + before)

let private geodesic flattening (latitude, longitude) azimuth =
    let oneMinusFlattening = 1.0 - flattening
    // Retain the reduced-latitude tangent for the starting arc near a geodesic vertex.
    let tanBeta = oneMinusFlattening * tan latitude
    let beta = atan tanBeta
    let sign =
        if (halfPi < azimuth && azimuth < Math.PI) || (-halfPi < azimuth && azimuth < 0.0) then -1.0
        else 1.0
    let clairaut = sign * cos beta * sin azimuth
    let squared = clairaut * clairaut
    let maximum = sqrt (1.0 - squared)
    { Longitude = longitude
      OneMinusFlattening = oneMinusFlattening
      EccentricitySquared = flattening * (2.0 - flattening)
      Clairaut = clairaut
      ClairautSquared = squared
      MaxSinBeta = maximum
      SinBetaStart = sin beta
      Direction = if azimuth >= 0.0 then 1.0 else -1.0
      VertexTangent = if equal clairaut 0.0 then 0.0 else maximum / clairaut
      StartArc = if equal maximum 0.0 then 0.0 else asin (tanBeta * clairaut / maximum) }

let private longitudeInRange (g: Geodesic) (_, start) (_, endpoint) longitude =
    if g.IsMeridian then true
    else
        let distance, length = difference start longitude, difference start endpoint
        let distance, length = if length < 0.0 then -distance, -length else distance, length
        (0.0 <= distance && distance <= length) || equal distance 0.0 || equal distance length

let private newton (first: Geodesic) (second: Geodesic) initialTangent sphericalLongitude =
    let longitudeDelta = difference second.Longitude first.Longitude
    let rec iterate iteration beta tangent lastDelta =
        let cosBeta, sinBeta = cos beta, sin beta
        let squared = cosBeta * cosBeta
        let g = 1.0 - first.EccentricitySquared * squared
        let derivative (geodesic: Geodesic) =
            if geodesic.IsMeridian then Some(0.0, 0.0)
            elif equal cosBeta 0.0 || equal (squared - geodesic.ClairautSquared) 0.0 then None
            else
                geodesic.LongitudeDifference sinBeta tangent
                |> Option.map (fun longitude ->
                    longitude, geodesic.Clairaut / cosBeta * sqrt (g / (squared - geodesic.ClairautSquared)))
        match derivative first, derivative second with
        | Some(lon1, f1), Some(lon2, f2) when not (equal (f1 - f2) 0.0) ->
            let delta = (longitudeDelta + lon1 - lon2) / (f1 - f2)
            if iteration > 0 && abs delta > lastDelta then None
            elif equal delta 0.0 || iteration = 9 then
                // The final longitude uses the last evaluated offsets, as in Boost.
                let finalTangent = if equal delta 0.0 then tangent else tan (beta - delta)
                Some(first.Latitude finalTangent,
                     if first.IsMeridian then second.Longitude + lon2 else first.Longitude + lon1)
            else iterate (iteration + 1) (beta - delta) (tan (beta - delta)) (abs delta)
        | _ -> None
    if (first.IsMeridian && abs (first.Longitude - sphericalLongitude) > halfPi)
       || (second.IsMeridian && abs (second.Longitude - sphericalLongitude) > halfPi) then None
    else iterate 0 (atan initialTangent) initialTangent 0.0

type private ConvergenceStep =
    { FirstLongitude: float
      SecondLongitude: float
      NearTangent: float
      FarTangent: float }
    member step.Residual = abs (difference step.FirstLongitude step.SecondLongitude)

let private tangentCandidates tangent k (first: Geodesic) (second: Geodesic) =
    let sine = sin k
    let lesser, greater =
        if first.IsMeridian then
            let value = sine * second.VertexTangent
            value, -value
        elif second.IsMeridian then
            let value = -sine * first.VertexTangent
            value, -value
        else
            let a = first.VertexTangent * first.VertexTangent + second.VertexTangent * second.VertexTangent
            let b = 2.0 * first.VertexTangent * second.VertexTangent * sqrt (1.0 - sine * sine)
            let numerator = sine * first.VertexTangent * second.VertexTangent
            let divide denominator = if equal denominator 0.0 then 0.0 else numerator / denominator
            let firstValue, secondValue = divide (sqrt (a + b)), divide (sqrt (a - b))
            let maximum = max (abs firstValue) (abs secondValue)
            let candidates = [ firstValue; secondValue; -firstValue; -secondValue ]
            let lesser =
                if -maximum < tangent then candidates |> List.fold (fun best value -> if value < tangent && value > best then value else best) -maximum
                else -maximum
            let greater =
                if maximum > tangent then candidates |> List.fold (fun best value -> if value > tangent && value < best then value else best) maximum
                else maximum
            lesser, greater
    if abs (tangent - greater) < abs (tangent - lesser) then greater, lesser else lesser, greater

let private converge (first: Geodesic) (second: Geodesic) initialTangent sphericalLongitude =
    let longitudeDelta = difference second.Longitude first.Longitude
    let step tangent checkRange =
        let sinBeta = sin (atan tangent)
        let longitude (g: Geodesic) =
            g.LongitudeOffsets sinBeta checkRange
            |> Option.map (fun (before, behind) ->
                let arc = if g.IsMeridian then 0.0 else g.Direction * asin (bounded (tangent / g.VertexTangent))
                let beforeLongitude, behindLongitude = g.Longitude + arc + before, g.Longitude - arc + behind
                if abs (difference beforeLongitude sphericalLongitude) <= abs (difference behindLongitude sphericalLongitude) then
                    beforeLongitude, before
                else behindLongitude, behind)
        match longitude first, longitude second with
        | Some(lon1, k1), Some(lon2, k2) ->
            let near, far = tangentCandidates tangent (longitudeDelta + k1 - k2) first second
            Some { FirstLongitude = lon1; SecondLongitude = lon2; NearTangent = near; FarTangent = far }
        | _ -> None
    let result tangent (state: ConvergenceStep) =
        Some(first.Latitude tangent, normalize (if first.IsMeridian then state.SecondLongitude else state.FirstLongitude))
    let rec iterate iteration tangent direction (state: ConvergenceStep) =
        if iteration = 20 then result tangent state
        else
            let near, far, direction =
                if direction < 0 then min state.NearTangent state.FarTangent, max state.NearTangent state.FarTangent, direction
                elif direction > 0 then max state.NearTangent state.FarTangent, min state.NearTangent state.FarTangent, direction
                else state.NearTangent, state.FarTangent, (if state.NearTangent < state.FarTangent then -1 else 1)
            if equal (atan near - atan tangent) 0.0 then result tangent state
            else
                match step near true with
                | Some next when state.Residual > next.Residual -> iterate (iteration + 1) near direction next
                | attempted when near = far ->
                    match attempted with Some _ -> result tangent state | None -> None
                | _ ->
                    if equal (atan far - atan tangent) 0.0 then result tangent state
                    else
                        match step far true with
                        | None -> None
                        | Some next when state.Residual > next.Residual -> iterate (iteration + 1) far -direction next
                        | Some _ -> result tangent state
    step initialTangent false |> Option.bind (iterate 2 initialTangent 0)

/// Inputs and result use latitude/longitude radians on the normalized spheroid.
let intersect multiplyAdd flattening firstStart firstEnd firstAzimuth secondStart secondEnd secondAzimuth =
    let first = geodesic flattening firstStart firstAzimuth
    let second = geodesic flattening secondStart secondAzimuth
    if first.IsEquator && second.IsEquator then None
    elif first.IsEquator || second.IsEquator then
        let g = if first.IsEquator then second else first
        Some(0.0, g.Longitude - g.StartArc + g.LongitudeCorrection 0.0)
    elif first.IsMeridian && second.IsMeridian then
        let delta = difference second.Longitude first.Longitude
        if equal delta 0.0 || equal (delta + (if delta < 0.0 then Math.PI else -Math.PI)) 0.0 then None
        else
            let firstLatitude, _ = firstStart
            let endLatitude, _ = firstEnd
            let pole = if equal (abs firstLatitude) (abs endLatitude) then halfPi
                       elif (if abs firstLatitude > abs endLatitude then firstLatitude else endLatitude) >= 0.0 then halfPi
                       else -halfPi
            Some(pole, 0.0)
    else
        // Boost still attempts convergence from zero when the spherical
        // starting-point calculation cannot distinguish the two geodesics.
        let longitude, tangentLatitude =
            sphericalGuess multiplyAdd firstStart firstEnd secondStart secondEnd
            |> Option.defaultValue (0.0, 0.0)
        if equal flattening 0.0 then Some(atan tangentLatitude, longitude)
        else
            let tangent = (1.0 - flattening) * tangentLatitude
            match newton first second tangent longitude with
            | Some(_, candidateLongitude) as candidate
                when longitudeInRange first firstStart firstEnd candidateLongitude
                     && longitudeInRange second secondStart secondEnd candidateLongitude -> candidate
            | _ -> converge first second tangent longitude
