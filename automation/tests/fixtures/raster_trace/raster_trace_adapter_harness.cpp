#include <bitmap_trace.h>

#include <algorithm>
#include <cassert>
#include <iostream>
#include <numeric>
#include <string>
#include <vector>

static BITMAP_TRACE_RASTER Fixture()
{
    BITMAP_TRACE_RASTER raster{ 64, 64, std::vector<uint8_t>( 64 * 64, 0 ) };
    auto at = [&]( int x, int y ) -> uint8_t& { return raster.foreground[static_cast<size_t>( y ) * 64 + x]; };
    for( int y = 4; y < 45; ++y )
        for( int x = 4; x < 45; ++x )
            at( x, y ) = 1;
    for( int y = 16; y < 33; ++y )
        for( int x = 16; x < 33; ++x )
            at( x, y ) = 0;
    for( int y = 22; y < 27; ++y )
        for( int x = 22; x < 27; ++x )
            at( x, y ) = 1;
    for( int y = 48; y < 60; ++y )
        for( int x = 49; x < 61; ++x )
            at( x, y ) = 1;
    for( int x = 8; x <= 20; ++x )
        for( int d = -1; d <= 1; ++d )
            if( x + d >= 0 && x + d < 64 )
                at( x, x + d ) = 0;
    return raster;
}

int main()
{
    auto raster = Fixture();
    BITMAP_TRACE_SETTINGS settings{ 64'000'000, 128, false, 0, 1'000, 4096, 1'000'000 };
    auto first = TraceBitmap( raster, settings );
    if( !first.accepted )
    {
        std::cerr << "reject " << first.rejectionCode << "\n";
        for( const auto& diagnostic : first.diagnostics )
            std::cerr << diagnostic << "\n";
        return 2;
    }
    const size_t rootCount = std::count_if( first.contours.begin(), first.contours.end(),
        []( const auto& contour ) { return !contour.hole && contour.parentId.empty(); } );
    const size_t holeCount = std::count_if( first.contours.begin(), first.contours.end(),
        []( const auto& contour ) { return contour.hole; } );
    const size_t nestedIslandCount = std::count_if( first.contours.begin(), first.contours.end(),
        []( const auto& contour ) { return !contour.hole && !contour.parentId.empty(); } );
    assert( rootCount == 2 );
    assert( holeCount == 1 );
    assert( nestedIslandCount == 1 );
    assert( !first.filledPieces.empty() );

    auto second = TraceBitmap( raster, settings );
    assert( second.accepted && second.geometrySha256 == first.geometrySha256 );
    settings.threshold = 127;
    auto changed = TraceBitmap( raster, settings );
    assert( changed.geometrySha256 != first.geometrySha256 || !changed.accepted );

    const uint32_t contourVertices = std::accumulate( first.contours.begin(), first.contours.end(), 0u,
        []( uint32_t total, const auto& contour ) { return total + static_cast<uint32_t>( contour.points.size() ); } );
    const uint32_t filledVertices = std::accumulate( first.filledPieces.begin(), first.filledPieces.end(), 0u,
        []( uint32_t total, const auto& piece ) { return total + static_cast<uint32_t>( piece.points.size() ); } );
    settings = { 64'000'000, 128, false, 0, 1'000, 4096, contourVertices + filledVertices - 1 };
    auto aggregateBudget = TraceBitmap( raster, settings );
    assert( !aggregateBudget.accepted );
    assert( aggregateBudget.rejectionCode == "invalid_trace_geometry" );
    assert( std::any_of( aggregateBudget.diagnostics.begin(), aggregateBudget.diagnostics.end(),
        []( const std::string& message ) { return message.find( "filled pieces" ) != std::string::npos; } ) );
    assert( contourVertices < contourVertices + filledVertices - 1 );

    settings = { 64'000'000, 128, false, 0, 1'000, 3, 1'000'000 };
    assert( !TraceBitmap( raster, settings ).accepted );
    settings = { 64'000'000, 128, false, 0, 1, 4096, 1'000'000 };
    assert( !TraceBitmap( raster, settings ).accepted );
    settings = { 3'000'000'000LL, 128, false, 0, 1'000, 4096, 1'000'000 };
    assert( !TraceBitmap( raster, settings ).accepted );
    settings = { 64'000'000, 128, false, 0, 1'000, 4096, 3 };
    assert( !TraceBitmap( raster, settings ).accepted );
    std::cout << first.geometrySha256 << ' ' << first.contours.size() << ' ' << first.filledPieces.size() << '\n';
}
