// Compile with Boost 1.84 headers to reproduce MySQL 8.4.11's geographic
// line/polygon relations and first turns at near-hole residuals and nearby controls.
// clang++ -std=c++17 -O3 -I/path/to/boost_1_84_0 \
//   geographic-line-polygon-relate-probe.cpp -o geographic-relate-probe
#include <boost/geometry/algorithms/correct.hpp>
#include <boost/geometry/algorithms/crosses.hpp>
#include <boost/geometry/algorithms/intersects.hpp>
#include <boost/geometry/algorithms/is_valid.hpp>
#include <boost/geometry/algorithms/relation.hpp>
#include <boost/geometry/algorithms/touches.hpp>
#include <boost/geometry/geometries/linestring.hpp>
#include <boost/geometry/geometries/point.hpp>
#include <boost/geometry/geometries/polygon.hpp>
#include <boost/geometry/strategies/relate/geographic.hpp>
#include <iostream>
#include <vector>

namespace bg = boost::geometry;
using Point = bg::model::point<double, 2, bg::cs::geographic<bg::radian>>;
using Line = bg::model::linestring<Point>;
using Polygon = bg::model::polygon<Point>;

void probe(double base, double longitude) {
    const double unit = 0.017453292519943278;
    auto point = [unit](double lon, double lat) { return Point(lon * unit, lat * unit); };
    Line line{point(longitude, base + 1), point(longitude, base + 3)};
    Polygon polygon;
    polygon.outer() = {point(0, base), point(4, base), point(4, base + 4), point(0, base + 4), point(0, base)};
    polygon.inners().resize(1);
    polygon.inners()[0] = {point(1, base + 1), point(1, base + 3), point(3, base + 3), point(3, base + 1), point(1, base + 1)};
    bg::correct(polygon);
    std::cout << base << ' ' << longitude << " valid=" << bg::is_valid(polygon)
              << " crosses=" << bg::crosses(line, polygon)
              << " touches=" << bg::touches(line, polygon)
              << " intersects=" << bg::intersects(line, polygon)
              << " relation=" << bg::relation(line, polygon).str();

    using Strategy = bg::strategies::relate::geographic<>;
    using TurnFinder = bg::detail::relate::turns::get_turns<Line, Polygon>;
    Strategy strategy;
    bg::detail::get_turns::no_interrupt_policy uninterrupted;
    std::vector<TurnFinder::turn_info_type<Strategy>::type> turns;
    TurnFinder::apply(turns, line, polygon, uninterrupted, strategy);
    if (turns.size() == 1) {
        auto const& turn = turns.front();
        std::cout << " start_fraction_zero="
                  << (turn.operations[0].fraction.numerator() == 0)
                  << " from_inside="
                  << bg::detail::relate::calculate_from_inside<0>(line, polygon, turn, strategy);
    }
    std::cout << '\n';
}

int main() {
    probe(0, 1.00001);
    probe(0, 1.001);
    probe(0, 2.99);
    probe(0, 2.999);
    probe(0, 2.99999);
    probe(40, 1.00001);
    probe(40, 2.99999);
    probe(60, 1.00001);
    probe(60, 2.99999);
    probe(70, 2.99999);
    probe(80, 1.00001);
    probe(80, 2.99999);
    probe(85, 1.00001);
}
