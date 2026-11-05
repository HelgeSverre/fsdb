// Compile with Boost 1.84 headers to trace the point-to-segment calculation
// behind MySQL 8.4.11's near-hole distance oracle.
#define BOOST_GEOMETRY_DEBUG_GEOGRAPHIC_CROSS_TRACK
#include <boost/geometry/geometries/point.hpp>
#include <boost/geometry/srs/spheroid.hpp>
#include <boost/geometry/strategies/geographic/distance_cross_track.hpp>

#include <iomanip>
#include <iostream>

namespace bg = boost::geometry;

int main() {
    using Point = bg::model::point<double, 2, bg::cs::geographic<bg::degree>>;
    const bg::srs::spheroid<double> wgs84(6378137.0, 6356752.314245179);
    const bg::strategy::distance::geographic_cross_track<bg::strategy::andoyer> strategy(wgs84);

    // Boost takes longitude, latitude; MySQL's SRID 4326 WKT takes latitude, longitude.
    const Point query(1.00001, 3.0);
    const Point start(1.0, 3.0);
    const Point end(3.0, 3.0);
    std::cout << std::setprecision(17) << "distance=" << strategy.apply(query, start, end) << '\n';
}
