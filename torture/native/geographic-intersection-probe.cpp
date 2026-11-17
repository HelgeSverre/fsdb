// Compile with clang++ -std=c++17 -O3 -I/path/to/boost_1_84_0.
#include <boost/geometry/algorithms/intersection.hpp>
#include <boost/geometry/algorithms/intersects.hpp>
#include <boost/geometry/formulas/sjoberg_intersection.hpp>
#include <boost/geometry/geometries/linestring.hpp>
#include <boost/geometry/geometries/point.hpp>
#include <boost/geometry/strategies/geographic/intersection.hpp>
#include <boost/geometry/strategies/geographic/line_interpolate.hpp>
#include <algorithm>
#include <cmath>
#include <iomanip>
#include <iostream>
#include <vector>

namespace bg = boost::geometry;
using Point = bg::model::point<double, 2, bg::cs::geographic<bg::radian>>;
using Line = bg::model::linestring<Point>;
using Inverse = bg::strategy::andoyer::inverse<double, true, true, false, false, false>;
using Intersection = bg::formula::sjoberg_intersection<double, bg::strategy::andoyer::inverse, 1>;

bool sampled_crosses(double unit, int steps) {
    using SphericalPoint = bg::model::point<double, 2, bg::cs::spherical_equatorial<bg::radian>>;
    using SphericalLine = bg::model::linestring<SphericalPoint>;
    bg::srs::spheroid<double> earth(6378137.0, 6356752.3142451793);
    bg::strategy::line_interpolate::geographic<> interpolate(earth);
    auto sample = [&](double lat1, double lon1, double lat2, double lon2) {
        Point first(lon1 * unit, lat1 * unit), second(lon2 * unit, lat2 * unit);
        double distance = Inverse::apply(lon1 * unit, lat1 * unit, lon2 * unit, lat2 * unit, earth).distance;
        SphericalLine line;
        for (int index = 0; index <= steps; ++index) {
            Point point;
            if (index == 0) point = first;
            else if (index == steps) point = second;
            else interpolate.apply(first, second, double(index) / steps, point, distance);
            line.emplace_back(bg::get<0>(point), bg::get<1>(point));
        }
        return line;
    };
    auto first = sample(13.81883866, 96.65582728, -16.27065705, -81.50116582);
    auto second = sample(-5.47457294, 82.28363800, 57.65336721, 169.58817054);
    bool intersects = bg::intersects(first, second);
    std::cout << "unit=" << unit << " sampled_steps=" << steps << " intersects=" << intersects << '\n';
    return intersects;
}

bool probe(double unit, bool reverse_first, bool reverse_second, bool swap_operands) {
    auto point = [unit](double latitude, double longitude) {
        return Point(longitude * unit, latitude * unit);
    };
    Line first{point(13.81883866, 96.65582728), point(-16.27065705, -81.50116582)};
    Line second{point(-5.47457294, 82.28363800), point(57.65336721, 169.58817054)};
    if (reverse_first) std::reverse(first.begin(), first.end());
    if (reverse_second) std::reverse(second.begin(), second.end());
    if (swap_operands) std::swap(first, second);
    bg::srs::spheroid<double> earth(6378137.0, 6356752.3142451793);
    std::vector<Point> points;
    bg::intersection(first, second, points, bg::strategy::intersection::geographic_segments<>(earth));

    // The public strategy orders both segments by latitude before solving.
    if (bg::get<1>(first[0]) > bg::get<1>(first[1])) std::reverse(first.begin(), first.end());
    if (bg::get<1>(second[0]) > bg::get<1>(second[1])) std::reverse(second.begin(), second.end());
    auto normalized = bg::formula::unit_spheroid<bg::srs::spheroid<double>>(earth);
    auto inverse = [&normalized](Point const& a, Point const& b) {
        return Inverse::apply(bg::get<0>(a), bg::get<1>(a), bg::get<0>(b), bg::get<1>(b), normalized);
    };
    auto first_inverse = inverse(first[0], first[1]);
    auto second_inverse = inverse(second[0], second[1]);
    auto first_to_second_start = inverse(first[0], second[0]);
    auto first_to_second_end = inverse(first[0], second[1]);
    auto second_to_first_start = inverse(second[0], first[0]);
    auto second_to_first_end = inverse(second[0], first[1]);
    double longitude = 0, latitude = 0;
    bool solved = Intersection::apply(
        bg::get<0>(first[0]), bg::get<1>(first[0]), bg::get<0>(first[1]), bg::get<1>(first[1]), first_inverse.azimuth,
        bg::get<0>(second[0]), bg::get<1>(second[0]), bg::get<0>(second[1]), bg::get<1>(second[1]), second_inverse.azimuth,
        longitude, latitude, normalized);
    std::cout << "unit=" << unit << " reverse=" << reverse_first << reverse_second
              << " swap=" << swap_operands << " intersections=" << points.size()
              << " solved=" << solved << " latitude=" << latitude / unit
              << " longitude=" << longitude / unit;
    Point candidate(longitude, latitude);
    auto ratio = [&inverse, &candidate](Line const& line, auto const& segment_inverse) {
        auto to_candidate = inverse(line[0], candidate);
        double sign = std::cos(to_candidate.azimuth - segment_inverse.azimuth) >= 0 ? 1 : -1;
        return sign * to_candidate.distance / segment_inverse.distance;
    };
    double first_ratio = ratio(first, first_inverse);
    double second_ratio = ratio(second, second_inverse);
    std::cout << " first_ratio=" << first_ratio
              << " second_ratio=" << second_ratio
              << " first_sides="
              << bg::formula::azimuth_side_value(first_to_second_start.azimuth, first_inverse.azimuth) << ','
              << bg::formula::azimuth_side_value(first_to_second_end.azimuth, first_inverse.azimuth)
              << " second_sides="
              << bg::formula::azimuth_side_value(second_to_first_start.azimuth, second_inverse.azimuth) << ','
              << bg::formula::azimuth_side_value(second_to_first_end.azimuth, second_inverse.azimuth)
              << " first_azimuth=" << first_inverse.azimuth
              << " second_azimuth=" << second_inverse.azimuth << '\n';
    bool mysql_unit = unit == 0.017453292519943278;
    return solved && points.size() == (mysql_unit ? 1 : 0)
        && (mysql_unit ? first_ratio > 0 && first_ratio < 1 && second_ratio > 0 && second_ratio < 1
                      : first_ratio < 0 || second_ratio < 0);
}

int main() {
    std::cout << std::setprecision(17);
    bool passed = true;
    for (double unit : {0.017453292519943278, std::acos(-1.0) / 180.0}) {
        for (int steps : {256, 4096})
            passed = !sampled_crosses(unit, steps) && passed;
        for (bool first : {false, true})
            for (bool second : {false, true})
                for (bool swap : {false, true})
                    passed = probe(unit, first, second, swap) && passed;
    }
    return passed ? 0 : 1;
}
