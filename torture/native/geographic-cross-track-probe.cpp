// Compile with Boost 1.84 headers to trace the point-to-segment calculation
// behind MySQL 8.4.11's near-hole distance oracle.
#define BOOST_GEOMETRY_DEBUG_GEOGRAPHIC_CROSS_TRACK
#include <boost/geometry/formulas/andoyer_inverse.hpp>
#include <boost/geometry/geometries/point.hpp>
#include <boost/geometry/srs/spheroid.hpp>
#include <boost/geometry/strategies/geographic/distance_cross_track.hpp>
#include <boost/geometry/strategies/geographic/distance.hpp>
#include <boost/geometry/util/math.hpp>

#include <iomanip>
#include <iostream>
#include <string>

namespace bg = boost::geometry;

int main(int argc, char** argv) {
    if (argc != 1 && argc != 7) {
        std::cerr << "usage: geographic-cross-track-probe [query-lat query-lon start-lat start-lon end-lat end-lon]\n";
        return 2;
    }
    using Point = bg::model::point<double, 2, bg::cs::geographic<bg::degree>>;
    const bg::srs::spheroid<double> wgs84(6378137.0, 6356752.314245179);
    const bg::strategy::distance::geographic_cross_track<bg::strategy::andoyer> strategy(wgs84);

    // Boost takes longitude, latitude; MySQL's SRID 4326 WKT takes latitude, longitude.
    const Point query(argc == 7 ? std::stod(argv[2]) : 1.00001,
                      argc == 7 ? std::stod(argv[1]) : 3.0);
    const Point start(argc == 7 ? std::stod(argv[4]) : 1.0,
                      argc == 7 ? std::stod(argv[3]) : 3.0);
    const Point end(argc == 7 ? std::stod(argv[6]) : 3.0,
                    argc == 7 ? std::stod(argv[5]) : 3.0);
    std::cout << std::setprecision(17) << "distance=" << strategy.apply(query, start, end) << '\n';
    if (argc == 7) {
        const bg::strategy::distance::geographic<bg::strategy::andoyer> pointStrategy(wgs84);
        std::cout << "query-to-start=" << pointStrategy.apply(query, start) << '\n';
        std::cout << "query-to-end=" << pointStrategy.apply(query, end) << '\n';
        using RadianPoint = bg::model::point<double, 2, bg::cs::geographic<bg::radian>>;
        const double radiansPerDegree = bg::math::d2r<double>();
        const RadianPoint queryRadians(std::stod(argv[2]) * radiansPerDegree,
                                       std::stod(argv[1]) * radiansPerDegree);
        const RadianPoint startRadians(std::stod(argv[4]) * radiansPerDegree,
                                       std::stod(argv[3]) * radiansPerDegree);
        const RadianPoint endRadians(std::stod(argv[6]) * radiansPerDegree,
                                     std::stod(argv[5]) * radiansPerDegree);
        std::cout << "radian-query-to-start=" << pointStrategy.apply(queryRadians, startRadians) << '\n';
        std::cout << "radian-query-to-end=" << pointStrategy.apply(queryRadians, endRadians) << '\n';
    }

    if (argc != 1) return 0;

    // The strategy's first direct point, captured in radians from the pinned
    // Boost trace. A one-ULP latitude change reverses the tiny reduced length.
    using Inverse = bg::formula::andoyer_inverse<double, true, true, true, true, true>;
    const double queryLongitude = 1.00001 * bg::math::d2r<double>();
    const double queryLatitude = 3.0 * bg::math::d2r<double>();
    const double firstLongitude = 0.017453467809108461;
    for (double firstLatitude : {0.052359877720815996, 0.052359877720815989}) {
        const auto inverse = Inverse::apply(firstLongitude, firstLatitude,
                                            queryLongitude, queryLatitude, wgs84);
        std::cout << "inverse lat=" << firstLatitude
                  << " azimuth=" << inverse.azimuth
                  << " reverse=" << inverse.reverse_azimuth
                  << " reduced=" << inverse.reduced_length << '\n';
    }
}
