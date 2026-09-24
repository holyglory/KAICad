/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#include "recursive_diagram_canvas.h"
#include "recursive_diagram_frame.h"
#include <bitmaps.h>
#include <kiid.h>
#include <algorithm>
#include <chrono>
#include <climits>
#include <cmath>
#include <cstdlib>
#include <wx/button.h>
#include <wx/control.h>
#include <wx/dc.h>
#include <wx/dcbuffer.h>
#include <wx/dcclient.h>
#include <wx/menu.h>
#include <wx/textctrl.h>
#include <wx/sizer.h>
#include <wx/settings.h>
#include <wx/statline.h>
#include <wx/tglbtn.h>

namespace RECURSIVE_DIAGRAM
{
std::string FreshId() { return Utf8( KIID().AsString() ); }

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
    // Rule F2: the edge facing the peer, at the legacy offset.
    return { rect.x + rect.w / 2 < peerX ? rect.Right() : rect.x, rect.y + ( index + 1 ) * rect.h / std::max<int64_t>( 2, count + 1 ) };
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
    const auto& first = link.endpoints[0]; const auto& second = link.endpoints[index];
    POINT from = Anchor( first, centreX( second ) ), to = Anchor( second, centreX( first ) );
    for( const auto& route : m_routes )
        if( route.connection_id() == link.id && route.endpoint_index() == static_cast<unsigned>( index ) )
        {
            std::vector<POINT> points{ from };
            for( const auto& waypoint : route.waypoints() )
            { POINT point; if( ParseUnits( waypoint.x(), point.x ) && ParseUnits( waypoint.y(), point.y ) ) points.push_back( point ); }
            points.push_back( to );
            return points;
        }
    // Rule F4: the legacy three-segment path.
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

wxRect CaptionRect( wxDC& dc, const NODE& node, const wxRect& inner, const wxFont& captionFont )
{
    dc.SetFont( captionFont );
    wxSize extent = dc.GetTextExtent( Text( node.name ) );
    return wxRect( wxPoint( inner.x + 10, inner.y + 24 ), extent ).Intersect( inner );
}

BLOCK_CHIPS LayoutChips( wxDC& dc, const NODE& node, const wxRect& box, const wxFont& small, const wxRect& caption )
{
    BLOCK_CHIPS result; result.caption = caption;
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
    // aBox is the block's content area: the caption sits at its top (24 to about 46 pixels down).
    const int height = dc.GetCharHeight() + 6, gap = 3, left = box.x + 10, width = box.width - 20, top = box.y + 50;
    const int mark = height - 9, textLeft = 6 + mark + 6;
    int bottom = box.GetBottom() - 1;
    wxSize linkSize = dc.GetTextExtent( _( "Review facets" ) );
    if( linkSize.x <= width && bottom - linkSize.y >= top )
    { result.link = wxRect( left, bottom - linkSize.y, linkSize.x, linkSize.y ); bottom = result.link->y - gap; }
    int rows = bottom - top >= height ? ( bottom - top + gap ) / ( height + gap ) : 0;
    size_t shown = std::min<size_t>( chosen.size(), static_cast<size_t>( std::max( 0, rows ) ) );
    auto moreText = []( size_t count ) { return wxString::Format( _( "+%u more" ), static_cast<unsigned>( count ) ); };
    int moreWidth = 0; bool ownRow = false;
    if( shown < chosen.size() )
    {
        moreWidth = dc.GetTextExtent( moreText( chosen.size() - shown ) ).x + 16;
        // "+N more" shares the last row when a chip can keep some text beside it; otherwise it takes that row.
        if( shown > 0 && moreWidth + gap + textLeft + 40 > width )
        { --shown; ownRow = true; moreWidth = dc.GetTextExtent( moreText( chosen.size() - shown ) ).x + 16; }
    }
    for( size_t i = 0; i < shown; ++i )
    {
        const auto& choice = *Facet( node.definition, chosen[i] );
        bool shareRow = i + 1 == shown && shown < chosen.size() && !ownRow;
        int available = shareRow ? width - moreWidth - gap : width;
        wxString text = FacetLabel( chosen[i] ) + wxS( ": " ) + ChoiceValue( choice, wxS( " / " ) );
        text = wxControl::Ellipsize( text, dc, wxELLIPSIZE_END, std::max( 0, available - textLeft - 6 ) );
        int chipWidth = std::min( available, textLeft + dc.GetTextExtent( text ).x + 8 );
        result.chips.push_back( { chosen[i], choice.state(), text, wxRect( left, top + static_cast<int>( i ) * ( height + gap ), chipWidth, height ) } );
    }
    result.hidden = static_cast<unsigned>( chosen.size() - result.chips.size() );
    if( result.hidden && moreWidth <= width )
    {
        if( !result.chips.empty() && !ownRow )
            result.more = wxRect( result.chips.back().rect.GetRight() + 1 + gap, result.chips.back().rect.y, moreWidth, height );
        else if( rows > 0 )
            result.more = wxRect( left, top + static_cast<int>( result.chips.size() ) * ( height + gap ), moreWidth, height );
    }
    if( result.chips.empty() && !result.more && !chosen.empty() )
    {
        // Too small for a chip: the choices' state marks follow the caption where they fit.
        const int size = std::max( 6, std::min( 12, mark ) ), step = size + 3;
        int x = box.GetRight() - static_cast<int>( chosen.size() ) * step;
        if( x >= std::max( captionRight + 6, left ) && box.y + 29 + size <= box.GetBottom() )
            for( size_t i = 0; i < chosen.size(); ++i )
                result.marks.push_back( { chosen[i], Facet( node.definition, chosen[i] )->state(), wxRect( x + static_cast<int>( i ) * step, box.y + 29, size, size ) } );
    }
    return result;
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
        wxWindow( parent, wxID_ANY, wxDefaultPosition, wxDefaultSize, wxBORDER_NONE | wxWANTS_CHARS | wxFULL_REPAINT_ON_RESIZE ),
        m_facet( facet ), m_open( std::move( open ) )
{
    static const char* NAMES[FACETS] = { "Purpose", "Type", "Manufacturer", "Family", "Model", "OrderablePart", "Package" };
    SetName( wxString( "RecursiveFacetRow" ) + NAMES[std::clamp( facet, 0, FACETS - 1 )] );
    SetBackgroundStyle( wxBG_STYLE_PAINT );
    SetMinSize( FromDIP( wxSize( 200, 28 ) ) );
    Bind( wxEVT_PAINT, [this]( wxPaintEvent& ) { paint(); } );
    Bind( wxEVT_LEFT_DOWN, [this]( wxMouseEvent& ) { SetFocus(); if( m_open ) m_open( m_facet ); } );
    Bind( wxEVT_ENTER_WINDOW, [this]( wxMouseEvent& ) { m_hover = true; Refresh(); } );
    Bind( wxEVT_LEAVE_WINDOW, [this]( wxMouseEvent& ) { m_hover = false; Refresh(); } );
    Bind( wxEVT_SET_FOCUS, [this]( wxFocusEvent& event ) { Refresh(); event.Skip(); } );
    Bind( wxEVT_KILL_FOCUS, [this]( wxFocusEvent& event ) { Refresh(); event.Skip(); } );
    Bind( wxEVT_KEY_DOWN, [this]( wxKeyEvent& event )
          {
              int key = event.GetKeyCode();
              if( !event.HasAnyModifiers() && ( key == WXK_RETURN || key == WXK_NUMPAD_ENTER || key == WXK_SPACE ) )
              { if( m_open ) m_open( m_facet ); return; }
              if( key == WXK_TAB ) { Navigate( event.ShiftDown() ? wxNavigationKeyEvent::IsBackward : wxNavigationKeyEvent::IsForward ); return; }
              event.Skip();
          } );
}

void FACET_ROW::SetChoice( const D::DefinitionTextChoiceData& choice, bool open )
{
    wxString value = ChoiceValue( choice, wxS( ", " ) );
    if( choice.state() == D::DCSD_CANDIDATES )
        value += choice.values_size() == 1 ? _( " (candidate)" ) : _( " (candidates)" );
    if( value == m_value && choice.state() == m_state && open == m_isOpen ) return;
    m_value = value; m_state = choice.state(); m_isOpen = open;
    SetLabel( FacetLabel( m_facet ) + wxS( ": " ) + value ); SetToolTip( value );
    Refresh();
}

void FACET_ROW::paint()
{
    wxAutoBufferedPaintDC dc( this );
    wxColour background = GetParent()->GetBackgroundColour();
    wxColour foreground = wxSystemSettings::GetColour( wxSYS_COLOUR_WINDOWTEXT );
    wxColour muted = wxSystemSettings::GetColour( wxSYS_COLOUR_GRAYTEXT );
    wxColour accent = wxSystemSettings::GetColour( wxSYS_COLOUR_HIGHLIGHT );
    bool dark = darkBackground( background );
    wxRect area( GetClientSize() );
    dc.SetBackground( wxBrush( background ) ); dc.Clear();
    if( m_isOpen || m_hover )
    {
        dc.SetPen( *wxTRANSPARENT_PEN );
        dc.SetBrush( wxBrush( m_isOpen ? accent.ChangeLightness( dark ? 60 : 180 ) : background.ChangeLightness( dark ? 115 : 95 ) ) );
        dc.DrawRectangle( area );
    }
    // A thin rule below each row, as the sketch's table.
    dc.SetPen( wxPen( background.ChangeLightness( dark ? 135 : 85 ), 1 ) );
    dc.DrawLine( area.x, area.GetBottom(), area.GetRight() + 1, area.GetBottom() );
    dc.SetFont( GetFont() );
    // The label column fits the longest facet name, so every row's value starts at the same place.
    int text = ( area.height - dc.GetCharHeight() ) / 2, labelWidth = FromDIP( 90 );
    for( int facet = 0; facet < FACETS; ++facet ) labelWidth = std::max( labelWidth, dc.GetTextExtent( FacetLabel( facet ) ).x + FromDIP( 20 ) );
    dc.SetTextForeground( m_isOpen ? foreground : muted.ChangeLightness( dark ? 130 : 70 ) );
    dc.DrawText( wxControl::Ellipsize( FacetLabel( m_facet ), dc, wxELLIPSIZE_END, labelWidth - FromDIP( 12 ) ), FromDIP( 6 ), text );
    int mark = std::min( FromDIP( 14 ), area.height - FromDIP( 10 ) );
    DrawChoiceMark( dc, wxRect( labelWidth, ( area.height - mark ) / 2, mark, mark ), m_state, dark );
    wxString chevron = wxS( "›" ); int chevronWidth = dc.GetTextExtent( chevron ).x;
    int valueLeft = labelWidth + mark + FromDIP( 6 ), valueWidth = area.width - valueLeft - chevronWidth - FromDIP( 14 );
    dc.SetTextForeground( foreground );
    dc.DrawText( wxControl::Ellipsize( m_value, dc, wxELLIPSIZE_END, std::max( 0, valueWidth ) ), valueLeft, text );
    dc.SetTextForeground( muted ); dc.DrawText( chevron, area.width - chevronWidth - FromDIP( 8 ), text );
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

wxAnyButton* ToolButton( wxWindow* parent, const wxString& label, BITMAPS bitmap, const char* name, bool toggle )
{
    wxAnyButton* button = toggle ? static_cast<wxAnyButton*>( new wxToggleButton( parent, wxID_ANY, label, wxDefaultPosition, wxDefaultSize, wxBORDER_NONE ) )
                                 : static_cast<wxAnyButton*>( new wxButton( parent, wxID_ANY, label, wxDefaultPosition, wxDefaultSize, wxBORDER_NONE ) );
    button->SetBitmap( KiBitmapBundle( bitmap ) ); button->SetBitmapPosition( wxTOP ); button->SetName( name ); button->SetToolTip( label );
    button->SetMinSize( parent->FromDIP( wxSize( 86, 50 ) ) );
    return button;
}

TOOL_PALETTE::TOOL_PALETTE( wxWindow* parent, ACTIONS actions ) :
        wxPanel( parent, wxID_ANY, wxDefaultPosition, wxDefaultSize, wxBORDER_NONE | wxTAB_TRAVERSAL ), m_actions( std::move( actions ) )
{
    SetName( "DiagramToolPalette" );
    wxColour window = wxSystemSettings::GetColour( wxSYS_COLOUR_WINDOW );
    bool dark = window.Red() + window.Green() + window.Blue() < 384;
    SetBackgroundColour( window.ChangeLightness( dark ? 125 : 96 ) );
    // A quiet rounded outline, like the sketch's floating palette, rather than a black frame.
    wxColour outline = window.ChangeLightness( dark ? 175 : 78 );
    Bind( wxEVT_PAINT, [this, outline]( wxPaintEvent& )
          {
              wxPaintDC dc( this ); dc.SetPen( wxPen( outline, 1 ) ); dc.SetBrush( *wxTRANSPARENT_BRUSH );
              dc.DrawRoundedRectangle( GetClientRect(), FromDIP( 4 ) );
          } );
    auto* column = new wxBoxSizer( wxVERTICAL );
    auto addTool = [&]( TOOL tool, const wxString& label, BITMAPS bitmap, const char* name )
    {
        auto* button = static_cast<wxToggleButton*>( ToolButton( this, label, bitmap, name, true ) );
        button->Bind( wxEVT_TOGGLEBUTTON, [this, tool]( wxCommandEvent& ) { if( m_actions.choose ) m_actions.choose( tool ); } );
        column->Add( button, 0, wxEXPAND | wxLEFT | wxRIGHT | wxTOP, FromDIP( 4 ) );
        m_tools.emplace_back( tool, button );
    };
    addTool( TOOL::SELECT, _( "Select" ), BITMAPS::cursor, "DiagramPaletteSelect" );
    addTool( TOOL::ADD_BLOCK, _( "Add block" ), BITMAPS::add_rectangle, "DiagramPaletteAddBlock" );
    addTool( TOOL::CONNECT, _( "Connect" ), BITMAPS::add_line, "DiagramPaletteConnect" );
    addTool( TOOL::ADD_PORT, _( "Place port" ), BITMAPS::add_hierar_pin, "DiagramPalettePlacePort" );
    m_delete = static_cast<wxButton*>( ToolButton( this, _( "Delete" ), BITMAPS::trash, "DiagramPaletteDelete", false ) );
    m_delete->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { if( m_actions.remove ) m_actions.remove(); } );
    column->Add( m_delete, 0, wxEXPAND | wxLEFT | wxRIGHT | wxTOP, FromDIP( 4 ) );
    auto* line = new wxStaticLine( this ); line->SetName( "staticLine" );
    column->Add( line, 0, wxEXPAND | wxALL, FromDIP( 6 ) );
    m_undo = static_cast<wxButton*>( ToolButton( this, _( "Undo" ), BITMAPS::undo, "DiagramPaletteUndo", false ) );
    m_undo->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { if( m_actions.undo ) m_actions.undo(); } );
    column->Add( m_undo, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, FromDIP( 4 ) );
    SetSizerAndFit( column );
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
/// How far two drawn paths run along each other: the summed length of their collinear horizontal and
/// vertical segments. Crossing at a point or meeting at a shared end point does not count.
int64_t sharedLength( const std::vector<R::POINT>& path, const std::vector<std::vector<R::POINT>>& others )
{
    int64_t total = 0;
    for( size_t i = 1; i < path.size(); ++i )
        for( const auto& other : others )
            for( size_t j = 1; j < other.size(); ++j )
            {
                const R::POINT &a = path[i - 1], &b = path[i], &c = other[j - 1], &d = other[j];
                if( a.y == b.y && c.y == d.y && a.y == c.y )
                    total += std::max<int64_t>( 0, std::min( std::max( a.x, b.x ), std::max( c.x, d.x ) ) - std::max( std::min( a.x, b.x ), std::min( c.x, d.x ) ) );
                else if( a.x == b.x && c.x == d.x && a.x == c.x )
                    total += std::max<int64_t>( 0, std::min( std::max( a.y, b.y ), std::max( c.y, d.y ) ) - std::max( std::min( a.y, b.y ), std::min( c.y, d.y ) ) );
            }
    return total;
}
/// Where a connection's caption goes: centred above the middle of its longest horizontal leg, or beside the
/// middle of its longest vertical leg, so each caption sits on its own connection.
wxPoint captionPosition( const std::vector<wxPoint>& points, const wxSize& extent )
{
    size_t longest = 1; int length = -1;
    for( size_t i = 1; i < points.size(); ++i )
    {
        int here = std::abs( points[i].x - points[i - 1].x ) + std::abs( points[i].y - points[i - 1].y );
        if( here > length ) { length = here; longest = i; }
    }
    const wxPoint &a = points[longest - 1], &b = points[longest];
    wxPoint middle( ( a.x + b.x ) / 2, ( a.y + b.y ) / 2 );
    if( std::abs( b.y - a.y ) > std::abs( b.x - a.x ) ) return { middle.x + 6, middle.y - extent.y / 2 };
    return { middle.x - extent.x / 2, middle.y - extent.y - 4 };
}
}

void RECURSIVE_DIAGRAM_FRAME::routeNewConnection( const std::string& id )
{
    // Rule F4 draws a new connection's computed path. Where that path would run along a connection already on
    // the level (two connections ending on the same block edge share an anchor, rule F2), the editor stores a
    // route whose middle leg moves to a free channel between the ends, so each connection stays visible and
    // selectable on its own. Storing a route is a layout edit (rule F1a).
    auto drawn = layout( current(), true );
    const R::LINK* link = drawn.Link( id );
    if( !link || link->endpoints.size() != 2 || drawn.HasRoute( id, 1 ) ) return;
    auto computed = drawn.Route( *link, 1 );
    if( computed.size() != 4 ) return;
    std::vector<std::vector<R::POINT>> others;
    for( const auto& other : drawn.Links() ) if( other.id != id )
        for( int i = 1; i < static_cast<int>( other.endpoints.size() ); ++i ) others.push_back( drawn.Route( other, i ) );
    const R::POINT from = computed.front(), to = computed.back();
    const int64_t middle = ( from.x + to.x ) / 2, margin = 10 * Q;
    const int64_t low = std::min( from.x, to.x ) + margin, high = std::max( from.x, to.x ) - margin;
    int64_t best = sharedLength( computed, others ), channel = middle;
    for( int step = 1; step <= 6 && best > 0; ++step )
        for( int sign : { 1, -1 } )
        {
            int64_t x = middle + sign * step * 30 * Q;
            if( x < low || x > high ) continue;
            int64_t shared = sharedLength( { from, { x, from.y }, { x, to.y }, to }, others );
            if( shared < best ) { best = shared; channel = x; }
        }
    if( channel == middle ) return;
    materialize();
    auto* row = presentation()->add_routes(); row->set_connection_id( id ); row->set_endpoint_index( 1 );
    encodePoint( { channel, from.y }, row->add_waypoints() ); encodePoint( { channel, to.y }, row->add_waypoints() );
}
void RECURSIVE_DIAGRAM_FRAME::followRoutes( const R::LEVEL_LAYOUT& before )
{
    // A route laid out as one offset channel (two waypoints on one vertical line at the heights of its two
    // ends) keeps its offset from the computed middle when an end moves, so it never leaves a stale corner.
    // Any other stored route is kept exactly as drawn.
    if( !m_level.scope().local_diagram().has_presentation() ) return;
    auto after = layout( current(), true );
    for( auto& row : *m_level.mutable_scope()->mutable_local_diagram()->mutable_presentation()->mutable_routes() )
    {
        const R::LINK *was = before.Link( row.connection_id() ), *now = after.Link( row.connection_id() );
        int index = static_cast<int>( row.endpoint_index() );
        if( !was || !now || row.waypoints_size() != 2 ) continue;
        R::POINT first, second;
        if( !R::ParseUnits( row.waypoints( 0 ).x(), first.x ) || !R::ParseUnits( row.waypoints( 0 ).y(), first.y )
            || !R::ParseUnits( row.waypoints( 1 ).x(), second.x ) || !R::ParseUnits( row.waypoints( 1 ).y(), second.y ) ) continue;
        auto previous = before.Route( *was, index ), next = after.Route( *now, index );
        if( previous.size() != 4 || next.size() != 4 || first.x != second.x || first.y != previous.front().y || second.y != previous.back().y ) continue;
        const R::POINT from = next.front(), to = next.back();
        if( from.x == previous.front().x && from.y == previous.front().y && to.x == previous.back().x && to.y == previous.back().y ) continue;
        int64_t x = ( from.x + to.x ) / 2 + first.x - ( previous.front().x + previous.back().x ) / 2;
        const int64_t margin = 10 * Q, low = std::min( from.x, to.x ) + margin, high = std::max( from.x, to.x ) - margin;
        x = low <= high ? std::clamp( x, low, high ) : ( from.x + to.x ) / 2;
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
        { links.push_back( { root.connection_id(), added->name(), true, { added->endpoints().begin(), added->endpoints().end() } } ); continue; }
        const LINK_DRAFT* edited = draft ? connectionDraft( root.connection_id() ) : nullptr;
        for( const auto& archive : m_document.graph().connection_archives() ) if( archive.owner_block_id() == scope->selection().block_id() )
            for( const auto& item : archive.revisions() ) if( sameLink( item.selection(), root ) )
            {
                const auto& endpoints = edited ? edited->endpoints() : item.endpoints();
                links.push_back( { root.connection_id(), edited ? edited->name() : item.name(), false, { endpoints.begin(), endpoints.end() } } );
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
}

const R::PORT* RECURSIVE_DIAGRAM_FRAME::portAt( const R::LEVEL_LAYOUT& drawn, const wxPoint& point ) const
{
    int reach = FromDIP( 7 );
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
int RECURSIVE_DIAGRAM_FRAME::handleAt( const wxPoint& point ) const
{
    if( !drawingAvailable() || m_tool != TOOL::SELECT || !m_connectionId.empty() || !m_portId.empty()
        || m_selected.empty() || m_selected == m_level.scope().baseline().block_id() ) return -1;
    wxRect box = toScreen( layout( current(), true ).Rect( m_selected ) );
    wxPoint handles[] = { box.GetTopLeft(), { box.x + box.width / 2, box.y }, box.GetTopRight(), { box.GetRight(), box.y + box.height / 2 },
                          box.GetBottomRight(), { box.x + box.width / 2, box.GetBottom() }, box.GetBottomLeft(), { box.x, box.y + box.height / 2 } };
    int reach = FromDIP( 6 );
    for( int i = 0; i < 8; ++i ) if( std::abs( handles[i].x - point.x ) <= reach && std::abs( handles[i].y - point.y ) <= reach ) return i;
    return -1;
}

int RECURSIVE_DIAGRAM_FRAME::paletteReserve() const
{
    // The canvas-edge palette keeps its strip; the diagram fits beside it.
    return m_paletteShown && m_palette->IsShown() ? m_palette->GetPosition().x + m_palette->GetSize().x + FromDIP( 12 ) : 0;
}
RECURSIVE_DIAGRAM_FRAME::LABEL_ROOM RECURSIVE_DIAGRAM_FRAME::labelRoom( const R::LEVEL_LAYOUT& drawn ) const
{
    // A port on the level frame names itself outside the frame at a fixed text size (see paint), so fitting
    // leaves that many pixels beside the drawing on the port's side.
    LABEL_ROOM room;
    wxClientDC dc( m_canvas ); dc.SetFont( GetFont() );
    for( const auto& port : drawn.Ports() ) if( port.boundary && port.placed )
    {
        wxSize extent = dc.GetTextExtent( Text( port.name ) );
        switch( port.side )
        {
        case D::DPS_RIGHT: room.right = std::max( room.right, extent.x + 12 ); break;
        case D::DPS_TOP: room.top = std::max( room.top, extent.y + 10 ); break;
        case D::DPS_BOTTOM: room.bottom = std::max( room.bottom, extent.y + 10 ); break;
        default: room.left = std::max( room.left, extent.x + 12 ); break;
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
    int reserve = paletteReserve() + room.left;
    double available = std::max( 64, area.x - reserve - room.right ), tall = std::max( 64, area.y - room.top - room.bottom );
    m_scale = std::max( 0.000000001, std::min( { 1.0, available / width, tall / height } ) );
    m_origin = { left - reserve / m_scale - ( available / m_scale - width ) / 2, top - room.top / m_scale - ( tall / m_scale - height ) / 2 };
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
    auto* link = m_level.add_new_connections();
    link->mutable_selection()->set_connection_id( FreshId() ); link->mutable_selection()->set_state_id( FreshId() );
    link->mutable_selection()->set_revision_id( FreshId() ); link->set_requirement_revision_id( FreshId() );
    link->set_implementation_name( "Initial" ); link->set_name( caption ); link->set_kind( D::DCK_ABSTRACT ); link->mutable_fields();
    *link->add_endpoints() = *m_connectFrom; *link->add_endpoints() = *m_connectTo;
    *m_level.mutable_scope()->mutable_local_diagram()->add_connections() = link->selection();
    std::string id = link->selection().connection_id();
    routeNewConnection( id );
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
    wxColour background = wxSystemSettings::GetColour( wxSYS_COLOUR_WINDOW );
    wxColour foreground = wxSystemSettings::GetColour( wxSYS_COLOUR_WINDOWTEXT );
    wxColour muted = wxSystemSettings::GetColour( wxSYS_COLOUR_GRAYTEXT );
    wxColour accent = wxSystemSettings::GetColour( wxSYS_COLOUR_HIGHLIGHT );
    dc.SetBackground( wxBrush( background ) ); dc.Clear(); dc.SetTextForeground( foreground );
    auto* scope = current(); if( !m_ready || !scope ) { dc.DrawText( m_error.empty() ? _( "Loading diagram…" ) : Text( m_error ), 24, 24 ); return; }
    bool dark = background.Red() + background.Green() + background.Blue() < 384;
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
            // A boundary already names its interface. Avoid duplicating the title on it.
            if( points.size() >= 2 && link.endpoints[0].block_id() != drawn.ScopeId() && link.endpoints[i].block_id() != drawn.ScopeId() )
            {
                wxString caption = Text( link.name ); wxSize extent = dc.GetTextExtent( caption );
                wxPoint at = captionPosition( points, extent );
                if( auto label = drawn.RouteLabel( link.id, i ) ) at = toScreen( *label ) - wxPoint( extent.x / 2, extent.y / 2 );
                dc.DrawText( caption, at );
            }
        }
    }
    for( const auto& node : drawn.Nodes() )
    {
        wxRect box = toScreen( drawn.Rect( node.id ) ); bool selected = node.id == m_selected && m_connectionId.empty();
        dc.SetPen( wxPen( selected ? accent : muted, selected ? 2 : 1 ) );
        dc.SetBrush( wxBrush( selected ? accent.ChangeLightness( dark ? 60 : 175 ) : background.ChangeLightness( dark ? 120 : 97 ) ) ); dc.DrawRectangle( box );
        // The block's content sits inside an 8-pixel margin; the outline and its handles stay on the block's edges.
        wxRect inner = wxRect( box ).Deflate( 8 );
        dc.SetClippingRegion( inner );
        wxRect caption = R::CaptionRect( dc, node, inner, GetFont().Bold().Larger() );
        dc.DrawText( Text( node.name ), inner.x + 10, inner.y + 24 ); dc.SetFont( GetFont() );
        // A block shows a chip for each chosen or candidate component choice once it has any (Round A4, owner
        // decision n0b2a908b00e78823); until then it is only its caption (owner decision n98a3f3c41084f0ed).
        auto chips = R::LayoutChips( dc, node, inner, chipFont(), caption );
        if( chips.shown ) R::DrawChips( dc, chips, chipFont(), dark, foreground, linkColour() );
        else if( !node.isNew ) dc.DrawText( wxString::Format( "v%d", node.version ), inner.x + 10, inner.y + 58 );
        dc.SetFont( GetFont() ); dc.SetTextForeground( foreground );
        dc.DestroyClippingRegion();
        if( selected && drawingAvailable() && m_tool == TOOL::SELECT && m_portId.empty() )
        {
            dc.SetPen( wxPen( accent, 1 ) ); dc.SetBrush( wxBrush( background ) );
            wxPoint handles[] = { box.GetTopLeft(), { box.x + box.width / 2, box.y }, box.GetTopRight(), { box.GetRight(), box.y + box.height / 2 },
                                  box.GetBottomRight(), { box.x + box.width / 2, box.GetBottom() }, box.GetBottomLeft(), { box.x, box.y + box.height / 2 } };
            for( const auto& handle : handles ) dc.DrawRectangle( handle.x - 3, handle.y - 3, 7, 7 );
        }
    }
    for( const auto& port : drawnPorts( drawn, screen ) )
    {
        bool selected = port.owner == m_portOwner && port.id == m_portId;
        dc.SetPen( wxPen( selected ? accent : foreground, selected ? 2 : 1 ) );
        dc.SetBrush( selected ? wxBrush( accent.ChangeLightness( dark ? 70 : 160 ) ) : wxBrush( background ) );
        dc.DrawRectangle( port.at.x - 4, port.at.y - 4, 8, 8 );
        if( port.boundary && !port.placed ) dc.DrawText( Text( port.name ), port.at.x + 12, port.at.y - 24 );
        else if( port.boundary )
        {
            // A port on the level frame names itself outside the frame, beside its side.
            const R::PORT* stored = drawn.Port( port.owner, port.id );
            wxSize extent = dc.GetTextExtent( Text( port.name ) );
            switch( stored ? stored->side : D::DPS_LEFT )
            {
            case D::DPS_RIGHT: dc.DrawText( Text( port.name ), port.at.x + 8, port.at.y - extent.y - 4 ); break;
            case D::DPS_TOP: dc.DrawText( Text( port.name ), port.at.x + 8, port.at.y - extent.y - 6 ); break;
            case D::DPS_BOTTOM: dc.DrawText( Text( port.name ), port.at.x + 8, port.at.y + 6 ); break;
            default: dc.DrawText( Text( port.name ), port.at.x - extent.x - 8, port.at.y - extent.y - 4 ); break;
            }
        }
        else if( port.placed || selected )
        {
            // A block's port names itself just inside the block edge, as KiCad labels sheet pins,
            // so it never collides with connection captions drawn outside the block.
            wxFont small = GetFont(); small.SetPointSize( std::max( 8, small.GetPointSize() - 2 ) ); dc.SetFont( small );
            wxSize extent = dc.GetTextExtent( Text( port.name ) ); wxRect box = toScreen( drawn.Rect( port.owner ) );
            const R::PORT* stored = drawn.Port( port.owner, port.id );
            D::DiagramPortSide side = stored && stored->placed ? stored->side : ( port.at.x >= box.x + box.width / 2 ? D::DPS_RIGHT : D::DPS_LEFT );
            switch( side )
            {
            case D::DPS_RIGHT: dc.DrawText( Text( port.name ), port.at.x - extent.x - 8, port.at.y - extent.y / 2 ); break;
            case D::DPS_TOP: dc.DrawText( Text( port.name ), port.at.x - extent.x / 2, port.at.y + 6 ); break;
            case D::DPS_BOTTOM: dc.DrawText( Text( port.name ), port.at.x - extent.x / 2, port.at.y - extent.y - 6 ); break;
            default: dc.DrawText( Text( port.name ), port.at.x + 8, port.at.y - extent.y / 2 ); break;
            }
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
        wxString value = Text( note.text() ); int y = box.y;
        while( !value.empty() && y + dc.GetCharHeight() <= box.GetBottom() )
        {
            size_t count = value.find( '\n' ); if( count == wxString::npos ) count = value.length();
            while( count > 0 && dc.GetTextExtent( value.Left( count ) ).x > box.width ) --count;
            if( count == 0 && value[0] != '\n' ) count = 1;
            wxString line = value.Left( count ); value = value.Mid( count ); if( value.StartsWith( "\n" ) ) value = value.Mid( 1 );
            if( !value.empty() && y + dc.GetCharHeight() * 2 > box.GetBottom() )
            { while( !line.empty() && dc.GetTextExtent( line + wxS( "…" ) ).x > box.width ) line.RemoveLast(); line += wxS( "…" ); }
            dc.DrawText( line, box.x, y ); y += dc.GetCharHeight();
        }
        dc.DestroyClippingRegion();
    }
    if( m_captionKind == 1 )
    {
        // The block being added, at the point the user chose, while its caption is typed.
        dc.SetPen( wxPen( accent, 1, wxPENSTYLE_SHORT_DASH ) ); dc.SetBrush( *wxTRANSPARENT_BRUSH );
        dc.DrawRectangle( toScreen( R::RECT{ snap( m_pendingPoint.x - 120 * Q ), snap( m_pendingPoint.y - 70 * Q ), 240 * Q, 140 * Q } ) );
    }
    if( m_tool == TOOL::CONNECT && m_connectFrom && !m_historyPreview )
    {
        // The connection in progress and the hint shared by the toolbar strip and the palette.
        wxPoint from = toScreen( drawn.Anchor( *m_connectFrom, toDiagram( m_pointer ).x ) );
        wxPoint to = m_connectTo ? toScreen( drawn.Anchor( *m_connectTo, drawn.Anchor( *m_connectFrom, 0 ).x ) ) : m_pointer;
        dc.SetPen( wxPen( accent, 2, wxPENSTYLE_SHORT_DASH ) ); dc.DrawLine( from, to );
        dc.SetBrush( *wxTRANSPARENT_BRUSH ); dc.DrawCircle( to, 5 );
        if( !m_captionKind )
        {
            wxString hint = _( "Click a port to finish connection" ); wxSize extent = dc.GetTextExtent( hint );
            wxRect callout( to.x + 14, to.y + 10, extent.x + 16, extent.y + 10 );
            dc.SetPen( wxPen( accent, 1 ) ); dc.SetBrush( wxBrush( accent.ChangeLightness( dark ? 50 : 185 ) ) );
            dc.DrawRoundedRectangle( callout, 4 ); dc.DrawText( hint, callout.x + 8, callout.y + 5 );
        }
    }
    m_rendered = true;
}

void RECURSIVE_DIAGRAM_FRAME::click( wxMouseEvent& event )
{
    if( !m_ready || m_process || !current() || m_diagramHistoryOpen ) return;
    wxPoint point = event.GetPosition(); m_pointer = point;
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
        if( !m_connectFrom ) { m_connectFrom = endpoint; m_notice.clear(); refresh(); return; }
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
    m_pointer = event.GetPosition();
    if( m_tool == TOOL::CONNECT && m_connectFrom && !m_connectTo ) { m_rendered = false; m_canvas->Refresh(); }
    if( m_tool == TOOL::SELECT && m_drag == DRAG::NONE && m_ready && !m_process && !m_historyPreview )
    {
        bool link = false;
        for( const auto& [block, chips] : drawnChips() ) link |= chips.link && chips.link->Contains( m_pointer );
        m_canvas->SetCursor( wxCursor( link ? wxCURSOR_HAND : wxCURSOR_ARROW ) );
    }
    if( m_drag == DRAG::NONE || !event.Dragging() || m_process ) return;
    wxPoint delta = m_pointer - m_dragStart;
    if( !m_dragMoved && std::abs( delta.x ) + std::abs( delta.y ) < FromDIP( 4 ) ) return;
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
