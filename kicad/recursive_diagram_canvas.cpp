/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#include "recursive_diagram_canvas.h"
#include "recursive_diagram_frame.h"
#include "dialogs/dialog_diagram_field_history.h"
#include <bitmaps.h>
#include <kiid.h>
#include <algorithm>
#include <chrono>
#include <climits>
#include <cmath>
#include <cstdlib>
#include <wx/control.h>
#include <wx/dc.h>
#include <wx/dcbuffer.h>
#include <wx/dcclient.h>
#include <wx/dcgraph.h>
#include <wx/image.h>
#include <wx/graphics.h>
#include <wx/menu.h>
#include <wx/textctrl.h>
#include <wx/sizer.h>
#include <wx/settings.h>
#include <wx/tglbtn.h>
#include <memory>
#include <queue>
#include <set>
#include <tuple>
#if defined( __WXGTK__ )
#include <dlfcn.h>
#endif

namespace RECURSIVE_DIAGRAM
{
std::string FreshId() { return Utf8( KIID().AsString() ); }

namespace
{
// The colour arithmetic is shared with the history and conflict dialogs (dialogs/dialog_diagram_field_history.h).
using HSL = DIAGRAM_LOOK::HSL;
HSL toHsl( const wxColour& colour ) { return DIAGRAM_LOOK::ToHsl( colour ); }
wxColour fromHsl( const HSL& hsl ) { return DIAGRAM_LOOK::FromHsl( hsl ); }
wxColour mix( const wxColour& a, const wxColour& b, double weightOfA )
{
    auto one = [&]( unsigned char x, unsigned char y ) { return static_cast<unsigned char>( std::lround( x * weightOfA + y * ( 1 - weightOfA ) ) ); };
    return wxColour( one( a.Red(), b.Red() ), one( a.Green(), b.Green() ), one( a.Blue(), b.Blue() ) );
}
}

double Contrast( const wxColour& a, const wxColour& b ) { return DIAGRAM_LOOK::Contrast( a, b ); }

bool IsDark( const wxColour& surface ) { return surface.Red() + surface.Green() + surface.Blue() < 384; }

wxColour Readable( const wxColour& colour, std::initializer_list<wxColour> against, double minimum )
{
    auto worst = [&]( const wxColour& candidate )
    {
        double result = 21;
        for( const wxColour& surface : against ) result = std::min( result, Contrast( candidate, surface ) );
        return result;
    };
    if( worst( colour ) >= minimum ) return colour;
    HSL hsl = toHsl( colour );
    wxColour best = colour; double bestContrast = worst( colour );
    for( int step = 1; step <= 100; ++step )
        for( int sign : { 1, -1 } )
        {
            HSL moved = hsl; moved.l += sign * step * 0.01;
            if( moved.l < 0 || moved.l > 1 ) continue;
            wxColour candidate = fromHsl( moved ); double reached = worst( candidate );
            if( reached >= minimum ) return candidate;
            if( reached > bestContrast ) { best = candidate; bestContrast = reached; }
        }
    return best;
}

ACCENT_FILL AccentFill( const wxColour& accent, const wxColour& surface )
{
    DIAGRAM_LOOK::ACCENT fill = DIAGRAM_LOOK::AccentFill( accent, surface );
    return { fill.fill, fill.text };
}

CANVAS_COLOURS CanvasColours()
{
    CANVAS_COLOURS colours;
    colours.background = wxSystemSettings::GetColour( wxSYS_COLOUR_WINDOW );
    colours.foreground = wxSystemSettings::GetColour( wxSYS_COLOUR_WINDOWTEXT );
    colours.muted = wxSystemSettings::GetColour( wxSYS_COLOUR_GRAYTEXT );
    colours.dark = IsDark( colours.background );
    // The theme's selection colour, lightened on a dark canvas until selected items stand out from it (design QA P1-1:
    // the dark theme's own blue reached only 1.8:1 there, less than an unselected wire).
    colours.accent = Readable( wxSystemSettings::GetColour( wxSYS_COLOUR_HIGHLIGHT ), { colours.background }, colours.dark ? 4.5 : 3.0 );
    // The selected block's fill is a tint of the accent over the canvas, light enough that the accent outline and handles
    // stay 3:1 or more from it.
    double weight = 0.25;
    colours.selectedFill = mix( colours.accent, colours.background, weight );
    while( weight > 0.05 && Contrast( colours.accent, colours.selectedFill ) < 3.2 )
    { weight -= 0.01; colours.selectedFill = mix( colours.accent, colours.background, weight ); }
    colours.blockFill = colours.background.ChangeLightness( colours.dark ? 120 : 97 );
    // White handles with an accent outline, as the sketch; on a dark canvas the handles take the canvas's foreground.
    colours.handleFill = colours.dark ? colours.foreground : colours.background;
    return colours;
}

void PadTextBox( wxTextCtrl* control, int horizontal, int vertical ) { DIAGRAM_LOOK::PadTextBox( control, horizontal, vertical ); }

void DrawGlyph( wxDC& dc, GLYPH glyph, const wxRect& box, const wxColour& colour )
{
    // A 24-unit design grid scaled to the box; strokes are about 1.75 units wide with round ends. A graphics DC (a platform
    // button the editor paints, see PaintNatively) lends its own context; any other DC gets one of its own.
    std::unique_ptr<wxGraphicsContext> owned;
    wxGraphicsContext* gc = nullptr;
    if( auto* graphics = dynamic_cast<wxGCDC*>( &dc ) ) gc = graphics->GetGraphicsContext();
    else { owned.reset( wxGraphicsContext::CreateFromUnknownDC( dc ) ); gc = owned.get(); }
    if( !gc ) return;
    // The DC's own pen and brush are applied again after the glyph, so the DC draws on as before.
    struct RESTORE
    {
        wxDC& dc;
        ~RESTORE() { dc.SetPen( dc.GetPen() ); dc.SetBrush( dc.GetBrush() ); }
    } restore{ dc };
    gc->SetAntialiasMode( wxANTIALIAS_DEFAULT );
    const double s = std::min( box.width, box.height ) / 24.0;
    const double ox = box.x + ( box.width - 24 * s ) / 2, oy = box.y + ( box.height - 24 * s ) / 2;
    auto X = [&]( double x ) { return ox + x * s; };
    auto Y = [&]( double y ) { return oy + y * s; };
    gc->SetPen( gc->CreatePen( wxGraphicsPenInfo( colour ).Width( std::max( 1.5, 1.75 * s ) ).Cap( wxCAP_ROUND ).Join( wxJOIN_ROUND ) ) );
    gc->SetBrush( *wxTRANSPARENT_BRUSH );
    wxGraphicsPath path = gc->CreatePath();
    switch( glyph )
    {
    case GLYPH::SELECT:
    {
        const double points[][2] = { { 6, 3 }, { 6, 19 }, { 10, 15.2 }, { 13, 21.5 }, { 15.6, 20.3 }, { 12.6, 14 }, { 18, 14 } };
        path.MoveToPoint( X( points[0][0] ), Y( points[0][1] ) );
        for( size_t i = 1; i < std::size( points ); ++i ) path.AddLineToPoint( X( points[i][0] ), Y( points[i][1] ) );
        path.CloseSubpath();
        gc->SetBrush( gc->CreateBrush( wxBrush( colour ) ) );
        gc->DrawPath( path );
        return;
    }
    case GLYPH::ADD_BLOCK:
        path.AddRoundedRectangle( X( 3.5 ), Y( 6 ), 17 * s, 12 * s, 1.5 * s );
        break;
    case GLYPH::CONNECT:
        path.MoveToPoint( X( 6.8 ), Y( 17.2 ) ); path.AddLineToPoint( X( 17.2 ), Y( 6.8 ) );
        path.AddCircle( X( 5 ), Y( 19 ), 2.5 * s ); path.AddCircle( X( 19 ), Y( 5 ), 2.5 * s );
        break;
    case GLYPH::PORT:
        // A hollow square with a short lead, like a port on the canvas.
        path.AddRectangle( X( 10 ), Y( 7.5 ), 9 * s, 9 * s );
        path.MoveToPoint( X( 3 ), Y( 12 ) ); path.AddLineToPoint( X( 10 ), Y( 12 ) );
        break;
    case GLYPH::REMOVE:
        path.MoveToPoint( X( 4 ), Y( 6.5 ) ); path.AddLineToPoint( X( 20 ), Y( 6.5 ) );
        path.MoveToPoint( X( 9.5 ), Y( 6.5 ) ); path.AddLineToPoint( X( 9.5 ), Y( 3.8 ) );
        path.AddLineToPoint( X( 14.5 ), Y( 3.8 ) ); path.AddLineToPoint( X( 14.5 ), Y( 6.5 ) );
        path.MoveToPoint( X( 6.5 ), Y( 6.5 ) ); path.AddLineToPoint( X( 7.6 ), Y( 20.5 ) );
        path.AddLineToPoint( X( 16.4 ), Y( 20.5 ) ); path.AddLineToPoint( X( 17.5 ), Y( 6.5 ) );
        path.MoveToPoint( X( 10.3 ), Y( 10 ) ); path.AddLineToPoint( X( 10.3 ), Y( 17 ) );
        path.MoveToPoint( X( 13.7 ), Y( 10 ) ); path.AddLineToPoint( X( 13.7 ), Y( 17 ) );
        break;
    case GLYPH::UNDO:
        path.MoveToPoint( X( 9 ), Y( 5 ) ); path.AddLineToPoint( X( 5 ), Y( 9 ) ); path.AddLineToPoint( X( 9 ), Y( 13 ) );
        path.MoveToPoint( X( 5 ), Y( 9 ) ); path.AddLineToPoint( X( 14 ), Y( 9 ) );
        path.AddArc( X( 14 ), Y( 14 ), 5 * s, -M_PI / 2, M_PI / 2, true );
        path.AddLineToPoint( X( 8 ), Y( 19 ) );
        break;
    }
    gc->StrokePath( path );
}

void EditorOrigin( D::DiagramRevisionOriginData* origin, const std::string& summary )
{
    origin->set_kind( D::DAK_EDITOR ); origin->set_actor( "Native editor" ); origin->set_summary( summary );
    auto now = std::chrono::system_clock::now().time_since_epoch(); auto seconds = std::chrono::duration_cast<std::chrono::seconds>( now );
    origin->mutable_recorded_at()->set_seconds( seconds.count() );
    origin->mutable_recorded_at()->set_nanos( static_cast<int>( std::chrono::duration_cast<std::chrono::nanoseconds>( now - seconds ).count() / 100 * 100 ) );
}

bool ParseUnits( const std::string& text, int64_t& value )
{
    size_t start = 0; bool negative = false;
    if( !text.empty() && ( text[0] == '-' || text[0] == '+' ) ) { negative = text[0] == '-'; start = 1; }
    size_t dot = text.find( '.', start );
    std::string whole = text.substr( start, dot == std::string::npos ? std::string::npos : dot - start );
    std::string fraction = dot == std::string::npos ? std::string() : text.substr( dot + 1 );
    if( whole.empty() || ( dot != std::string::npos && fraction.empty() ) ) return false;
    auto digits = []( const std::string& part ) { return std::all_of( part.begin(), part.end(), []( char c ) { return c >= '0' && c <= '9'; } ); };
    if( !digits( whole ) || !digits( fraction ) ) return false;
    // Digits beyond the 0.001 quantum must be zero: the value is exact, never rounded.
    for( size_t i = 3; i < fraction.size(); ++i ) if( fraction[i] != '0' ) return false;
    size_t first = whole.find_first_not_of( '0' );
    std::string significant = first == std::string::npos ? std::string() : whole.substr( first );
    if( significant.size() > 10 ) return false;
    int64_t units = 0;
    for( char c : significant ) units = units * 10 + ( c - '0' );
    int64_t thousandths = 0;
    for( size_t i = 0; i < 3; ++i ) thousandths = thousandths * 10 + ( i < fraction.size() ? fraction[i] - '0' : 0 );
    int64_t result = units * QUANTUM + thousandths;
    if( result > 1000000000LL * QUANTUM ) return false;
    value = negative ? -result : result;
    return true;
}

std::string FormatUnits( int64_t value )
{
    if( value == 0 ) return "0";
    std::string result = value < 0 ? "-" : "";
    uint64_t magnitude = value < 0 ? static_cast<uint64_t>( -( value + 1 ) ) + 1 : static_cast<uint64_t>( value );
    result += std::to_string( magnitude / QUANTUM );
    if( uint64_t fraction = magnitude % QUANTUM )
    {
        std::string digits = std::to_string( fraction );
        digits.insert( 0, 3 - digits.size(), '0' );
        while( !digits.empty() && digits.back() == '0' ) digits.pop_back();
        result += "." + digits;
    }
    return result;
}

namespace
{
bool ParseRect( const D::DiagramRectData& data, RECT& rect )
{
    return ParseUnits( data.x(), rect.x ) && ParseUnits( data.y(), rect.y ) && ParseUnits( data.width(), rect.w )
           && ParseUnits( data.height(), rect.h ) && rect.w > 0 && rect.h > 0;
}
void EncodeRect( const RECT& rect, D::DiagramRectData* out )
{
    out->set_x( FormatUnits( rect.x ) ); out->set_y( FormatUnits( rect.y ) );
    out->set_width( FormatUnits( rect.w ) ); out->set_height( FormatUnits( rect.h ) );
}
void EncodePoint( const POINT& point, D::DiagramAnnotationPointData* out )
{
    out->set_x( FormatUnits( point.x ) ); out->set_y( FormatUnits( point.y ) );
}
/// Rule F2a: a placed port sits on its side of the rectangle at its offset.
POINT OnSide( const RECT& rect, D::DiagramPortSide side, int64_t offset )
{
    switch( side )
    {
    case D::DPS_LEFT: return { rect.x, rect.y + offset };
    case D::DPS_TOP: return { rect.x + offset, rect.y };
    case D::DPS_BOTTOM: return { rect.x + offset, rect.Bottom() };
    default: return { rect.Right(), rect.y + offset };
    }
}
int64_t Columns( size_t count ) { return std::max<int64_t>( 1, static_cast<int64_t>( std::ceil( std::sqrt( static_cast<double>( count ) ) ) ) ); }
}

LEVEL_LAYOUT::LEVEL_LAYOUT( std::string scope, std::vector<BOUNDARY> scopeInterfaces, std::vector<NODE> nodes,
                            std::vector<LINK> links, const D::DiagramPresentationViewData* view, std::vector<RECT> notes ) :
        m_scope( std::move( scope ) ), m_scopeInterfaces( std::move( scopeInterfaces ) ), m_nodes( std::move( nodes ) ),
        m_links( std::move( links ) ), m_notes( std::move( notes ) )
{
    m_rects.resize( m_nodes.size() ); m_placed.assign( m_nodes.size(), false );
    unsigned entries = 0, active = 0;
    if( view )
    {
        entries = view->blocks_size() + view->ports_size() + view->routes_size();
        RECT frame;
        if( view->has_frame() && ParseRect( view->frame(), frame ) ) m_frame = frame;
        for( const auto& placement : view->blocks() )
            for( size_t i = 0; i < m_nodes.size(); ++i )
                if( m_nodes[i].id == placement.block_id() && !m_placed[i] && ParseRect( placement.rect(), m_rects[i] ) )
                { m_placed[i] = true; ++active; break; }
    }
    // Rule F1: the legacy grid for a level without placements; otherwise the unplaced children
    // continue to the right of the placed ones on a grid of their own.
    std::vector<size_t> unplaced;
    for( size_t i = 0; i < m_nodes.size(); ++i ) if( !m_placed[i] ) unplaced.push_back( i );
    int64_t originX = 140 * QUANTUM, originY = 110 * QUANTUM, columns = Columns( m_nodes.size() );
    if( unplaced.size() != m_nodes.size() )
    {
        int64_t right = LLONG_MIN, top = LLONG_MAX;
        for( size_t i = 0; i < m_nodes.size(); ++i ) if( m_placed[i] ) { right = std::max( right, m_rects[i].Right() ); top = std::min( top, m_rects[i].y ); }
        originX = right + 130 * QUANTUM; originY = top; columns = Columns( unplaced.size() );
    }
    for( size_t k = 0; k < unplaced.size(); ++k )
    {
        int64_t index = static_cast<int64_t>( k );
        m_rects[unplaced[k]] = { originX + ( index % columns ) * 370 * QUANTUM, originY + ( index / columns ) * 250 * QUANTUM, 240 * QUANTUM, 145 * QUANTUM };
    }
    auto placedPort = [&]( const std::string& block, const std::string& port ) -> const D::DiagramPortPlacementData*
    {
        if( view ) for( const auto& row : view->ports() ) if( row.block_id() == block && row.interface_id() == port ) return &row;
        return nullptr;
    };
    for( size_t i = 0; i < m_nodes.size(); ++i )
    {
        const auto& node = m_nodes[i]; const RECT& rect = m_rects[i]; int64_t count = static_cast<int64_t>( node.interfaces.size() );
        for( int64_t k = 0; k < count; ++k )
        {
            PORT port; port.blockId = node.id; port.interfaceId = node.interfaces[k].id; port.name = node.interfaces[k].name;
            int64_t offset = 0;
            if( const auto* stored = placedPort( node.id, port.interfaceId ); stored && m_placed[i] && ParseUnits( stored->offset(), offset )
                && stored->side() != D::DPS_UNSPECIFIED )
            { port.side = stored->side(); port.offset = offset; port.placed = true; ++active; }
            else
            {
                // Rule F2: reported as the right side at the legacy offset.
                port.side = D::DPS_RIGHT; port.offset = ( k + 1 ) * rect.h / std::max<int64_t>( 2, count + 1 );
            }
            port.anchor = OnSide( rect, port.side, port.offset );
            m_ports.push_back( port );
        }
    }
    for( int64_t k = 0; k < static_cast<int64_t>( m_scopeInterfaces.size() ); ++k )
    {
        PORT port; port.blockId = m_scope; port.interfaceId = m_scopeInterfaces[k].id; port.name = m_scopeInterfaces[k].name; port.boundary = true;
        int64_t offset = 0;
        if( const auto* stored = placedPort( m_scope, port.interfaceId ); stored && m_frame && ParseUnits( stored->offset(), offset )
            && stored->side() != D::DPS_UNSPECIFIED )
        { port.side = stored->side(); port.offset = offset; port.placed = true; port.anchor = OnSide( *m_frame, port.side, offset ); ++active; }
        else
        {
            // Rule F3: an unplaced boundary port sits at (40, 90 + 85k), reported as a left-side port.
            port.side = D::DPS_LEFT; port.offset = ( 90 + 85 * k ) * QUANTUM; port.anchor = { 40 * QUANTUM, port.offset };
        }
        m_ports.push_back( port );
    }
    if( view )
        for( const auto& route : view->routes() )
            for( const auto& link : m_links )
                if( link.id == route.connection_id() && route.endpoint_index() >= 1
                    && route.endpoint_index() < static_cast<unsigned>( link.endpoints.size() ) )
                { m_routes.push_back( route ); ++active; break; }
    m_dormant = entries > active ? entries - active : 0;
    placeBlockEnds();
    placePaths();
}

const NODE* LEVEL_LAYOUT::Node( const std::string& id ) const
{
    for( const auto& node : m_nodes ) if( node.id == id ) return &node;
    return nullptr;
}
const LINK* LEVEL_LAYOUT::Link( const std::string& id ) const
{
    for( const auto& link : m_links ) if( link.id == id ) return &link;
    return nullptr;
}
RECT LEVEL_LAYOUT::Rect( const std::string& id ) const
{
    for( size_t i = 0; i < m_nodes.size(); ++i ) if( m_nodes[i].id == id ) return m_rects[i];
    return {};
}
bool LEVEL_LAYOUT::Placed( const std::string& id ) const
{
    for( size_t i = 0; i < m_nodes.size(); ++i ) if( m_nodes[i].id == id ) return m_placed[i];
    return false;
}
const PORT* LEVEL_LAYOUT::Port( const std::string& block, const std::string& port ) const
{
    for( const auto& item : m_ports ) if( item.blockId == block && item.interfaceId == port ) return &item;
    return nullptr;
}
int64_t LEVEL_LAYOUT::centreX( const D::DiagramEndpointBindingData& endpoint ) const
{
    if( endpoint.block_id() == m_scope ) return Anchor( endpoint, 0 ).x;
    RECT rect = Rect( endpoint.block_id() );
    return rect.x + rect.w / 2;
}
POINT LEVEL_LAYOUT::Anchor( const D::DiagramEndpointBindingData& endpoint, int64_t peerX ) const
{
    if( endpoint.block_id() == m_scope )
    {
        if( endpoint.has_interface_id() ) if( const PORT* port = Port( m_scope, endpoint.interface_id() ) ) return port->anchor;
        return { 40 * QUANTUM, ( 90 + 85 * static_cast<int64_t>( m_scopeInterfaces.size() ) ) * QUANTUM };
    }
    const NODE* node = Node( endpoint.block_id() ); if( !node ) return {};
    RECT rect = Rect( node->id );
    int64_t count = static_cast<int64_t>( node->interfaces.size() ), index = count;
    if( endpoint.has_interface_id() )
        for( int64_t k = 0; k < count; ++k ) if( node->interfaces[k].id == endpoint.interface_id() ) index = k;
    if( endpoint.has_interface_id() && index < count )
        if( const PORT* port = Port( node->id, endpoint.interface_id() ); port && port->placed ) return port->anchor;
    int64_t x = rect.x + rect.w / 2 < peerX ? rect.Right() : rect.x;
    // Rule F2: a port on the edge facing the peer, at the legacy offset.
    if( index < count ) return { x, rect.y + ( index + 1 ) * rect.h / std::max<int64_t>( 2, count + 1 ) };
    // An end on the block itself that is not part of a connection yet (the Connect preview): the middle of that edge.
    return { x, rect.y + rect.h / 2 };
}
bool LEVEL_LAYOUT::isBlockEnd( const D::DiagramEndpointBindingData& endpoint ) const
{
    if( endpoint.block_id() == m_scope ) return false;
    const NODE* node = Node( endpoint.block_id() );
    if( !node ) return false;
    if( !endpoint.has_interface_id() ) return true;
    return std::none_of( node->interfaces.begin(), node->interfaces.end(), [&]( const BOUNDARY& port ) { return port.id == endpoint.interface_id(); } );
}
D::DiagramPortSide LEVEL_LAYOUT::facing( const D::DiagramEndpointBindingData& endpoint, const D::DiagramEndpointBindingData& peer ) const
{
    RECT rect = Rect( endpoint.block_id() );
    return rect.x + rect.w / 2 < centreX( peer ) ? D::DPS_RIGHT : D::DPS_LEFT;
}
int64_t LEVEL_LAYOUT::referenceY( const D::DiagramEndpointBindingData& endpoint ) const
{
    if( endpoint.block_id() == m_scope ) return Anchor( endpoint, 0 ).y;
    if( !isBlockEnd( endpoint ) ) if( const PORT* port = Port( endpoint.block_id(), endpoint.interface_id() ) ) return port->anchor.y;
    RECT rect = Rect( endpoint.block_id() );
    return rect.y + rect.h / 2;
}
void LEVEL_LAYOUT::placeBlockEnds()
{
    // Rule F2 for an end on a block itself (design QA P2-5; the legacy k = n put every such end on the block's bottom corner
    // once it had a port, and gave all of them one shared point). The ends on one edge of one block get their own points: with
    // p ports on that edge (the block's placed ports on it and all of its unplaced ports) and m such ends, the edge is divided
    // into p + m + 1 equal steps rounded to the unit; each port, in order of its offset, takes the step point nearest to it
    // (the lower on a tie), and the ends take the m points left, in order of their peers' heights and then level order. An
    // end shared by several legs of one connection is one end. A single end on a block without ports stays at the middle.
    struct ATTACH { std::string link; int endpoint = 0; int side = 0; int64_t peerY = 0; size_t order = 0; };
    std::map<std::pair<std::string, int>, std::vector<ATTACH>> edges;
    std::set<std::tuple<std::string, int, int>> seen;
    size_t order = 0;
    for( const auto& link : m_links )
        for( int leg = 1; leg < static_cast<int>( link.endpoints.size() ); ++leg )
            for( auto [end, peer] : { std::pair<int, int>{ 0, leg }, std::pair<int, int>{ leg, 0 } } )
            {
                const auto& endpoint = link.endpoints[end];
                if( !isBlockEnd( endpoint ) ) continue;
                int side = static_cast<int>( facing( endpoint, link.endpoints[peer] ) );
                if( !seen.insert( { link.id, end, side } ).second ) continue;
                edges[{ endpoint.block_id(), side }].push_back( { link.id, end, side, referenceY( link.endpoints[peer] ), order++ } );
            }
    for( auto& [edge, ends] : edges )
    {
        RECT rect = Rect( edge.first );
        std::vector<int64_t> occupied;
        for( const auto& port : m_ports )
            if( !port.boundary && port.blockId == edge.first && ( !port.placed || static_cast<int>( port.side ) == edge.second ) )
                occupied.push_back( port.offset );
        std::sort( occupied.begin(), occupied.end() );
        const int64_t count = static_cast<int64_t>( occupied.size() + ends.size() ), steps = count + 1;
        std::vector<int64_t> points;
        for( int64_t j = 1; j <= count; ++j ) points.push_back( ( rect.h * j / steps + QUANTUM / 2 ) / QUANTUM * QUANTUM );
        for( int64_t offset : occupied )
        {
            size_t nearest = 0;
            for( size_t k = 1; k < points.size(); ++k ) if( std::llabs( points[k] - offset ) < std::llabs( points[nearest] - offset ) ) nearest = k;
            if( !points.empty() ) points.erase( points.begin() + static_cast<std::ptrdiff_t>( nearest ) );
        }
        std::stable_sort( ends.begin(), ends.end(), []( const ATTACH& a, const ATTACH& b )
                          { return a.peerY != b.peerY ? a.peerY < b.peerY : a.order < b.order; } );
        for( size_t k = 0; k < ends.size() && k < points.size(); ++k )
            m_blockEnds[{ ends[k].link, ends[k].endpoint, ends[k].side }] = { edge.second == D::DPS_RIGHT ? rect.Right() : rect.x, rect.y + points[k] };
    }
}
POINT LEVEL_LAYOUT::EndAnchor( const LINK& link, int endpoint, int leg ) const
{
    const auto& end = link.endpoints[endpoint];
    const auto& peer = link.endpoints[endpoint == 0 ? leg : 0];
    if( isBlockEnd( end ) )
        if( auto found = m_blockEnds.find( { link.id, endpoint, static_cast<int>( facing( end, peer ) ) } ); found != m_blockEnds.end() )
            return found->second;
    return Anchor( end, centreX( peer ) );
}
std::pair<int, int> LEVEL_LAYOUT::leaving( const D::DiagramEndpointBindingData& endpoint, const POINT& at ) const
{
    // Which way a path leaves an end (rules F4c and F4d): a child block's end points out of the block, a boundary port
    // into the level.
    auto out = []( D::DiagramPortSide side, bool boundary ) -> std::pair<int, int>
    {
        const int sign = boundary ? -1 : 1;
        switch( side )
        {
        case D::DPS_LEFT: return { -sign, 0 };
        case D::DPS_TOP: return { 0, -sign };
        case D::DPS_BOTTOM: return { 0, sign };
        default: return { sign, 0 };
        }
    };
    if( endpoint.block_id() == m_scope )
    {
        if( endpoint.has_interface_id() )
            if( const PORT* port = Port( m_scope, endpoint.interface_id() ); port && port->placed ) return out( port->side, true );
        return { 1, 0 };
    }
    if( endpoint.has_interface_id() )
        if( const PORT* port = Port( endpoint.block_id(), endpoint.interface_id() ); port && port->placed ) return out( port->side, false );
    return { at.x == Rect( endpoint.block_id() ).Right() ? 1 : -1, 0 };
}
int LEVEL_LAYOUT::ownBlock( const D::DiagramEndpointBindingData& endpoint ) const
{
    if( endpoint.block_id() == m_scope ) return -1;
    for( size_t i = 0; i < m_nodes.size(); ++i ) if( m_nodes[i].id == endpoint.block_id() ) return static_cast<int>( i );
    return -1;
}
bool LEVEL_LAYOUT::Sideways( const LINK& link, int index ) const
{
    if( index < 1 || index >= static_cast<int>( link.endpoints.size() ) ) return false;
    return leaving( link.endpoints[0], EndAnchor( link, 0, index ) ).second == 0
           && leaving( link.endpoints[index], EndAnchor( link, index, index ) ).second == 0;
}

namespace
{
ROUTE_STATS g_routeStats;

/// The computed legs of one level geometry, laid out once: an unchanged level (a repaint, a state report, the geometry
/// before and after a drag step) reuses its layout instead of laying it out again.
std::vector<std::vector<POINT>> cachedRoutes( const std::vector<RECT>& blocks, const std::vector<std::vector<POINT>>& stored,
                                              const std::vector<ROUTE_LEG>& legs )
{
    std::string key;
    auto put = [&key]( int64_t value ) { key.append( reinterpret_cast<const char*>( &value ), sizeof value ); };
    put( static_cast<int64_t>( blocks.size() ) );
    for( const RECT& rect : blocks ) { put( rect.x ); put( rect.y ); put( rect.w ); put( rect.h ); }
    put( static_cast<int64_t>( stored.size() ) );
    for( const auto& path : stored ) { put( static_cast<int64_t>( path.size() ) ); for( const POINT& point : path ) { put( point.x ); put( point.y ); } }
    put( static_cast<int64_t>( legs.size() ) );
    for( const ROUTE_LEG& leg : legs )
        for( const ROUTE_END* end : { &leg.from, &leg.to } ) { put( end->at.x ); put( end->at.y ); put( end->dx ); put( end->dy ); put( end->block ); }
    struct ENTRY { std::string key; std::vector<std::vector<POINT>> paths; };
    static std::vector<ENTRY> recent;
    for( size_t i = 0; i < recent.size(); ++i )
        if( recent[i].key == key )
        {
            std::rotate( recent.begin(), recent.begin() + static_cast<std::ptrdiff_t>( i ), recent.begin() + static_cast<std::ptrdiff_t>( i ) + 1 );
            return recent.front().paths;
        }
    const auto started = std::chrono::steady_clock::now();
    auto paths = RouteLegs( blocks, stored, legs );
    const auto micros = static_cast<uint64_t>( std::chrono::duration_cast<std::chrono::microseconds>( std::chrono::steady_clock::now() - started ).count() );
    ++g_routeStats.layouts; g_routeStats.latestMicros = micros; g_routeStats.slowestMicros = std::max( g_routeStats.slowestMicros, micros );
    recent.insert( recent.begin(), ENTRY{ std::move( key ), paths } );
    if( recent.size() > 16 ) recent.pop_back();
    return paths;
}
}

ROUTE_STATS RouteStats() { return g_routeStats; }

std::vector<std::vector<POINT>> RouteLegs( const std::vector<RECT>& blocks, const std::vector<std::vector<POINT>>& stored,
                                           const std::vector<ROUTE_LEG>& legs )
{
    // Rule F4 for computed legs, as revised for design QA P2-5 and its review. A leg runs from its first end to a channel (a
    // vertical line at x) and on to its other end:
    //  - An end on a block's left or right edge, or a boundary port on the frame's left or right side (or unplaced), is left
    //    level: the leg runs from, (x, from.y), (x, to.y), to, with the channel at least 20 units out along the edge (F4c).
    //  - An end on a top or bottom side is left along its normal (F4d): the leg first runs straight up or down from it for a
    //    lead of 20, 30 or 40 units, and only then level to the channel. The channel may also be the end's own x, where the
    //    lead runs on to the other end's level run.
    // Candidates: the preferred channel, the middle between the ends moved into their limits, and every channel 10k units
    // from it within the limits, at most 20 either side (and up to 200 units beyond the ends on a side without a limit); then
    // each limit itself (exactly 20 units out from its end) and, for an end on a top or bottom side, its own x; each with
    // every lead. When the limits of the two ends cannot both hold,
    // the preferred channel is the middle and the candidates stay 10 units inside the ends. A leg whose ends are level and
    // leave sideways has only the preferred channel. A candidate scores, in this order (F4a):
    //  (a) its runs beside other legs: two level segments less than 10 units apart in height whose runs overlap (on one
    //      height, also runs that end less than 10 units apart), or two upright segments less than 10 units apart that share
    //      or meet at a height, unless both segments start at an end the two legs share (two connections of one port);
    //  (b) how far, in total, it runs beside them;
    //  (c) its contacts: each segment that comes within 10 units of a child block, not counting a block for the segment that
    //      leaves or reaches that block's end;
    //  (d) the other legs' segments it crosses;
    //  (e) its distance from the preferred channel plus the length of its leads beyond 20 units;
    // then the lower channel, then the earlier candidate. The legs are laid out in level order, each against the paths
    // already drawn; then, up to four times over, a leg still running beside another is laid out again together with each
    // leg it runs beside, over every pair of each one's 16 best candidates (scored against all the other legs), and the first
    // pair (in candidate order) whose summed score is the lowest and lower than their current one is taken. Each candidate's
    // score against the other legs is kept up to date as legs change, and paths or segments whose boxes lie 10 units or
    // more apart are not compared, so a level of fifty connections is laid out in bounded time.
    const int64_t apart = 10 * QUANTUM, stub = 20 * QUANTUM, reach = 200 * QUANTUM;
    const int steps = 20;
    const size_t shortlist = 16;
    const std::vector<int64_t> leads{ 20 * QUANTUM, 30 * QUANTUM, 40 * QUANTUM }, level{ 0 };
    using PATH = std::vector<POINT>;
    struct SCORE
    {
        int64_t conflicts = 0, alongside = 0, contacts = 0, crossings = 0, distance = 0;
        SCORE operator+( const SCORE& other ) const
        {
            return { conflicts + other.conflicts, alongside + other.alongside, contacts + other.contacts, crossings + other.crossings,
                     distance + other.distance };
        }
        SCORE operator-( const SCORE& other ) const
        {
            return { conflicts - other.conflicts, alongside - other.alongside, contacts - other.contacts, crossings - other.crossings,
                     distance - other.distance };
        }
        bool operator<( const SCORE& other ) const
        {
            return std::tie( conflicts, alongside, contacts, crossings, distance )
                   < std::tie( other.conflicts, other.alongside, other.contacts, other.crossings, other.distance );
        }
    };
    // A path as its segments (zero-length ones left out), each with its box, and the box of the whole path: two paths or
    // segments whose boxes are 10 units apart or more can neither cross nor run beside each other.
    struct SEGMENT
    {
        POINT a, b;
        int64_t left, top, right, bottom;
        bool level, upright, first, last;
    };
    struct DRAWN
    {
        PATH path;
        std::vector<SEGMENT> segments;
        int64_t left = 0, top = 0, right = 0, bottom = 0;
        explicit DRAWN( PATH points = {} ) : path( std::move( points ) )
        {
            if( path.empty() ) return;
            left = right = path.front().x; top = bottom = path.front().y;
            for( size_t i = 1; i < path.size(); ++i )
            {
                const POINT &a = path[i - 1], &b = path[i];
                if( a.x == b.x && a.y == b.y ) continue;
                SEGMENT segment{ a, b, a.x < b.x ? a.x : b.x, a.y < b.y ? a.y : b.y, a.x < b.x ? b.x : a.x, a.y < b.y ? b.y : a.y,
                                 a.y == b.y, a.x == b.x, i == 1, i + 1 == path.size() };
                left = left < segment.left ? left : segment.left; right = right > segment.right ? right : segment.right;
                top = top < segment.top ? top : segment.top; bottom = bottom > segment.bottom ? bottom : segment.bottom;
                segments.push_back( segment );
            }
        }
    };
    struct CANDIDATE { DRAWN drawn; int64_t x = 0; SCORE own; };
    auto same = []( const POINT& a, const POINT& b ) { return a.x == b.x && a.y == b.y; };
    auto simplify = [&]( const PATH& points )
    {
        PATH out;
        for( const POINT& point : points ) if( out.empty() || !same( out.back(), point ) ) out.push_back( point );
        // A point in the middle of a straight run goes; a point where the path turns, or turns back, stays.
        for( size_t i = 1; i + 1 < out.size(); )
        {
            const POINT &a = out[i - 1], &b = out[i], &c = out[i + 1];
            bool straight = ( a.x == b.x && b.x == c.x && ( b.y - a.y ) * ( c.y - b.y ) > 0 )
                            || ( a.y == b.y && b.y == c.y && ( b.x - a.x ) * ( c.x - b.x ) > 0 );
            if( straight ) out.erase( out.begin() + static_cast<std::ptrdiff_t>( i ) );
            else ++i;
        }
        return out;
    };
    auto pathOf = [&]( const ROUTE_LEG& leg, int64_t x, int64_t leadFrom, int64_t leadTo ) -> PATH
    {
        const POINT f = leg.from.at, t = leg.to.at;
        const bool upFrom = leg.from.dy != 0, upTo = leg.to.dy != 0;
        if( !upFrom && !upTo ) return { f, { x, f.y }, { x, t.y }, t };
        const POINT f2{ f.x, f.y + leg.from.dy * leadFrom }, t2{ t.x, t.y + leg.to.dy * leadTo };
        PATH points{ f };
        if( upFrom ) points.push_back( f2 );
        points.push_back( { x, upFrom ? f2.y : f.y } );
        points.push_back( { x, upTo ? t2.y : t.y } );
        if( upTo ) points.push_back( t2 );
        points.push_back( t );
        return simplify( points );
    };
    // How one path runs beside and crosses another (the same both ways round).
    auto against = [&]( const DRAWN& mine, const DRAWN& other ) -> SCORE
    {
        SCORE score;
        if( mine.left >= other.right + apart || other.left >= mine.right + apart || mine.top >= other.bottom + apart
            || other.top >= mine.bottom + apart )
            return score;
        const POINT &mineFrom = mine.path.front(), &mineTo = mine.path.back();
        const POINT &otherFrom = other.path.front(), &otherTo = other.path.back();
        for( const SEGMENT& s : mine.segments )
            for( const SEGMENT& t : other.segments )
            {
                // A slanted segment (a stored route drawn as stored) neither runs beside nor crosses anything.
                if( !( s.level || s.upright ) || !( t.level || t.upright ) ) continue;
                if( s.left >= t.right + apart || t.left >= s.right + apart || s.top >= t.bottom + apart || t.top >= s.bottom + apart )
                    continue;
                if( s.level != t.level )
                {
                    // A crossing: the upright one passes strictly through the level one.
                    const SEGMENT &h = s.level ? s : t, &v = s.level ? t : s;
                    if( v.left > h.left && v.left < h.right && h.top > v.top && h.top < v.bottom ) ++score.crossings;
                    continue;
                }
                const int64_t apartAcross = s.level ? ( s.top > t.top ? s.top - t.top : t.top - s.top ) : ( s.left > t.left ? s.left - t.left : t.left - s.left );
                const int64_t overlap = s.level ? ( s.right < t.right ? s.right : t.right ) - ( s.left > t.left ? s.left : t.left )
                                                : ( s.bottom < t.bottom ? s.bottom : t.bottom ) - ( s.top > t.top ? s.top : t.top );
                // Level runs on one height beside each other when they overlap or end less than 10 units apart, at nearby
                // heights when they overlap; upright runs when they share or meet at a height.
                const bool beside = apartAcross < apart
                                    && ( s.level ? ( apartAcross == 0 ? overlap > -apart : overlap > 0 ) : overlap >= 0 );
                if( !beside ) continue;
                // Two connections of one port share the run at that port: both segments start at an end of both legs.
                const bool onePort = ( s.first && ( ( t.first && same( mineFrom, otherFrom ) ) || ( t.last && same( mineFrom, otherTo ) ) ) )
                                     || ( s.last && ( ( t.first && same( mineTo, otherFrom ) ) || ( t.last && same( mineTo, otherTo ) ) ) );
                if( onePort ) continue;
                ++score.conflicts; score.alongside += overlap > 0 ? overlap : 0;
            }
        return score;
    };
    // Its own part of the score: the blocks it comes near.
    auto contacts = [&]( const ROUTE_LEG& leg, const PATH& path ) -> int64_t
    {
        int64_t count = 0;
        for( size_t j = 1; j < path.size(); ++j )
        {
            const POINT &a = path[j - 1], &b = path[j];
            if( same( a, b ) ) continue;
            for( size_t n = 0; n < blocks.size(); ++n )
            {
                const int block = static_cast<int>( n );
                if( ( j == 1 && block == leg.from.block ) || ( j + 1 == path.size() && block == leg.to.block ) ) continue;
                const RECT& r = blocks[n];
                if( std::max( a.x, b.x ) > r.x - apart && std::min( a.x, b.x ) < r.Right() + apart && std::max( a.y, b.y ) > r.y - apart
                    && std::min( a.y, b.y ) < r.Bottom() + apart )
                    ++count;
            }
        }
        return count;
    };
    std::vector<std::vector<CANDIDATE>> candidates( legs.size() );
    for( size_t i = 0; i < legs.size(); ++i )
    {
        const ROUTE_LEG& leg = legs[i];
        const POINT f = leg.from.at, t = leg.to.at;
        const bool upFrom = leg.from.dy != 0, upTo = leg.to.dy != 0;
        std::optional<int64_t> low, high;
        for( const ROUTE_END* end : { &leg.from, &leg.to } )
        {
            if( end->dx > 0 ) low = low ? std::max( *low, end->at.x + stub ) : end->at.x + stub;
            else if( end->dx < 0 ) high = high ? std::min( *high, end->at.x - stub ) : end->at.x - stub;
        }
        int64_t left, right, preferred = ( f.x + t.x ) / 2;
        if( !low || !high || *low <= *high )
        {
            if( low ) preferred = std::max( preferred, *low );
            if( high ) preferred = std::min( preferred, *high );
            left = low ? *low : std::min( f.x, t.x ) - reach;
            right = high ? *high : std::max( f.x, t.x ) + reach;
        }
        else
        {
            left = std::min( f.x, t.x ) + apart; right = std::max( f.x, t.x ) - apart;
        }
        std::vector<int64_t> channels{ preferred };
        if( upFrom || upTo || f.y != t.y )
            for( int k = 1; k <= steps; ++k )
                for( int64_t x : { preferred - k * apart, preferred + k * apart } )
                    if( x >= left && x <= right ) channels.push_back( x );
        // The channels exactly 20 units out from an end, so a run beside another can be kept to that end's own stub, and
        // an upright end's own x.
        std::vector<int64_t> extra;
        if( upFrom || upTo || f.y != t.y )
        {
            if( low ) extra.push_back( *low );
            if( high ) extra.push_back( *high );
        }
        for( const ROUTE_END* end : { &leg.from, &leg.to } ) if( end->dy != 0 ) extra.push_back( end->at.x );
        for( int64_t x : extra )
            if( x >= left && x <= right && std::find( channels.begin(), channels.end(), x ) == channels.end() ) channels.push_back( x );
        for( int64_t x : channels )
            for( int64_t leadFrom : upFrom ? leads : level )
                for( int64_t leadTo : upTo ? leads : level )
                {
                    CANDIDATE candidate;
                    candidate.x = x; candidate.drawn = DRAWN( pathOf( leg, x, leadFrom, leadTo ) );
                    candidate.own.contacts = contacts( leg, candidate.drawn.path );
                    candidate.own.distance = std::llabs( x - preferred ) + ( upFrom ? leadFrom - stub : 0 ) + ( upTo ? leadTo - stub : 0 );
                    candidates[i].push_back( std::move( candidate ) );
                }
    }
    std::vector<DRAWN> fixed;
    for( const auto& path : stored ) fixed.emplace_back( path );
    std::vector<size_t> chosen( legs.size(), 0 );
    // The paths drawn so far: every stored route and the first aCount computed legs, leaving out aSkipA and aSkipB.
    auto others = [&]( size_t count, size_t skipA, size_t skipB )
    {
        std::vector<const DRAWN*> result;
        for( const auto& path : fixed ) result.push_back( &path );
        for( size_t i = 0; i < count; ++i ) if( i != skipA && i != skipB ) result.push_back( &candidates[i][chosen[i]].drawn );
        return result;
    };
    auto cost = [&]( size_t leg, size_t index, const std::vector<const DRAWN*>& drawn )
    {
        SCORE score = candidates[leg][index].own;
        for( const DRAWN* other : drawn ) score = score + against( candidates[leg][index].drawn, *other );
        return score;
    };
    for( size_t i = 0; i < legs.size(); ++i )
    {
        const auto before = others( i, SIZE_MAX, SIZE_MAX );
        std::optional<SCORE> best; size_t bestIndex = 0;
        for( size_t c = 0; c < candidates[i].size(); ++c )
        {
            SCORE score = cost( i, c, before );
            if( !best || score < *best || ( !( *best < score ) && candidates[i][c].x < candidates[i][bestIndex].x ) ) { best = score; bestIndex = c; }
        }
        chosen[i] = bestIndex;
    }
    // Every candidate's score against all the other legs as they are drawn now (and the stored routes), kept up to date as
    // legs take another channel.
    std::vector<std::vector<SCORE>> all( legs.size() );
    for( size_t x = 0; x < legs.size(); ++x )
    {
        const auto drawn = others( legs.size(), x, SIZE_MAX );
        for( size_t i = 0; i < candidates[x].size(); ++i ) all[x].push_back( cost( x, i, drawn ) );
    }
    // The box around all of a leg's candidates: a leg whose box is 10 units or more from a path cannot score against it.
    struct BOX { int64_t left, top, right, bottom; };
    std::vector<BOX> reachOf;
    for( const auto& list : candidates )
    {
        BOX box{ LLONG_MAX, LLONG_MAX, LLONG_MIN, LLONG_MIN };
        for( const CANDIDATE& candidate : list )
        {
            box.left = std::min( box.left, candidate.drawn.left ); box.top = std::min( box.top, candidate.drawn.top );
            box.right = std::max( box.right, candidate.drawn.right ); box.bottom = std::max( box.bottom, candidate.drawn.bottom );
        }
        reachOf.push_back( box );
    }
    auto near = [&]( const BOX& box, const DRAWN& path )
    {
        return !( box.left >= path.right + apart || path.left >= box.right + apart || box.top >= path.bottom + apart || path.top >= box.bottom + apart );
    };
    auto take = [&]( size_t leg, size_t index )
    {
        const DRAWN &was = candidates[leg][chosen[leg]].drawn, &now = candidates[leg][index].drawn;
        for( size_t x = 0; x < legs.size(); ++x )
            if( x != leg && ( near( reachOf[x], was ) || near( reachOf[x], now ) ) )
                for( size_t i = 0; i < candidates[x].size(); ++i )
                    all[x][i] = all[x][i] - against( candidates[x][i].drawn, was ) + against( candidates[x][i].drawn, now );
        chosen[leg] = index;
    };
    for( int round = 0; round < 4; ++round )
    {
        bool improved = false;
        for( size_t a = 0; a < legs.size(); ++a )
        {
            if( all[a][chosen[a]].conflicts == 0 ) continue;
            for( size_t b = 0; b < legs.size(); ++b )
            {
                // Only a leg this one runs beside is laid out again with it.
                const DRAWN &nowA = candidates[a][chosen[a]].drawn, &nowB = candidates[b][chosen[b]].drawn;
                if( b == a || against( nowA, nowB ).conflicts == 0 ) continue;
                // Each leg's score against all the other legs but these two.
                std::vector<SCORE> baseA, baseB;
                for( size_t i = 0; i < candidates[a].size(); ++i ) baseA.push_back( all[a][i] - against( candidates[a][i].drawn, nowB ) );
                for( size_t j = 0; j < candidates[b].size(); ++j ) baseB.push_back( all[b][j] - against( candidates[b][j].drawn, nowA ) );
                auto total = [&]( size_t i, size_t j )
                {
                    const SCORE between = against( candidates[a][i].drawn, candidates[b][j].drawn );
                    return baseA[i] + between + baseB[j] + between;
                };
                const SCORE current = total( chosen[a], chosen[b] );
                // Each leg's best candidates against all the other legs (at most `shortlist`, in candidate order among equals),
                // so a dense level is laid out in bounded time.
                auto best = [&]( const std::vector<SCORE>& base )
                {
                    std::vector<size_t> order( base.size() );
                    for( size_t i = 0; i < order.size(); ++i ) order[i] = i;
                    std::stable_sort( order.begin(), order.end(), [&]( size_t x, size_t y ) { return base[x] < base[y]; } );
                    if( order.size() > shortlist ) order.resize( shortlist );
                    std::sort( order.begin(), order.end() );
                    return order;
                };
                const auto listA = best( baseA ), listB = best( baseB );
                std::optional<std::tuple<SCORE, size_t, size_t>> found;
                for( size_t i : listA )
                    for( size_t j : listB )
                    {
                        SCORE score = total( i, j );
                        if( score < current && ( !found || score < std::get<0>( *found ) ) ) found = { score, i, j };
                    }
                if( !found ) continue;
                take( a, std::get<1>( *found ) ); take( b, std::get<2>( *found ) );
                improved = true;
                if( std::get<0>( *found ).conflicts == 0 ) break;
            }
        }
        if( !improved ) break;
    }
    std::vector<PATH> result;
    for( size_t i = 0; i < legs.size(); ++i ) result.push_back( candidates[i][chosen[i]].drawn.path );
    return result;
}

std::vector<wxPoint> OrthogonalDetour( const std::vector<wxRect>& obstacles, const wxPoint& start, const wxPoint& startDirection,
                                       const wxPoint& goal, const wxPoint& goalDirection, const wxRect& area )
{
    // A route on the lines through the two ends and one pixel outside each obstacle's sides (every turn a shortest orthogonal
    // way around rectangles needs lies on them), searched for the fewest turns and then the shortest length. A run between
    // two neighbouring lines is open unless it passes through an obstacle.
    auto inside = [&]( const wxPoint& p )
    { return std::any_of( obstacles.begin(), obstacles.end(), [&]( const wxRect& r ) { return r.Contains( p ); } ); };
    if( inside( start ) || inside( goal ) || !area.Contains( start ) || !area.Contains( goal ) ) return {};
    std::vector<int> xs{ start.x, goal.x }, ys{ start.y, goal.y };
    for( const wxRect& r : obstacles )
    { xs.push_back( r.x - 1 ); xs.push_back( r.GetRight() + 1 ); ys.push_back( r.y - 1 ); ys.push_back( r.GetBottom() + 1 ); }
    auto tidy = []( std::vector<int>& lines, int low, int high )
    {
        lines.erase( std::remove_if( lines.begin(), lines.end(), [&]( int v ) { return v < low || v > high; } ), lines.end() );
        std::sort( lines.begin(), lines.end() ); lines.erase( std::unique( lines.begin(), lines.end() ), lines.end() );
    };
    tidy( xs, area.x, area.GetRight() ); tidy( ys, area.y, area.GetBottom() );
    const int columns = static_cast<int>( xs.size() ), rows = static_cast<int>( ys.size() );
    // Whether the run from (a, at) to (b, at) along x (or, with aAlongY, along y) passes through an obstacle between its ends.
    auto blocked = [&]( int a, int b, int at, bool alongY )
    {
        const int low = std::min( a, b ) + 1, high = std::max( a, b ) - 1;
        for( const wxRect& r : obstacles )
        {
            const int from = alongY ? r.y : r.x, to = alongY ? r.GetBottom() : r.GetRight();
            const int acrossFrom = alongY ? r.x : r.y, acrossTo = alongY ? r.GetRight() : r.GetBottom();
            if( at >= acrossFrom && at <= acrossTo && std::max( low, from ) <= std::min( high, to ) ) return true;
        }
        return false;
    };
    const int column0 = static_cast<int>( std::find( xs.begin(), xs.end(), start.x ) - xs.begin() );
    const int row0 = static_cast<int>( std::find( ys.begin(), ys.end(), start.y ) - ys.begin() );
    const int column1 = static_cast<int>( std::find( xs.begin(), xs.end(), goal.x ) - xs.begin() );
    const int row1 = static_cast<int>( std::find( ys.begin(), ys.end(), goal.y ) - ys.begin() );
    // Directions: 0 right, 1 left, 2 down, 3 up.
    const int dx[] = { 1, -1, 0, 0 }, dy[] = { 0, 0, 1, -1 };
    auto directionOf = []( const wxPoint& d ) { return d.x > 0 ? 0 : d.x < 0 ? 1 : d.y > 0 ? 2 : d.y < 0 ? 3 : -1; };
    const int first = directionOf( startDirection ), last = directionOf( goalDirection );
    constexpr int64_t TURN = 1000000;
    const int states = columns * rows * 4;
    std::vector<int64_t> cost( states, INT64_MAX );
    std::vector<int> previous( states, -1 );
    using ENTRY = std::pair<int64_t, int>;
    std::priority_queue<ENTRY, std::vector<ENTRY>, std::greater<ENTRY>> open;
    auto state = [&]( int column, int row, int direction ) { return ( row * columns + column ) * 4 + direction; };
    for( int direction = 0; direction < 4; ++direction )
    {
        // Leaving the start another way than it must costs a turn; with no way given, any way is free.
        const int64_t initial = first < 0 || direction == first ? 0 : TURN;
        const int at = state( column0, row0, direction );
        cost[at] = initial; open.push( { initial, at } );
    }
    int reached = -1; int64_t best = INT64_MAX;
    while( !open.empty() )
    {
        auto [here, at] = open.top(); open.pop();
        if( here != cost[at] ) continue;
        const int direction = at % 4, node = at / 4, column = node % columns, row = node / columns;
        if( column == column1 && row == row1 )
        {
            const int64_t total = here + ( last < 0 || direction == last ? 0 : TURN );
            if( total < best ) { best = total; reached = at; }
            continue;
        }
        if( here >= best ) continue;
        for( int next = 0; next < 4; ++next )
        {
            if( ( next ^ 1 ) == direction && ( next >> 1 ) == ( direction >> 1 ) ) continue;
            const int c = column + dx[next], r = row + dy[next];
            if( c < 0 || c >= columns || r < 0 || r >= rows ) continue;
            const wxPoint to( xs[c], ys[r] );
            if( inside( to ) ) continue;
            if( next < 2 ? blocked( xs[column], to.x, ys[row], false ) : blocked( ys[row], to.y, xs[column], true ) ) continue;
            const int64_t step = std::abs( to.x - xs[column] ) + std::abs( to.y - ys[row] ) + ( next == direction ? 0 : TURN );
            const int target = state( c, r, next );
            if( here + step < cost[target] ) { cost[target] = here + step; previous[target] = at; open.push( { cost[target], target } ); }
        }
    }
    if( reached < 0 ) return {};
    std::vector<wxPoint> points;
    for( int at = reached; at >= 0; at = previous[at] )
    {
        const int node = at / 4;
        const wxPoint point( xs[node % columns], ys[node / columns] );
        if( points.empty() || points.back() != point ) points.push_back( point );
    }
    std::reverse( points.begin(), points.end() );
    // Only the corners stay.
    std::vector<wxPoint> corners;
    for( const wxPoint& point : points )
    {
        if( corners.size() >= 2 )
        {
            const wxPoint &a = corners[corners.size() - 2], &b = corners.back();
            if( ( a.x == b.x && b.x == point.x ) || ( a.y == b.y && b.y == point.y ) ) corners.pop_back();
        }
        corners.push_back( point );
    }
    return corners;
}

void LEVEL_LAYOUT::placePaths()
{
    // Rule F4 (revised for design QA P2-5). A leg with a stored route runs through its waypoints; an unlocked channel route
    // (two waypoints on one vertical line) whose ends both leave sideways runs level from each of its ends to its channel, so
    // a route stored against ends that have since moved never draws a slanted leg (F4b). Every other leg is computed
    // (RouteLegs): stored routes are laid out first, then the computed legs in level order (connections in the level's order,
    // each connection's legs in endpoint order).
    std::vector<std::vector<POINT>> stored;
    std::vector<ROUTE_LEG> legs;
    std::vector<std::pair<std::string, int>> keys;
    for( const auto& link : m_links )
        for( int leg = 1; leg < static_cast<int>( link.endpoints.size() ); ++leg )
        {
            const POINT from = EndAnchor( link, 0, leg ), to = EndAnchor( link, leg, leg );
            const auto [fromDx, fromDy] = leaving( link.endpoints[0], from );
            const auto [toDx, toDy] = leaving( link.endpoints[leg], to );
            const D::DiagramConnectionRouteData* route = nullptr;
            for( const auto& row : m_routes )
                if( row.connection_id() == link.id && row.endpoint_index() == static_cast<unsigned>( leg ) ) { route = &row; break; }
            if( route )
            {
                std::vector<POINT> waypoints;
                for( const auto& waypoint : route->waypoints() )
                { POINT point; if( ParseUnits( waypoint.x(), point.x ) && ParseUnits( waypoint.y(), point.y ) ) waypoints.push_back( point ); }
                if( !route->locked() && waypoints.size() == 2 && waypoints[0].x == waypoints[1].x && fromDy == 0 && toDy == 0 )
                { waypoints[0].y = from.y; waypoints[1].y = to.y; }
                std::vector<POINT> points{ from };
                points.insert( points.end(), waypoints.begin(), waypoints.end() );
                points.push_back( to );
                m_paths[{ link.id, leg }] = points; stored.push_back( std::move( points ) );
                continue;
            }
            legs.push_back( { { from, fromDx, fromDy, ownBlock( link.endpoints[0] ) }, { to, toDx, toDy, ownBlock( link.endpoints[leg] ) } } );
            keys.emplace_back( link.id, leg );
        }
    const auto paths = cachedRoutes( m_rects, stored, legs );
    for( size_t i = 0; i < keys.size(); ++i ) m_paths[keys[i]] = paths[i];
}
bool LEVEL_LAYOUT::HasRoute( const std::string& id, int endpoint ) const
{
    return std::any_of( m_routes.begin(), m_routes.end(), [&]( const auto& route )
                        { return route.connection_id() == id && route.endpoint_index() == static_cast<unsigned>( endpoint ); } );
}
std::optional<POINT> LEVEL_LAYOUT::RouteLabel( const std::string& id, int endpoint ) const
{
    for( const auto& route : m_routes )
        if( route.connection_id() == id && route.endpoint_index() == static_cast<unsigned>( endpoint ) && route.has_label() )
        {
            POINT point;
            if( ParseUnits( route.label().x(), point.x ) && ParseUnits( route.label().y(), point.y ) ) return point;
        }
    return std::nullopt;
}
std::vector<POINT> LEVEL_LAYOUT::Route( const LINK& link, int index ) const
{
    if( index < 1 || index >= static_cast<int>( link.endpoints.size() ) ) return {};
    if( auto found = m_paths.find( { link.id, index } ); found != m_paths.end() ) return found->second;
    // A connection that is not on this level: the legacy three-segment path between its anchors.
    POINT from = EndAnchor( link, 0, index ), to = EndAnchor( link, index, index );
    int64_t middle = ( from.x + to.x ) / 2;
    return { from, { middle, from.y }, { middle, to.y }, to };
}
RECT LEVEL_LAYOUT::FallbackFrame() const
{
    int64_t left = LLONG_MAX, top = LLONG_MAX, right = LLONG_MIN, bottom = LLONG_MIN;
    auto include = [&]( int64_t x, int64_t y ) { left = std::min( left, x ); top = std::min( top, y ); right = std::max( right, x ); bottom = std::max( bottom, y ); };
    for( const auto& rect : m_rects ) { include( rect.x, rect.y ); include( rect.Right(), rect.Bottom() ); }
    for( const auto& note : m_notes ) { include( note.x, note.y ); include( note.Right(), note.Bottom() ); }
    for( const auto& port : m_ports ) if( port.boundary ) include( port.anchor.x, port.anchor.y );
    if( left == LLONG_MAX ) return { 0, 0, 800 * QUANTUM, 500 * QUANTUM };
    const int64_t margin = 40 * QUANTUM;
    return { left - margin, top - margin, right - left + 2 * margin, bottom - top + 2 * margin };
}
RECT LEVEL_LAYOUT::Bounds() const
{
    bool anyPlaced = m_frame.has_value() || std::find( m_placed.begin(), m_placed.end(), true ) != m_placed.end();
    int64_t left = 0, top = 0, right, bottom;
    if( !anyPlaced )
    {
        // The legacy extent of an unplaced level: the whole grid and the origin.
        int64_t count = std::max<int64_t>( 1, static_cast<int64_t>( m_nodes.size() ) ), columns = Columns( static_cast<size_t>( count ) );
        int64_t rows = ( count + columns - 1 ) / columns;
        right = ( 140 + columns * 370 ) * QUANTUM; bottom = ( 110 + rows * 250 ) * QUANTUM;
    }
    else
    {
        left = LLONG_MAX; top = LLONG_MAX; right = LLONG_MIN; bottom = LLONG_MIN;
        for( const auto& rect : m_rects )
        { left = std::min( left, rect.x ); top = std::min( top, rect.y ); right = std::max( right, rect.Right() ); bottom = std::max( bottom, rect.Bottom() ); }
        for( const auto& port : m_ports )
        { left = std::min( left, port.anchor.x ); top = std::min( top, port.anchor.y ); right = std::max( right, port.anchor.x ); bottom = std::max( bottom, port.anchor.y ); }
        if( m_frame )
        { left = std::min( left, m_frame->x ); top = std::min( top, m_frame->y ); right = std::max( right, m_frame->Right() ); bottom = std::max( bottom, m_frame->Bottom() ); }
        left -= 40 * QUANTUM; top -= 40 * QUANTUM; right += 40 * QUANTUM; bottom += 40 * QUANTUM;
    }
    for( const auto& note : m_notes )
    {
        left = std::min( left, note.x - 20 * QUANTUM ); top = std::min( top, note.y - 20 * QUANTUM );
        right = std::max( right, note.Right() + 20 * QUANTUM ); bottom = std::max( bottom, note.Bottom() + 20 * QUANTUM );
    }
    return { left, top, std::max<int64_t>( QUANTUM, right - left ), std::max<int64_t>( QUANTUM, bottom - top ) };
}
void LEVEL_LAYOUT::Report( D::ResolvedDiagramLayoutData* out ) const
{
    for( size_t i = 0; i < m_nodes.size(); ++i )
    {
        auto* block = out->add_blocks(); block->set_block_id( m_nodes[i].id ); EncodeRect( m_rects[i], block->mutable_rect() );
        block->set_source( m_placed[i] ? D::RPS_PLACED : D::RPS_FALLBACK );
    }
    for( const auto& port : m_ports )
    {
        auto* row = out->add_ports(); row->set_block_id( port.blockId ); row->set_interface_id( port.interfaceId ); row->set_side( port.side );
        row->set_offset( FormatUnits( port.offset ) ); EncodePoint( port.anchor, row->mutable_anchor() );
        row->set_source( port.placed ? D::RPS_PLACED : D::RPS_FALLBACK );
    }
    for( const auto& link : m_links )
        for( int i = 1; i < static_cast<int>( link.endpoints.size() ); ++i )
        {
            auto* row = out->add_routes(); row->set_connection_id( link.id ); row->set_endpoint_index( static_cast<unsigned>( i ) );
            for( const auto& point : Route( link, i ) ) EncodePoint( point, row->add_points() );
            row->set_source( HasRoute( link.id, i ) ? D::RPS_PLACED : D::RPS_FALLBACK );
        }
    EncodeRect( m_frame ? *m_frame : FallbackFrame(), out->mutable_frame() );
    out->set_frame_source( m_frame ? D::RPS_PLACED : D::RPS_FALLBACK );
    out->set_dormant_entries( m_dormant );
}

const char* FacetName( int facet )
{
    static const char* NAMES[FACETS] = { "purpose", "type", "manufacturer", "family", "model", "orderable-part", "package" };
    return facet >= 0 && facet < FACETS ? NAMES[facet] : "";
}

wxString FacetLabel( int facet )
{
    switch( facet )
    {
    case 0: return _( "Purpose" );
    case 1: return _( "Type" );
    case 2: return _( "Manufacturer" );
    case 3: return _( "Family" );
    case 4: return _( "Model" );
    case 5: return _( "Orderable part" );
    default: return _( "Package" );
    }
}

const D::DefinitionTextChoiceData* Facet( const D::BlockDefinitionData& definition, int facet )
{
    switch( facet )
    {
    case 0: return definition.has_purpose() ? &definition.purpose() : nullptr;
    case 1: return definition.has_type() ? &definition.type() : nullptr;
    case 2: return definition.has_manufacturer() ? &definition.manufacturer() : nullptr;
    case 3: return definition.has_family() ? &definition.family() : nullptr;
    case 4: return definition.has_model() ? &definition.model() : nullptr;
    case 5: return definition.has_orderable_part() ? &definition.orderable_part() : nullptr;
    case 6: return definition.has_package() ? &definition.package() : nullptr;
    default: return nullptr;
    }
}

D::DefinitionTextChoiceData* MutableFacet( D::BlockDefinitionData* definition, int facet )
{
    switch( facet )
    {
    case 0: return definition->mutable_purpose();
    case 1: return definition->mutable_type();
    case 2: return definition->mutable_manufacturer();
    case 3: return definition->mutable_family();
    case 4: return definition->mutable_model();
    case 5: return definition->mutable_orderable_part();
    default: return definition->mutable_package();
    }
}

void ClearFacet( D::BlockDefinitionData* definition, int facet )
{
    switch( facet )
    {
    case 0: definition->clear_purpose(); break;
    case 1: definition->clear_type(); break;
    case 2: definition->clear_manufacturer(); break;
    case 3: definition->clear_family(); break;
    case 4: definition->clear_model(); break;
    case 5: definition->clear_orderable_part(); break;
    default: definition->clear_package(); break;
    }
}

bool HasValue( const D::DefinitionTextChoiceData* choice )
{
    return choice && choice->state() != D::DCSD_UNSPECIFIED;
}

bool EmptyDefinition( const D::BlockDefinitionData& definition )
{
    for( int facet = 0; facet < FACETS; ++facet ) if( Facet( definition, facet ) ) return false;
    return !definition.has_knowledge_class();
}

wxString ChoiceValue( const D::DefinitionTextChoiceData& choice, const wxString& join )
{
    if( choice.state() == D::DCSD_UNKNOWN ) return _( "Unknown" );
    wxString result;
    for( const auto& value : choice.values() ) { if( !result.empty() ) result += join; result += Text( value ); }
    return result;
}

namespace
{
bool darkBackground( const wxColour& colour ) { return colour.Red() + colour.Green() + colour.Blue() < 384; }
wxColour markColour( D::DefinitionChoiceStateData state, bool dark )
{
    if( state == D::DCSD_SELECTED ) return dark ? wxColour( 102, 187, 106 ) : wxColour( 46, 125, 50 );
    if( state == D::DCSD_CANDIDATES ) return dark ? wxColour( 255, 202, 40 ) : wxColour( 176, 128, 0 );
    return dark ? wxColour( 158, 158, 158 ) : wxColour( 117, 117, 117 );
}
wxColour chipFill( D::DefinitionChoiceStateData state, bool dark )
{
    if( state == D::DCSD_SELECTED ) return dark ? wxColour( 30, 58, 34 ) : wxColour( 232, 245, 233 );
    if( state == D::DCSD_CANDIDATES ) return dark ? wxColour( 66, 54, 18 ) : wxColour( 255, 248, 220 );
    return dark ? wxColour( 58, 58, 58 ) : wxColour( 242, 242, 242 );
}
}

void DrawChoiceMark( wxDC& dc, const wxRect& box, D::DefinitionChoiceStateData state, bool dark )
{
    wxColour colour = markColour( state, dark );
    int diameter = std::min( box.width, box.height ), radius = diameter / 2;
    wxPoint centre( box.x + box.width / 2, box.y + box.height / 2 );
    if( state == D::DCSD_SELECTED )
    {
        dc.SetPen( wxPen( colour, 1 ) ); dc.SetBrush( wxBrush( colour ) ); dc.DrawCircle( centre, radius );
        // A white check mark inside the filled circle.
        dc.SetPen( wxPen( *wxWHITE, std::max( 2, diameter / 7 ) ) );
        dc.DrawLine( centre.x - radius / 2, centre.y, centre.x - radius / 8, centre.y + radius / 3 );
        dc.DrawLine( centre.x - radius / 8, centre.y + radius / 3, centre.x + radius / 2, centre.y - radius / 3 );
    }
    else if( state == D::DCSD_CANDIDATES )
    {
        dc.SetPen( wxPen( colour, std::max( 2, diameter / 6 ) ) ); dc.SetBrush( *wxTRANSPARENT_BRUSH );
        dc.DrawCircle( centre, std::max( 1, radius - 1 ) );
    }
    else
    {
        dc.SetPen( wxPen( colour, 1 ) ); dc.SetBrush( wxBrush( colour ) ); dc.DrawCircle( centre, std::max( 1, radius - 1 ) );
    }
}

CAPTION BlockCaption( wxDC& dc, const NODE& node, const wxRect& inner, const wxFont& captionFont )
{
    // Design QA P2-7: text keeps its size while a zoomed-out block shrinks, so the caption moves up (a third of the way down a
    // small block, 24 pixels down a full one), is shortened with "…" to the block's width, and is drawn whole or not at all.
    dc.SetFont( captionFont );
    CAPTION caption;
    const int height = dc.GetCharHeight(), left = inner.x + 10, width = inner.GetRight() - 4 - left + 1;
    const int top = inner.y + std::clamp( inner.height / 3 - height / 2, 0, 24 );
    if( width < dc.GetTextExtent( wxS( "W…" ) ).x || top + height > inner.GetBottom() + 1 ) return caption;
    caption.text = wxControl::Ellipsize( Text( node.name ), dc, wxELLIPSIZE_END, width );
    caption.rect = wxRect( wxPoint( left, top ), wxSize( dc.GetTextExtent( caption.text ).x, height ) );
    return caption;
}

wxRect CaptionRect( wxDC& dc, const NODE& node, const wxRect& inner, const wxFont& captionFont )
{
    return BlockCaption( dc, node, inner, captionFont ).rect;
}

wxRect VersionRect( wxDC& dc, const wxString& text, const wxRect& inner, const wxRect& caption )
{
    if( caption.IsEmpty() ) return wxRect();
    // 34 pixels below the caption's top, as a full block has always drawn it (58 pixels down its content area).
    wxRect line( wxPoint( inner.x + 10, caption.y + 34 ), dc.GetTextExtent( text ) );
    return inner.Contains( line ) ? line : wxRect();
}

std::vector<wxString> NoteLines( wxDC& dc, const wxString& text, int width, int height )
{
    std::vector<wxString> lines;
    wxString value = text; int y = 0;
    while( !value.empty() && y + dc.GetCharHeight() <= height )
    {
        size_t end = value.find( '\n' ); if( end == wxString::npos ) end = value.length();
        size_t count = end;
        while( count > 0 && dc.GetTextExtent( value.Left( count ) ).x > width ) --count;
        // Keep words whole: break at the last space that fits. Only a word wider than the note breaks inside it.
        bool wrapped = count < end;
        if( wrapped )
            if( size_t space = value.Left( count + 1 ).find_last_of( ' ' ); space != wxString::npos && space > 0 ) count = space;
        if( count == 0 && value[0] != '\n' ) count = 1;
        wxString line = value.Left( count ); value = value.Mid( count );
        if( value.StartsWith( "\n" ) ) value = value.Mid( 1 );
        else if( wrapped )
        {
            // The spaces a line breaks at are not drawn, and neither is a line break that follows them.
            while( value.StartsWith( " " ) ) value = value.Mid( 1 );
            if( value.StartsWith( "\n" ) ) value = value.Mid( 1 );
        }
        if( !value.empty() && y + dc.GetCharHeight() * 2 > height )
        { while( !line.empty() && dc.GetTextExtent( line + wxS( "…" ) ).x > width ) line.RemoveLast(); line += wxS( "…" ); }
        lines.push_back( line ); y += dc.GetCharHeight();
    }
    return lines;
}

wxRect PortNameRect( wxDC& dc, const wxString& name, D::DiagramPortSide side, const wxPoint& at )
{
    // Clear of the port's 12-pixel square (design QA P2-6) by 4 pixels.
    wxSize extent = dc.GetTextExtent( name );
    switch( side )
    {
    case D::DPS_RIGHT: return wxRect( wxPoint( at.x - extent.x - 10, at.y - extent.y / 2 ), extent );
    case D::DPS_TOP: return wxRect( wxPoint( at.x - extent.x / 2, at.y + 10 ), extent );
    case D::DPS_BOTTOM: return wxRect( wxPoint( at.x - extent.x / 2, at.y - extent.y - 10 ), extent );
    default: return wxRect( wxPoint( at.x + 10, at.y - extent.y / 2 ), extent );
    }
}

BLOCK_CHIPS LayoutChips( wxDC& dc, const NODE& node, const wxRect& box, const wxFont& small, const wxRect& caption,
                         const std::vector<wxRect>& portNames )
{
    BLOCK_CHIPS result; result.caption = caption; result.portNames = portNames;
    const int captionRight = caption.IsEmpty() ? box.x : caption.GetRight() + 1;
    std::vector<int> chosen;
    for( int facet = 0; facet < FACETS; ++facet )
    {
        const auto* choice = Facet( node.definition, facet );
        result.shown |= HasValue( choice );
        if( choice && ( choice->state() == D::DCSD_SELECTED || choice->state() == D::DCSD_CANDIDATES ) && choice->values_size() )
            chosen.push_back( facet );
    }
    if( !result.shown ) return result;
    dc.SetFont( small );
    // Below the caption: one chip per row, and the Review facets link at the bottom of the block.
    // aBox is the block's content area: the caption sits at its top (24 to about 46 pixels down a full block, higher in a
    // small one), and the chips follow 26 pixels below its top.
    const int height = dc.GetCharHeight() + 6, gap = 3, left = box.x + 10, width = box.width - 20;
    const int top = caption.IsEmpty() ? box.y + 50 : caption.y + 26;
    const int mark = height - 9, textLeft = 6 + mark + 6;
    // The part of a row from aFrom to aTo that is clear of the port names drawn inside the block edge: a name in the
    // right half of the block ends the row before it, one in the left half starts the row after it.
    auto span = [&]( int y, int h, int from, int to ) -> std::pair<int, int>
    {
        for( const auto& name : portNames )
            if( name.y < y + h && name.GetBottom() >= y )
            {
                if( name.x + name.width / 2 >= box.x + box.width / 2 ) to = std::min( to, name.x - gap );
                else from = std::max( from, name.GetRight() + 1 + gap );
            }
        return { from, std::max( from, to ) };
    };
    auto rowTop = [&]( size_t row ) { return top + static_cast<int>( row ) * ( height + gap ); };
    auto rowSpan = [&]( size_t row ) { return span( rowTop( row ), height, left, left + width ); };
    int bottom = box.GetBottom() - 1;
    wxSize linkSize = dc.GetTextExtent( _( "Review facets" ) );
    if( bottom - linkSize.y >= top )
    {
        auto [from, to] = span( bottom - linkSize.y, linkSize.y, left, left + width );
        if( linkSize.x <= to - from ) { result.link = wxRect( from, bottom - linkSize.y, linkSize.x, linkSize.y ); bottom = result.link->y - gap; }
    }
    int rows = bottom - top >= height ? ( bottom - top + gap ) / ( height + gap ) : 0;
    auto moreText = []( size_t count ) { return wxString::Format( _( "+%u more" ), static_cast<unsigned>( count ) ); };
    auto rowWidth = [&]( size_t row ) { auto [from, to] = rowSpan( row ); return to - from; };
    // Every chip shows its whole "Facet: value" and is at least its state mark and 40 pixels of text wide (padded when its
    // text is shorter). A chip is never cut short: "Family: T…" no longer showed the value the chip exists for (design QA round
    // 2, R2-P2-6). The chips take the rows in facet order, one per row; a chip wider than the row it would take goes behind
    // "+N more", and the next chip tries that row. "+N more"
    // shares the last chip's row when both fit whole beside each other, and otherwise takes the next row; when no row is left
    // for it, the last chip goes behind it as well.
    const int minimumText = 40;
    auto chipText = [&]( int facet ) { return FacetLabel( facet ) + wxS( ": " ) + ChoiceValue( *Facet( node.definition, facet ), wxS( " / " ) ); };
    auto chipWidth = [&]( int facet ) { return textLeft + std::max( minimumText, dc.GetTextExtent( chipText( facet ) ).x ) + 8; };
    auto moreWidthFor = [&]( size_t count ) { return dc.GetTextExtent( moreText( count ) ).x + 16; };
    const size_t rowCount = static_cast<size_t>( std::max( 0, rows ) );
    std::vector<int> placed, behind;
    for( int facet : chosen )
    {
        if( placed.size() < rowCount && chipWidth( facet ) <= rowWidth( placed.size() ) ) placed.push_back( facet );
        else behind.push_back( facet );
    }
    while( !behind.empty() )
    {
        const int moreWidth = moreWidthFor( behind.size() );
        if( !placed.empty() )
        {
            auto [from, to] = rowSpan( placed.size() - 1 );
            const int x = from + chipWidth( placed.back() ) + gap;
            if( x + moreWidth <= to ) { result.more = wxRect( x, rowTop( placed.size() - 1 ), moreWidth, height ); break; }
        }
        if( placed.size() < rowCount && moreWidth <= rowWidth( placed.size() ) )
        { result.more = wxRect( rowSpan( placed.size() ).first, rowTop( placed.size() ), moreWidth, height ); break; }
        if( placed.empty() ) break;
        behind.push_back( placed.back() ); placed.pop_back();
        std::sort( behind.begin(), behind.end() );
    }
    for( size_t i = 0; i < placed.size(); ++i )
    {
        const auto& choice = *Facet( node.definition, placed[i] );
        result.chips.push_back( { placed[i], choice.state(), chipText( placed[i] ), wxRect( rowSpan( i ).first, rowTop( i ), chipWidth( placed[i] ), height ) } );
    }
    result.hidden = static_cast<unsigned>( behind.size() );
    result.hiddenFacets = behind;
    if( result.chips.empty() && !result.more && !chosen.empty() && !caption.IsEmpty() )
    {
        // Too small for a chip: the choices' state marks follow the caption, level with it, where they fit. They keep 6 pixels
        // from the block's edge handles and from the port names inside the block (design QA P2-7).
        const int size = std::max( 6, std::min( 12, mark ) ), step = size + 3, clear = 6;
        const int y = caption.y + ( caption.height - size ) / 2;
        int from = std::max( captionRight + 6, left ), to = box.GetRight() - 2;
        for( const auto& name : portNames )
            if( name.y < y + size + clear && name.GetBottom() + clear >= y )
            {
                if( name.x + name.width / 2 >= box.x + box.width / 2 ) to = std::min( to, name.x - clear );
                else from = std::max( from, name.GetRight() + 1 + clear );
            }
        int x = to - static_cast<int>( chosen.size() ) * step;
        if( x >= from && y >= box.y && y + size <= box.GetBottom() )
            for( size_t i = 0; i < chosen.size(); ++i )
                result.marks.push_back( { chosen[i], Facet( node.definition, chosen[i] )->state(), wxRect( x + static_cast<int>( i ) * step, y, size, size ) } );
    }
    return result;
}

wxString MoreToolTip( const NODE& node, const BLOCK_CHIPS& chips )
{
    // The facet overview's words for a choice: its value, and "(candidate)" for a candidate; a chosen value says so as well.
    wxString tip;
    for( int facet : chips.hiddenFacets )
        if( const auto* choice = Facet( node.definition, facet ) )
        {
            wxString line = FacetLabel( facet ) + wxS( ": " ) + ChoiceValue( *choice, wxS( ", " ) );
            line += choice->state() == D::DCSD_CANDIDATES ? ( choice->values_size() == 1 ? _( " (candidate)" ) : _( " (candidates)" ) )
                                                          : _( " (chosen)" );
            if( !tip.empty() ) tip += wxS( "\n" );
            tip += line;
        }
    return tip;
}

void DrawChips( wxDC& dc, const BLOCK_CHIPS& chips, const wxFont& small, bool dark, const wxColour& foreground, const wxColour& link )
{
    if( !chips.shown ) return;
    dc.SetFont( small );
    for( const auto& chip : chips.chips )
    {
        dc.SetPen( wxPen( markColour( chip.state, dark ), 1 ) ); dc.SetBrush( wxBrush( chipFill( chip.state, dark ) ) );
        dc.DrawRoundedRectangle( chip.rect, 4 );
        int mark = chip.rect.height - 9;
        DrawChoiceMark( dc, wxRect( chip.rect.x + 6, chip.rect.y + ( chip.rect.height - mark ) / 2, mark, mark ), chip.state, dark );
        dc.SetTextForeground( foreground );
        dc.DrawText( chip.text, chip.rect.x + 6 + mark + 6, chip.rect.y + ( chip.rect.height - dc.GetCharHeight() ) / 2 );
    }
    if( chips.more )
    {
        dc.SetPen( wxPen( markColour( D::DCSD_UNKNOWN, dark ), 1 ) ); dc.SetBrush( wxBrush( chipFill( D::DCSD_UNKNOWN, dark ) ) );
        dc.DrawRoundedRectangle( *chips.more, 4 ); dc.SetTextForeground( foreground );
        dc.DrawText( wxString::Format( _( "+%u more" ), chips.hidden ), chips.more->x + 8, chips.more->y + ( chips.more->height - dc.GetCharHeight() ) / 2 );
    }
    for( const auto& mark : chips.marks ) DrawChoiceMark( dc, mark.rect, mark.state, dark );
    if( chips.link )
    {
        wxFont underlined = small; underlined.SetUnderlined( true ); dc.SetFont( underlined ); dc.SetTextForeground( link );
        dc.DrawText( _( "Review facets" ), chips.link->GetTopLeft() );
        dc.SetFont( small );
    }
    dc.SetTextForeground( foreground );
}

FACET_ROW::FACET_ROW( wxWindow* parent, int facet, std::function<void( int )> open ) :
        wxToggleButton( parent, wxID_ANY, wxEmptyString, wxDefaultPosition, wxDefaultSize, wxBORDER_NONE ),
        m_facet( facet ), m_open( std::move( open ) )
{
    static const char* NAMES[FACETS] = { "Purpose", "Type", "Manufacturer", "Family", "Model", "OrderablePart", "Package" };
    SetName( wxString( "RecursiveFacetRow" ) + NAMES[std::clamp( facet, 0, FACETS - 1 )] );
    // Pressing the row opens its facet; the row stays pressed exactly while that facet's detail is open (SetChoice).
    Bind( wxEVT_TOGGLEBUTTON, [this]( wxCommandEvent& )
          {
              SetValue( m_isOpen );
              if( m_open ) m_open( m_facet );
          } );
#if defined( __WXGTK__ )
    PaintNatively( this, this );
#endif
    SetMinSize( DoGetBestSize() );
}

wxSize FACET_ROW::DoGetBestSize() const
{
#if defined( __WXGTK__ )
    return FromDIP( wxSize( 200, 28 ) );
#else
    return wxToggleButton::DoGetBestSize();
#endif
}

void FACET_ROW::SetChoice( const D::DefinitionTextChoiceData& choice, bool open )
{
    wxString value = ChoiceValue( choice, wxS( ", " ) );
    m_bareValue = value;
    if( choice.state() == D::DCSD_CANDIDATES )
        value += choice.values_size() == 1 ? _( " (candidate)" ) : _( " (candidates)" );
    if( GetValue() != open ) SetValue( open );
    if( value == m_value && choice.state() == m_state && open == m_isOpen ) return;
    m_value = value; m_state = choice.state(); m_isOpen = open;
    // The label is the row's accessible name: its facet and value. GTK makes a toggle button's label a mnemonic label, so an
    // "&" in a value (a manufacturer such as "C&K") is escaped: it is read as written and opens nothing.
    SetLabel( wxControl::EscapeMnemonics( FacetLabel( m_facet ) + wxS( ": " ) + value ) ); SetToolTip( value );
    SetMinSize( DoGetBestSize() );
    Refresh();
}

void FACET_ROW::PaintButton( wxDC& dc, bool hover, bool )
{
    wxColour background = GetParent()->GetBackgroundColour();
    wxColour foreground = wxSystemSettings::GetColour( wxSYS_COLOUR_WINDOWTEXT );
    wxColour muted = wxSystemSettings::GetColour( wxSYS_COLOUR_GRAYTEXT );
    wxColour accent = wxSystemSettings::GetColour( wxSYS_COLOUR_HIGHLIGHT );
    bool dark = darkBackground( background );
    wxRect area( GetClientSize() );
    dc.SetPen( *wxTRANSPARENT_PEN ); dc.SetBrush( wxBrush( background ) ); dc.DrawRectangle( area );
    if( m_isOpen || hover )
    {
        dc.SetBrush( wxBrush( m_isOpen ? accent.ChangeLightness( dark ? 60 : 180 ) : background.ChangeLightness( dark ? 115 : 95 ) ) );
        dc.DrawRectangle( area );
    }
    // A thin rule below each row, as the sketch's table.
    dc.SetPen( wxPen( background.ChangeLightness( dark ? 135 : 85 ), 1 ) );
    dc.DrawLine( area.x, area.GetBottom(), area.GetRight() + 1, area.GetBottom() );
    dc.SetFont( GetFont() );
    // The label column fits the longest facet name, so every row's value starts at the same place. Facet names use the
    // normal text colour, so a row never looks disabled (design QA P3 17).
    int text = ( area.height - dc.GetCharHeight() ) / 2, labelWidth = FromDIP( 90 );
    for( int facet = 0; facet < FACETS; ++facet ) labelWidth = std::max( labelWidth, dc.GetTextExtent( FacetLabel( facet ) ).x + FromDIP( 20 ) );
    dc.SetTextForeground( foreground );
    dc.DrawText( wxControl::Ellipsize( FacetLabel( m_facet ), dc, wxELLIPSIZE_END, labelWidth - FromDIP( 12 ) ), FromDIP( 6 ), text );
    int mark = std::min( FromDIP( 14 ), area.height - FromDIP( 10 ) );
    DrawChoiceMark( dc, wxRect( labelWidth, ( area.height - mark ) / 2, mark, mark ), m_state, dark );
    // A text-size chevron at 3:1 or better on the row and on the open row's fill shows that the row opens its facet
    // (design QA P2-11).
    const int chevronWidth = FromDIP( 6 ), chevronHeight = FromDIP( 11 );
    int valueLeft = labelWidth + mark + FromDIP( 6 ), valueWidth = area.width - valueLeft - chevronWidth - FromDIP( 18 );
    dc.SetTextForeground( foreground );
    // A value too long for its column first drops its "(candidate)" suffix, which the ring already shows, so the value itself
    // is not cut mid-word (design QA round 2, P3 11); only a value that still does not fit is shortened.
    wxString shown = dc.GetTextExtent( m_value ).x <= valueWidth ? m_value : m_bareValue;
    dc.DrawText( wxControl::Ellipsize( shown, dc, wxELLIPSIZE_END, std::max( 0, valueWidth ) ), valueLeft, text );
    wxColour open = accent.ChangeLightness( dark ? 60 : 180 ), hovered = background.ChangeLightness( dark ? 115 : 95 );
    wxColour chevron = Readable( muted, { background, open, hovered }, 3.5 );
    {
        const int right = area.width - FromDIP( 9 ), middle = area.height / 2;
        const wxPoint points[] = { { right - chevronWidth, middle - chevronHeight / 2 }, { right, middle }, { right - chevronWidth, middle + chevronHeight / 2 } };
        dc.SetPen( wxPen( chevron, FromDIP( 2 ) ) ); dc.DrawLines( 3, points );
    }
    if( HasFocus() )
    {
        dc.SetPen( wxPen( accent, 1, wxPENSTYLE_DOT ) ); dc.SetBrush( *wxTRANSPARENT_BRUSH );
        dc.DrawRectangle( area.Deflate( 1 ) );
    }
}

const char* ToolName( TOOL tool )
{
    switch( tool )
    {
    case TOOL::ADD_BLOCK: return "add-block";
    case TOOL::CONNECT: return "connect";
    case TOOL::ADD_PORT: return "add-port";
    case TOOL::NOTE: return "note";
    default: return "select";
    }
}

namespace
{
/// The glyph size of every drawing tool, and the gap to its label (the toolbar's own tools leave 4 pixels).
constexpr int GLYPH_DIP = 24;
int glyphGap( TOOL_STYLE style ) { return style == TOOL_STYLE::STRIP ? 4 : 3; }

#if defined( __WXGTK__ )
/// A GTK, GLib or ATK function of the toolkit this build already runs on, looked up at run time so no GTK header is needed.
template <typename FUNCTION>
FUNCTION toolkit( const char* aName )
{
    return reinterpret_cast<FUNCTION>( dlsym( RTLD_DEFAULT, aName ) );
}
constexpr unsigned GTK_STATE_ACTIVE = 1u << 0, GTK_STATE_PRELIGHT = 1u << 1;
void* accessibleOf( wxWindow* window )
{
    static const auto get = toolkit<void* ( * )( void* )>( "gtk_widget_get_accessible" );
    return get && window && window->GetHandle() ? get( window->GetHandle() ) : nullptr;
}
int drawButton( void* widget, void* cairo, void* painter )
{
    // GTK's own drawing of the button is replaced by the editor's: returning true ends the draw signal before GTK paints
    // the theme's frame and label.
    static const auto flagsOf = toolkit<unsigned ( * )( void* )>( "gtk_widget_get_state_flags" );
    static const auto scaleOf = toolkit<int ( * )( void* )>( "gtk_widget_get_scale_factor" );
    wxGraphicsRenderer* renderer = wxGraphicsRenderer::GetCairoRenderer();
    wxGraphicsContext* context = renderer ? renderer->CreateContextFromNativeContext( cairo ) : nullptr;
    if( !context ) return 0;
    // As the toolkit's own paint DC does, one-pixel lines fall on whole pixels (at the display's own scale).
    context->EnableOffset( !scaleOf || scaleOf( widget ) <= 1 );
    unsigned flags = flagsOf ? flagsOf( widget ) : 0;
    wxGCDC dc( context );
    static_cast<BUTTON_PAINTER*>( painter )->PaintButton( dc, ( flags & GTK_STATE_PRELIGHT ) != 0, ( flags & GTK_STATE_ACTIVE ) != 0 );
    return 1;
}
void redrawButton( void* widget, unsigned, void* )
{
    static const auto queue = toolkit<void ( * )( void* )>( "gtk_widget_queue_draw" );
    if( queue ) queue( widget );
}
#endif
}

void PaintNatively( wxWindow* aButton, BUTTON_PAINTER* aPainter )
{
#if defined( __WXGTK__ )
    using CONNECT = unsigned long ( * )( void*, const char*, void*, void*, void*, int );
    static const auto connect = toolkit<CONNECT>( "g_signal_connect_data" );
    static const auto focusOnClick = toolkit<void ( * )( void*, int )>( "gtk_widget_set_focus_on_click" );
    static const auto provider = toolkit<void* ( * )()>( "gtk_css_provider_new" );
    static const auto load = toolkit<int ( * )( void*, const char*, long, void** )>( "gtk_css_provider_load_from_data" );
    static const auto styleOf = toolkit<void* ( * )( void* )>( "gtk_widget_get_style_context" );
    static const auto addProvider = toolkit<void ( * )( void*, void*, unsigned )>( "gtk_style_context_add_provider" );
    static const auto unref = toolkit<void ( * )( void* )>( "g_object_unref" );
    void* widget = aButton->GetHandle();
    if( !widget || !connect ) return;
    connect( widget, "draw", reinterpret_cast<void*>( &drawButton ), aPainter, nullptr, 0 );
    connect( widget, "state-flags-changed", reinterpret_cast<void*>( &redrawButton ), nullptr, nullptr, 0 );
    // Like the toolbar's own tools, a press leaves the keyboard focus where it was (the canvas); Tab still reaches the button.
    if( focusOnClick ) focusOnClick( widget, 0 );
    // Without the theme's button padding, border and minimum size, the button is exactly the size the editor draws.
    if( provider && load && styleOf && addProvider && unref )
        if( void* css = provider() )
        {
            static const char RESET[] = "* { padding: 0; margin: 0; border-width: 0; min-width: 0; min-height: 0; box-shadow: none; }";
            if( load( css, RESET, -1, nullptr ) ) addProvider( styleOf( widget ), css, 800 );
            unref( css );
        }
#else
    wxUnusedVar( aButton ); wxUnusedVar( aPainter );
#endif
}

wxColour DrawnSurface( wxWindow* aControl )
{
#if defined( __WXGTK__ )
    // GTK draws a plain window with the background of the first widget above it whose theme gives it one, not with the colour
    // wxWidgets reports for it: the inspector reports (241, 240, 238) in the light theme while GTK draws the window's
    // (246, 245, 244) (review of design QA round 2).
    struct RGBA { double red, green, blue, alpha; };
    static const auto parentOf = toolkit<void* ( * )( void* )>( "gtk_widget_get_parent" );
    static const auto styleOf = toolkit<void* ( * )( void* )>( "gtk_widget_get_style_context" );
    static const auto stateOf = toolkit<unsigned ( * )( void* )>( "gtk_style_context_get_state" );
    static const auto backgroundOf = toolkit<void ( * )( void*, unsigned, RGBA* )>( "gtk_style_context_get_background_color" );
    if( aControl && aControl->GetHandle() && parentOf && styleOf && stateOf && backgroundOf )
        for( void* widget = parentOf( aControl->GetHandle() ); widget; widget = parentOf( widget ) )
        {
            void* style = styleOf( widget );
            RGBA colour{ 0, 0, 0, 0 };
            if( style ) backgroundOf( style, stateOf( style ), &colour );
            if( colour.alpha >= 0.99 )
            {
                auto channel = []( double value ) { return static_cast<unsigned char>( std::lround( std::clamp( value, 0.0, 1.0 ) * 255 ) ); };
                return wxColour( channel( colour.red ), channel( colour.green ), channel( colour.blue ) );
            }
        }
#endif
    return aControl && aControl->GetParent() ? aControl->GetParent()->GetBackgroundColour() : wxSystemSettings::GetColour( wxSYS_COLOUR_BTNFACE );
}

void SetAccessibleRole( wxWindow* aWindow, const char* aRole, const wxString& aName )
{
#if defined( __WXGTK__ )
    static const auto roleFor = toolkit<int ( * )( const char* )>( "atk_role_for_name" );
    static const auto setRole = toolkit<void ( * )( void*, int )>( "atk_object_set_role" );
    static const auto setName = toolkit<void ( * )( void*, const char* )>( "atk_object_set_name" );
    void* accessible = accessibleOf( aWindow );
    if( !accessible ) return;
    if( aRole && roleFor && setRole )
    {
        // ATK 2.36 renamed "push button" to "button"; an older ATK knows only the old name.
        int role = roleFor( aRole );
        if( role == 0 && std::string( aRole ) == "button" ) role = roleFor( "push button" );
        if( role != 0 ) setRole( accessible, role );
    }
    if( !aName.empty() && setName ) setName( accessible, aName.utf8_str() );
#else
    wxUnusedVar( aWindow ); wxUnusedVar( aRole ); wxUnusedVar( aName );
#endif
}

std::optional<ACCESSIBLE> AccessibleOf( wxWindow* aWindow )
{
#if defined( __WXGTK__ )
    static const auto getRole = toolkit<int ( * )( void* )>( "atk_object_get_role" );
    static const auto roleName = toolkit<const char* ( * )( int )>( "atk_role_get_name" );
    static const auto getName = toolkit<const char* ( * )( void* )>( "atk_object_get_name" );
    static const auto states = toolkit<void* ( * )( void* )>( "atk_object_ref_state_set" );
    static const auto stateFor = toolkit<int ( * )( const char* )>( "atk_state_type_for_name" );
    static const auto contains = toolkit<int ( * )( void*, int )>( "atk_state_set_contains_state" );
    static const auto unref = toolkit<void ( * )( void* )>( "g_object_unref" );
    void* accessible = accessibleOf( aWindow );
    if( !accessible || !getRole || !roleName || !getName ) return std::nullopt;
    ACCESSIBLE result;
    if( const char* role = roleName( getRole( accessible ) ) ) result.role = role;
    if( const char* name = getName( accessible ) ) result.name = name;
    if( states && stateFor && contains && unref )
        if( void* set = states( accessible ) )
        {
            result.checked = contains( set, stateFor( "checked" ) ) != 0;
            unref( set );
        }
    return result;
#else
    wxUnusedVar( aWindow );
    return std::nullopt;
#endif
}

wxSize TOOL_FACE::BestSize( const wxWindow* aWindow ) const
{
    const wxSize text = aWindow->GetTextExtent( label );
    const int glyphSize = aWindow->FromDIP( GLYPH_DIP ), padding = aWindow->FromDIP( style == TOOL_STYLE::STRIP ? 5 : 6 );
    return wxSize( std::max( glyphSize, text.x ) + 2 * aWindow->FromDIP( 8 ), glyphSize + aWindow->FromDIP( glyphGap( style ) ) + text.y + 2 * padding );
}

wxRect TOOL_FACE::GlyphRect( const wxWindow* aWindow ) const
{
    // The glyph and label are centred as one block, as the toolbar centres its own tools, so the strip's labels share their
    // baseline (design QA P2-2).
    const wxSize area = aWindow->GetClientSize(), text = aWindow->GetTextExtent( label );
    const int glyphSize = aWindow->FromDIP( GLYPH_DIP ), top = ( area.y - ( glyphSize + aWindow->FromDIP( glyphGap( style ) ) + text.y ) ) / 2;
    return wxRect( ( area.x - glyphSize ) / 2, top, glyphSize, glyphSize );
}

wxRect TOOL_FACE::LabelRect( const wxWindow* aWindow ) const
{
    const wxSize area = aWindow->GetClientSize(), text = aWindow->GetTextExtent( label );
    const wxRect box = GlyphRect( aWindow );
    return wxRect( ( area.x - text.x ) / 2, box.GetBottom() + 1 + aWindow->FromDIP( glyphGap( style ) ), text.x, text.y );
}

void TOOL_FACE::Paint( wxDC& dc, const wxWindow* aWindow, bool aActive, bool aHover, bool aDown ) const
{
    const wxRect area( aWindow->GetClientSize() );
    const bool palette = style == TOOL_STYLE::PALETTE, enabled = aWindow->IsEnabled();
    const wxColour surface = palette ? TOOL_PALETTE::Surface() : wxSystemSettings::GetColour( wxSYS_COLOUR_BTNFACE );
    const bool dark = IsDark( surface );
    const wxColour highlight = wxSystemSettings::GetColour( wxSYS_COLOUR_HIGHLIGHT );
    wxColour content = enabled ? Readable( wxSystemSettings::GetColour( wxSYS_COLOUR_BTNTEXT ), { surface }, 4.5 )
                               : wxSystemSettings::GetColour( wxSYS_COLOUR_GRAYTEXT );
    // The palette cell paints the palette's one surface; the strip keeps the toolbar's own background.
    if( palette ) { dc.SetPen( *wxTRANSPARENT_PEN ); dc.SetBrush( wxBrush( surface ) ); dc.DrawRectangle( area ); }
    const wxRect tile = wxRect( area ).Deflate( aWindow->FromDIP( palette ? 2 : 1 ) );
    const int radius = aWindow->FromDIP( palette ? 6 : 4 );
    if( enabled && aActive )
    {
        // The active tool (design QA P2-1): a solid accent cell with a white or dark label in the palette (sketch 2); a pale
        // accent tile with an accent border, glyph and label in the strip (sketch 1). Each reaches 3:1 for the tile and 4.5:1
        // for its label.
        if( palette )
        {
            ACCENT_FILL fill = AccentFill( highlight, surface );
            dc.SetPen( *wxTRANSPARENT_PEN ); dc.SetBrush( wxBrush( fill.fill ) ); dc.DrawRoundedRectangle( tile, radius );
            content = fill.text;
        }
        else
        {
            wxColour pale = mix( highlight, surface, dark ? 0.30 : 0.14 );
            dc.SetPen( wxPen( Readable( highlight, { surface, pale }, 3.0 ), aWindow->FromDIP( 1 ) ) ); dc.SetBrush( wxBrush( pale ) );
            dc.DrawRoundedRectangle( tile, radius );
            content = Readable( highlight, { pale }, 4.5 );
        }
    }
    else if( enabled && ( aDown || aHover ) )
    {
        dc.SetPen( *wxTRANSPARENT_PEN );
        dc.SetBrush( wxBrush( surface.ChangeLightness( dark ? ( aDown ? 140 : 122 ) : ( aDown ? 86 : 93 ) ) ) );
        dc.DrawRoundedRectangle( tile, radius );
    }
    DrawGlyph( dc, glyph, GlyphRect( aWindow ), content );
    dc.SetFont( aWindow->GetFont() ); dc.SetTextForeground( content ); dc.DrawText( label, LabelRect( aWindow ).GetTopLeft() );
    if( aWindow->HasFocus() )
    {
        dc.SetPen( wxPen( Readable( highlight, { surface }, 3.0 ), 1, wxPENSTYLE_DOT ) ); dc.SetBrush( *wxTRANSPARENT_BRUSH );
        dc.DrawRoundedRectangle( wxRect( tile ).Deflate( aWindow->FromDIP( 2 ) ), radius );
    }
}

namespace
{
/// The palette's cells are icon-first: a 9-point label under the 24 DIP glyph (design QA P2-2).
void smallLabel( wxWindow* window, TOOL_STYLE style )
{
    if( style != TOOL_STYLE::PALETTE ) return;
    wxFont small = window->GetFont(); small.SetPointSize( std::min( 9, small.GetPointSize() ) ); window->SetFont( small );
}
#if !defined( __WXGTK__ )
/// Where the editor cannot paint a platform button itself, the button shows the tool's glyph above its label.
wxBitmap glyphBitmap( wxWindow* window, GLYPH glyph )
{
    const int size = window->FromDIP( GLYPH_DIP );
    wxImage image( size, size );
    image.InitAlpha();
    std::fill( image.GetAlpha(), image.GetAlpha() + size * size, static_cast<unsigned char>( 0 ) );
    {
        wxGCDC dc( wxGraphicsContext::Create( image ) );
        DrawGlyph( dc, glyph, wxRect( 0, 0, size, size ), wxSystemSettings::GetColour( wxSYS_COLOUR_BTNTEXT ) );
    }
    return wxBitmap( image );
}
#endif
}

TOOL_BUTTON::TOOL_BUTTON( wxWindow* parent, const wxString& label, GLYPH glyph, TOOL_STYLE style, const char* name, const wxString& toolTip ) :
        wxToggleButton( parent, wxID_ANY, label, wxDefaultPosition, wxDefaultSize, wxBORDER_NONE, wxDefaultValidator, name ),
        m_face{ label, glyph, style }
{
    // The platform's own toggle button, so assistive technology reads its role, its label as its name and whether it is
    // pressed; on GTK the editor paints it (design QA P2-1 to P2-3).
    SetToolTip( toolTip ); smallLabel( this, style );
#if defined( __WXGTK__ )
    PaintNatively( this, this );
#else
    SetBitmap( glyphBitmap( this, glyph ) ); SetBitmapPosition( wxTOP );
#endif
    SetInitialSize( DoGetBestSize() );
}

void TOOL_BUTTON::PaintButton( wxDC& aDC, bool aHover, bool aDown ) { m_face.Paint( aDC, this, GetValue(), aHover, aDown ); }

wxSize TOOL_BUTTON::DoGetBestSize() const
{
#if defined( __WXGTK__ )
    return m_face.BestSize( this );
#else
    return wxToggleButton::DoGetBestSize();
#endif
}

TOOL_ACTION::TOOL_ACTION( wxWindow* parent, const wxString& label, GLYPH glyph, TOOL_STYLE style, const char* name, const wxString& toolTip ) :
        wxButton( parent, wxID_ANY, label, wxDefaultPosition, wxDefaultSize, wxBORDER_NONE, wxDefaultValidator, name ),
        m_face{ label, glyph, style }
{
    SetToolTip( toolTip ); smallLabel( this, style );
#if defined( __WXGTK__ )
    PaintNatively( this, this );
#else
    SetBitmap( glyphBitmap( this, glyph ) ); SetBitmapPosition( wxTOP );
#endif
    SetInitialSize( DoGetBestSize() );
}

void TOOL_ACTION::PaintButton( wxDC& aDC, bool aHover, bool aDown ) { m_face.Paint( aDC, this, false, aHover, aDown ); }

wxSize TOOL_ACTION::DoGetBestSize() const
{
#if defined( __WXGTK__ )
    return m_face.BestSize( this );
#else
    return wxButton::DoGetBestSize();
#endif
}

LINK_BUTTON::LINK_BUTTON( wxWindow* parent, const wxString& label, const char* name ) :
        wxButton( parent, wxID_ANY, label, wxDefaultPosition, wxDefaultSize, wxBORDER_NONE | wxBU_EXACTFIT, wxDefaultValidator, name )
{
    // A platform button that assistive technology announces as a link, with its label as its name (design QA P2-10).
    SetCursor( wxCursor( wxCURSOR_HAND ) );
#if defined( __WXGTK__ )
    PaintNatively( this, this );
#endif
    SetAccessibleRole( this, "link", wxEmptyString );
    SetInitialSize( DoGetBestSize() );
}

wxColour LINK_BUTTON::LinkColour( const wxColour& surface )
{
    wxColour accent = wxSystemSettings::GetColour( wxSYS_COLOUR_HIGHLIGHT );
    return Readable( accent.ChangeLightness( IsDark( surface ) ? 170 : 62 ), { surface }, 4.5 );
}

wxSize LINK_BUTTON::DoGetBestSize() const
{
#if defined( __WXGTK__ )
    return GetTextExtent( GetLabel() ) + wxSize( 2 * FromDIP( 3 ), 2 * FromDIP( 3 ) );
#else
    return wxButton::DoGetBestSize();
#endif
}

void LINK_BUTTON::SetLabel( const wxString& label )
{
    wxButton::SetLabel( label ); InvalidateBestSize(); SetInitialSize( DoGetBestSize() ); Refresh();
}

void LINK_BUTTON::PaintButton( wxDC& dc, bool aHover, bool )
{
    // A link has no box of its own: GTK has already drawn the inspector's surface behind it, and its colours are chosen
    // against that surface (review of design QA round 2: filling with the parent's reported colour drew a faint box).
    const wxColour background = DrawnSurface( this );
    wxFont font = GetFont(); font.SetUnderlined( true ); dc.SetFont( font );
    wxColour colour = IsEnabled() ? LinkColour( background ) : wxSystemSettings::GetColour( wxSYS_COLOUR_GRAYTEXT );
    if( IsEnabled() && aHover ) colour = Readable( colour, { background }, 7.0 );
    dc.SetTextForeground( colour ); dc.DrawText( GetLabel(), FromDIP( 3 ), FromDIP( 3 ) );
    if( HasFocus() )
    {
        dc.SetPen( wxPen( colour, 1, wxPENSTYLE_DOT ) ); dc.SetBrush( *wxTRANSPARENT_BRUSH );
        dc.DrawRectangle( wxRect( GetClientSize() ) );
    }
}

CHOICE_BUTTON::CHOICE_BUTTON( wxWindow* parent, const wxString& label, const wxString& name ) :
        wxToggleButton( parent, wxID_ANY, label, wxDefaultPosition, wxDefaultSize, wxBU_EXACTFIT, wxDefaultValidator, name )
{
#if defined( __WXGTK__ )
    PaintNatively( this, this );
#endif
    SetInitialSize( DoGetBestSize() );
}

void CHOICE_BUTTON::SetLabel( const wxString& label )
{
    wxToggleButton::SetLabel( label ); InvalidateBestSize(); SetInitialSize( DoGetBestSize() ); Refresh();
}

wxSize CHOICE_BUTTON::DoGetBestSize() const
{
#if defined( __WXGTK__ )
    // As the toolkit's own exact-fit button was: the label with 10 DIP either side and 8 DIP above and below.
    return GetTextExtent( GetLabelText() ) + wxSize( 2 * FromDIP( 10 ), 2 * FromDIP( 8 ) );
#else
    return wxToggleButton::DoGetBestSize();
#endif
}

CHOICE_BUTTON::LOOK CHOICE_BUTTON::Look( const wxColour& surface, bool chosen, bool hover, bool enabled )
{
    const bool dark = IsDark( surface );
    const wxColour highlight = wxSystemSettings::GetColour( wxSYS_COLOUR_HIGHLIGHT );
    const wxColour grey = wxSystemSettings::GetColour( wxSYS_COLOUR_GRAYTEXT );
    LOOK look;
    if( chosen )
    {
        // The strip's active tool (design QA P2-1): a pale accent tile, an accent border 3:1 or more from the inspector and
        // the tile, and an accent label 4.5:1 or more on the tile.
        look.fill = mix( highlight, surface, dark ? 0.30 : 0.14 );
        look.border = Readable( highlight, { surface, look.fill }, 3.0 );
        look.text = enabled ? Readable( highlight, { look.fill }, 4.5 ) : grey;
        return look;
    }
    // Idle, a quiet framed button; under the pointer, a neutral grey fill, never the accent, so hover and chosen look different.
    look.fill = enabled && hover ? surface.ChangeLightness( dark ? 125 : 91 ) : surface.ChangeLightness( dark ? 112 : 102 );
    look.border = enabled ? surface.ChangeLightness( dark ? 150 : 80 ) : surface.ChangeLightness( dark ? 130 : 88 );
    look.text = enabled ? Readable( wxSystemSettings::GetColour( wxSYS_COLOUR_BTNTEXT ), { look.fill }, 4.5 ) : grey;
    return look;
}

void CHOICE_BUTTON::PaintButton( wxDC& dc, bool aHover, bool aDown )
{
    // The tile's corners show the inspector's surface, which GTK has already drawn behind the choice; its colours are chosen
    // against that surface (review of design QA round 2).
    const wxColour surface = DrawnSurface( this );
    const wxRect area( GetClientSize() );
    const LOOK look = Look( surface, GetValue(), aHover || aDown, IsEnabled() );
    const wxRect tile = wxRect( area ).Deflate( FromDIP( 1 ) );
    const int radius = FromDIP( 4 );
    dc.SetPen( wxPen( look.border, FromDIP( 1 ) ) ); dc.SetBrush( wxBrush( look.fill ) ); dc.DrawRoundedRectangle( tile, radius );
    dc.SetFont( GetFont() ); dc.SetTextForeground( look.text );
    const wxString label = GetLabelText(); const wxSize extent = dc.GetTextExtent( label );
    dc.DrawText( label, ( area.width - extent.x ) / 2, ( area.height - extent.y ) / 2 );
    if( HasFocus() )
    {
        dc.SetPen( wxPen( Readable( wxSystemSettings::GetColour( wxSYS_COLOUR_HIGHLIGHT ), { look.fill }, 3.0 ), 1, wxPENSTYLE_DOT ) );
        dc.SetBrush( *wxTRANSPARENT_BRUSH ); dc.DrawRoundedRectangle( wxRect( tile ).Deflate( FromDIP( 2 ) ), radius );
    }
}

bool CHOICE_FLOW::SetWrapWidth( int width )
{
    width = std::max( 0, width );
    if( width == m_width ) return false;
    m_width = width; return true;
}

std::vector<std::pair<wxSizerItem*, wxRect>> CHOICE_FLOW::arrange( wxSize& extent )
{
    // The same arrangement serves the minimum size and the placement, so the row always takes the lines it counted.
    std::vector<std::pair<wxSizerItem*, wxRect>> placed;
    int x = 0, y = 0, line = 0; extent = wxSize( 0, 0 );
    for( wxSizerItemList::compatibility_iterator node = m_children.GetFirst(); node; node = node->GetNext() )
    {
        wxSizerItem* item = node->GetData();
        if( !item->IsShown() ) continue;
        wxSize size = item->CalcMin();
        if( x > 0 && m_width > 0 && x + m_gap + size.x > m_width ) { y += line; x = 0; line = 0; }
        if( x > 0 ) x += m_gap;
        placed.emplace_back( item, wxRect( wxPoint( x, y ), size ) );
        x += size.x; line = std::max( line, size.y ); extent.x = std::max( extent.x, x );
    }
    extent.y = y + line;
    return placed;
}

wxSize CHOICE_FLOW::CalcMin()
{
    wxSize extent; arrange( extent ); return extent;
}

void CHOICE_FLOW::RepositionChildren( const wxSize& )
{
    wxSize extent;
    for( const auto& [item, rect] : arrange( extent ) ) item->SetDimension( m_position + rect.GetPosition(), rect.GetSize() );
}

wxColour TOOL_PALETTE::Surface()
{
    wxColour window = wxSystemSettings::GetColour( wxSYS_COLOUR_WINDOW );
    return window.ChangeLightness( IsDark( window ) ? 125 : 96 );
}

TOOL_PALETTE::TOOL_PALETTE( wxWindow* parent, ACTIONS actions ) :
        wxPanel( parent, wxID_ANY, wxDefaultPosition, wxDefaultSize, wxBORDER_NONE | wxTAB_TRAVERSAL ), m_actions( std::move( actions ) )
{
    SetName( "DiagramToolPalette" ); SetBackgroundStyle( wxBG_STYLE_PAINT );
    Bind( wxEVT_PAINT, [this]( wxPaintEvent& ) { paint(); } );
    auto* column = new wxBoxSizer( wxVERTICAL );
    auto add = [&]( wxWindow* item ) { column->Add( item, 0, wxEXPAND | wxLEFT | wxRIGHT | wxTOP, FromDIP( 6 ) ); };
    auto tool = [&]( TOOL which, const wxString& label, GLYPH glyph, const char* name, const wxString& tip )
    {
        auto* item = new TOOL_BUTTON( this, label, glyph, TOOL_STYLE::PALETTE, name, tip );
        item->Bind( wxEVT_TOGGLEBUTTON, [this, which]( wxCommandEvent& ) { if( m_actions.choose ) m_actions.choose( which ); } );
        add( item ); m_tools.emplace_back( which, item );
    };
    tool( TOOL::SELECT, _( "Select" ), GLYPH::SELECT, "DiagramPaletteSelect", _( "Select and move items (Esc)" ) );
    tool( TOOL::ADD_BLOCK, _( "Add block" ), GLYPH::ADD_BLOCK, "DiagramPaletteAddBlock", _( "Add a block where you click (B)" ) );
    tool( TOOL::CONNECT, _( "Connect" ), GLYPH::CONNECT, "DiagramPaletteConnect", _( "Connect two blocks or ports (C)" ) );
    tool( TOOL::ADD_PORT, _( "Place port" ), GLYPH::PORT, "DiagramPalettePlacePort", _( "Place a port on a block edge or the level boundary (P)" ) );
    m_delete = new TOOL_ACTION( this, _( "Delete" ), GLYPH::REMOVE, TOOL_STYLE::PALETTE, "DiagramPaletteDelete", _( "Delete the selection (Delete)" ) );
    m_delete->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { if( m_actions.remove ) m_actions.remove(); } );
    add( m_delete );
    // Room for the one divider before Undo, drawn by paint().
    column->AddSpacer( FromDIP( 7 ) );
    m_undo = new TOOL_ACTION( this, _( "Undo" ), GLYPH::UNDO, TOOL_STYLE::PALETTE, "DiagramPaletteUndo", _( "Undo (Ctrl+Z)" ) );
    m_undo->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { if( m_actions.undo ) m_actions.undo(); } );
    add( m_undo );
    column->AddSpacer( FromDIP( 6 ) );
    SetSizerAndFit( column );
}

void TOOL_PALETTE::paint()
{
    // One card: the canvas shows around its rounded corners, one surface, a quiet outline and one divider before Undo
    // (design QA P2-4 and P3 11).
    wxAutoBufferedPaintDC dc( this );
    const wxColour window = wxSystemSettings::GetColour( wxSYS_COLOUR_WINDOW );
    const bool dark = IsDark( window );
    const wxRect area( GetClientSize() );
    dc.SetBackground( wxBrush( window ) ); dc.Clear();
    // A quiet outline in both themes: the dark theme's was near-white, 8.4:1 on the canvas against the light theme's 1.7:1
    // (design QA round 2, P3 9).
    dc.SetPen( wxPen( window.ChangeLightness( dark ? 130 : 78 ), 1 ) ); dc.SetBrush( wxBrush( Surface() ) );
    dc.DrawRoundedRectangle( area, FromDIP( 8 ) );
    const int y = ( m_delete->GetRect().GetBottom() + m_undo->GetRect().y ) / 2;
    dc.SetPen( wxPen( window.ChangeLightness( dark ? 160 : 82 ), 1 ) );
    dc.DrawLine( FromDIP( 12 ), y, area.width - FromDIP( 12 ), y );
}

void TOOL_PALETTE::SetState( TOOL active, bool enabled, bool canDelete, bool canUndo )
{
    for( auto& [tool, button] : m_tools ) { button->SetValue( tool == active ); button->Enable( enabled ); }
    m_delete->Enable( enabled && canDelete ); m_undo->Enable( enabled && canUndo );
}
}

// ---- The per-level editor's drawing surface ---------------------------------------------------
// Frame members that paint, hit-test and edit one level's layout and its drawn blocks,
// connections and ports (Round A1). Every edit changes the level draft only; nothing is
// written until Save, and Decline discards it.

namespace D = kiapi::automation::diagrams::v1;
namespace R = RECURSIVE_DIAGRAM;
using R::Text;
using R::Utf8;
using R::FreshId;
using R::EditorOrigin;

namespace
{
constexpr int64_t Q = R::QUANTUM;
bool sameLink( const D::ConnectionSelectionData& a, const D::ConnectionSelectionData& b )
{ return a.connection_id() == b.connection_id() && a.state_id() == b.state_id() && a.revision_id() == b.revision_id(); }
bool sameSelection( const D::BlockSelectionData& a, const D::BlockSelectionData& b )
{ return a.block_id() == b.block_id() && a.state_id() == b.state_id() && a.revision_id() == b.revision_id(); }
int64_t snap( int64_t value, int64_t step = 10 * Q )
{ return static_cast<int64_t>( std::llround( static_cast<double>( value ) / static_cast<double>( step ) ) ) * step; }
void encodeRect( const R::RECT& rect, D::DiagramRectData* out )
{
    out->set_x( R::FormatUnits( rect.x ) ); out->set_y( R::FormatUnits( rect.y ) );
    out->set_width( R::FormatUnits( rect.w ) ); out->set_height( R::FormatUnits( rect.h ) );
}
/// The side of aRect nearest to aPoint and the offset along it (contract rbg-v2 PV3).
std::pair<D::DiagramPortSide, int64_t> project( const R::POINT& point, const R::RECT& rect )
{
    int64_t left = std::llabs( point.x - rect.x ), right = std::llabs( point.x - rect.Right() );
    int64_t top = std::llabs( point.y - rect.y ), bottom = std::llabs( point.y - rect.Bottom() );
    int64_t best = std::min( { left, right, top, bottom } );
    auto along = []( int64_t value, int64_t length ) { return std::clamp<int64_t>( snap( value, Q ), 0, length ); };
    if( best == left ) return { D::DPS_LEFT, along( point.y - rect.y, rect.h ) };
    if( best == right ) return { D::DPS_RIGHT, along( point.y - rect.y, rect.h ) };
    if( best == top ) return { D::DPS_TOP, along( point.x - rect.x, rect.w ) };
    return { D::DPS_BOTTOM, along( point.x - rect.x, rect.w ) };
}
struct DRAWN_PORT { std::string owner, id, name; wxPoint at; bool boundary, placed; };
void encodePoint( const R::POINT& point, D::DiagramAnnotationPointData* out )
{
    out->set_x( R::FormatUnits( point.x ) ); out->set_y( R::FormatUnits( point.y ) );
}
}

void RECURSIVE_DIAGRAM_FRAME::followRoutes( const R::LEVEL_LAYOUT& before )
{
    // A channel route (two unlocked waypoints on one vertical line) always runs level from each end to its channel:
    // its two heights are the current heights of its ends, even when a merge or an earlier writer left them out of
    // line, so every leg stays horizontal or vertical. When an end moved since aBefore and the route is still the one
    // aBefore drew, the channel keeps its offset from the middle between the ends. A route that changed since aBefore
    // (a merge took another writer's route, whose channel that writer already placed against the ends it moved) keeps
    // its channel; only its heights follow the ends. A locked route, a route with an end on a top or bottom side (its leg
    // leaves that end upright, so a level run from it would lie along the block's outline), and any other stored route are
    // kept exactly as drawn.
    if( !m_level.scope().local_diagram().has_presentation() ) return;
    auto after = layout( current(), true );
    for( auto& row : *m_level.mutable_scope()->mutable_local_diagram()->mutable_presentation()->mutable_routes() )
    {
        if( row.locked() || row.waypoints_size() != 2 ) continue;
        const R::LINK* now = after.Link( row.connection_id() );
        int index = static_cast<int>( row.endpoint_index() );
        if( !now || !after.Sideways( *now, index ) ) continue;
        R::POINT first, second;
        if( !R::ParseUnits( row.waypoints( 0 ).x(), first.x ) || !R::ParseUnits( row.waypoints( 0 ).y(), first.y )
            || !R::ParseUnits( row.waypoints( 1 ).x(), second.x ) || !R::ParseUnits( row.waypoints( 1 ).y(), second.y ) ) continue;
        auto next = after.Route( *now, index );
        if( next.size() != 4 || first.x != second.x ) continue;
        const R::POINT from = next.front(), to = next.back();
        int64_t x = first.x;
        const R::LINK* was = before.Link( row.connection_id() );
        auto previous = was && before.HasRoute( row.connection_id(), index ) ? before.Route( *was, index ) : std::vector<R::POINT>();
        bool sameRoute = previous.size() == 4 && previous[1].x == first.x && previous[2].x == second.x;
        if( sameRoute && ( from.x != previous.front().x || from.y != previous.front().y
                           || to.x != previous.back().x || to.y != previous.back().y ) )
        {
            x = ( from.x + to.x ) / 2 + first.x - ( previous.front().x + previous.back().x ) / 2;
            const int64_t margin = 10 * Q, low = std::min( from.x, to.x ) + margin, high = std::max( from.x, to.x ) - margin;
            x = low <= high ? std::clamp( x, low, high ) : ( from.x + to.x ) / 2;
        }
        if( x == first.x && from.y == first.y && to.y == second.y ) continue;
        encodePoint( { x, from.y }, row.mutable_waypoints( 0 ) ); encodePoint( { x, to.y }, row.mutable_waypoints( 1 ) );
    }
}

R::LEVEL_LAYOUT RECURSIVE_DIAGRAM_FRAME::layout( const REVISION* scope, bool withDraft ) const
{
    if( !scope ) return R::LEVEL_LAYOUT( "", {}, {}, {}, nullptr, {} );
    bool draft = withDraft && m_ready && sameSelection( m_level.scope().baseline(), scope->selection() );
    const auto& local = draft ? m_level.scope().local_diagram() : scope->local_diagram();
    const auto& children = draft ? m_level.scope().children() : scope->children();
    std::vector<R::BOUNDARY> own;
    for( const auto& port : local.interfaces() ) own.push_back( { port.id(), port.name() } );
    std::vector<R::NODE> nodes;
    for( const auto& child : children )
    {
        if( const auto* added = draft ? newChild( child.block_id() ) : nullptr )
        {
            R::NODE node{ added->selection().block_id(), added->name(), 0, true, {}, {} };
            for( const auto& port : added->interfaces() ) node.interfaces.push_back( { port.id(), port.name() } );
            node.definition = added->definition();
            nodes.push_back( std::move( node ) ); continue;
        }
        const REVISION* saved = revision( child ); if( !saved ) continue;
        const DRAFT* edited = nullptr;
        if( draft ) for( const auto& item : m_level.child_drafts() ) if( item.baseline().block_id() == child.block_id() ) edited = &item;
        R::NODE node{ child.block_id(), edited ? edited->name() : saved->name(), version( *saved ), false, {}, {} };
        for( const auto& port : edited ? edited->local_diagram().interfaces() : saved->local_diagram().interfaces() )
            node.interfaces.push_back( { port.id(), port.name() } );
        node.definition = edited ? edited->definition() : saved->definition();
        nodes.push_back( std::move( node ) );
    }
    std::vector<R::LINK> links;
    for( const auto& root : local.connections() )
    {
        if( const auto* added = draft ? newConnection( root.connection_id() ) : nullptr )
        {
            links.push_back( { root.connection_id(), added->name(), true, { added->endpoints().begin(), added->endpoints().end() }, added->direction() } );
            continue;
        }
        const LINK_DRAFT* edited = draft ? connectionDraft( root.connection_id() ) : nullptr;
        for( const auto& archive : m_document.graph().connection_archives() ) if( archive.owner_block_id() == scope->selection().block_id() )
            for( const auto& item : archive.revisions() ) if( sameLink( item.selection(), root ) )
            {
                const auto& endpoints = edited ? edited->endpoints() : item.endpoints();
                links.push_back( { root.connection_id(), edited ? edited->name() : item.name(), false, { endpoints.begin(), endpoints.end() },
                                   edited ? edited->direction() : item.direction() } );
            }
    }
    return R::LEVEL_LAYOUT( scope->selection().block_id(), std::move( own ), std::move( nodes ), std::move( links ),
                            local.has_presentation() ? &local.presentation() : nullptr, noteBoxes( draft ? m_level.scope().local_diagram().annotations()
                                                                                                          : scope->local_diagram().annotations() ) );
}
std::vector<R::RECT> RECURSIVE_DIAGRAM_FRAME::noteBoxes( const google::protobuf::RepeatedPtrField<D::DiagramAnnotationData>& notes ) const
{
    std::vector<R::RECT> boxes;
    for( int i = 0; i < notes.size(); ++i )
    {
        const auto& note = notes.Get( i );
        if( note.target_kind() == D::DAT_CANVAS || note.has_position() )
        {
            double x = 60 + i * 260, y = 420;
            if( note.has_position() ) { Text( note.position().x() ).ToDouble( &x ); Text( note.position().y() ).ToDouble( &y ); }
            boxes.push_back( { static_cast<int64_t>( std::floor( x * Q ) ), static_cast<int64_t>( std::floor( y * Q ) ), 240 * Q, 100 * Q } );
        }
        for( const auto& stroke : note.strokes() ) for( const auto& point : stroke.points() )
        {
            double x = 0, y = 0; Text( point.x() ).ToDouble( &x ); Text( point.y() ).ToDouble( &y );
            boxes.push_back( { static_cast<int64_t>( std::floor( x * Q ) ), static_cast<int64_t>( std::floor( y * Q ) ), 1, 1 } );
        }
    }
    return boxes;
}
const google::protobuf::RepeatedPtrField<D::DiagramAnnotationData>& RECURSIVE_DIAGRAM_FRAME::visibleNotes() const
{
    if( m_historyPreview && current() ) return current()->local_diagram().annotations();
    return m_level.scope().local_diagram().annotations();
}
D::DiagramPresentationViewData* RECURSIVE_DIAGRAM_FRAME::presentation()
{
    auto* view = m_level.mutable_scope()->mutable_local_diagram()->mutable_presentation();
    if( view->units().empty() ) view->set_units( "diagram-unit" );
    return view;
}
void RECURSIVE_DIAGRAM_FRAME::materialize()
{
    // Rule F1a: the first layout edit keeps every still-unplaced child where it is drawn.
    auto drawn = layout( current(), true );
    std::vector<std::string> missing;
    for( const auto& node : drawn.Nodes() ) if( !drawn.Placed( node.id ) ) missing.push_back( node.id );
    if( missing.empty() ) return;
    auto* view = presentation();
    for( const auto& id : missing ) { auto* row = view->add_blocks(); row->set_block_id( id ); encodeRect( drawn.Rect( id ), row->mutable_rect() ); }
}
void RECURSIVE_DIAGRAM_FRAME::materializeFrame()
{
    // Rule F3: the first boundary-port placement stores the frame around what is drawn, and
    // existing boundary ports move onto its left side at their drawn height.
    materialize();
    auto drawn = layout( current(), true );
    if( drawn.Frame() ) return;
    R::RECT frame = drawn.FallbackFrame();
    auto* view = presentation(); encodeRect( frame, view->mutable_frame() );
    for( const auto& port : drawn.Ports() ) if( port.boundary && !port.placed )
    {
        auto* row = view->add_ports(); row->set_block_id( port.blockId ); row->set_interface_id( port.interfaceId ); row->set_side( D::DPS_LEFT );
        row->set_offset( R::FormatUnits( std::clamp<int64_t>( port.anchor.y - frame.y, 0, frame.h ) ) );
    }
}
void RECURSIVE_DIAGRAM_FRAME::encloseInFrame( const R::RECT& rect )
{
    // A stored level frame grows, never shrinks, to keep a moved, resized or added block inside
    // it with the margin rule F3 uses. Boundary ports stay where they are drawn: offsets measured
    // from a side that moved outward are shifted by the same amount.
    auto frame = layout( current(), true ).Frame();
    if( !frame ) return;
    const int64_t margin = 40 * Q;
    int64_t left = std::min( frame->x, rect.x - margin ), top = std::min( frame->y, rect.y - margin );
    int64_t right = std::max( frame->Right(), rect.Right() + margin ), bottom = std::max( frame->Bottom(), rect.Bottom() + margin );
    if( left == frame->x && top == frame->y && right == frame->Right() && bottom == frame->Bottom() ) return;
    auto* view = presentation(); encodeRect( { left, top, right - left, bottom - top }, view->mutable_frame() );
    const std::string& scope = m_level.scope().baseline().block_id();
    for( auto& row : *view->mutable_ports() ) if( row.block_id() == scope )
    {
        int64_t offset = 0; if( !R::ParseUnits( row.offset(), offset ) ) continue;
        bool vertical = row.side() == D::DPS_LEFT || row.side() == D::DPS_RIGHT;
        row.set_offset( R::FormatUnits( offset + ( vertical ? frame->y - top : frame->x - left ) ) );
    }
}

wxPoint RECURSIVE_DIAGRAM_FRAME::toScreen( const R::POINT& point ) const
{
    return { static_cast<int>( std::lround( ( point.x / double( Q ) - m_origin.m_x ) * m_scale ) ),
             static_cast<int>( std::lround( ( point.y / double( Q ) - m_origin.m_y ) * m_scale ) ) };
}
wxRect RECURSIVE_DIAGRAM_FRAME::toScreen( const R::RECT& rect ) const
{
    wxPoint from = toScreen( R::POINT{ rect.x, rect.y } ), to = toScreen( R::POINT{ rect.Right(), rect.Bottom() } );
    return { from, wxSize( std::max( 1, to.x - from.x ), std::max( 1, to.y - from.y ) ) };
}
R::POINT RECURSIVE_DIAGRAM_FRAME::toDiagram( const wxPoint& point ) const
{
    return { static_cast<int64_t>( std::llround( ( point.x / m_scale + m_origin.m_x ) * Q ) ),
             static_cast<int64_t>( std::llround( ( point.y / m_scale + m_origin.m_y ) * Q ) ) };
}
wxRect RECURSIVE_DIAGRAM_FRAME::noteRect( const D::DiagramAnnotationData& note, int index ) const
{
    double x = 60 + index * 260, y = 420;
    if( note.has_position() ) { Text( note.position().x() ).ToDouble( &x ); Text( note.position().y() ).ToDouble( &y ); }
    return { static_cast<int>( ( x - m_origin.m_x ) * m_scale ), static_cast<int>( ( y - m_origin.m_y ) * m_scale ),
             static_cast<int>( 240 * m_scale ), static_cast<int>( 100 * m_scale ) };
}

namespace
{
/// Where ports are drawn: placed ports on their side; an unplaced child port where its connections
/// attach (rule F2), or at its reported fallback when it has none; boundary ports at their anchors.
std::vector<DRAWN_PORT> drawnPorts( const R::LEVEL_LAYOUT& drawn, const std::function<wxPoint( const R::POINT& )>& screen )
{
    std::vector<DRAWN_PORT> result;
    for( const auto& port : drawn.Ports() )
    {
        if( port.boundary || port.placed ) { result.push_back( { port.blockId, port.interfaceId, port.name, screen( port.anchor ), port.boundary, port.placed } ); continue; }
        std::vector<wxPoint> attached;
        for( const auto& link : drawn.Links() )
            for( int i = 1; i < static_cast<int>( link.endpoints.size() ); ++i )
            {
                auto points = drawn.Route( link, i ); if( points.empty() ) continue;
                auto consider = [&]( const D::DiagramEndpointBindingData& endpoint, const R::POINT& at )
                {
                    if( endpoint.block_id() == port.blockId && endpoint.interface_id() == port.interfaceId )
                    { wxPoint point = screen( at ); if( std::find( attached.begin(), attached.end(), point ) == attached.end() ) attached.push_back( point ); }
                };
                consider( link.endpoints[0], points.front() ); consider( link.endpoints[i], points.back() );
            }
        if( attached.empty() ) attached.push_back( screen( port.anchor ) );
        for( const auto& point : attached ) result.push_back( { port.blockId, port.interfaceId, port.name, point, false, false } );
    }
    return result;
}
/// Where a boundary port drawn at aAt names itself, in canvas pixels, with aDC's font: outside the level frame beside the
/// port's side, or above and right of a boundary port that has no stored placement yet. aBlocks are the child blocks as
/// drawn: where the name above and right of an unplaced port would reach one of them (or the ports on its edge), as the
/// fallback column's names do in a small window, it goes above and left of the port instead, outside the level, as the name
/// of a port placed on the level's left side does (mockup audit M1-2).
wxRect boundaryName( wxDC& dc, const R::PORT& port, const wxPoint& at, const std::vector<wxRect>& blocks )
{
    wxSize extent = dc.GetTextExtent( Text( port.name ) );
    // Clear of the port's 12-pixel square (design QA P2-6).
    if( !port.placed )
    {
        wxRect right( wxPoint( at.x + 12, at.y - 26 ), extent );
        if( std::none_of( blocks.begin(), blocks.end(), [&]( const wxRect& block ) { return wxRect( block ).Inflate( 8 ).Intersects( right ); } ) )
            return right;
        return wxRect( wxPoint( at.x - extent.x - 10, at.y - extent.y - 6 ), extent );
    }
    switch( port.side )
    {
    case D::DPS_RIGHT: return wxRect( wxPoint( at.x + 10, at.y - extent.y - 6 ), extent );
    case D::DPS_TOP: return wxRect( wxPoint( at.x + 10, at.y - extent.y - 8 ), extent );
    case D::DPS_BOTTOM: return wxRect( wxPoint( at.x + 10, at.y + 8 ), extent );
    default: return wxRect( wxPoint( at.x - extent.x - 10, at.y - extent.y - 6 ), extent );
    }
}
}

std::vector<wxRect> RECURSIVE_DIAGRAM_FRAME::blockBoxes( const R::LEVEL_LAYOUT& drawn ) const
{
    std::vector<wxRect> boxes;
    for( const auto& node : drawn.Nodes() ) boxes.push_back( toScreen( drawn.Rect( node.id ) ) );
    return boxes;
}
std::vector<std::pair<std::string, wxRect>> RECURSIVE_DIAGRAM_FRAME::boundaryNames( const R::LEVEL_LAYOUT& drawn ) const
{
    std::vector<std::pair<std::string, wxRect>> result;
    wxClientDC dc( m_canvas ); dc.SetFont( GetFont() );
    const auto blocks = blockBoxes( drawn );
    for( const auto& port : drawn.Ports() )
        if( port.boundary ) result.emplace_back( port.name, boundaryName( dc, port, toScreen( port.anchor ), blocks ) );
    return result;
}
std::vector<wxRect> RECURSIVE_DIAGRAM_FRAME::portNames( wxDC& dc, const R::LEVEL_LAYOUT& drawn, const std::string& block ) const
{
    // The names a block always shows inside its edge: its placed ports' (see paint). The chips keep clear of them.
    std::vector<wxRect> result;
    dc.SetFont( chipFont() );
    for( const auto& port : drawn.Ports() )
        if( !port.boundary && port.placed && port.blockId == block )
            result.push_back( R::PortNameRect( dc, Text( port.name ), port.side, toScreen( port.anchor ) ) );
    return result;
}

const R::PORT* RECURSIVE_DIAGRAM_FRAME::portAt( const R::LEVEL_LAYOUT& drawn, const wxPoint& point ) const
{
    // A port is hit within 9 DIP of its point at every zoom, a target of at least 16 pixels (design QA P2-6).
    int reach = FromDIP( 9 );
    for( const auto& port : drawnPorts( drawn, [this]( const R::POINT& p ) { return toScreen( p ); } ) )
        if( std::abs( port.at.x - point.x ) <= reach && std::abs( port.at.y - point.y ) <= reach ) return drawn.Port( port.owner, port.id );
    return nullptr;
}
std::string RECURSIVE_DIAGRAM_FRAME::blockAt( const R::LEVEL_LAYOUT& drawn, const wxPoint& point ) const
{
    const auto& nodes = drawn.Nodes();
    for( auto node = nodes.rbegin(); node != nodes.rend(); ++node )
        if( toScreen( drawn.Rect( node->id ) ).Contains( point ) ) return node->id;
    return {};
}
std::string RECURSIVE_DIAGRAM_FRAME::connectionAt( const R::LEVEL_LAYOUT& drawn, const wxPoint& point ) const
{
    // The connection drawn nearest the click wins. Where two connections share a segment (they end at one
    // anchor), the one already selected stays selected.
    std::string nearest; int best = FromDIP( 6 ) + 1;
    for( const auto& link : drawn.Links() )
        for( int i = 1; i < static_cast<int>( link.endpoints.size() ); ++i )
        {
            auto route = drawn.Route( link, i );
            for( size_t j = 1; j < route.size(); ++j )
            {
                wxPoint a = toScreen( route[j - 1] ), b = toScreen( route[j] );
                int x = std::clamp( point.x, std::min( a.x, b.x ), std::max( a.x, b.x ) );
                int y = std::clamp( point.y, std::min( a.y, b.y ), std::max( a.y, b.y ) );
                int distance = std::abs( point.x - x ) + std::abs( point.y - y );
                if( distance < best || ( distance == best && link.id == m_connectionId ) ) { best = distance; nearest = link.id; }
            }
        }
    return nearest;
}
std::vector<RECURSIVE_DIAGRAM_FRAME::ARROW> RECURSIVE_DIAGRAM_FRAME::drawnArrows() const
{
    // FromFirst points every leg into its far end, ToFirst into the first end, Bidirectional both ways. The arrowhead sits
    // on the last (or first) drawn segment of the leg, its tip on the end's anchor.
    std::vector<ARROW> result;
    if( !m_ready || !current() ) return result;
    auto drawn = layout( current(), !m_historyPreview );
    int size = FromDIP( 11 );
    auto head = [&]( const std::string& id, unsigned endpoint, const std::vector<wxPoint>& points, bool atEnd )
    {
        wxPoint tip = atEnd ? points.back() : points.front();
        // The nearest point of the leg that is not the tip gives the arrow's direction.
        for( size_t k = 1; k < points.size(); ++k )
        {
            wxPoint other = atEnd ? points[points.size() - 1 - k] : points[k];
            double dx = tip.x - other.x, dy = tip.y - other.y, length = std::hypot( dx, dy );
            if( length < 1 ) continue;
            double scale = std::min( 1.0, size / length );
            result.push_back( { id, endpoint, tip, wxPoint( static_cast<int>( std::lround( tip.x - dx * scale ) ), static_cast<int>( std::lround( tip.y - dy * scale ) ) ) } );
            return;
        }
    };
    for( const auto& link : drawn.Links() )
    {
        if( link.direction == D::DCDR_UNSPECIFIED ) continue;
        for( int i = 1; i < static_cast<int>( link.endpoints.size() ); ++i )
        {
            std::vector<wxPoint> points;
            for( const auto& point : drawn.Route( link, i ) ) points.push_back( toScreen( point ) );
            if( points.size() < 2 ) continue;
            if( link.direction == D::DCDR_FROM_FIRST || link.direction == D::DCDR_BIDIRECTIONAL ) head( link.id, static_cast<unsigned>( i ), points, true );
            if( link.direction == D::DCDR_TO_FIRST || link.direction == D::DCDR_BIDIRECTIONAL ) head( link.id, 0, points, false );
        }
    }
    return result;
}
int RECURSIVE_DIAGRAM_FRAME::handleAt( const wxPoint& point ) const
{
    if( !drawingAvailable() || m_tool != TOOL::SELECT || !m_connectionId.empty() || !m_portId.empty()
        || m_selected.empty() || m_selected == m_level.scope().baseline().block_id() ) return -1;
    auto handles = selectionHandles( layout( current(), true ) );
    for( int i = 0; i < static_cast<int>( handles.size() ); ++i ) if( wxRect( handles[i] ).Inflate( FromDIP( 2 ) ).Contains( point ) ) return i;
    return -1;
}
std::vector<wxRect> RECURSIVE_DIAGRAM_FRAME::selectionHandles( const R::LEVEL_LAYOUT& drawn ) const
{
    // Eight 9 DIP handles on the selected block's corners and edge middles (the order handleAt and the resize use).
    if( !drawingAvailable() || m_tool != TOOL::SELECT || !m_connectionId.empty() || !m_portId.empty()
        || m_selected.empty() || m_selected == m_level.scope().baseline().block_id() || !drawn.Node( m_selected ) ) return {};
    wxRect box = toScreen( drawn.Rect( m_selected ) );
    wxPoint points[] = { box.GetTopLeft(), { box.x + box.width / 2, box.y }, box.GetTopRight(), { box.GetRight(), box.y + box.height / 2 },
                         box.GetBottomRight(), { box.x + box.width / 2, box.GetBottom() }, box.GetBottomLeft(), { box.x, box.y + box.height / 2 } };
    const int half = FromDIP( 4 );
    std::vector<wxRect> result;
    for( const auto& point : points ) result.emplace_back( point.x - half, point.y - half, 2 * half + 1, 2 * half + 1 );
    return result;
}
std::vector<RECURSIVE_DIAGRAM_FRAME::PORT_MARK> RECURSIVE_DIAGRAM_FRAME::portMarks( const R::LEVEL_LAYOUT& drawn ) const
{
    std::vector<PORT_MARK> result;
    for( const auto& port : drawnPorts( drawn, [this]( const R::POINT& p ) { return toScreen( p ); } ) )
        result.push_back( { port.owner, port.id, Text( port.name ), portMark( port.at ) } );
    return result;
}
wxRect RECURSIVE_DIAGRAM_FRAME::portMark( const wxPoint& at ) const
{
    const int half = FromDIP( 6 );
    return wxRect( at.x - half, at.y - half, 2 * half, 2 * half );
}
std::optional<RECURSIVE_DIAGRAM_FRAME::TARGET> RECURSIVE_DIAGRAM_FRAME::connectTarget( const R::LEVEL_LAYOUT& drawn ) const
{
    // Design QA P2-6 and P3 4: while Connect is active, the port under the pointer gets a ring (the sketch's snap circle) and a
    // block under it an outline, when a connection can start or finish there (never on the end it started from).
    if( m_tool != TOOL::CONNECT || !drawingAvailable() || m_captionKind || m_connectTo || !m_pointerInside ) return std::nullopt;
    if( !wxRect( wxPoint( 0, 0 ), m_canvas->GetClientSize() ).Contains( m_pointer ) ) return std::nullopt;
    const int reach = FromDIP( 9 );
    std::optional<DRAWN_PORT> nearest; int best = INT_MAX;
    for( const auto& port : drawnPorts( drawn, [this]( const R::POINT& p ) { return toScreen( p ); } ) )
    {
        int distance = std::max( std::abs( port.at.x - m_pointer.x ), std::abs( port.at.y - m_pointer.y ) );
        if( distance <= reach && distance < best ) { best = distance; nearest = port; }
    }
    if( nearest )
    {
        if( m_connectFrom && m_connectFrom->block_id() == nearest->owner && m_connectFrom->interface_id() == nearest->id ) return std::nullopt;
        const int radius = FromDIP( 11 );
        return TARGET{ wxRect( nearest->at.x - radius, nearest->at.y - radius, 2 * radius + 1, 2 * radius + 1 ), Text( nearest->name ), true,
                       nearest->owner, nearest->id };
    }
    std::string block = blockAt( drawn, m_pointer );
    if( block.empty() || ( m_connectFrom && m_connectFrom->block_id() == block && !m_connectFrom->has_interface_id() ) ) return std::nullopt;
    const R::NODE* node = drawn.Node( block );
    return TARGET{ toScreen( drawn.Rect( block ) ).Inflate( FromDIP( 4 ) ), node ? Text( node->name ) : wxString(), false, block, {} };
}
std::vector<RECURSIVE_DIAGRAM_FRAME::CAPTION_PLACE> RECURSIVE_DIAGRAM_FRAME::connectionCaptions( const R::LEVEL_LAYOUT& drawn, const wxSize& area ) const
{
    // Every connection shows its caption (owner rule 1, design QA round 2, R2-P2-1 and R2-P2-8; the approved sketch A3 captions
    // a connection to a boundary port as well, beside the port's own name). Text keeps its size while the drawing zooms out
    // (design QA P2-7), so each caption looks for a clear place beside its own connection, in this order:
    //  1. a stored route's caption position, then beside the middle of each leg, longest leg first: above or below a level leg,
    //     right or left of an upright one;
    //  2. the same places moved along each leg in 6-pixel steps, nearest the middle first, where the caption may reach past the
    //     leg's end (past a bend, or beside a short leg) as long as it stays beside at least 8 pixels of the leg;
    //  3. the free space around the connection: the same places up to six steps further out from each leg.
    // Clear means inside the canvas, off the palette, and apart from every block by the handle size plus 4 pixels (so a resize
    // handle never covers a caption), from every port and its name, from every note, from every wire, from the level's dashed
    // boundary and from the captions already placed. Only a caption whose whole text has no clear place is shortened with "…"
    // (at least four characters kept) into the first clear place of 1 and 2, and shows its whole text on hover; one that has no
    // clear place even so is drawn shortened to four characters where it covers least, on its knocked-out background, never
    // left out.
    std::vector<CAPTION_PLACE> result;
    if( !m_ready || !current() ) return result;
    wxClientDC dc( m_canvas ); dc.SetFont( GetFont() );
    const wxRect canvas( wxPoint( 0, 0 ), area );
    std::vector<wxRect> obstacles;
    const int clearance = FromDIP( 8 );
    for( const auto& node : drawn.Nodes() ) obstacles.push_back( toScreen( drawn.Rect( node.id ) ).Inflate( clearance ) );
    auto screen = [this]( const R::POINT& p ) { return toScreen( p ); };
    for( const auto& port : drawnPorts( drawn, screen ) ) obstacles.push_back( portMark( port.at ).Inflate( FromDIP( 2 ) ) );
    for( const auto& [name, rect] : boundaryNames( drawn ) ) obstacles.push_back( wxRect( rect ).Inflate( FromDIP( 2 ) ) );
    const auto& notes = visibleNotes();
    for( int i = 0; i < notes.size(); ++i )
        if( notes.Get( i ).target_kind() == D::DAT_CANVAS || notes.Get( i ).has_position() ) obstacles.push_back( noteRect( notes.Get( i ), i ) );
    // The palette covers the canvas's left edge; a caption under it could not be read.
    if( m_paletteShown && m_palette->IsShown() ) obstacles.push_back( wxRect( m_palette->GetRect() ).Inflate( FromDIP( 4 ) ) );
    std::vector<std::pair<wxPoint, wxPoint>> wires;
    std::string key;
    auto put = [&key]( int64_t value ) { key.append( reinterpret_cast<const char*>( &value ), sizeof value ); };
    for( const auto& link : drawn.Links() )
        for( int i = 1; i < static_cast<int>( link.endpoints.size() ); ++i )
        {
            auto route = drawn.Route( link, i );
            key += link.id; key += '\0'; key += link.name; key += '\0'; put( i );
            if( auto label = drawn.RouteLabel( link.id, i ) ) { wxPoint at = toScreen( *label ); put( at.x ); put( at.y ); }
            for( const auto& point : route ) { wxPoint at = toScreen( point ); put( at.x ); put( at.y ); }
            for( size_t j = 1; j < route.size(); ++j ) wires.emplace_back( toScreen( route[j - 1] ), toScreen( route[j] ) );
        }
    // The level's dashed boundary is kept clear as a wire is, so a caption lies wholly inside the frame or wholly outside it,
    // as a connection to a boundary port may be captioned beside the port's own name (review of design QA round 2).
    if( auto frame = drawn.Frame() )
    {
        const wxRect outline = toScreen( *frame );
        const wxPoint corners[] = { outline.GetTopLeft(), outline.GetTopRight(), outline.GetBottomRight(), outline.GetBottomLeft() };
        for( int k = 0; k < 4; ++k ) wires.emplace_back( corners[k], corners[( k + 1 ) % 4] );
        put( outline.x ); put( outline.y ); put( outline.width ); put( outline.height );
    }
    put( area.x ); put( area.y ); put( GetFont().GetPointSize() );
    for( const wxRect& rect : obstacles ) { put( rect.x ); put( rect.y ); put( rect.width ); put( rect.height ); }
    // An unchanged canvas (a repaint, a state read, a pointer move) keeps the places already found.
    if( key == m_captionKey ) return m_captionCache;
    std::vector<wxRect> placed;
    for( const auto& link : drawn.Links() )
        for( int i = 1; i < static_cast<int>( link.endpoints.size() ); ++i )
        {
            auto route = drawn.Route( link, i );
            if( route.size() < 2 ) continue;
            std::vector<wxPoint> points; for( const auto& point : route ) points.push_back( toScreen( point ) );
            std::vector<size_t> legs;
            for( size_t j = 1; j < points.size(); ++j ) if( points[j] != points[j - 1] ) legs.push_back( j );
            if( legs.empty() ) continue;
            auto length = [&]( size_t j ) { return std::abs( points[j].x - points[j - 1].x ) + std::abs( points[j].y - points[j - 1].y ); };
            std::stable_sort( legs.begin(), legs.end(), [&]( size_t a, size_t b ) { return length( a ) > length( b ); } );
            wxString full = Text( link.name );
            const wxSize fullExtent = dc.GetTextExtent( full );
            // Only what lies near this connection can touch its caption.
            wxRect reach( points.front(), points.front() );
            for( const auto& point : points ) reach.Union( wxRect( point, point ) );
            reach.Inflate( fullExtent.x + FromDIP( 90 ), fullExtent.y + FromDIP( 90 ) );
            // The blocks (the first obstacles) apart from everything else: where no place is clear, a caption covers a wire
            // rather than a port, a name or another caption, and any of those rather than a block.
            std::vector<wxRect> nearBlocks, nearby;
            for( size_t k = 0; k < obstacles.size(); ++k )
                if( obstacles[k].Intersects( reach ) ) ( k < drawn.Nodes().size() ? nearBlocks : nearby ).push_back( obstacles[k] );
            for( const wxRect& rect : placed ) if( rect.Intersects( reach ) ) nearby.push_back( rect );
            std::vector<wxRect> nearWires;
            for( const auto& [a, b] : wires )
            {
                wxRect segment( wxPoint( std::min( a.x, b.x ), std::min( a.y, b.y ) ), wxPoint( std::max( a.x, b.x ), std::max( a.y, b.y ) ) );
                if( segment.Intersects( reach ) ) nearWires.push_back( segment );
            }
            auto collisions = [&]( const wxRect& candidate, bool stopAtFirst )
            {
                // A place outside the canvas could not be seen at all: in the last resort it counts as worse than any overlap.
                int count = canvas.Contains( candidate ) ? 0 : 1000;
                if( count && stopAtFirst ) return count;
                const wxRect zone = wxRect( candidate ).Inflate( FromDIP( 2 ) );
                for( const wxRect& rect : nearBlocks ) if( rect.Intersects( candidate ) && ( count += 100 ) && stopAtFirst ) return count;
                for( const wxRect& rect : nearby ) if( rect.Intersects( candidate ) && ( count += 10 ) && stopAtFirst ) return count;
                for( const wxRect& segment : nearWires ) if( segment.Intersects( zone ) && ++count && stopAtFirst ) return count;
                return count;
            };
            // The places for a text of aExtent (see above), up to aLevel.
            auto places = [&]( const wxSize& extent, int level )
            {
                std::vector<wxRect> candidates;
                if( auto label = drawn.RouteLabel( link.id, i ) ) candidates.emplace_back( toScreen( *label ) - wxPoint( extent.x / 2, extent.y / 2 ), extent );
                // Beside the point `shift` pixels from the middle of leg j, `out` steps away from it; false once the caption would
                // no longer lie beside at least 8 pixels (or the whole) of the leg.
                auto beside = [&]( size_t j, int shift, int out ) -> bool
                {
                    const wxPoint &a = points[j - 1], &b = points[j];
                    const int along = a.y == b.y ? extent.x : extent.y, span = std::abs( a.y == b.y ? b.x - a.x : b.y - a.y );
                    const int keep = std::min( FromDIP( 8 ), span );
                    if( shift != 0 && std::abs( shift ) > span / 2 + along / 2 - keep ) return false;
                    const wxPoint middle( ( a.x + b.x ) / 2, ( a.y + b.y ) / 2 );
                    if( a.y == b.y )
                    {
                        const int x = middle.x + shift - extent.x / 2, gap = FromDIP( 4 ) + out * ( extent.y / 2 + FromDIP( 2 ) );
                        candidates.emplace_back( wxPoint( x, a.y - extent.y - gap ), extent );
                        candidates.emplace_back( wxPoint( x, a.y + gap ), extent );
                    }
                    else if( a.x == b.x )
                    {
                        const int y = middle.y + shift - extent.y / 2, gap = FromDIP( 6 ) + out * FromDIP( 12 );
                        candidates.emplace_back( wxPoint( a.x + gap, y ), extent );
                        candidates.emplace_back( wxPoint( a.x - gap - extent.x, y ), extent );
                    }
                    return true;
                };
                for( size_t j : legs ) beside( j, 0, 0 );
                if( level >= 1 )
                {
                    const int step = FromDIP( 6 );
                    for( int shift = step; ; shift += step )
                    {
                        bool any = false;
                        for( size_t j : legs ) { any |= beside( j, shift, 0 ); any |= beside( j, -shift, 0 ); }
                        if( !any ) break;
                    }
                }
                if( level >= 2 )
                    for( int out = 1; out <= 6; ++out )
                    {
                        for( size_t j : legs ) beside( j, 0, out );
                        const int step = FromDIP( 12 );
                        for( int shift = step; ; shift += step )
                        {
                            bool any = false;
                            for( size_t j : legs ) { any |= beside( j, shift, out ); any |= beside( j, -shift, out ); }
                            if( !any ) break;
                        }
                    }
                return candidates;
            };
            CAPTION_PLACE place{ link.id, full, full, wxRect(), false, true };
            for( const wxRect& candidate : places( fullExtent, 2 ) )
                if( !collisions( candidate, true ) ) { place.rect = candidate; place.shown = true; break; }
            // Shortened only when the whole caption has no clear place (as a block caption is shortened to its block's width).
            for( size_t keep = full.length() - 1; !place.shown && keep >= 4 && keep < full.length(); --keep )
            {
                wxString text = wxString( full.Left( keep ) ).Trim() + wxS( "…" );
                if( text.length() >= full.length() ) continue;
                for( const wxRect& candidate : places( dc.GetTextExtent( text ), 1 ) )
                    if( !collisions( candidate, true ) ) { place.text = text; place.rect = candidate; place.shown = true; break; }
            }
            if( !place.shown )
            {
                // Never left out: the shortest shortening (four characters and "…", the whole caption when it is not longer), where
                // it covers least (a wire rather than a port, a name or another caption, and any of those rather than a block), on
                // its knocked-out background, naming the whole caption on hover.
                if( full.length() > 5 ) place.text = wxString( full.Left( 4 ) ).Trim() + wxS( "…" );
                int fewest = INT_MAX;
                for( const wxRect& candidate : places( dc.GetTextExtent( place.text ), 2 ) )
                    if( int count = collisions( candidate, false ); count < fewest ) { fewest = count; place.rect = candidate; }
                place.shown = true; place.clear = false;
            }
            placed.push_back( wxRect( place.rect ).Inflate( FromDIP( 2 ) ) );
            result.push_back( std::move( place ) );
        }
    m_captionKey = std::move( key ); m_captionCache = result;
    return result;
}

std::vector<wxPoint> RECURSIVE_DIAGRAM_FRAME::connectPreview( const R::LEVEL_LAYOUT& drawn ) const
{
    // Design QA round 2, R2-P2-2: the preview is the path the connection's router would draw for it (rule F4 as revised), from
    // where its first end attaches and leaving it outward. Where that path would run through a block (the router's legs have
    // three segments) or across a port's name, the preview leaves its first end outward for at least 12 pixels and goes around
    // the blocks, 8 pixels clear of them, with as few turns as it can, into its target the way the target's edge faces.
    // Only the preview goes around: the finished connection still takes rule F4's three-segment path, through the block when
    // its target lies behind its source (Rail to DC input). Letting computed paths go around blocks too is a contract change
    // of CN-2 F4 that awaits the integration owner (per-level-editor-design-qa.md, "Design QA round 2 fixes").
    if( m_tool != TOOL::CONNECT || m_historyPreview || !m_connectFrom || !m_ready || !current() ) return {};
    std::optional<D::DiagramEndpointBindingData> toEndpoint = m_connectTo;
    if( !toEndpoint )
        if( auto target = connectTarget( drawn ) )
        {
            toEndpoint.emplace(); toEndpoint->set_block_id( target->owner );
            if( target->port ) { toEndpoint->set_kind( D::DEK_INTERFACE ); toEndpoint->set_interface_id( target->id ); }
            else toEndpoint->set_kind( D::DEK_UNRESOLVED );
        }
    auto centreOf = [&]( const D::DiagramEndpointBindingData& endpoint ) -> int64_t
    {
        if( endpoint.has_interface_id() ) if( const R::PORT* port = drawn.Port( endpoint.block_id(), endpoint.interface_id() ) ) return port->anchor.x;
        if( endpoint.block_id() == drawn.ScopeId() ) return drawn.Anchor( endpoint, 0 ).x;
        R::RECT rect = drawn.Rect( endpoint.block_id() ); return rect.x + rect.w / 2;
    };
    const R::POINT pointer = toDiagram( m_pointer );
    R::ROUTE_END from, to;
    from.at = drawn.Anchor( *m_connectFrom, toEndpoint ? centreOf( *toEndpoint ) : pointer.x );
    std::tie( from.dx, from.dy ) = drawn.Leaving( *m_connectFrom, from.at ); from.block = drawn.OwnBlock( *m_connectFrom );
    if( toEndpoint )
    {
        to.at = drawn.Anchor( *toEndpoint, from.at.x );
        std::tie( to.dx, to.dy ) = drawn.Leaving( *toEndpoint, to.at ); to.block = drawn.OwnBlock( *toEndpoint );
    }
    else to.at = pointer;
    std::vector<std::vector<R::POINT>> others;
    for( const auto& link : drawn.Links() )
        for( int i = 1; i < static_cast<int>( link.endpoints.size() ); ++i ) others.push_back( drawn.Route( link, i ) );
    // The router's result is kept in a local: a range-for over a member of a temporary would outlive the temporary (C++20).
    const auto routed = R::RouteLegs( drawn.Rects(), others, { R::ROUTE_LEG{ from, to } } );
    std::vector<wxPoint> path;
    if( !routed.empty() ) for( const auto& point : routed.front() ) path.push_back( toScreen( point ) );
    if( path.size() < 2 ) return path;
    std::vector<wxRect> blocks, names;
    for( const auto& node : drawn.Nodes() ) blocks.push_back( toScreen( drawn.Rect( node.id ) ) );
    {
        wxClientDC dc( m_canvas );
        for( const auto& node : drawn.Nodes() ) for( const wxRect& name : portNames( dc, drawn, node.id ) ) names.push_back( name );
    }
    for( const auto& [name, rect] : boundaryNames( drawn ) ) names.push_back( rect );
    auto crosses = [&]( const std::vector<wxPoint>& points )
    {
        for( size_t i = 1; i < points.size(); ++i )
        {
            const wxRect run( wxPoint( std::min( points[i - 1].x, points[i].x ), std::min( points[i - 1].y, points[i].y ) ),
                              wxPoint( std::max( points[i - 1].x, points[i].x ), std::max( points[i - 1].y, points[i].y ) ) );
            for( const wxRect& block : blocks ) if( run.Intersects( wxRect( block ).Deflate( 1 ) ) ) return true;
            for( const wxRect& name : names ) if( run.Intersects( name ) ) return true;
        }
        return false;
    };
    if( !crosses( path ) ) return path;
    std::vector<wxRect> obstacles;
    for( const wxRect& block : blocks ) obstacles.push_back( wxRect( block ).Inflate( FromDIP( 8 ) ) );
    for( const wxRect& name : names ) obstacles.push_back( wxRect( name ).Inflate( FromDIP( 2 ) ) );
    const int stub = FromDIP( 12 );
    const wxPoint start = path.front(), goal = path.back();
    const wxPoint startOut = start + wxPoint( from.dx * stub, from.dy * stub );
    const bool fixedEnd = toEndpoint.has_value();
    const wxPoint goalOut = fixedEnd ? goal + wxPoint( to.dx * stub, to.dy * stub ) : goal;
    const wxRect around = wxRect( wxPoint( 0, 0 ), m_canvas->GetClientSize() ).Inflate( FromDIP( 200 ) );
    auto middle = R::OrthogonalDetour( obstacles, startOut, wxPoint( from.dx, from.dy ), goalOut,
                                       fixedEnd ? wxPoint( -to.dx, -to.dy ) : wxPoint(), around );
    if( middle.empty() ) return path;
    std::vector<wxPoint> detour{ start };
    for( const wxPoint& point : middle ) if( detour.back() != point ) detour.push_back( point );
    if( detour.back() != goal ) detour.push_back( goal );
    // Only the corners stay.
    std::vector<wxPoint> corners;
    for( const wxPoint& point : detour )
    {
        if( corners.size() >= 2 )
        {
            const wxPoint &a = corners[corners.size() - 2], &b = corners.back();
            if( ( a.x == b.x && b.x == point.x ) || ( a.y == b.y && b.y == point.y ) ) corners.pop_back();
        }
        corners.push_back( point );
    }
    return corners;
}

void RECURSIVE_DIAGRAM_FRAME::updateCanvasTip()
{
    m_canvasTipQueued = false;
    wxString tip;
    if( m_pointerInside && m_drag == DRAG::NONE && m_ready && !m_process && current() )
        tip = canvasTipAt( layout( current(), !m_historyPreview ), m_pointer );
    if( tip == m_canvasTip ) return;
    m_canvasTip = tip;
    if( tip.empty() ) m_canvas->UnsetToolTip(); else m_canvas->SetToolTip( tip );
}

wxString RECURSIVE_DIAGRAM_FRAME::canvasTipAt( const R::LEVEL_LAYOUT& drawn, const wxPoint& point ) const
{
    // A shortened connection caption shows its whole text, and "+N more" the choices behind it (design QA round 2, R2-P2-1
    // and R2-P2-6).
    for( const auto& place : connectionCaptions( drawn, m_canvas->GetClientSize() ) )
        if( place.shown && place.text != place.full && place.rect.Contains( point ) ) return place.full;
    for( const auto& [block, chips] : drawnChips() )
        if( chips.more && chips.more->Contains( point ) )
            if( const R::NODE* node = drawn.Node( block ) ) return R::MoreToolTip( *node, chips );
    return wxString();
}

std::optional<wxRect> RECURSIVE_DIAGRAM_FRAME::connectHint( const R::LEVEL_LAYOUT& drawn, const std::vector<wxPoint>& preview ) const
{
    if( m_tool != TOOL::CONNECT || m_historyPreview || !m_connectFrom || m_captionKind || preview.empty() ) return std::nullopt;
    wxClientDC dc( m_canvas ); dc.SetFont( GetFont() );
    const wxSize extent = dc.GetTextExtent( _( "Click a port to finish connection" ) ), size( extent.x + 16, extent.y + 10 );
    const wxPoint at = preview.back();
    // Below right of the pointer as before, then above right, below left and above left, then the same places moved down and
    // up in half-steps of the hint's height, nearest first: the first that covers no block, no port name, no part of the preview
    // and not the palette, inside the canvas.
    std::vector<wxRect> spots;
    const int step = size.y / 2 + FromDIP( 4 );
    for( int k = 0; k <= 12; ++k )
        for( int sign : { 1, -1 } )
        {
            if( k == 0 && sign < 0 ) continue;
            const int dy = sign * k * step;
            spots.emplace_back( wxPoint( at.x + 14, at.y + 10 + dy ), size );
            spots.emplace_back( wxPoint( at.x + 14, at.y - 10 - size.y + dy ), size );
            spots.emplace_back( wxPoint( at.x - 14 - size.x, at.y + 10 + dy ), size );
            spots.emplace_back( wxPoint( at.x - 14 - size.x, at.y - 10 - size.y + dy ), size );
        }
    std::vector<wxRect> avoid;
    if( m_paletteShown && m_palette->IsShown() ) avoid.push_back( m_palette->GetRect() );
    for( const auto& node : drawn.Nodes() ) avoid.push_back( toScreen( drawn.Rect( node.id ) ).Inflate( FromDIP( 4 ) ) );
    for( const auto& node : drawn.Nodes() ) for( const wxRect& name : portNames( dc, drawn, node.id ) ) avoid.push_back( name );
    for( const auto& [name, rect] : boundaryNames( drawn ) ) avoid.push_back( rect );
    for( size_t i = 1; i < preview.size(); ++i )
        avoid.emplace_back( wxPoint( std::min( preview[i - 1].x, preview[i].x ) - 2, std::min( preview[i - 1].y, preview[i].y ) - 2 ),
                            wxPoint( std::max( preview[i - 1].x, preview[i].x ) + 2, std::max( preview[i - 1].y, preview[i].y ) + 2 ) );
    const wxRect canvas( wxPoint( 0, 0 ), m_canvas->GetClientSize() );
    for( const wxRect& spot : spots )
        if( canvas.Contains( spot ) && std::none_of( avoid.begin(), avoid.end(), [&]( const wxRect& other ) { return other.Intersects( spot ); } ) )
            return spot;
    for( const wxRect& spot : spots ) if( canvas.Contains( spot ) ) return spot;
    return spots[0];
}

int RECURSIVE_DIAGRAM_FRAME::paletteReserve() const
{
    // The canvas-edge palette keeps its strip; the diagram fits beside it.
    return m_paletteShown && m_palette->IsShown() ? m_palette->GetPosition().x + m_palette->GetSize().x + FromDIP( 12 ) : 0;
}
RECURSIVE_DIAGRAM_FRAME::LABEL_ROOM RECURSIVE_DIAGRAM_FRAME::labelRoom( const R::LEVEL_LAYOUT& drawn ) const
{
    // A port on the level frame names itself outside the frame at a fixed text size (see paint), so fitting
    // leaves that many pixels beside the drawing on the port's side. So does a port of the fallback column whose name
    // goes left of it at the current scale, because the first column's blocks leave it no room on the right (M1-2).
    LABEL_ROOM room;
    wxClientDC dc( m_canvas ); dc.SetFont( GetFont() );
    const auto blocks = blockBoxes( drawn );
    for( const auto& port : drawn.Ports() ) if( port.boundary && !port.placed )
    {
        wxPoint at = toScreen( port.anchor );
        wxRect name = boundaryName( dc, port, at, blocks );
        if( name.x < at.x ) room.left = std::max( room.left, name.width + 14 );
    }
    for( const auto& port : drawn.Ports() ) if( port.boundary && port.placed )
    {
        wxSize extent = dc.GetTextExtent( Text( port.name ) );
        switch( port.side )
        {
        case D::DPS_RIGHT: room.right = std::max( room.right, extent.x + 14 ); break;
        case D::DPS_TOP: room.top = std::max( room.top, extent.y + 12 ); break;
        case D::DPS_BOTTOM: room.bottom = std::max( room.bottom, extent.y + 12 ); break;
        default: room.left = std::max( room.left, extent.x + 14 ); break;
        }
    }
    return room;
}
bool RECURSIVE_DIAGRAM_FRAME::drawingFits() const
{
    if( !current() ) return true;
    auto drawn = layout( current(), !m_historyPreview );
    wxRect drawing = toScreen( drawn.Bounds() ); LABEL_ROOM room = labelRoom( drawn );
    drawing.x -= room.left; drawing.y -= room.top; drawing.width += room.left + room.right; drawing.height += room.top + room.bottom;
    int reserve = paletteReserve(); auto area = m_canvas->GetClientSize();
    return wxRect( reserve, 0, std::max( 0, area.x - reserve ), area.y ).Contains( drawing );
}
void RECURSIVE_DIAGRAM_FRAME::fit()
{
    if( !current() ) return;
    auto drawn = layout( current(), !m_historyPreview );
    R::RECT bounds = drawn.Bounds(); LABEL_ROOM room = labelRoom( drawn );
    double left = bounds.x / double( Q ), top = bounds.y / double( Q ), width = bounds.w / double( Q ), height = bounds.h / double( Q );
    auto area = m_canvas->GetClientSize();
    // Which side a fallback port's name takes depends on the scale (M1-2), so the room beside the drawing is measured again at
    // the scale just fitted; on a change the fit is repeated, and a third fit keeps the room every earlier fit needed.
    for( int pass = 0; ; ++pass )
    {
        int reserve = paletteReserve() + room.left;
        double available = std::max( 64, area.x - reserve - room.right ), tall = std::max( 64, area.y - room.top - room.bottom );
        m_scale = std::max( 0.000000001, std::min( { 1.0, available / width, tall / height } ) );
        m_origin = { left - reserve / m_scale - ( available / m_scale - width ) / 2, top - room.top / m_scale - ( tall / m_scale - height ) / 2 };
        LABEL_ROOM after = labelRoom( drawn );
        if( pass == 2 || ( after.left == room.left && after.top == room.top && after.right == room.right && after.bottom == room.bottom ) ) break;
        room = pass == 0 ? after : LABEL_ROOM{ std::max( room.left, after.left ), std::max( room.top, after.top ),
                                               std::max( room.right, after.right ), std::max( room.bottom, after.bottom ) };
    }
    m_fitted = true; ++m_viewRevision; m_rendered = false; m_canvas->Refresh();
}
void RECURSIVE_DIAGRAM_FRAME::placePaletteAndEditor()
{
    m_palette->Show( m_paletteShown );
    if( m_paletteShown ) m_palette->Move( FromDIP( 12 ), FromDIP( 12 ) );
}

bool RECURSIVE_DIAGRAM_FRAME::drawingAvailable() const
{
    return m_ready && !m_process && !m_diagramHistoryOpen && !m_historyPreview && current() && m_level.has_scope();
}
bool RECURSIVE_DIAGRAM_FRAME::canDelete() const
{
    return drawingAvailable() && ( !m_connectionId.empty() || !m_portId.empty()
                                   || ( !m_selected.empty() && m_selected != m_level.scope().baseline().block_id() ) );
}
void RECURSIVE_DIAGRAM_FRAME::setTool( TOOL tool )
{
    if( tool != TOOL::SELECT && ( tool == TOOL::NOTE ? !m_ready || m_process || m_diagramHistoryOpen || m_historyPreview : !drawingAvailable() ) )
    { refresh(); return; }
    if( m_captionKind ) finishCaption( !m_caption->GetValue().Strip( wxString::both ).empty() );
    m_tool = tool; m_connectFrom.reset(); m_connectTo.reset(); m_notice.clear();
    m_canvas->SetCursor( wxCursor( tool == TOOL::SELECT ? wxCURSOR_ARROW : wxCURSOR_CROSS ) );
    if( tool != TOOL::SELECT ) m_canvas->SetFocus();
    refresh();
}
void RECURSIVE_DIAGRAM_FRAME::removeSelection( bool detach )
{
    if( !canDelete() ) return;
    finishCaption( false );
    m_removalBefore = m_level;
    bool port = !m_portId.empty(), link = !m_connectionId.empty();
    // Removing a block is a layout edit: every other block stays where it is drawn (rule F1a).
    if( !port && !link ) materialize();
    LEVEL working = m_level; m_level = m_removalBefore;
    std::vector<SELECTION> path; if( !findPath( working.scope().baseline().block_id(), path ) ) return;
    REQUEST request; request.set_action( D::RFA_PREPARE_LEVEL_EDIT ); request.set_expected_source_token( m_document.source_token() );
    auto* edit = request.mutable_level_edit(); *edit->mutable_expected_root() = m_document.graph().selected_root();
    for( const auto& step : path ) *edit->add_block_path() = step;
    *edit->mutable_draft() = std::move( working );
    if( port ) { edit->set_kind( D::LECK_REMOVE_INTERFACE ); edit->set_block_id( m_portOwner ); edit->set_interface_id( m_portId ); edit->set_detach_connections( detach ); }
    else if( link ) { edit->set_kind( D::LECK_REMOVE_CONNECTION ); edit->set_connection_id( m_connectionId ); }
    else { edit->set_kind( D::LECK_REMOVE_CHILD ); edit->set_block_id( m_selected ); }
    EditorOrigin( edit->mutable_origin(), "Remove from diagram level" );
    execute( std::move( request ) );
}

void RECURSIVE_DIAGRAM_FRAME::beginCaption( int kind, const wxRect& box, const wxString& value )
{
    m_captionKind = kind;
    // What to type, in the field itself where the platform shows a hint in a focused field (GTK 3 shows it only in an unfocused
    // one, and this field always has the focus; there the status bar says what to type and how to finish, design QA P3 2).
    m_caption->SetHint( kind == 1 ? _( "Block name" ) : kind == 2 ? _( "Connection caption" ) : kind == 3 ? _( "Port name" ) : wxString() );
    int height = m_caption->GetBestSize().y;
    m_caption->SetSize( box.x, box.y, std::max( FromDIP( 140 ), box.width ), height );
    m_caption->ChangeValue( value ); m_caption->Show(); m_caption->Raise(); m_caption->SetFocus(); m_caption->SelectAll();
    m_notice.clear(); ++m_viewRevision; refresh();
}
void RECURSIVE_DIAGRAM_FRAME::finishCaption( bool commit )
{
    if( !m_captionKind ) return;
    int kind = m_captionKind;
    std::string caption = Utf8( m_caption->GetValue().Strip( wxString::both ) );
    if( commit && caption.empty() )
    {
        // A drawn element starts as its caption; it cannot be blank.
        m_notice = Utf8( _( "Type a caption, or press Escape to cancel." ) ); refresh(); m_caption->SetFocus(); return;
    }
    // Keyboard focus returns to the canvas only from the caption itself; focus the person moved elsewhere
    // (for example into the inspector) stays where they put it.
    wxWindow* focus = wxWindow::FindFocus();
    bool returnFocus = !focus || focus == m_caption || focus == m_canvas;
    m_captionKind = 0; m_caption->Hide(); m_notice.clear();
    if( commit )
    {
        if( kind == 1 ) commitNewBlock( caption );
        else if( kind == 2 ) commitNewConnection( caption );
        else if( kind == 3 ) commitNewPort( caption );
        else renameNew( caption );
    }
    m_connectFrom.reset(); m_connectTo.reset();
    ++m_viewRevision; refresh();
    if( !m_closing && returnFocus ) m_canvas->SetFocus();
}
void RECURSIVE_DIAGRAM_FRAME::commitNewBlock( const std::string& caption )
{
    pushUndo(); materialize();
    auto before = layout( current(), true );
    auto* child = m_level.add_new_children();
    child->mutable_selection()->set_block_id( FreshId() ); child->mutable_selection()->set_state_id( FreshId() );
    child->mutable_selection()->set_revision_id( FreshId() ); child->set_requirement_revision_id( FreshId() );
    child->set_implementation_name( "Initial" ); child->set_name( caption ); child->mutable_fields();
    *m_level.mutable_scope()->add_children() = child->selection();
    // New blocks are placed explicitly where the user clicked (rule 9.2).
    R::RECT rect{ snap( m_pendingPoint.x - 120 * Q ), snap( m_pendingPoint.y - 70 * Q ), 240 * Q, 140 * Q };
    auto* row = presentation()->add_blocks(); row->set_block_id( child->selection().block_id() ); encodeRect( rect, row->mutable_rect() );
    encloseInFrame( rect ); followRoutes( before );
    m_selected = child->selection().block_id(); m_connectionId.clear(); m_portOwner.clear(); m_portId.clear(); m_commentId.clear();
    m_lastEffects.Clear(); m_tool = TOOL::SELECT; m_canvas->SetCursor( wxCursor( wxCURSOR_ARROW ) ); changed();
}
void RECURSIVE_DIAGRAM_FRAME::commitNewConnection( const std::string& caption )
{
    if( !m_connectFrom || !m_connectTo ) return;
    pushUndo();
    // A new end on a block takes its own point on the block's edge, which moves the other ends there (rule F2); stored channel
    // routes follow their ends. Computed paths keep apart by themselves (F4a), so no route is stored for the new connection.
    auto before = layout( current(), true );
    auto* link = m_level.add_new_connections();
    link->mutable_selection()->set_connection_id( FreshId() ); link->mutable_selection()->set_state_id( FreshId() );
    link->mutable_selection()->set_revision_id( FreshId() ); link->set_requirement_revision_id( FreshId() );
    link->set_implementation_name( "Initial" ); link->set_name( caption ); link->set_kind( D::DCK_ABSTRACT ); link->mutable_fields();
    *link->add_endpoints() = *m_connectFrom; *link->add_endpoints() = *m_connectTo;
    *m_level.mutable_scope()->mutable_local_diagram()->add_connections() = link->selection();
    std::string id = link->selection().connection_id();
    followRoutes( before );
    m_selected = m_level.scope().baseline().block_id(); m_connectionId = id;
    m_portOwner.clear(); m_portId.clear(); m_commentId.clear(); m_lastEffects.Clear();
    m_tool = TOOL::SELECT; m_canvas->SetCursor( wxCursor( wxCURSOR_ARROW ) ); changed();
}
void RECURSIVE_DIAGRAM_FRAME::commitNewPort( const std::string& caption )
{
    pushUndo(); materialize();
    // A new port changes where unresolved ends on its owner attach (rule F2); channel routes follow.
    auto before = layout( current(), true );
    std::string scope = m_level.scope().baseline().block_id(), owner = m_pendingOwner.empty() ? scope : m_pendingOwner;
    D::DiagramBoundaryInterfaceData port; port.set_id( FreshId() ); port.set_name( caption );
    R::RECT outline;
    if( owner == scope )
    {
        // The frame is stored around what is drawn before the new port joins the boundary.
        materializeFrame(); outline = *layout( current(), true ).Frame();
        *m_level.mutable_scope()->mutable_local_diagram()->add_interfaces() = port;
    }
    else
    {
        materialize(); outline = layout( current(), true ).Rect( owner );
        if( auto* added = newChild( owner ) ) *added->add_interfaces() = port;
        else
        {
            std::string previous = m_selected; m_selected = owner;
            if( DRAFT* child = editBlock( true ) ) *child->mutable_local_diagram()->add_interfaces() = port;
            m_selected = previous;
        }
    }
    auto [side, offset] = project( m_pendingPoint, outline );
    auto* row = presentation()->add_ports(); row->set_block_id( owner ); row->set_interface_id( port.id() ); row->set_side( side );
    row->set_offset( R::FormatUnits( offset ) );
    followRoutes( before );
    m_selected = owner; m_connectionId.clear(); m_portOwner = owner; m_portId = port.id(); m_commentId.clear(); m_lastEffects.Clear();
    m_tool = TOOL::SELECT; m_canvas->SetCursor( wxCursor( wxCURSOR_ARROW ) ); changed();
}
void RECURSIVE_DIAGRAM_FRAME::renameNew( const std::string& caption )
{
    if( caption == selectedName() ) return;
    pushUndo();
    if( !m_connectionId.empty() )
    {
        if( auto* added = newConnection( m_connectionId ) ) added->set_name( caption );
        else if( auto* draft = editConnection( true ) ) draft->set_name( caption );
    }
    else if( auto* added = newChild( m_selected ) ) added->set_name( caption );
    else if( auto* draft = editBlock( true ) ) draft->set_name( caption );
    changed();
}

void RECURSIVE_DIAGRAM_FRAME::paint( wxDC& dc )
{
    // Design QA P1-1: one accent for everything selected or in progress, readable in both themes (3:1 or more against the
    // canvas and against the selected block's fill), and handles that stand out from both.
    const R::CANVAS_COLOURS colours = R::CanvasColours();
    const wxColour &background = colours.background, &foreground = colours.foreground, &muted = colours.muted, &accent = colours.accent;
    // What is drawn may have changed under a pointer that stays still (another level, a rename, a new fit, a history preview),
    // so the canvas's tooltip is found again once this repaint is done (review of design QA round 2).
    if( ( m_pointerInside || !m_canvasTip.empty() ) && !m_canvasTipQueued )
    {
        m_canvasTipQueued = true;
        CallAfter( [this] { if( !m_closing ) updateCanvasTip(); } );
    }
    dc.SetBackground( wxBrush( background ) ); dc.Clear(); dc.SetTextForeground( foreground );
    auto* scope = current(); if( !m_ready || !scope ) { dc.DrawText( m_error.empty() ? _( "Loading diagram…" ) : Text( m_error ), 24, 24 ); return; }
    const bool dark = colours.dark;
    auto drawn = layout( scope, !m_historyPreview );
    auto screen = [this]( const R::POINT& point ) { return toScreen( point ); };
    if( auto frame = drawn.Frame() )
    {
        dc.SetPen( wxPen( muted, 1, wxPENSTYLE_SHORT_DASH ) ); dc.SetBrush( *wxTRANSPARENT_BRUSH ); dc.DrawRectangle( toScreen( *frame ) );
    }
    for( const auto& link : drawn.Links() )
    {
        bool highlighted = link.id == m_connectionId;
        dc.SetPen( wxPen( highlighted ? accent : muted, highlighted ? 2 : 1 ) );
        for( int i = 1; i < static_cast<int>( link.endpoints.size() ); ++i )
        {
            // Port side is a presentation choice toward the peer, not an inferred
            // electrical signal direction (rules F2 and F4).
            auto route = drawn.Route( link, i );
            std::vector<wxPoint> points; for( const auto& point : route ) points.push_back( toScreen( point ) );
            for( size_t segment = 1; segment < points.size(); ++segment ) dc.DrawLine( points[segment - 1], points[segment] );
        }
    }
    // A connection's direction detail shows as arrowheads into the ends it points to (Round A3). The selected
    // connection's arrowheads are drawn last.
    auto arrows = drawnArrows();
    std::stable_partition( arrows.begin(), arrows.end(), [this]( const ARROW& arrow ) { return arrow.connection != m_connectionId; } );
    for( const auto& arrow : arrows )
    {
        bool highlighted = arrow.connection == m_connectionId;
        wxColour colour = highlighted ? accent : muted;
        double dx = arrow.tip.x - arrow.from.x, dy = arrow.tip.y - arrow.from.y, length = std::hypot( dx, dy );
        if( length < 1 ) continue;
        double half = FromDIP( 5 ) / length;
        wxPoint points[] = { arrow.tip, wxPoint( static_cast<int>( std::lround( arrow.from.x - dy * half ) ), static_cast<int>( std::lround( arrow.from.y + dx * half ) ) ),
                             wxPoint( static_cast<int>( std::lround( arrow.from.x + dy * half ) ), static_cast<int>( std::lround( arrow.from.y - dx * half ) ) ) };
        dc.SetPen( wxPen( colour, 1 ) ); dc.SetBrush( wxBrush( colour ) ); dc.DrawPolygon( 3, points );
    }
    const auto handles = selectionHandles( drawn );
    for( const auto& node : drawn.Nodes() )
    {
        wxRect box = toScreen( drawn.Rect( node.id ) ); bool selected = node.id == m_selected && m_connectionId.empty();
        dc.SetPen( wxPen( selected ? accent : muted, selected ? 2 : 1 ) );
        dc.SetBrush( wxBrush( selected ? colours.selectedFill : colours.blockFill ) ); dc.DrawRectangle( box );
        // The block's content sits inside an 8-pixel margin; the outline and its handles stay on the block's edges. Text is
        // drawn whole or not at all (design QA P2-7).
        wxRect inner = wxRect( box ).Deflate( 8 );
        dc.SetClippingRegion( inner );
        auto names = portNames( dc, drawn, node.id );
        R::CAPTION caption = R::BlockCaption( dc, node, inner, GetFont().Bold().Larger() );
        if( !caption.rect.IsEmpty() ) dc.DrawText( caption.text, caption.rect.GetTopLeft() );
        dc.SetFont( GetFont() );
        // A block shows a chip for each chosen or candidate component choice once it has any (Round A4, owner
        // decision n0b2a908b00e78823); until then it is only its caption (owner decision n98a3f3c41084f0ed).
        auto chips = R::LayoutChips( dc, node, inner, chipFont(), caption.rect, names );
        // While the whole-diagram history is open (browsing or previewing a past revision) the canvas takes no
        // presses: the blocks keep their chips, but a Review facets link would do nothing there.
        if( !facetLinkOffered() ) chips.link.reset();
        dc.SetFont( GetFont() );
        if( chips.shown ) R::DrawChips( dc, chips, chipFont(), dark, foreground, linkColour() );
        else if( !node.isNew )
        {
            wxString version = wxString::Format( "v%d", node.version );
            if( wxRect line = R::VersionRect( dc, version, inner, caption.rect ); !line.IsEmpty() ) dc.DrawText( version, line.GetTopLeft() );
        }
        dc.SetFont( GetFont() ); dc.SetTextForeground( foreground );
        dc.DestroyClippingRegion();
        if( selected && !handles.empty() )
        {
            dc.SetPen( wxPen( accent, 1 ) ); dc.SetBrush( wxBrush( colours.handleFill ) );
            for( const auto& handle : handles ) dc.DrawRectangle( handle );
        }
    }
    // Connection captions are drawn after the blocks and their resize handles, each in a clear place beside its connection
    // on a knocked-out background (design QA P2-7).
    dc.SetFont( GetFont() ); dc.SetTextForeground( foreground );
    for( const auto& place : connectionCaptions( drawn, dc.GetSize() ) )
    {
        if( !place.shown ) continue;
        dc.SetPen( *wxTRANSPARENT_PEN ); dc.SetBrush( wxBrush( background ) ); dc.DrawRectangle( wxRect( place.rect ).Inflate( 1 ) );
        dc.DrawText( place.text, place.rect.GetTopLeft() );
    }
    std::optional<TARGET> target = connectTarget( drawn );
    const auto blocks = blockBoxes( drawn );
    for( const auto& port : drawnPorts( drawn, screen ) )
    {
        // Ports are 12 DIP squares at every zoom (design QA P2-6).
        bool selected = port.owner == m_portOwner && port.id == m_portId;
        bool aimed = target && target->port && target->owner == port.owner && target->id == port.id;
        dc.SetPen( wxPen( selected || aimed ? accent : foreground, selected || aimed ? 2 : 1 ) );
        dc.SetBrush( selected || aimed ? wxBrush( colours.selectedFill ) : wxBrush( background ) );
        dc.DrawRectangle( portMark( port.at ) );
        if( port.boundary )
        {
            // A port on the level frame names itself outside the frame, beside its side.
            if( const R::PORT* stored = drawn.Port( port.owner, port.id ) )
                dc.DrawText( Text( port.name ), boundaryName( dc, *stored, port.at, blocks ).GetTopLeft() );
        }
        else if( port.placed || selected )
        {
            // A block's port names itself just inside the block edge, as KiCad labels sheet pins,
            // so it never collides with connection captions drawn outside the block; chips keep clear of it.
            dc.SetFont( chipFont() ); wxRect box = toScreen( drawn.Rect( port.owner ) );
            const R::PORT* stored = drawn.Port( port.owner, port.id );
            D::DiagramPortSide side = stored && stored->placed ? stored->side : ( port.at.x >= box.x + box.width / 2 ? D::DPS_RIGHT : D::DPS_LEFT );
            dc.DrawText( Text( port.name ), R::PortNameRect( dc, Text( port.name ), side, port.at ).GetTopLeft() );
            dc.SetFont( GetFont() );
        }
    }
    const auto& notes = visibleNotes();
    for( int i = 0; i < notes.size(); ++i )
    {
        const auto& note = notes.Get( i );
        dc.SetPen( wxPen( note.id() == m_commentId ? accent : foreground, 2 ) );
        for( const auto& stroke : note.strokes() )
        {
            std::optional<wxPoint> previous;
            for( const auto& point : stroke.points() )
            {
                double x = 0, y = 0; Text( point.x() ).ToDouble( &x ); Text( point.y() ).ToDouble( &y );
                wxPoint now( static_cast<int>( ( x - m_origin.m_x ) * m_scale ), static_cast<int>( ( y - m_origin.m_y ) * m_scale ) );
                if( previous ) dc.DrawLine( *previous, now ); previous = now;
            }
        }
        if( note.target_kind() != D::DAT_CANVAS && !note.has_position() ) continue;
        wxRect box = noteRect( note, i );
        dc.SetPen( wxPen( note.id() == m_commentId ? accent : wxColour( 176, 142, 52 ), 1 ) );
        dc.SetBrush( wxBrush( dark ? wxColour( 76, 66, 34 ) : wxColour( 255, 247, 213 ) ) ); dc.DrawRectangle( box );
        box.Deflate( 8 ); dc.SetClippingRegion( box );
        int y = box.y;
        for( const wxString& line : R::NoteLines( dc, Text( note.text() ), box.width, box.GetBottom() - box.y ) )
        { dc.DrawText( line, box.x, y ); y += dc.GetCharHeight(); }
        dc.DestroyClippingRegion();
    }
    if( m_captionKind == 1 )
    {
        // The block being added, at the point the user chose, while its caption is typed.
        dc.SetPen( wxPen( accent, 2, wxPENSTYLE_SHORT_DASH ) ); dc.SetBrush( *wxTRANSPARENT_BRUSH );
        dc.DrawRectangle( toScreen( R::RECT{ snap( m_pendingPoint.x - 120 * Q ), snap( m_pendingPoint.y - 70 * Q ), 240 * Q, 140 * Q } ) );
    }
    if( m_captionKind && m_caption->IsShown() )
    {
        // The in-place caption field's focus ring in the accent, which the theme's own focus border does not reach in the
        // dark theme (design QA P1-1).
        dc.SetPen( wxPen( accent, 2 ) ); dc.SetBrush( *wxTRANSPARENT_BRUSH );
        dc.DrawRectangle( m_caption->GetRect().Inflate( FromDIP( 3 ) ) );
    }
    if( m_tool == TOOL::CONNECT && !m_historyPreview )
    {
        // The port or block under the pointer where the connection can start or finish (design QA P2-6).
        if( target )
        {
            dc.SetPen( wxPen( accent, 2 ) ); dc.SetBrush( *wxTRANSPARENT_BRUSH );
            if( target->port ) dc.DrawCircle( target->rect.x + target->rect.width / 2, target->rect.y + target->rect.height / 2, target->rect.width / 2 );
            else dc.DrawRectangle( target->rect );
        }
        if( m_connectFrom )
        {
            // The connection in progress on the path its router would draw, or around the blocks in its way (design QA round 2,
            // R2-P2-2), ending in an open ring at the pointer that the crosshair does not hide (P3 8), and the hint shared by the
            // toolbar strip and the palette, placed clear of the blocks (P3 1).
            std::vector<wxPoint> path = connectPreview( drawn );
            if( path.size() >= 2 )
            {
                dc.SetPen( wxPen( accent, 2, wxPENSTYLE_SHORT_DASH ) ); dc.DrawLines( static_cast<int>( path.size() ), path.data() );
                dc.SetPen( wxPen( accent, 2 ) ); dc.SetBrush( *wxTRANSPARENT_BRUSH ); dc.DrawCircle( path.back(), FromDIP( 6 ) );
            }
            if( auto spot = connectHint( drawn, path ) )
            {
                wxString hint = _( "Click a port to finish connection" );
                wxRect callout = *spot;
                wxColour fill = accent.ChangeLightness( dark ? 45 : 185 );
                dc.SetPen( wxPen( accent, 1 ) ); dc.SetBrush( wxBrush( fill ) );
                dc.SetTextForeground( R::Readable( foreground, { fill }, 4.5 ) );
                dc.DrawRoundedRectangle( callout, 4 ); dc.DrawText( hint, callout.x + 8, callout.y + 5 );
                dc.SetTextForeground( foreground );
            }
        }
    }
    m_rendered = true;
}

void RECURSIVE_DIAGRAM_FRAME::click( wxMouseEvent& event )
{
    ++m_canvasPresses;
    if( !m_ready || m_process || !current() || m_diagramHistoryOpen ) return;
    wxPoint point = event.GetPosition(); m_pointer = point; m_pointerInside = true;
    // A new press ends any drag whose release was not delivered; a press the canvas receives only through
    // its mouse capture, outside its own area, belongs to another control and changes nothing here.
    if( m_drag != DRAG::NONE ) release();
    if( !wxRect( wxPoint( 0, 0 ), m_canvas->GetClientSize() ).Contains( point ) ) return;
    if( m_captionKind )
    {
        // Clicking elsewhere keeps a typed caption, or cancels an empty one.
        finishCaption( !m_caption->GetValue().Strip( wxString::both ).empty() );
        if( m_captionKind ) return;
    }
    m_canvas->SetFocus();
    if( m_historyPreview ) return;
    auto drawn = layout( current(), true );
    R::POINT at = toDiagram( point );
    std::string scope = m_level.scope().baseline().block_id();
    if( m_tool == TOOL::NOTE )
    {
        pushUndo(); auto* note = m_level.mutable_scope()->mutable_local_diagram()->add_annotations();
        note->set_id( FreshId() ); note->set_role( D::DAR_COMMENT ); note->set_target_kind( D::DAT_CANVAS ); note->set_units( "diagram-unit" );
        note->mutable_position()->set_x( std::to_string( event.GetX() / m_scale + m_origin.m_x ) );
        note->mutable_position()->set_y( std::to_string( event.GetY() / m_scale + m_origin.m_y ) );
        EditorOrigin( note->mutable_origin(), "Place diagram comment" );
        m_selected = scope; m_connectionId.clear(); m_portOwner.clear(); m_portId.clear(); m_commentId = note->id(); m_newComment = false;
        m_tool = TOOL::SELECT; m_canvas->SetCursor( wxCursor( wxCURSOR_ARROW ) ); changed(); m_comments->SetFocus(); return;
    }
    if( m_tool == TOOL::ADD_BLOCK )
    {
        m_pendingPoint = at;
        wxRect box = toScreen( R::RECT{ snap( at.x - 120 * Q ), snap( at.y - 70 * Q ), 240 * Q, 140 * Q } );
        beginCaption( 1, wxRect( box.x + FromDIP( 8 ), box.y + FromDIP( 16 ), box.width - FromDIP( 16 ), -1 ), wxEmptyString );
        return;
    }
    if( m_tool == TOOL::ADD_PORT )
    {
        std::string owner;
        for( const auto& node : drawn.Nodes() ) if( toScreen( drawn.Rect( node.id ) ).Inflate( FromDIP( 10 ) ).Contains( point ) ) owner = node.id;
        m_pendingOwner = owner.empty() ? scope : owner; m_pendingPoint = at;
        beginCaption( 3, wxRect( point.x + FromDIP( 10 ), point.y - FromDIP( 14 ), FromDIP( 160 ), -1 ), wxEmptyString );
        return;
    }
    if( m_tool == TOOL::CONNECT )
    {
        std::optional<D::DiagramEndpointBindingData> endpoint;
        if( const R::PORT* port = portAt( drawn, point ) )
        { endpoint.emplace(); endpoint->set_kind( D::DEK_INTERFACE ); endpoint->set_block_id( port->blockId ); endpoint->set_interface_id( port->interfaceId ); }
        else if( auto block = blockAt( drawn, point ); !block.empty() )
        { endpoint.emplace(); endpoint->set_kind( D::DEK_UNRESOLVED ); endpoint->set_block_id( block ); }
        if( !endpoint )
        {
            m_notice = Utf8( m_connectFrom ? _( "Finish the connection on a block or port, or press Escape to cancel." )
                                           : _( "Start the connection on a block or port." ) );
            refresh(); return;
        }
        if( !m_connectFrom )
        {
            // The connection's first end becomes the selection and fills the inspector, from the palette or the strip alike, so
            // what is selected is what the person is connecting (design QA round 2, R2-P2-3); the finished connection is
            // selected in its turn (commitNewConnection).
            m_connectFrom = endpoint; m_notice.clear();
            if( endpoint->has_interface_id() ) selectPort( endpoint->block_id(), endpoint->interface_id() );
            else select( endpoint->block_id() );
            refresh(); return;
        }
        if( endpoint->block_id() == m_connectFrom->block_id() && endpoint->interface_id() == m_connectFrom->interface_id() )
        { m_notice = Utf8( _( "Connect two different blocks or ports." ) ); refresh(); return; }
        m_connectTo = endpoint;
        wxPoint a = toScreen( drawn.Anchor( *m_connectFrom, drawn.Anchor( *m_connectTo, 0 ).x ) ), b = toScreen( drawn.Anchor( *m_connectTo, 0 ) );
        beginCaption( 2, wxRect( ( a.x + b.x ) / 2 - FromDIP( 90 ), ( a.y + b.y ) / 2 - FromDIP( 36 ), FromDIP( 180 ), -1 ), wxEmptyString );
        return;
    }
    const auto& notes = visibleNotes();
    for( int i = notes.size() - 1; i >= 0; --i )
    {
        const auto& note = notes.Get( i );
        if( note.target_kind() != D::DAT_CANVAS || !noteRect( note, i ).Contains( point ) ) continue;
        select( scope ); m_commentId = note.id(); m_newComment = false; refresh();
        if( event.LeftDClick() ) { m_comments->SetFocus(); return; }
        wxRect box = noteRect( note, i ); m_noteStartX = box.x / m_scale + m_origin.m_x; m_noteStartY = box.y / m_scale + m_origin.m_y;
        m_dragStart = point; m_dragBefore = m_level; m_drag = DRAG::NOTE; m_dragMoved = false;
        if( !m_canvas->HasCapture() ) m_canvas->CaptureMouse();
        return;
    }
    if( int handle = handleAt( point ); handle >= 0 )
    {
        m_drag = DRAG::RESIZE; m_dragHandle = handle; m_dragStart = point; m_dragBefore = m_level; m_dragMoved = false;
        m_dragRect = drawn.Rect( m_selected ); if( !m_canvas->HasCapture() ) m_canvas->CaptureMouse(); return;
    }
    if( const R::PORT* port = portAt( drawn, point ) )
    {
        std::string owner = port->blockId, id = port->interfaceId;
        selectPort( owner, id );
        m_drag = DRAG::PORT; m_dragStart = point; m_dragBefore = m_level; m_dragMoved = false;
        if( !m_canvas->HasCapture() ) m_canvas->CaptureMouse(); return;
    }
    // Round A4: a block's Review facets link opens its facet detail in the inspector.
    for( const auto& [block, chips] : drawnChips() )
        if( chips.link && chips.link->Contains( point ) ) { reviewFacets( block ); return; }
    if( auto block = blockAt( drawn, point ); !block.empty() )
    {
        if( event.LeftDClick() )
        {
            if( newChild( block ) ) { select( block ); beginCaption( 4, toScreen( drawn.Rect( block ) ).Deflate( FromDIP( 8 ) ), Text( selectedName() ) ); }
            else navigate( block );
            return;
        }
        select( block );
        m_drag = DRAG::MOVE; m_dragStart = point; m_dragBefore = m_level; m_dragMoved = false; m_dragRect = drawn.Rect( block );
        if( !m_canvas->HasCapture() ) m_canvas->CaptureMouse(); return;
    }
    if( auto link = connectionAt( drawn, point ); !link.empty() ) { selectConnection( link ); return; }
    select( scope );
}

void RECURSIVE_DIAGRAM_FRAME::motion( wxMouseEvent& event )
{
    m_pointer = event.GetPosition(); m_pointerInside = true; ++m_canvasMotions;
    // A shortened connection caption shows its whole text on hover, and "+N more" the choices behind it (design QA round 2).
    updateCanvasTip();
    // The Connect tool follows the pointer: its preview, and the port or block it would start or finish on.
    if( m_tool == TOOL::CONNECT && !m_connectTo ) { m_rendered = false; m_canvas->Refresh(); }
    if( m_tool == TOOL::SELECT && m_drag == DRAG::NONE && m_ready && !m_process && facetLinkOffered() )
    {
        bool link = false;
        for( const auto& [block, chips] : drawnChips() ) link |= chips.link && chips.link->Contains( m_pointer );
        m_canvas->SetCursor( wxCursor( link ? wxCURSOR_HAND : wxCURSOR_ARROW ) );
    }
    if( m_drag == DRAG::NONE || !event.Dragging() || m_process ) return;
    dragTo( m_pointer );
}

void RECURSIVE_DIAGRAM_FRAME::dragTo( const wxPoint& point )
{
    if( m_drag == DRAG::NONE || m_process ) return;
    m_pointer = point;
    wxPoint delta = m_pointer - m_dragStart;
    if( !m_dragMoved && std::abs( delta.x ) + std::abs( delta.y ) < FromDIP( 4 ) ) return;
    if( !m_dragMoved ) m_dragGeometry = m_dragBefore.scope().local_diagram().presentation().SerializeAsString();
    m_dragMoved = true;
    if( m_drag == DRAG::NOTE )
    {
        for( auto& note : *m_level.mutable_scope()->mutable_local_diagram()->mutable_annotations() ) if( note.id() == m_commentId )
        {
            note.mutable_position()->set_x( std::to_string( m_noteStartX + delta.x / m_scale ) );
            note.mutable_position()->set_y( std::to_string( m_noteStartY + delta.y / m_scale ) );
        }
        m_rendered = false; m_canvas->Refresh(); return;
    }
    // Recompute from the state before the drag, so materialization and the edit are one step.
    m_level = m_dragBefore; materialize();
    auto before = layout( current(), true );
    int64_t dx = snap( std::llround( delta.x / m_scale * Q ) ), dy = snap( std::llround( delta.y / m_scale * Q ) );
    auto place = [this]( const std::string& id, const R::RECT& rect )
    {
        auto* view = presentation();
        for( auto& row : *view->mutable_blocks() ) if( row.block_id() == id ) { encodeRect( rect, row.mutable_rect() ); return; }
        auto* row = view->add_blocks(); row->set_block_id( id ); encodeRect( rect, row->mutable_rect() );
    };
    if( m_drag == DRAG::MOVE ) { R::RECT rect = m_dragRect; rect.x += dx; rect.y += dy; place( m_selected, rect ); encloseInFrame( rect ); }
    else if( m_drag == DRAG::RESIZE )
    {
        int64_t left = m_dragRect.x, top = m_dragRect.y, right = m_dragRect.Right(), bottom = m_dragRect.Bottom();
        if( m_dragHandle == 0 || m_dragHandle == 6 || m_dragHandle == 7 ) left += dx;
        if( m_dragHandle >= 2 && m_dragHandle <= 4 ) right += dx;
        if( m_dragHandle <= 2 ) top += dy;
        if( m_dragHandle >= 4 && m_dragHandle <= 6 ) bottom += dy;
        // Resize never shrinks a block below its placed ports (rule 9.2).
        int64_t minimumWidth = 80 * Q, minimumHeight = 50 * Q;
        for( const auto& row : m_level.scope().local_diagram().presentation().ports() ) if( row.block_id() == m_selected )
        {
            int64_t offset = 0; if( !R::ParseUnits( row.offset(), offset ) ) continue;
            if( row.side() == D::DPS_LEFT || row.side() == D::DPS_RIGHT ) minimumHeight = std::max( minimumHeight, offset + 10 * Q );
            else minimumWidth = std::max( minimumWidth, offset + 10 * Q );
        }
        if( right - left < minimumWidth ) { if( m_dragHandle == 0 || m_dragHandle == 6 || m_dragHandle == 7 ) left = right - minimumWidth; else right = left + minimumWidth; }
        if( bottom - top < minimumHeight ) { if( m_dragHandle <= 2 ) top = bottom - minimumHeight; else bottom = top + minimumHeight; }
        place( m_selected, { left, top, right - left, bottom - top } ); encloseInFrame( { left, top, right - left, bottom - top } );
    }
    else if( m_drag == DRAG::PORT )
    {
        std::string scope = m_level.scope().baseline().block_id();
        if( m_portOwner == scope ) materializeFrame();
        auto drawn = layout( current(), true );
        auto outline = m_portOwner == scope ? drawn.Frame() : std::optional<R::RECT>( drawn.Rect( m_portOwner ) );
        if( outline )
        {
            auto [side, offset] = project( toDiagram( m_pointer ), *outline );
            auto* view = presentation(); D::DiagramPortPlacementData* row = nullptr;
            for( auto& item : *view->mutable_ports() ) if( item.block_id() == m_portOwner && item.interface_id() == m_portId ) row = &item;
            if( !row ) { row = view->add_ports(); row->set_block_id( m_portOwner ); row->set_interface_id( m_portId ); }
            row->set_side( side ); row->set_offset( R::FormatUnits( offset ) );
        }
    }
    followRoutes( before );
    // Each distinct geometry the drag reaches is one position the level must be laid out for; a journey compares the
    // layouts a drag made with these positions (the dense-level check).
    if( std::string geometry = m_level.scope().local_diagram().presentation().SerializeAsString(); geometry != m_dragGeometry )
    { m_dragGeometry = std::move( geometry ); ++m_dragPositions; }
    m_rendered = false; m_canvas->Refresh();
}

void RECURSIVE_DIAGRAM_FRAME::release()
{
    if( m_canvas->HasCapture() ) m_canvas->ReleaseMouse();
    if( m_drag == DRAG::NONE ) return;
    DRAG drag = m_drag; m_drag = DRAG::NONE;
    if( !m_dragMoved || m_level.SerializeAsString() == m_dragBefore.SerializeAsString() ) { m_level = m_dragBefore; m_dragMoved = false; refresh(); return; }
    if( drag == DRAG::NOTE )
        for( auto& note : *m_level.mutable_scope()->mutable_local_diagram()->mutable_annotations() ) if( note.id() == m_commentId ) EditorOrigin( note.mutable_origin(), "Move diagram comment" );
    m_dragMoved = false; m_undo.push_back( m_dragBefore ); m_redo.clear(); m_notice.clear(); m_lastEffects.Clear(); changed();
}

bool RECURSIVE_DIAGRAM_FRAME::canvasKey( wxKeyEvent& event )
{
    if( !m_ready || m_process || !current() || m_diagramHistoryOpen ) return false;
    const auto& children = m_level.scope().children();
    if( event.GetKeyCode() == WXK_TAB )
    { m_canvas->Navigate( event.ShiftDown() ? wxNavigationKeyEvent::IsBackward : wxNavigationKeyEvent::IsForward ); return true; }
    int index = -1;
    for( int i = 0; i < children.size(); ++i ) if( children.Get( i ).block_id() == m_selected ) index = i;
    if( event.GetKeyCode() == WXK_RIGHT || event.GetKeyCode() == WXK_DOWN )
    { ++m_navigationInputRevision; if( children.size() ) select( children.Get( ( index + 1 ) % children.size() ).block_id() ); return true; }
    if( event.GetKeyCode() == WXK_LEFT || event.GetKeyCode() == WXK_UP )
    { ++m_navigationInputRevision; if( children.size() ) select( children.Get( ( index + children.size() - 1 ) % children.size() ).block_id() ); return true; }
    if( event.GetKeyCode() == WXK_RETURN ) { navigate( m_selected ); return true; }
    // Letter shortcuts are plain letters: Ctrl+C, Ctrl+B or Alt+P belong to other commands and never switch tools.
    bool plain = !event.HasAnyModifiers();
    if( plain && event.GetKeyCode() == 'N' ) { setTool( TOOL::NOTE ); return true; }
    if( plain && event.GetKeyCode() == 'B' ) { setTool( TOOL::ADD_BLOCK ); return true; }
    if( plain && event.GetKeyCode() == 'C' ) { setTool( TOOL::CONNECT ); return true; }
    if( plain && event.GetKeyCode() == 'P' ) { setTool( TOOL::ADD_PORT ); return true; }
    if( event.GetKeyCode() == WXK_DELETE ) { removeSelection(); return true; }
    if( event.GetKeyCode() == WXK_F2 && drawingAvailable() && ( !m_connectionId.empty() || !m_selected.empty() ) )
    {
        auto drawn = layout( current(), true );
        wxRect box = !m_connectionId.empty() || m_selected == m_level.scope().baseline().block_id()
            ? wxRect( m_canvas->GetClientSize().x / 2 - FromDIP( 100 ), FromDIP( 16 ), FromDIP( 200 ), -1 )
            : toScreen( drawn.Rect( m_selected ) ).Deflate( FromDIP( 8 ) );
        beginCaption( 4, box, Text( selectedName() ) ); return true;
    }
    const auto& connections = m_level.scope().local_diagram().connections();
    if( plain && event.GetKeyCode() == 'L' && connections.size() )
    {
        int selected = -1;
        for( int i = 0; i < connections.size(); ++i ) if( connections.Get( i ).connection_id() == m_connectionId ) selected = i;
        selectConnection( connections.Get( ( selected + 1 ) % connections.size() ).connection_id() ); return true;
    }
    if( event.GetKeyCode() == WXK_BACK && m_path.size() > 1 ) { navigate( m_path[m_path.size() - 2].block_id() ); return true; }
    return false;
}
