/* Pure Potrace geometry adapter used by read-only raster proposals. GPL-3.0-or-later. */
#include <bitmap_trace.h>

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <limits>
#include <set>
#include <sstream>
#include <stdexcept>

#include <geometry/shape_poly_set.h>
#include <picosha2.h>
#include <potracelib.h>

namespace
{
constexpr uint32_t HARD_MAX_CONTOURS = 4096;
constexpr uint32_t HARD_MAX_VERTICES = 1'000'000;

BITMAP_TRACE_RESULT Reject( const char* aCode, const char* aMessage )
{
    BITMAP_TRACE_RESULT result;
    result.rejectionCode = aCode;
    result.diagnostics.emplace_back( aMessage );
    return result;
}

BITMAP_TRACE_BOUNDS Bounds( const std::vector<BITMAP_TRACE_POINT>& aPoints )
{
    BITMAP_TRACE_BOUNDS bounds{ std::numeric_limits<int64_t>::max(), std::numeric_limits<int64_t>::max(),
                                std::numeric_limits<int64_t>::min(), std::numeric_limits<int64_t>::min() };
    for( const BITMAP_TRACE_POINT& point : aPoints )
    {
        bounds.minX = std::min( bounds.minX, point.x );
        bounds.minY = std::min( bounds.minY, point.y );
        bounds.maxX = std::max( bounds.maxX, point.x );
        bounds.maxY = std::max( bounds.maxY, point.y );
    }
    return bounds;
}

int64_t Quantize( double aValue, int64_t aNumerator, uint32_t aDenominator )
{
    const long double value = static_cast<long double>( aValue ) * aNumerator / aDenominator;
    if( !std::isfinite( static_cast<double>( value ) )
        || value < std::numeric_limits<int64_t>::min() || value > std::numeric_limits<int64_t>::max() )
        throw std::overflow_error( "raster trace coordinate exceeds integer nanometres" );
    return static_cast<int64_t>( std::llround( value ) );
}

long double SignedArea( const std::vector<BITMAP_TRACE_POINT>& aPoints )
{
    long double area = 0;
    for( size_t i = 0; i < aPoints.size(); ++i )
    {
        const auto& first = aPoints[i];
        const auto& second = aPoints[( i + 1 ) % aPoints.size()];
        area += static_cast<long double>( first.x ) * second.y
                - static_cast<long double>( second.x ) * first.y;
    }
    return area / 2;
}

void AddPoint( std::vector<BITMAP_TRACE_POINT>& aPoints, BITMAP_TRACE_POINT aPoint )
{
    if( aPoints.empty() || aPoints.back().x != aPoint.x || aPoints.back().y != aPoint.y )
        aPoints.push_back( aPoint );
}

double Flatness( potrace_dpoint_t aP0, potrace_dpoint_t aP1,
                 potrace_dpoint_t aP2, potrace_dpoint_t aP3 )
{
    const double ux = 3 * aP1.x - 2 * aP0.x - aP3.x;
    const double uy = 3 * aP1.y - 2 * aP0.y - aP3.y;
    const double vx = 3 * aP2.x - 2 * aP0.x - aP3.x;
    const double vy = 3 * aP2.y - 2 * aP0.y - aP3.y;
    return std::max( ux * ux + uy * uy, vx * vx + vy * vy );
}

potrace_dpoint_t Bezier( potrace_dpoint_t aP0, potrace_dpoint_t aP1,
                         potrace_dpoint_t aP2, potrace_dpoint_t aP3, double aT )
{
    const double u = 1.0 - aT;
    return { u * u * u * aP0.x + 3 * u * u * aT * aP1.x + 3 * u * aT * aT * aP2.x + aT * aT * aT * aP3.x,
             u * u * u * aP0.y + 3 * u * u * aT * aP1.y + 3 * u * aT * aT * aP2.y + aT * aT * aT * aP3.y };
}

std::vector<BITMAP_TRACE_POINT> PathPoints( const potrace_path_t& aPath,
                                             int64_t aScaleNumerator, uint32_t aScaleDenominator,
                                             int64_t aCurveToleranceNm )
{
    std::vector<BITMAP_TRACE_POINT> points;
    if( aPath.curve.n <= 0 ) return points;
    auto point = [&]( potrace_dpoint_t p )
    { AddPoint( points, { Quantize( p.x, aScaleNumerator, aScaleDenominator ),
                         Quantize( p.y, aScaleNumerator, aScaleDenominator ) } ); };

    potrace_dpoint_t current = aPath.curve.c[aPath.curve.n - 1][2];
    point( current );
    const double tolerancePx = std::max( 0.01, static_cast<double>( aCurveToleranceNm )
                                               * aScaleDenominator / aScaleNumerator );
    for( int i = 0; i < aPath.curve.n; ++i )
    {
        if( aPath.curve.tag[i] == POTRACE_CORNER )
        {
            point( aPath.curve.c[i][1] );
            point( aPath.curve.c[i][2] );
            current = aPath.curve.c[i][2];
        }
        else if( aPath.curve.tag[i] == POTRACE_CURVETO )
        {
            const potrace_dpoint_t p1 = aPath.curve.c[i][0];
            const potrace_dpoint_t p2 = aPath.curve.c[i][1];
            const potrace_dpoint_t p3 = aPath.curve.c[i][2];
            const double error = std::sqrt( Flatness( current, p1, p2, p3 ) );
            const int steps = std::clamp( static_cast<int>( std::ceil( error / tolerancePx ) ), 2, 256 );
            for( int step = 1; step <= steps; ++step )
                point( Bezier( current, p1, p2, p3, static_cast<double>( step ) / steps ) );
            current = p3;
        }
    }
    if( points.size() > 1 && points.front().x == points.back().x && points.front().y == points.back().y )
        points.pop_back();
    return points;
}

SHAPE_LINE_CHAIN ToChain( const BITMAP_TRACE_CONTOUR& aContour )
{
    SHAPE_LINE_CHAIN chain;
    for( const auto& point : aContour.points ) chain.Append( VECTOR2I( point.x, point.y ) );
    chain.SetClosed( true );
    return chain;
}

void AppendPathTree( const potrace_path_t* aPath, const std::string& aParent,
                     int64_t aScaleNumerator, uint32_t aScaleDenominator, int64_t aTolerance,
                     uint32_t aMaxContours, uint32_t aMaxVertices,
                     std::vector<BITMAP_TRACE_CONTOUR>& aContours )
{
    for( const potrace_path_t* path = aPath; path; path = path->sibling )
    {
        if( aContours.size() >= aMaxContours ) throw std::runtime_error( "maximum contour count exceeded" );
        BITMAP_TRACE_CONTOUR contour;
        contour.id = "c" + std::to_string( aContours.size() );
        contour.parentId = aParent;
        contour.hole = path->sign == '-';
        contour.points = PathPoints( *path, aScaleNumerator, aScaleDenominator, aTolerance );
        if( contour.points.size() < 3 ) throw std::runtime_error( "degenerate trace contour" );
        if( aContours.size() + contour.points.size() > aMaxVertices )
            throw std::runtime_error( "maximum vertex count exceeded" );
        const long double area = SignedArea( contour.points );
        if( area == 0 ) throw std::runtime_error( "zero-area trace contour" );
        contour.orientation = area > 0 ? 1 : -1;
        contour.bounds = Bounds( contour.points );
        aContours.push_back( contour );
        if( path->childlist ) AppendPathTree( path->childlist, aContours.back().id, aScaleNumerator,
                                               aScaleDenominator, aTolerance, aMaxContours, aMaxVertices, aContours );
    }
}

std::string GeometryDigest( const BITMAP_TRACE_RESULT& aResult )
{
    std::ostringstream stream;
    for( const auto& contour : aResult.contours )
    {
        stream << contour.id << ':' << contour.parentId << ':' << contour.hole << ':' << contour.orientation << ';';
        for( const auto& point : contour.points ) stream << point.x << ',' << point.y << ';';
    }
    stream << '|';
    for( const auto& piece : aResult.filledPieces )
    {
        stream << piece.id << ':';
        for( const auto& source : piece.sourceContourIds ) stream << source << ',';
        stream << ';';
        for( const auto& point : piece.points ) stream << point.x << ',' << point.y << ';';
    }
    return picosha2::hash256_hex_string( stream.str() );
}
}

BITMAP_TRACE_RESULT TraceBitmap( const BITMAP_TRACE_RASTER& aRaster, const BITMAP_TRACE_SETTINGS& aSettings )
{
    if( aRaster.width == 0 || aRaster.height == 0 || aRaster.foreground.size() != static_cast<size_t>( aRaster.width ) * aRaster.height )
        return Reject( "invalid_raster", "Decoded raster dimensions and pixels do not match." );
    if( aRaster.width > 4096 || aRaster.height > 4096 || static_cast<uint64_t>( aRaster.width ) * aRaster.height > 4'194'304 )
        return Reject( "raster_too_large", "Decoded raster exceeds the bounded pixel limit." );
    if( aSettings.physicalWidthNm <= 0 || aSettings.curveToleranceNm <= 0 || aSettings.maxContours == 0
        || aSettings.maxContours > HARD_MAX_CONTOURS || aSettings.maxVertices < 3 || aSettings.maxVertices > HARD_MAX_VERTICES )
        return Reject( "invalid_trace_settings", "Trace dimensions, tolerance and limits are outside supported bounds." );

    const __int128 heightNumerator = static_cast<__int128>( aSettings.physicalWidthNm ) * aRaster.height;
    const __int128 height = ( heightNumerator + aRaster.width / 2 ) / aRaster.width;
    if( height <= 0 || height > std::numeric_limits<int64_t>::max() )
        return Reject( "physical_height_overflow", "Physical height does not fit in integer nanometres." );

    potrace_bitmap_t bitmap{};
    bitmap.w = static_cast<int>( aRaster.width );
    bitmap.h = static_cast<int>( aRaster.height );
    bitmap.dy = ( bitmap.w + 8 * sizeof( potrace_word ) - 1 ) / ( 8 * sizeof( potrace_word ) );
    bitmap.map = static_cast<potrace_word*>( calloc( static_cast<size_t>( bitmap.dy ) * bitmap.h, sizeof( potrace_word ) ) );
    if( !bitmap.map ) return Reject( "trace_allocation_failed", "Potrace bitmap allocation failed." );
    for( uint32_t y = 0; y < aRaster.height; ++y )
        for( uint32_t x = 0; x < aRaster.width; ++x )
            if( aRaster.foreground[static_cast<size_t>( y ) * aRaster.width + x] )
                bitmap.map[static_cast<size_t>( y ) * bitmap.dy + x / ( 8 * sizeof( potrace_word ) ) ]
                        |= static_cast<potrace_word>( 1 ) << ( 8 * sizeof( potrace_word ) - 1 - ( x % ( 8 * sizeof( potrace_word ) ) ) );

    potrace_param_t* params = potrace_param_default();
    if( !params ) { free( bitmap.map ); return Reject( "trace_allocation_failed", "Potrace parameter allocation failed." ); }
    params->turdsize = static_cast<int>( aSettings.turdSize );
    params->opttolerance = 0.2;
    potrace_state_t* state = potrace_trace( params, &bitmap );
    potrace_param_free( params );
    free( bitmap.map );
    if( !state || state->status != POTRACE_STATUS_OK )
    {
        if( state ) potrace_state_free( state );
        return Reject( "trace_failed", "Potrace could not trace the decoded raster." );
    }

    BITMAP_TRACE_RESULT result;
    result.physicalHeightNm = static_cast<int64_t>( height );
    result.scaleNumerator = aSettings.physicalWidthNm;
    result.scaleDenominator = aRaster.width;
    try
    {
        std::set<const potrace_path_t*> children;
        for( const potrace_path_t* path = state->plist; path; path = path->next )
            for( const potrace_path_t* child = path->childlist; child; child = child->sibling ) children.insert( child );
        for( const potrace_path_t* path = state->plist; path; path = path->next )
            if( !children.contains( path ) )
                AppendPathTree( path, "", result.scaleNumerator, result.scaleDenominator,
                                aSettings.curveToleranceNm, aSettings.maxContours, aSettings.maxVertices, result.contours );
        if( result.contours.empty() ) throw std::runtime_error( "the raster contains no foreground contour" );

        SHAPE_POLY_SET combined;
        for( const auto& contour : result.contours )
        {
            SHAPE_POLY_SET poly;
            poly.AddOutline( ToChain( contour ) );
            if( contour.hole ) combined.BooleanSubtract( poly );
            else combined.BooleanAdd( poly );
        }
        combined.Simplify();
        if( !combined.NormalizeAreaOutlines() ) throw std::runtime_error( "trace geometry is self-intersecting" );
        combined.Fracture();
        std::vector<std::string> allIds;
        for( const auto& contour : result.contours ) allIds.push_back( contour.id );
        for( int index = 0; index < combined.OutlineCount(); ++index )
        {
            const SHAPE_LINE_CHAIN& outline = combined.Outline( index );
            BITMAP_TRACE_FILLED_PIECE piece;
            piece.id = "p" + std::to_string( index );
            piece.sourceContourIds = allIds;
            for( int point = 0; point < outline.PointCount(); ++point )
                piece.points.push_back( { outline.CPoint( point ).x, outline.CPoint( point ).y } );
            if( piece.points.size() < 3 ) throw std::runtime_error( "trace filled piece is degenerate" );
            piece.bounds = Bounds( piece.points );
            result.filledPieces.push_back( std::move( piece ) );
        }
        if( result.filledPieces.empty() ) throw std::runtime_error( "trace produced no filled polygon" );
        result.bounds = result.contours.front().bounds;
        for( const auto& contour : result.contours )
        {
            result.bounds.minX = std::min( result.bounds.minX, contour.bounds.minX );
            result.bounds.minY = std::min( result.bounds.minY, contour.bounds.minY );
            result.bounds.maxX = std::max( result.bounds.maxX, contour.bounds.maxX );
            result.bounds.maxY = std::max( result.bounds.maxY, contour.bounds.maxY );
        }
        result.vertexCount = 0;
        for( const auto& contour : result.contours ) result.vertexCount += contour.points.size();
        result.geometrySha256 = GeometryDigest( result );
        result.accepted = true;
    }
    catch( const std::exception& error )
    {
        result.rejectionCode = "invalid_trace_geometry";
        result.diagnostics.emplace_back( error.what() );
        result.contours.clear();
        result.filledPieces.clear();
    }
    potrace_state_free( state );
    return result;
}
