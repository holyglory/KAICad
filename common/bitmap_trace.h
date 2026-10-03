/* Pure Potrace geometry adapter used by read-only raster proposals. GPL-3.0-or-later. */
#ifndef KICAD_BITMAP_TRACE_H
#define KICAD_BITMAP_TRACE_H

#include <cstdint>
#include <string>
#include <vector>

struct BITMAP_TRACE_POINT
{
    int64_t x = 0;
    int64_t y = 0;
};

struct BITMAP_TRACE_BOUNDS
{
    int64_t minX = 0;
    int64_t minY = 0;
    int64_t maxX = 0;
    int64_t maxY = 0;
};

struct BITMAP_TRACE_CONTOUR
{
    std::string id;
    std::string parentId;
    bool hole = false;
    int orientation = 0;
    std::vector<BITMAP_TRACE_POINT> points;
    BITMAP_TRACE_BOUNDS bounds;
};

struct BITMAP_TRACE_FILLED_PIECE
{
    std::string id;
    std::vector<std::string> sourceContourIds;
    std::vector<BITMAP_TRACE_POINT> points;
    BITMAP_TRACE_BOUNDS bounds;
    bool sourceContoursAreWholeTrace = true;
};

struct BITMAP_TRACE_RASTER
{
    uint32_t width = 0;
    uint32_t height = 0;
    // One byte per pixel: non-zero means foreground.
    std::vector<uint8_t> foreground;
};

struct BITMAP_TRACE_SETTINGS
{
    int64_t physicalWidthNm = 0;
    uint32_t turdSize = 0;
    int64_t curveToleranceNm = 0;
    uint32_t maxContours = 0;
    uint32_t maxVertices = 0;
};

struct BITMAP_TRACE_RESULT
{
    bool accepted = false;
    std::string rejectionCode;
    std::vector<std::string> diagnostics;
    int64_t physicalHeightNm = 0;
    int64_t scaleNumerator = 0;
    int64_t scaleDenominator = 0;
    std::vector<BITMAP_TRACE_CONTOUR> contours;
    std::vector<BITMAP_TRACE_FILLED_PIECE> filledPieces;
    BITMAP_TRACE_BOUNDS bounds;
    std::string geometrySha256;
    uint32_t vertexCount = 0;
    uint32_t rasterWidth = 0;
    uint32_t rasterHeight = 0;
    uint32_t turdSize = 0;
    int64_t curveToleranceNm = 0;
    double potraceOptTolerancePx = 0.2;
    uint32_t maxContours = 0;
    uint32_t maxVertices = 0;
};

// The input raster is already decoded and bounded by the native request
// handler. This adapter owns only Potrace and geometry; it does not depend on
// PCB/schematic classes and never writes files or native documents.
BITMAP_TRACE_RESULT TraceBitmap( const BITMAP_TRACE_RASTER& aRaster,
                                 const BITMAP_TRACE_SETTINGS& aSettings );

#endif
