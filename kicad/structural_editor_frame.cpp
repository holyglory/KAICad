/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#include "structural_editor_frame.h"
#include <bitmaps.h>
#include <kiid.h>
#include <google/protobuf/util/json_util.h>
#include <wx/artprov.h>
#include <wx/button.h>
#include <wx/choice.h>
#include <wx/clipbrd.h>
#include <wx/dcbuffer.h>
#include <wx/dialog.h>
#include <wx/filename.h>
#include <wx/menu.h>
#include <wx/msgdlg.h>
#include <wx/panel.h>
#include <wx/settings.h>
#include <wx/sizer.h>
#include <wx/splitter.h>
#include <wx/statbox.h>
#include <wx/stattext.h>
#include <wx/textctrl.h>
#include <wx/toolbar.h>
#include <wx/treectrl.h>
#include <wx/utils.h>
#include <algorithm>
#include <cmath>
#include <set>
#include <map>
#include <wx/control.h>

namespace S = kiapi::automation::structure::v1;
namespace
{
enum { ADD_BLOCK = wxID_HIGHEST + 710, CONNECT, INSTRUCTION, OPEN_SCHEMATIC, FIT, ZOOM_IN, ZOOM_OUT };
constexpr int64_t GRID = 2540000;
wxString text( const std::string& value ) { return wxString::FromUTF8( value ); }
std::string utf8( const wxString& value ) { return value.ToUTF8().data(); }
int64_t snap( double value ) { return static_cast<int64_t>( std::llround( value / GRID ) ) * GRID; }
class BLOCK_TREE_DATA : public wxTreeItemData
{
public:
    explicit BLOCK_TREE_DATA( std::string value ) : id( std::move( value ) ) {}
    std::string id;
};
}

STRUCTURAL_EDITOR_FRAME::STRUCTURAL_EDITOR_FRAME( wxWindow* parent, const S::StructuralEditorDocument& document,
        wxString repositoryRoot, wxString helper, std::function<void()> openSchematic ) :
        wxFrame( parent, wxID_ANY, text( document.display_name() ) + _( " — Structure" ),
                 wxDefaultPosition, wxSize( 1487, 1058 ) ),
        m_document( document ), m_repositoryRoot( std::move( repositoryRoot ) ),
        m_helper( std::move( helper ) ), m_openSchematic( std::move( openSchematic ) ), m_ioTimer( this )
{
    SetName( "StructuralEditor" ); SetMinSize( wxSize( 900, 620 ) );
    auto* menu = new wxMenuBar;
    auto* file = new wxMenu; file->Append( wxID_SAVE, _( "&Save\tCtrl+S" ) ); file->Append( wxID_CLOSE, _( "&Close\tCtrl+W" ) );
    auto* edit = new wxMenu; edit->Append( wxID_UNDO, _( "&Undo\tCtrl+Z" ) ); edit->Append( wxID_REDO, _( "&Redo\tCtrl+Y" ) );
    edit->Append( wxID_DELETE, _( "Delete selected block\tDel" ) );
    auto* view = new wxMenu; view->Append( FIT, _( "Fit diagram\tHome" ) );
    view->Append( ZOOM_IN, _( "Zoom in" ) ); view->Append( ZOOM_OUT, _( "Zoom out" ) );
    menu->Append( file, _( "&File" ) ); menu->Append( edit, _( "&Edit" ) ); menu->Append( view, _( "&View" ) ); SetMenuBar( menu );
    m_toolbar = CreateToolBar( wxTB_HORIZONTAL | wxTB_FLAT | wxTB_TEXT );
    m_toolbar->AddTool( wxID_SAVE, _( "Save" ), KiBitmap( BITMAPS::save ) );
    m_toolbar->AddTool( wxID_UNDO, _( "Undo" ), KiBitmap( BITMAPS::undo ) );
    m_toolbar->AddTool( wxID_REDO, _( "Redo" ), KiBitmap( BITMAPS::redo ) ); m_toolbar->AddSeparator();
    m_toolbar->AddTool( ADD_BLOCK, _( "Add block" ), KiBitmap( BITMAPS::add_rectangle ) );
    m_toolbar->AddTool( CONNECT, _( "Connect" ), KiBitmap( BITMAPS::add_line ) );
    m_toolbar->AddTool( INSTRUCTION, _( "Add instruction" ), KiBitmap( BITMAPS::add_textbox ) );
    if( m_openSchematic ) m_toolbar->AddTool( OPEN_SCHEMATIC, _( "Open schematic" ), KiBitmap( BITMAPS::eeschema ) );
    m_toolbar->AddSeparator();
    m_toolbar->AddTool( FIT, _( "Fit" ), KiBitmap( BITMAPS::zoom_fit_in_page ) );
    m_toolbar->AddTool( ZOOM_IN, _( "Zoom in" ), KiBitmap( BITMAPS::zoom_in ) );
    m_toolbar->AddTool( ZOOM_OUT, _( "Zoom out" ), KiBitmap( BITMAPS::zoom_out ) ); m_toolbar->Realize();

    auto* split = new wxSplitterWindow( this ); split->SetMinimumPaneSize( 240 );
    auto* sidebar = new wxPanel( split ); auto* left = new wxBoxSizer( wxVERTICAL );
    auto* properties = new wxStaticBoxSizer( wxVERTICAL, sidebar, _( "Properties" ) );
    auto field = [&]( const wxString& label, bool multi )
    {
        properties->Add( new wxStaticText( sidebar, wxID_ANY, label ), 0, wxLEFT | wxRIGHT | wxTOP, 9 );
        auto* control = new wxTextCtrl( sidebar, wxID_ANY, {}, wxDefaultPosition,
                multi ? wxSize( -1, 78 ) : wxDefaultSize, multi ? wxTE_MULTILINE : wxTE_PROCESS_ENTER );
        control->SetName( label ); properties->Add( control, 0, wxEXPAND | wxALL, 7 );
        control->Bind( wxEVT_KILL_FOCUS, [this]( wxFocusEvent& e ) { propertiesChanged(); e.Skip(); } );
        if( !multi ) control->Bind( wxEVT_TEXT_ENTER, [this]( wxCommandEvent& ) { propertiesChanged(); } );
        return control;
    };
    m_name = field( _( "Name" ), false ); m_purpose = field( _( "Purpose" ), true );
    properties->Add( new wxStaticText( sidebar, wxID_ANY, _( "Instruction" ) ), 0, wxLEFT | wxTOP, 9 );
    m_instructions = new wxChoice( sidebar, wxID_ANY ); properties->Add( m_instructions, 0, wxEXPAND | wxLEFT | wxRIGHT, 7 );
    m_instructions->Bind( wxEVT_CHOICE, [this]( wxCommandEvent& )
    {
        if( m_updating ) return;
        int index = m_instructions->GetSelection();
        if( index < 0 || size_t( index ) >= m_instructionIds.size() ) return;
        auto id = m_instructionIds[index];
        if( !propertiesChanged() )
        { m_instructions->SetSelection( std::find( m_instructionIds.begin(), m_instructionIds.end(), m_instructionId ) - m_instructionIds.begin() ); return; }
        m_instructionId = id; fillInspector();
    } );
    m_strength = new wxChoice( sidebar, wxID_ANY );
    m_strength->Append( _( "Information" ) ); m_strength->Append( _( "Preference" ) ); m_strength->Append( _( "Requirement" ) );
    properties->Add( m_strength, 0, wxEXPAND | wxALL, 7 );
    m_instruction = field( _( "Text" ), true );
    m_strength->Bind( wxEVT_CHOICE, [this]( wxCommandEvent& ) { propertiesChanged(); } );
    m_source = new wxStaticText( sidebar, wxID_ANY, {} ); properties->Add( m_source, 0, wxEXPAND | wxALL, 9 );
    m_part = new wxStaticText( sidebar, wxID_ANY, {} ); properties->Add( m_part, 0, wxEXPAND | wxALL, 9 );
    m_addProperty = new wxButton( sidebar, wxID_ANY, _( "Add custom property" ) );
    properties->Add( m_addProperty, 0, wxEXPAND | wxALL, 9 ); m_addProperty->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { addProperty(); } );
    left->Add( properties, 0, wxEXPAND | wxALL, 4 );
    auto* hierarchy = new wxStaticBoxSizer( wxVERTICAL, sidebar, _( "Hierarchy" ) );
    m_tree = new wxTreeCtrl( sidebar, wxID_ANY, wxDefaultPosition, wxDefaultSize, wxTR_HAS_BUTTONS | wxTR_LINES_AT_ROOT | wxTR_SINGLE );
    m_tree->SetName( "StructuralHierarchy" ); hierarchy->Add( m_tree, 1, wxEXPAND | wxALL, 4 );
    left->Add( hierarchy, 1, wxEXPAND | wxALL, 4 ); sidebar->SetSizer( left );
    m_canvas = new wxPanel( split ); m_canvas->SetName( "StructuralCanvas" ); m_canvas->SetBackgroundStyle( wxBG_STYLE_PAINT );
    split->SplitVertically( sidebar, m_canvas, 390 );
    auto* main = new wxBoxSizer( wxVERTICAL ); main->Add( split, 1, wxEXPAND ); SetSizer( main );
    CreateStatusBar();
    m_canvas->Bind( wxEVT_PAINT, [this]( wxPaintEvent& ) { wxAutoBufferedPaintDC dc( m_canvas ); paint( dc ); } );
    m_canvas->Bind( wxEVT_LEFT_DOWN, &STRUCTURAL_EDITOR_FRAME::mouseDown, this );
    m_canvas->Bind( wxEVT_MOTION, &STRUCTURAL_EDITOR_FRAME::mouseMove, this );
    m_canvas->Bind( wxEVT_LEFT_UP, &STRUCTURAL_EDITOR_FRAME::mouseUp, this );
    m_canvas->Bind( wxEVT_MOUSEWHEEL, [this]( wxMouseEvent& e ) { zoom( e.GetWheelRotation() > 0 ? 1.15 : 1 / 1.15, e.GetPosition() ); } );
    m_canvas->Bind( wxEVT_MIDDLE_DOWN, [this]( wxMouseEvent& e )
    { if( m_dragging ) return; m_panning = true; m_dragStart = e.GetPosition(); m_panOrigin = m_origin; m_canvas->CaptureMouse(); } );
    m_canvas->Bind( wxEVT_MIDDLE_UP, [this]( wxMouseEvent& )
    { if( m_panning ) { m_panning = false; if( m_canvas->HasCapture() ) m_canvas->ReleaseMouse(); } } );
    m_canvas->Bind( wxEVT_MOUSE_CAPTURE_LOST, [this]( wxMouseCaptureLostEvent& )
    { m_panning = false; if( m_dragging ) { *m_document.mutable_diagram() = m_dragBefore; m_dragging = false; refreshModel(); } } );
    m_tree->Bind( wxEVT_TREE_SEL_CHANGED, [this]( wxTreeEvent& e )
    { if( !m_updating ) if( auto* data = dynamic_cast<BLOCK_TREE_DATA*>( m_tree->GetItemData( e.GetItem() ) ) ) select( data->id ); } );
    Bind( wxEVT_MENU, [this]( wxCommandEvent& e )
    {
        switch( e.GetId() )
        {
        case wxID_SAVE: save(); break; case wxID_CLOSE: Close(); break;
        case wxID_UNDO: undo(); break; case wxID_REDO: redo(); break; case wxID_DELETE: removeSelection(); break;
        case ADD_BLOCK: m_mode = MODE::ADD_BLOCK; SetStatusText( _( "Click the canvas to add a block." ) ); break;
        case CONNECT: m_mode = MODE::CONNECT; m_connectFrom.clear(); SetStatusText( _( "Select the first block, then the second block." ) ); break;
        case INSTRUCTION: addInstruction(); break; case OPEN_SCHEMATIC: if( m_openSchematic ) m_openSchematic(); break;
        case FIT: fit(); break; case ZOOM_IN: zoom( 1.2, wxPoint( m_canvas->GetClientSize().x / 2, m_canvas->GetClientSize().y / 2 ) ); break;
        case ZOOM_OUT: zoom( 1 / 1.2, wxPoint( m_canvas->GetClientSize().x / 2, m_canvas->GetClientSize().y / 2 ) ); break;
        }
    } );
    Bind( wxEVT_CLOSE_WINDOW, &STRUCTURAL_EDITOR_FRAME::closeEditor, this );
    Bind( wxEVT_UPDATE_UI, [this]( wxUpdateUIEvent& event )
    {
        switch( event.GetId() )
        {
        case wxID_UNDO: event.Enable( !m_undo.empty() ); break;
        case wxID_REDO: event.Enable( !m_redo.empty() ); break;
        case wxID_DELETE: case INSTRUCTION: event.Enable( selectedBlock() != nullptr ); break;
        case CONNECT: event.Enable( m_document.diagram().blocks_size() >= 2 ); break;
        case wxID_SAVE: event.Enable( !IsSaving() ); break;
        default: event.Skip();
        }
    } );
    Bind( wxEVT_CHAR_HOOK, [this]( wxKeyEvent& event )
    {
        // This is a separate editor window. Its shortcuts must not bubble to
        // the owning manager (whose Ctrl+Y opens the drawing-sheet editor).
        if( event.CmdDown() && !event.AltDown() )
        {
            switch( event.GetKeyCode() )
            {
            case 'S': save(); return; case 'Z': if( event.ShiftDown() ) redo(); else undo(); return;
            case 'Y': redo(); return; case 'W': Close(); return;
            }
        }
        if( event.GetKeyCode() != WXK_ESCAPE )
        {
            // Let the focused native widget process text and navigation, while
            // stopping the hook at this top-level window's ownership boundary.
            event.StopPropagation(); event.Skip(); return;
        }
        m_mode = MODE::SELECT; m_connectFrom.clear(); SetStatusText( {} );
        if( m_panning ) { m_origin = m_panOrigin; m_panning = false; if( m_canvas->HasCapture() ) m_canvas->ReleaseMouse(); m_canvas->Refresh(); }
        if( m_dragging ) { *m_document.mutable_diagram() = m_dragBefore; m_dragging = false; if( m_canvas->HasCapture() ) m_canvas->ReleaseMouse(); refreshModel(); }
    } );
    Bind( wxEVT_END_PROCESS, &STRUCTURAL_EDITOR_FRAME::helperFinished, this );
    Bind( wxEVT_TIMER, [this]( wxTimerEvent& ) { drainHelper(); } );
    if( m_document.diagram().blocks_size() ) m_selected = m_document.diagram().blocks( 0 ).id();
    refreshModel(); CallAfter( [this] { fit(); } );
}

STRUCTURAL_EDITOR_FRAME::~STRUCTURAL_EDITOR_FRAME()
{
    m_ioTimer.Stop();
    if( m_process ) { wxProcess::Kill( m_helperPid, wxSIGTERM, wxKILL_CHILDREN ); m_process->Detach(); m_process.release(); }
}

S::StructuralBlockPlacement STRUCTURAL_EDITOR_FRAME::blockLayout( const std::string& id ) const
{
    for( const auto& p : m_document.diagram().presentation().blocks() ) if( p.block_id() == id ) return p;
    int index = 0; for( const auto& b : m_document.diagram().blocks() ) { if( b.id() == id ) break; ++index; }
    S::StructuralBlockPlacement p; p.set_block_id( id ); p.set_width_nm( 20320000 ); p.set_height_nm( 15240000 );
    p.mutable_position()->set_x_nm( 10160000 + ( index % 4 ) * 35560000 );
    p.mutable_position()->set_y_nm( 10160000 + ( index / 4 ) * 30480000 ); return p;
}
S::StructuralBlockPlacement* STRUCTURAL_EDITOR_FRAME::placement( const std::string& id )
{
    for( auto& p : *m_document.mutable_diagram()->mutable_presentation()->mutable_blocks() ) if( p.block_id() == id ) return &p;
    auto value = blockLayout( id ); auto* result = m_document.mutable_diagram()->mutable_presentation()->add_blocks();
    *result = std::move( value ); return result;
}
wxPoint STRUCTURAL_EDITOR_FRAME::screenPoint( int64_t x, int64_t y ) const
{ return wxPoint( std::lround( ( x - m_origin.m_x ) * m_scale ), std::lround( ( y - m_origin.m_y ) * m_scale ) ); }
wxPoint2DDouble STRUCTURAL_EDITOR_FRAME::modelPoint( const wxPoint& p ) const
{ return { p.x / m_scale + m_origin.m_x, p.y / m_scale + m_origin.m_y }; }
const S::StructuralBlockData* STRUCTURAL_EDITOR_FRAME::selectedBlock() const
{ for( const auto& b : m_document.diagram().blocks() ) if( b.id() == m_selected ) return &b; return nullptr; }
std::string STRUCTURAL_EDITOR_FRAME::hitBlock( const wxPoint& point ) const
{
    for( const auto& b : m_document.diagram().blocks() )
    {
        auto p = blockLayout( b.id() ); auto a = screenPoint( p.position().x_nm(), p.position().y_nm() );
        auto z = screenPoint( p.position().x_nm() + p.width_nm(), p.position().y_nm() + p.height_nm() );
        if( wxRect( a, z ).Contains( point ) ) return b.id();
    }
    return {};
}
wxPoint STRUCTURAL_EDITOR_FRAME::portPoint( const std::string& id ) const
{
    for( const auto& port : m_document.diagram().ports() ) if( port.id() == id )
    {
        auto b = blockLayout( port.block_id() ); int64_t x = b.position().x_nm() + b.width_nm(), y = b.position().y_nm() + b.height_nm() / 2;
        for( const auto& p : m_document.diagram().presentation().ports() ) if( p.port_id() == id )
        {
            x = b.position().x_nm(); y = b.position().y_nm();
            if( p.side() == S::SPS_LEFT || p.side() == S::SPS_RIGHT )
            { y += p.offset_nm(); if( p.side() == S::SPS_RIGHT ) x += b.width_nm(); }
            else { x += p.offset_nm(); if( p.side() == S::SPS_BOTTOM ) y += b.height_nm(); }
        }
        return screenPoint( x, y );
    }
    return {};
}
wxPoint STRUCTURAL_EDITOR_FRAME::portDirection( const std::string& id ) const
{
    for( const auto& p : m_document.diagram().presentation().ports() ) if( p.port_id() == id )
    {
        switch( p.side() )
        {
        case S::SPS_LEFT: return { -1, 0 }; case S::SPS_TOP: return { 0, -1 };
        case S::SPS_BOTTOM: return { 0, 1 }; default: return { 1, 0 };
        }
    }
    return { 1, 0 };
}

void STRUCTURAL_EDITOR_FRAME::paint( wxDC& dc )
{
    dc.SetBackground( wxBrush( wxSystemSettings::GetColour( wxSYS_COLOUR_WINDOW ) ) ); dc.Clear();
    const wxSize size = m_canvas->GetClientSize();
    dc.SetPen( wxPen( wxColour( 190, 190, 190 ) ) );
    int spacing = std::max( 8, static_cast<int>( GRID * m_scale ) );
    for( int x = 0; x < size.x; x += spacing ) for( int y = 0; y < size.y; y += spacing ) dc.DrawPoint( x, y );
    dc.SetFont( GetFont() );
    for( const auto& c : m_document.diagram().connections() )
    {
        wxPoint a = portPoint( c.first_port_id() ), b = portPoint( c.second_port_id() );
        wxColour color = c.kind() == S::SLK_POWER ? wxColour( 155, 35, 35 ) : wxColour( 35, 100, 195 );
        dc.SetPen( wxPen( color, 2 ) );
        std::vector<wxPoint> route{ a }; const S::StructuralConnectionPlacement* layout = nullptr;
        for( const auto& p : m_document.diagram().presentation().connections() ) if( p.connection_id() == c.id() ) layout = &p;
        if( layout && layout->waypoints_size() )
            for( const auto& point : layout->waypoints() ) route.push_back( screenPoint( point.x_nm(), point.y_nm() ) );
        else
        {
            const wxPoint da = portDirection( c.first_port_id() ), db = portDirection( c.second_port_id() );
            const wxPoint start = a + da * 24, end = b + db * 24;
            route.push_back( start );
            if( da.x && db.x ) { int middle = ( start.x + end.x ) / 2; route.emplace_back( middle, start.y ); route.emplace_back( middle, end.y ); }
            else if( da.y && db.y ) { int middle = ( start.y + end.y ) / 2; route.emplace_back( start.x, middle ); route.emplace_back( end.x, middle ); }
            else route.emplace_back( da.x ? end.x : start.x, da.x ? start.y : end.y );
            route.push_back( end );
        }
        route.push_back( b );
        route.erase( std::unique( route.begin(), route.end() ), route.end() );
        dc.DrawLines( route.size(), route.data() );
        auto arrow = [&]( wxPoint tip, wxPoint from )
        {
            double length = std::hypot( tip.x - from.x, tip.y - from.y ); if( length < 1 ) return;
            double x = ( tip.x - from.x ) / length, y = ( tip.y - from.y ) / length;
            wxPoint points[] = { tip, { int( tip.x - 12 * x + 5 * y ), int( tip.y - 12 * y - 5 * x ) },
                { int( tip.x - 12 * x - 5 * y ), int( tip.y - 12 * y + 5 * x ) } };
            dc.SetBrush( wxBrush( color ) ); dc.DrawPolygon( 3, points );
        };
        if( route.size() > 1 )
        {
            if( c.direction() == S::SLD_FIRST_TO_SECOND || c.direction() == S::SLD_BIDIRECTIONAL ) arrow( b, route[route.size() - 2] );
            if( c.direction() == S::SLD_SECOND_TO_FIRST || c.direction() == S::SLD_BIDIRECTIONAL ) arrow( a, route[1] );
        }
        if( !c.description().empty() )
        {
            wxPoint label = layout && layout->has_label() ? screenPoint( layout->label().x_nm(), layout->label().y_nm() )
                : wxPoint( ( a.x + b.x ) / 2 + 5, ( a.y + b.y ) / 2 - 20 );
            dc.SetTextForeground( wxSystemSettings::GetColour( wxSYS_COLOUR_WINDOWTEXT ) ); dc.DrawText( text( c.description() ), label );
        }
    }
    for( const auto& block : m_document.diagram().blocks() )
    {
        auto p = blockLayout( block.id() ); wxPoint a = screenPoint( p.position().x_nm(), p.position().y_nm() );
        wxPoint b = screenPoint( p.position().x_nm() + p.width_nm(), p.position().y_nm() + p.height_nm() ); wxRect rect( a, b );
        bool selected = block.id() == m_selected; wxColour fill = selected ? wxColour( 218, 235, 255 ) : wxColour( 240, 242, 235 );
        if( p.has_fill_rgb() && !selected ) fill = wxColour( ( p.fill_rgb() >> 16 ) & 255, ( p.fill_rgb() >> 8 ) & 255, p.fill_rgb() & 255 );
        dc.SetBrush( wxBrush( fill ) ); dc.SetPen( wxPen( selected ? wxColour( 30, 105, 240 ) : wxColour( 70, 75, 65 ), 2 ) ); dc.DrawRectangle( rect );
        dc.SetTextForeground( wxColour( 20, 25, 30 ) ); dc.SetFont( GetFont().Bold().Scaled( 1.4 ) );
        auto label = wxControl::Ellipsize( text( block.name() ), dc, wxELLIPSIZE_END, std::max( 1, rect.width - 16 ) );
        dc.DrawLabel( label, rect.Deflate( 8 ), wxALIGN_CENTER );
        if( selected && !p.locked() ) { dc.SetBrush( *wxWHITE_BRUSH ); dc.DrawRectangle( b.x - 5, b.y - 5, 10, 10 ); }
    }
    dc.SetFont( GetFont() ); dc.SetBrush( *wxWHITE_BRUSH ); dc.SetPen( *wxBLACK_PEN );
    for( const auto& port : m_document.diagram().ports() )
    {
        auto p = portPoint( port.id() ), direction = portDirection( port.id() ); auto label = text( port.name() ); auto extent = dc.GetTextExtent( label );
        dc.DrawRectangle( p.x - 4, p.y - 4, 8, 8 );
        dc.SetTextForeground( wxSystemSettings::GetColour( wxSYS_COLOUR_WINDOWTEXT ) );
        dc.DrawText( label, direction.x < 0 ? p.x - extent.x - 8 : p.x + 8,
                    direction.y < 0 ? p.y - extent.y - 8 : direction.y > 0 ? p.y + 8 : p.y - extent.y - 4 );
    }
    m_rendered = true;
}

void STRUCTURAL_EDITOR_FRAME::select( const std::string& id )
{
    if( !propertiesChanged() ) return;
    if( m_selected != id ) m_instructionId.clear();
    m_selected = id; refreshModel();
}
void STRUCTURAL_EDITOR_FRAME::refreshModel()
{
    m_updating = true; m_tree->DeleteAllItems(); auto root = m_tree->AddRoot( text( m_document.display_name() ) );
    std::map<std::string, std::vector<const S::StructuralBlockData*>> children;
    for( const auto& block : m_document.diagram().blocks() ) children[block.has_parent_id() ? block.parent_id() : ""].push_back( &block );
    std::vector<std::pair<std::string, wxTreeItemId>> pending{ { "", root } };
    while( !pending.empty() )
    {
        auto [parent, item] = pending.back(); pending.pop_back();
        for( const auto* block : children[parent] )
        {
            auto child = m_tree->AppendItem( item, text( block->name() ), -1, -1, new BLOCK_TREE_DATA( block->id() ) );
            if( block->id() == m_selected ) m_tree->SelectItem( child ); pending.emplace_back( block->id(), child );
        }
    }
    m_tree->ExpandAll(); m_updating = false; fillInspector(); m_canvas->Refresh();
    m_toolbar->EnableTool( wxID_UNDO, !m_undo.empty() ); m_toolbar->EnableTool( wxID_REDO, !m_redo.empty() );
    // Update accelerator admission in the same mutation checkpoint. Waiting
    // for the idle UI-update event can drop an immediate Undo -> Redo key.
    GetMenuBar()->Enable( wxID_UNDO, !m_undo.empty() ); GetMenuBar()->Enable( wxID_REDO, !m_redo.empty() );
    SetTitle( ( m_dirty ? "*" : "" ) + text( m_document.display_name() ) + _( " — Structure" ) );
}
void STRUCTURAL_EDITOR_FRAME::fillInspector()
{
    m_updating = true; const auto* block = selectedBlock(); bool enabled = block != nullptr;
    m_name->Enable( enabled ); m_purpose->Enable( enabled ); m_instruction->Enable( enabled ); m_strength->Enable( enabled );
    m_name->ChangeValue( block ? text( block->name() ) : wxString() ); m_purpose->ChangeValue( block ? text( block->purpose() ) : wxString() );
    m_instruction->ChangeValue( {} ); m_strength->SetSelection( 1 ); m_source->SetLabel( _( "Source: Not linked" ) );
    m_instructions->Clear(); m_instructionIds.clear(); const S::StructuralStatementData* selected = nullptr;
    for( const auto& s : m_document.diagram().statements() ) if( s.target_id() == m_selected && s.role() == S::SSR_INTENT )
    {
        m_instructionIds.push_back( s.id() ); m_instructions->Append( text( s.text() ).BeforeFirst( '\n' ).Left( 60 ) );
        if( !selected || s.id() == m_instructionId ) selected = &s;
    }
    if( selected )
    {
        m_instructionId = selected->id(); m_instruction->ChangeValue( text( selected->text() ) ); m_strength->SetSelection( selected->strength() );
        m_instructions->SetSelection( std::find( m_instructionIds.begin(), m_instructionIds.end(), m_instructionId ) - m_instructionIds.begin() );
        if( selected->sources_size() ) m_source->SetLabel( _( "Source: " ) + text( selected->sources( 0 ).document_id() ) );
    }
    else m_instructionId.clear();
    m_instruction->Enable( selected != nullptr ); m_strength->Enable( selected != nullptr ); m_instructions->Show( m_instructionIds.size() > 1 );
    m_addProperty->Enable( enabled );
    wxString parts;
    if( block ) for( const auto& id : block->component_ids() ) for( const auto& c : m_document.components() ) if( c.component_id() == id )
    { if( !parts.empty() ) parts += ", "; parts += text( c.reference() + " " + c.part_name() ); }
    m_part->SetLabel( parts.empty() ? _( "Part: Not selected" ) : _( "Part: " ) + parts ); m_updating = false; m_name->GetParent()->Layout();
}
void STRUCTURAL_EDITOR_FRAME::commit( const S::StructuralDiagramData& before )
{
    if( before.SerializeAsString() == m_document.diagram().SerializeAsString() ) return;
    m_undo.push_back( before ); m_redo.clear(); ++m_revision; m_dirty = true; refreshModel();
}
bool STRUCTURAL_EDITOR_FRAME::propertiesChanged()
{
    if( m_updating || !selectedBlock() ) return true;
    if( m_name->GetValue().Strip( wxString::both ).empty() ) { SetStatusText( _( "A block needs a name." ) ); return false; }
    if( !m_instructionId.empty() && m_instruction->GetValue().Strip( wxString::both ).empty() )
    { SetStatusText( _( "Enter the instruction text." ) ); return false; }
    auto before = m_document.diagram();
    for( auto& b : *m_document.mutable_diagram()->mutable_blocks() ) if( b.id() == m_selected )
    { b.set_name( utf8( m_name->GetValue() ) ); b.set_purpose( utf8( m_purpose->GetValue() ) ); }
    if( !m_instructionId.empty() ) for( auto& s : *m_document.mutable_diagram()->mutable_statements() ) if( s.id() == m_instructionId )
    { s.set_text( utf8( m_instruction->GetValue() ) ); s.set_strength( static_cast<S::StructuralGuidanceStrength>( m_strength->GetSelection() ) ); }
    commit( before ); return true;
}
void STRUCTURAL_EDITOR_FRAME::addInstruction()
{
    if( !selectedBlock() || !propertiesChanged() ) return; auto before = m_document.diagram();
    auto* item = m_document.mutable_diagram()->add_statements(); item->set_id( KIID().AsStdString() ); item->set_target_id( m_selected );
    item->set_role( S::SSR_INTENT ); item->set_strength( S::SGS_PREFERENCE ); item->set_text( utf8( _( "New instruction" ) ) );
    m_instructionId = item->id(); commit( before );
    // Select the newly created instruction explicitly, not by text similarity.
    m_instructionId = item->id(); m_instruction->ChangeValue( text( item->text() ) ); m_instruction->SetFocus(); m_instruction->SelectAll();
}
void STRUCTURAL_EDITOR_FRAME::addProperty()
{
    if( !selectedBlock() || !propertiesChanged() ) return; wxDialog dialog( this, wxID_ANY, _( "Add custom property" ) );
    auto* sizer = new wxBoxSizer( wxVERTICAL );
    auto* name = new wxTextCtrl( &dialog, wxID_ANY ); auto* value = new wxTextCtrl( &dialog, wxID_ANY, {}, wxDefaultPosition, wxSize( 330, 90 ), wxTE_MULTILINE );
    sizer->Add( new wxStaticText( &dialog, wxID_ANY, _( "Name" ) ), 0, wxALL, 8 ); sizer->Add( name, 0, wxEXPAND | wxALL, 8 );
    sizer->Add( new wxStaticText( &dialog, wxID_ANY, _( "Value or instruction" ) ), 0, wxALL, 8 ); sizer->Add( value, 1, wxEXPAND | wxALL, 8 );
    sizer->Add( dialog.CreateStdDialogButtonSizer( wxOK | wxCANCEL ), 0, wxEXPAND | wxALL, 8 ); dialog.SetSizerAndFit( sizer ); name->SetFocus();
    dialog.Bind( wxEVT_BUTTON, [&]( wxCommandEvent& )
    {
        if( name->GetValue().Strip( wxString::both ).empty() || value->GetValue().Strip( wxString::both ).empty() )
        { wxMessageBox( _( "Enter a name and value." ), _( "Property not added" ), wxOK | wxICON_INFORMATION, &dialog ); return; }
        dialog.EndModal( wxID_OK );
    }, wxID_OK );
    if( dialog.ShowModal() != wxID_OK ) return;
    auto before = m_document.diagram(); auto* p = m_document.mutable_diagram()->add_properties();
    p->set_id( KIID().AsStdString() ); p->set_owner_id( m_selected ); p->set_key( utf8( name->GetValue() ) ); p->set_category( "custom" );
    p->set_text( utf8( value->GetValue() ) ); p->set_strength( S::SGS_INFORMATION ); p->set_verification( S::SV_UNVERIFIED ); commit( before );
}
void STRUCTURAL_EDITOR_FRAME::undo()
{ if( !propertiesChanged() || m_undo.empty() ) return; m_redo.push_back( m_document.diagram() ); *m_document.mutable_diagram() = m_undo.back(); m_undo.pop_back(); ++m_revision; m_dirty = true; refreshModel(); }
void STRUCTURAL_EDITOR_FRAME::redo()
{ if( !propertiesChanged() || m_redo.empty() ) return; m_undo.push_back( m_document.diagram() ); *m_document.mutable_diagram() = m_redo.back(); m_redo.pop_back(); ++m_revision; m_dirty = true; refreshModel(); }
void STRUCTURAL_EDITOR_FRAME::removeSelection()
{
    if( !selectedBlock() || !propertiesChanged() ) return;
    std::set<std::string> removed{ m_selected };
    bool changed = true;
    while( changed ) { changed = false; for( const auto& b : m_document.diagram().blocks() )
        if( b.has_parent_id() && removed.count( b.parent_id() ) ) changed |= removed.insert( b.id() ).second; }
    for( const auto& p : m_document.diagram().ports() ) if( removed.count( p.block_id() ) ) removed.insert( p.id() );
    for( const auto& c : m_document.diagram().connections() ) if( removed.count( c.first_port_id() ) || removed.count( c.second_port_id() ) ) removed.insert( c.id() );
    for( const auto& s : m_document.diagram().statements() ) if( removed.count( s.target_id() ) ) removed.insert( s.id() );
    for( const auto& s : m_document.diagram().statements() ) if( !removed.count( s.id() ) )
        for( const auto& source : s.derived_from() ) if( removed.count( source ) )
        { wxMessageBox( _( "An instruction outside this block depends on its history. Resolve that link before removing the block." ), _( "Block is still referenced" ), wxOK | wxICON_INFORMATION, this ); return; }
    if( wxMessageBox( _( "Remove this block, its child blocks and their abstract links and instructions? Existing schematic components are kept." ),
                     _( "Remove " ) + text( selectedBlock()->name() ), wxYES_NO | wxNO_DEFAULT | wxICON_QUESTION, this ) != wxYES ) return;
    auto before = m_document.diagram(); auto* d = m_document.mutable_diagram();
    auto filter = [&]( auto* rows, auto key ) { for( int i = rows->size() - 1; i >= 0; --i ) if( removed.count( key( rows->Get( i ) ) ) ) rows->DeleteSubrange( i, 1 ); };
    filter( d->mutable_blocks(), []( const auto& row ) { return row.id(); } );
    filter( d->mutable_ports(), []( const auto& row ) { return row.id(); } );
    filter( d->mutable_connections(), []( const auto& row ) { return row.id(); } );
    filter( d->mutable_statements(), []( const auto& row ) { return row.id(); } );
    filter( d->mutable_properties(), []( const auto& row ) { return row.owner_id(); } );
    filter( d->mutable_unresolved_nets(), []( const auto& row ) { return row.owner_id(); } );
    filter( d->mutable_unresolved_components(), []( const auto& row ) { return row.owner_id(); } );
    if( d->has_presentation() )
    { filter( d->mutable_presentation()->mutable_blocks(), []( const auto& row ) { return row.block_id(); } );
      filter( d->mutable_presentation()->mutable_ports(), []( const auto& row ) { return row.port_id(); } );
      filter( d->mutable_presentation()->mutable_connections(), []( const auto& row ) { return row.connection_id(); } ); }
    m_selected.clear(); commit( before );
}

std::string STRUCTURAL_EDITOR_FRAME::createPort( const std::string& blockId, bool right )
{
    auto* block = placement( blockId ); const int64_t middle = block->height_nm() / 2;
    auto* port = m_document.mutable_diagram()->add_ports(); port->set_id( KIID().AsStdString() ); port->set_block_id( blockId );
    port->set_name( "P" + std::to_string( m_document.diagram().ports_size() ) ); std::string id = port->id();
    auto* p = m_document.mutable_diagram()->mutable_presentation()->add_ports(); p->set_port_id( id ); p->set_side( right ? S::SPS_RIGHT : S::SPS_LEFT ); p->set_offset_nm( middle ); return id;
}
void STRUCTURAL_EDITOR_FRAME::mouseDown( wxMouseEvent& event )
{
    if( !propertiesChanged() ) return; m_canvas->SetFocus(); auto hit = hitBlock( event.GetPosition() );
    if( m_mode == MODE::ADD_BLOCK )
    {
        auto before = m_document.diagram(); auto* block = m_document.mutable_diagram()->add_blocks(); block->set_id( KIID().AsStdString() );
        block->set_name( utf8( _( "Block" ) ) + " " + std::to_string( m_document.diagram().blocks_size() ) ); m_selected = block->id();
        auto p = modelPoint( event.GetPosition() ); auto* layout = placement( m_selected ); layout->mutable_position()->set_x_nm( snap( p.m_x ) ); layout->mutable_position()->set_y_nm( snap( p.m_y ) );
        m_mode = MODE::SELECT; SetStatusText( {} ); commit( before ); return;
    }
    if( m_mode == MODE::CONNECT )
    {
        if( hit.empty() ) return; if( m_connectFrom.empty() ) { m_connectFrom = hit; select( hit ); return; }
        if( hit == m_connectFrom ) return; auto before = m_document.diagram(); auto first = createPort( m_connectFrom, true ); auto second = createPort( hit, false );
        auto* link = m_document.mutable_diagram()->add_connections(); link->set_id( KIID().AsStdString() ); link->set_first_port_id( first ); link->set_second_port_id( second );
        link->set_kind( S::SLK_UNSPECIFIED ); m_connectFrom.clear(); m_mode = MODE::SELECT; SetStatusText( {} ); commit( before ); return;
    }
    select( hit ); if( hit.empty() || blockLayout( hit ).locked() ) return;
    m_dragBefore = m_document.diagram(); auto p = blockLayout( hit ); m_dragStart = event.GetPosition(); m_dragX = p.position().x_nm(); m_dragY = p.position().y_nm();
    m_dragWidth = p.width_nm(); m_dragHeight = p.height_nm();
    wxPoint corner = screenPoint( m_dragX + m_dragWidth, m_dragY + m_dragHeight );
    m_resizing = std::abs( event.GetX() - corner.x ) <= 8 && std::abs( event.GetY() - corner.y ) <= 8;
    m_dragging = true; m_canvas->CaptureMouse();
}
void STRUCTURAL_EDITOR_FRAME::mouseMove( wxMouseEvent& event )
{
    if( m_panning ) { m_origin = { m_panOrigin.m_x - ( event.GetX() - m_dragStart.x ) / m_scale,
        m_panOrigin.m_y - ( event.GetY() - m_dragStart.y ) / m_scale }; m_canvas->Refresh(); return; }
    if( !m_dragging || !event.Dragging() ) return;
    auto* p = placement( m_selected );
    if( m_resizing )
    {
        int64_t minWidth = 4 * GRID, minHeight = 3 * GRID;
        for( const auto& port : m_document.diagram().ports() ) if( port.block_id() == m_selected )
            for( const auto& layout : m_document.diagram().presentation().ports() ) if( layout.port_id() == port.id() )
            { if( layout.side() == S::SPS_TOP || layout.side() == S::SPS_BOTTOM ) minWidth = std::max( minWidth, layout.offset_nm() );
              else minHeight = std::max( minHeight, layout.offset_nm() ); }
        p->set_width_nm( std::max( minWidth, snap( m_dragWidth + ( event.GetX() - m_dragStart.x ) / m_scale ) ) );
        p->set_height_nm( std::max( minHeight, snap( m_dragHeight + ( event.GetY() - m_dragStart.y ) / m_scale ) ) );
    }
    else
    {
        p->mutable_position()->set_x_nm( snap( m_dragX + ( event.GetX() - m_dragStart.x ) / m_scale ) );
        p->mutable_position()->set_y_nm( snap( m_dragY + ( event.GetY() - m_dragStart.y ) / m_scale ) );
    }
    m_canvas->Refresh();
}
void STRUCTURAL_EDITOR_FRAME::mouseUp( wxMouseEvent& )
{ if( !m_dragging ) return; m_dragging = false; if( m_canvas->HasCapture() ) m_canvas->ReleaseMouse(); commit( m_dragBefore ); }
void STRUCTURAL_EDITOR_FRAME::fit()
{
    if( m_document.diagram().blocks().empty() ) { m_origin = { 0, 0 }; m_scale = 0.000006; m_canvas->Refresh(); return; }
    double minX = 1e20, minY = 1e20, maxX = -1e20, maxY = -1e20;
    for( const auto& b : m_document.diagram().blocks() ) { auto p = blockLayout( b.id() ); minX = std::min( minX, double( p.position().x_nm() ) ); minY = std::min( minY, double( p.position().y_nm() ) ); maxX = std::max( maxX, double( p.position().x_nm() + p.width_nm() ) ); maxY = std::max( maxY, double( p.position().y_nm() + p.height_nm() ) ); }
    auto size = m_canvas->GetClientSize(); m_scale = std::min( ( size.x - 120.0 ) / ( maxX - minX ), ( size.y - 120.0 ) / ( maxY - minY ) );
    m_scale = std::clamp( m_scale, 0.0000001, 0.00001 );
    m_origin = { ( minX + maxX ) / 2 - size.x / ( 2 * m_scale ), ( minY + maxY ) / 2 - size.y / ( 2 * m_scale ) }; m_canvas->Refresh();
}
void STRUCTURAL_EDITOR_FRAME::zoom( double factor, wxPoint anchor )
{
    auto fixed = modelPoint( anchor ); m_scale = std::clamp( m_scale * factor, 0.0000001, 0.00002 );
    m_origin = { fixed.m_x - anchor.x / m_scale, fixed.m_y - anchor.y / m_scale }; m_canvas->Refresh();
}

void STRUCTURAL_EDITOR_FRAME::save()
{
    if( !propertiesChanged() || m_process ) return;
    if( !wxFileName::FileExists( m_helper ) ) { SetStatusText( _( "The compiled companion could not be found." ) ); return; }
    S::StructuralFileRequest request; request.set_schema_version( 1 ); request.set_action( S::SFA_SAVE );
    request.set_repository_root( utf8( m_repositoryRoot ) ); request.set_source_path( m_document.source_path() );
    request.set_expected_source_token( m_document.source_token() ); *request.mutable_diagram() = m_document.diagram();
    std::string json; auto status = google::protobuf::util::MessageToJsonString( request, &json );
    if( !status.ok() ) { SetStatusText( _( "The structural draft could not be serialized." ) ); return; }
    m_process = std::make_unique<wxProcess>( this ); m_process->Redirect(); m_stdout.clear(); m_stderr.clear(); m_saveRevision = m_revision; m_lastSaveError.clear();
    wxString dotnet = "dotnet", operation = "--structural-file";
    const wxChar* binaryArgs[] = { m_helper.c_str(), operation.c_str(), nullptr };
    const wxChar* managedArgs[] = { dotnet.c_str(), m_helper.c_str(), operation.c_str(), nullptr };
    m_helperPid = wxExecute( m_helper.EndsWith( ".dll" ) ? managedArgs : binaryArgs, wxEXEC_ASYNC | wxEXEC_MAKE_GROUP_LEADER, m_process.get() );
    if( m_helperPid <= 0 ) { m_process.reset(); SetStatusText( _( "The compiled companion could not start." ) ); return; }
    m_process->GetOutputStream()->Write( json.data(), json.size() ); m_process->CloseOutput(); m_ioTimer.Start( 50 ); SetStatusText( _( "Saving…" ) );
}
void STRUCTURAL_EDITOR_FRAME::drainHelper()
{
    if( !m_process ) return;
    auto read = []( wxInputStream* stream, std::string& output )
    { if( !stream ) return; char buffer[4096]; while( stream->CanRead() ) { stream->Read( buffer, sizeof( buffer ) ); auto count = stream->LastRead(); if( !count ) break; output.append( buffer, count ); } };
    read( m_process->GetInputStream(), m_stdout ); read( m_process->GetErrorStream(), m_stderr );
}
void STRUCTURAL_EDITOR_FRAME::helperFinished( wxProcessEvent& event )
{
    if( !m_process || event.GetPid() != m_helperPid ) return; drainHelper(); m_ioTimer.Stop(); m_process.reset();
    S::StructuralFileResult result; auto parsed = google::protobuf::util::JsonStringToMessage( m_stdout, &result );
    ++m_completedSaveCount;
    if( event.GetExitCode() != 0 || !parsed.ok() || !result.success()
        || result.document().document_id() != m_document.document_id() || result.document().source_path() != m_document.source_path()
        || result.document().schema_version() != 1 || result.document().source_token().size() != 64 )
    {
        m_closeAfterSave = false; m_lastSaveError = parsed.ok() && !result.error_message().empty() ? result.error_message() : "Save failed; the current draft is still open.";
        SetStatusText( text( m_lastSaveError ) ); return;
    }
    m_document.set_source_token( result.document().source_token() );
    if( m_revision == m_saveRevision ) { m_dirty = false; m_document = result.document(); }
    refreshModel(); SetStatusText( _( "Saved" ) ); if( m_closeAfterSave && !m_dirty ) { m_closeAfterSave = false; Close(); }
}
void STRUCTURAL_EDITOR_FRAME::closeEditor( wxCloseEvent& event )
{
    if( !propertiesChanged() || IsSaving() ) { event.Veto(); return; }
    if( m_dirty && event.CanVeto() )
    {
        int choice = wxMessageBox( _( "Save changes to this structural diagram?" ), _( "Unsaved changes" ), wxYES_NO | wxCANCEL | wxICON_QUESTION, this );
        if( choice == wxCANCEL ) { event.Veto(); return; }
        if( choice == wxYES ) { m_closeAfterSave = true; save(); event.Veto(); return; }
    }
    Destroy();
}
