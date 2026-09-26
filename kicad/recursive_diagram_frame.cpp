/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#include "recursive_diagram_frame.h"
#include "dialogs/dialog_diagram_field_history.h"
#include "dialogs/dialog_diagram_conflict.h"
#include "dialogs/panel_diagram_history.h"
#include <api/api_server.h>
#include <bitmaps.h>
#include <pgm_base.h>
#include <kiplatform/ui.h>
#include <kiid.h>
#include <google/protobuf/util/json_util.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <wx/accel.h>
#include <wx/arrstr.h>
#include <wx/button.h>
#include <wx/choice.h>
#include <wx/dcbuffer.h>
#include <wx/dcclient.h>
#include <wx/dcmemory.h>
#include <wx/image.h>
#include <wx/mstream.h>
#include <wx/filename.h>
#include <wx/menu.h>
#include <wx/msgdlg.h>
#include <wx/panel.h>
#include <wx/radiobut.h>
#include <wx/scrolwin.h>
#include <wx/settings.h>
#include <wx/sizer.h>
#include <wx/simplebook.h>
#include <wx/splitter.h>
#include <wx/stattext.h>
#include <wx/statline.h>
#include <wx/statusbr.h>
#include <wx/textctrl.h>
#include <wx/textdlg.h>
#include <wx/tglbtn.h>
#include <wx/toolbar.h>
#include <wx/weakref.h>

namespace D = kiapi::automation::diagrams::v1;
namespace R = RECURSIVE_DIAGRAM;
using R::Text;
using R::Utf8;
using R::FreshId;
using R::EditorOrigin;

namespace
{
enum { BACK = wxID_HIGHEST + 3900, UP, FIT, NOTE, DIAGRAM_HISTORY, VIEW_PALETTE };
bool same( const D::BlockSelectionData& a, const D::BlockSelectionData& b )
{ return a.block_id() == b.block_id() && a.state_id() == b.state_id() && a.revision_id() == b.revision_id(); }
bool sameConnection( const D::ConnectionSelectionData& a, const D::ConnectionSelectionData& b )
{ return a.connection_id() == b.connection_id() && a.state_id() == b.state_id() && a.revision_id() == b.revision_id(); }
/// The one-click values of a connection's direction, domain and type rows (Round A3), in display order.
constexpr std::array<D::DiagramConnectionDirection, 3> DIRECTIONS = { D::DCDR_FROM_FIRST, D::DCDR_TO_FIRST, D::DCDR_BIDIRECTIONAL };
constexpr std::array<D::DiagramDomain, 5> DOMAINS = { D::DD_POWER, D::DD_DATA, D::DD_CONTROL, D::DD_ANALOG, D::DD_MECHANICAL };
constexpr std::array<D::DiagramConnectionKind, 4> KINDS = { D::DCK_INTERFACE, D::DCK_SIGNAL_GROUP, D::DCK_DIFFERENTIAL_PAIR, D::DCK_SIGNAL };
const char* const DETAIL_NAMES[] = { "signals", "direction", "domain", "type" };
wxString detailLabel( int which )
{
    static const wxString labels[] = { _( "Signals" ), _( "Direction" ), _( "Domain" ), _( "Type" ) };
    return labels[which];
}
std::string field( const D::RequirementFieldsData& fields, int which )
{ return which == 0 ? fields.general() : which == 1 ? fields.schematic() : fields.routing(); }
void setField( D::RequirementFieldsData* fields, int which, const std::string& value )
{ if( which == 0 ) fields->set_general( value ); else if( which == 1 ) fields->set_schematic( value ); else fields->set_routing( value ); }
void dropRestoration( google::protobuf::RepeatedPtrField<D::FieldRestorationData>* restores, int which )
{ for( int n = restores->size() - 1; n >= 0; --n ) if( static_cast<int>( restores->Get( n ).field() ) == which + 1 ) restores->DeleteSubrange( n, 1 ); }
const wxString FIELD_LABELS[] = { _( "General requirements" ), _( "Schematic requirements" ), _( "Routing requirements" ) };
/// The gap between one-click choices, in DIP; each choice already pads its own label.
constexpr int FACET_CHOICE_GAP = 2;
/// The strength choices' full and short labels (StructuralGuidanceStrength Information, Preference, Requirement).
std::array<wxString, 3> strengthLabels( bool brief )
{
    if( brief ) return { _( "Info" ), _( "Pref." ), _( "Req." ) };
    return { _( "Information" ), _( "Preference" ), _( "Requirement" ) };
}
}

RECURSIVE_DIAGRAM_FRAME::RECURSIVE_DIAGRAM_FRAME( wxWindow* parent, const D::OpenRecursiveDiagramEditor& request ) :
        wxFrame( parent, wxID_ANY, _( "Structural diagram" ), wxDefaultPosition, wxSize( 1536, 1024 ) ),
        m_request( request ), m_ioTimer( this )
{
    SetName( "RecursiveDiagramEditor" ); SetMinSize( FromDIP( wxSize( 900, 650 ) ) );
    wxFont body = GetFont(); body.SetPointSize( std::max( 12, body.GetPointSize() ) ); SetFont( body );
    auto* menu = new wxMenuBar(); auto* file = new wxMenu();
    file->Append( wxID_SAVE, _( "Save\tCtrl+S" ) ); file->Append( wxID_REFRESH, _( "Reload saved diagram\tCtrl+R" ) );
    file->AppendSeparator(); file->Append( wxID_CLOSE, _( "Close\tCtrl+W" ) );
    menu->Append( file, _( "File" ) );
    auto* view = new wxMenu(); view->AppendCheckItem( VIEW_PALETTE, _( "Drawing &palette" ) ); view->Check( VIEW_PALETTE, true );
    menu->Append( view, _( "&View" ) ); SetMenuBar( menu );
    m_toolbar = CreateToolBar( wxTB_HORIZONTAL | wxTB_FLAT | wxTB_TEXT );
    m_toolbar->SetName( "RecursiveToolbar" );
    m_toolbar->AddTool( BACK, _( "Back" ), KiBitmap( BITMAPS::left ) );
    m_toolbar->AddTool( UP, _( "Up" ), KiBitmap( BITMAPS::up ) ); m_toolbar->AddSeparator();
    m_toolbar->AddTool( wxID_UNDO, _( "Undo" ), KiBitmap( BITMAPS::undo ) );
    m_toolbar->AddTool( wxID_REDO, _( "Redo" ), KiBitmap( BITMAPS::redo ) ); m_toolbar->AddSeparator();
    m_toolbar->AddTool( FIT, _( "Fit" ), KiBitmap( BITMAPS::zoom_fit_in_page ) );
    m_toolbar->AddTool( NOTE, _( "Note" ), KiBitmap( BITMAPS::add_textbox ) ); m_toolbar->AddSeparator();
    // Round A1 option 1: the drawing tools follow the approved commands as one compact strip. They are the same tool
    // buttons and glyphs as the canvas-edge palette, at the toolbar's icon size and label baseline, so both show the one
    // active tool the same clear way (design QA P2-1, P2-2 and P2-3).
    auto strip = [&]( TOOL tool, const wxString& label, R::GLYPH glyph, const char* name, const wxString& tip )
    {
        auto* button = new R::TOOL_BUTTON( m_toolbar, label, glyph, R::TOOL_STYLE::STRIP, name, tip );
        button->Bind( wxEVT_TOGGLEBUTTON, [this, tool]( wxCommandEvent& ) { setTool( tool ); m_canvas->SetFocus(); } );
        m_toolbar->AddControl( button ); m_strip.emplace_back( tool, button );
    };
    strip( TOOL::SELECT, _( "Select" ), R::GLYPH::SELECT, "RecursiveToolSelect", _( "Select and move items (Esc)" ) );
    strip( TOOL::ADD_BLOCK, _( "Add block" ), R::GLYPH::ADD_BLOCK, "RecursiveToolAddBlock", _( "Add a block where you click (B)" ) );
    strip( TOOL::CONNECT, _( "Connect" ), R::GLYPH::CONNECT, "RecursiveToolConnect", _( "Connect two blocks or ports (C)" ) );
    strip( TOOL::ADD_PORT, _( "Place port" ), R::GLYPH::PORT, "RecursiveToolPlacePort", _( "Place a port on a block edge or the level boundary (P)" ) );
    // Sketch 1 sets Delete apart from the drawing tools with a separator (design QA round 2, P3 4).
    m_toolbar->AddSeparator();
    m_stripDelete = new R::TOOL_ACTION( m_toolbar, _( "Delete" ), R::GLYPH::REMOVE, R::TOOL_STYLE::STRIP, "RecursiveToolDelete",
                                        _( "Delete the selection (Delete)" ) );
    m_stripDelete->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { removeSelection(); } );
    m_toolbar->AddControl( m_stripDelete );
    m_toolbar->Realize();
    auto* splitter = new wxSplitterWindow( this, wxID_ANY, wxDefaultPosition, wxDefaultSize, wxSP_LIVE_UPDATE );
    splitter->SetMinimumPaneSize( FromDIP( 300 ) ); splitter->SetSashGravity( 1.0 ); m_splitter = splitter;
    auto* diagram = new wxPanel( splitter ); auto* main = new wxBoxSizer( wxVERTICAL );
    m_breadcrumb = new wxStaticText( diagram, wxID_ANY, _( "Loading diagram…" ), wxDefaultPosition, wxDefaultSize, wxST_ELLIPSIZE_MIDDLE );
    m_breadcrumb->SetMinSize( FromDIP( wxSize( 80, -1 ) ) );
    m_breadcrumb->SetName( "RecursiveDiagramPath" );
    auto* pathRow = new wxBoxSizer( wxHORIZONTAL ); pathRow->Add( m_breadcrumb, 1, wxALIGN_CENTER_VERTICAL );
    m_implementation = new wxButton( diagram, wxID_ANY, _( "Implementation" ), wxDefaultPosition, wxDefaultSize, wxBU_EXACTFIT );
    m_implementation->SetName( "RecursiveImplementation" ); pathRow->Add( m_implementation, 0, wxLEFT, FromDIP( 12 ) );
    m_diagramHistory = new wxButton( diagram, wxID_ANY, _( "History" ), wxDefaultPosition, wxDefaultSize, wxBU_EXACTFIT );
    m_diagramHistory->SetName( "RecursiveDiagramHistory" ); pathRow->Add( m_diagramHistory, 0, wxLEFT, FromDIP( 12 ) );
    main->Add( pathRow, 0, wxEXPAND | wxALL, FromDIP( 12 ) );
    m_canvas = new wxWindow( diagram, wxID_ANY, wxDefaultPosition, wxDefaultSize, wxWANTS_CHARS | wxBORDER_NONE | wxFULL_REPAINT_ON_RESIZE );
    m_canvas->SetName( "RecursiveDiagramCanvas" );
    m_canvas->SetBackgroundStyle( wxBG_STYLE_PAINT ); main->Add( m_canvas, 1, wxEXPAND ); diagram->SetSizer( main );
    // Round A1 option 2: the same tools plus Undo on the canvas edge; hideable from View.
    R::TOOL_PALETTE::ACTIONS paletteActions;
    paletteActions.choose = [this]( TOOL tool ) { setTool( tool ); m_canvas->SetFocus(); };
    paletteActions.remove = [this] { removeSelection(); };
    paletteActions.undo = [this] { undo( false ); };
    m_palette = new R::TOOL_PALETTE( m_canvas, std::move( paletteActions ) );
    m_caption = new wxTextCtrl( m_canvas, wxID_ANY, wxEmptyString, wxDefaultPosition, wxDefaultSize, wxTE_PROCESS_ENTER );
    // The caption field's focus ring is drawn by the canvas in its accent (design QA P1-1); moving the field redraws it.
    m_caption->Bind( wxEVT_SET_FOCUS, [this]( wxFocusEvent& event ) { m_rendered = false; m_canvas->Refresh(); event.Skip(); } );
    m_caption->SetName( "DiagramCaptionEditor" ); m_caption->Hide();
    m_caption->Bind( wxEVT_TEXT_ENTER, [this]( wxCommandEvent& ) { finishCaption( true ); } );
    // Moving focus away keeps a typed caption and cancels a blank one, so focus is never pulled back.
    m_caption->Bind( wxEVT_KILL_FOCUS, [this]( wxFocusEvent& event )
    {
        event.Skip();
        if( m_captionKind ) CallAfter( [this] { if( m_captionKind && wxWindow::FindFocus() != m_caption )
            finishCaption( !m_caption->GetValue().Strip( wxString::both ).empty() ); } );
    } );
    auto* inspectorRoot = new wxPanel( splitter ); inspectorRoot->SetMinSize( FromDIP( wxSize( 380, -1 ) ) );
    m_inspectorBook = new wxSimplebook( inspectorRoot );
    auto* inspector = new wxPanel( m_inspectorBook ); auto* properties = new wxBoxSizer( wxVERTICAL );
    auto* side = new wxBoxSizer( wxVERTICAL );
    auto* scroll = new wxScrolledWindow( inspector ); scroll->SetScrollRate( 0, FromDIP( 12 ) );
    m_inspectorScroll = scroll; scroll->SetName( "RecursiveInspector" );
    auto* fields = new wxBoxSizer( wxVERTICAL );
    // Round A4 option 3 (owner decision n0b2a908b00e78823): the facet overview lists only the facets that have a
    // value, and one facet's detail edits its state, value, reason and strength. Built first so it leads the
    // scrolled inspector and its tab order.
    m_facetHeading = new wxStaticText( scroll, wxID_ANY, _( "Facet overview" ) );
    m_facetHeading->SetFont( GetFont().Bold() ); fields->Add( m_facetHeading, 0, wxLEFT | wxRIGHT | wxBOTTOM, FromDIP( 12 ) );
    for( int facet = 0; facet < R::FACETS; ++facet )
    {
        m_facetRows[facet] = new R::FACET_ROW( scroll, facet, [this]( int chosen ) { openFacet( chosen, true ); } );
        fields->Add( m_facetRows[facet], 0, wxEXPAND | wxLEFT | wxRIGHT, FromDIP( 12 ) );
    }
    // A clear gap after the facet table, the gap used between the other sections (design QA P2-12).
    m_facetGap = fields->AddSpacer( FromDIP( 12 ) );
    m_facetDetail = new wxBoxSizer( wxVERTICAL );
    // Back and Clear facet are actions, so they look like the canvas's Review facets link (design QA P2-10).
    m_facetBack = new R::LINK_BUTTON( scroll, wxS( "↑ " ) + _( "Back to facet overview" ), "RecursiveFacetBack" );
    m_facetBack->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { closeFacet( true ); } );
    m_facetDetail->Add( m_facetBack, 0, wxTOP | wxBOTTOM, FromDIP( 6 ) );
    m_facetTitle = new wxStaticText( scroll, wxID_ANY, wxEmptyString ); m_facetTitle->SetFont( GetFont().Bold() );
    m_facetTitle->SetName( "RecursiveFacetTitle" ); m_facetDetail->Add( m_facetTitle, 0, wxBOTTOM, FromDIP( 8 ) );
    // State and strength are small fixed sets, so each is a row of one-click choices under its label.
    auto label = [&]( const wxString& text )
    {
        auto* item = new wxStaticText( scroll, wxID_ANY, text );
        m_facetDetail->Add( item, 0, wxTOP, FromDIP( 6 ) ); return item;
    };
    // A row wraps only when even its short labels do not fit (fitFacetLabels collapses the strength labels first, and
    // gives both rows the width they wrap within before the inspector is laid out, so what follows a wrapped row moves
    // down with it).
    auto choices = [&]( std::array<wxRadioButton*, 3>& buttons, const std::array<wxString, 3>& labels, const char* name,
                        const std::array<const char*, 3>& names )
    {
        auto* row = new R::CHOICE_FLOW( FromDIP( FACET_CHOICE_GAP ) );
        for( int i = 0; i < 3; ++i )
        {
            buttons[i] = new wxRadioButton( scroll, wxID_ANY, labels[i], wxDefaultPosition, wxDefaultSize, i == 0 ? wxRB_GROUP : 0 );
            buttons[i]->SetName( wxString( name ) + names[i] ); buttons[i]->SetToolTip( labels[i] );
            row->Add( buttons[i], 0, wxTOP, FromDIP( 4 ) );
        }
        m_facetDetail->Add( row, 0, wxEXPAND );
        return row;
    };
    label( _( "State" ) );
    m_facetStateRow = choices( m_facetStates, { _( "Chosen" ), _( "Candidate" ), _( "Unknown" ) }, "RecursiveFacetState",
                               { "Chosen", "Candidate", "Unknown" } );
    m_facetValueLabel = label( _( "Value" ) );
    m_facetValue = new wxTextCtrl( scroll, wxID_ANY, wxEmptyString, wxDefaultPosition, wxDefaultSize, wxTE_PROCESS_ENTER );
    m_facetValue->SetName( "RecursiveFacetValue" ); m_facetDetail->Add( m_facetValue, 0, wxEXPAND | wxTOP, FromDIP( 4 ) );
    m_facetCandidates = new wxTextCtrl( scroll, wxID_ANY, wxEmptyString, wxDefaultPosition, FromDIP( wxSize( 200, 62 ) ), wxTE_MULTILINE );
    R::PadTextBox( m_facetCandidates, FromDIP( 8 ), FromDIP( 6 ) );
    m_facetCandidates->SetName( "RecursiveFacetCandidates" ); m_facetCandidates->SetHint( _( "One per line" ) );
    m_facetDetail->Add( m_facetCandidates, 0, wxEXPAND | wxTOP, FromDIP( 4 ) );
    m_facetReason = new wxTextCtrl( scroll, wxID_ANY, wxEmptyString, wxDefaultPosition, FromDIP( wxSize( 200, 62 ) ), wxTE_MULTILINE );
    R::PadTextBox( m_facetReason, FromDIP( 8 ), FromDIP( 6 ) );
    m_facetReason->SetName( "RecursiveFacetReason" ); m_facetDetail->Add( m_facetReason, 0, wxEXPAND | wxTOP, FromDIP( 4 ) );
    label( _( "Strength" ) );
    m_facetStrengthRow = choices( m_facetStrengths, strengthLabels( false ), "RecursiveFacetStrength",
                                  { "Information", "Preference", "Requirement" } );
    m_facetNotice = new wxStaticText( scroll, wxID_ANY, wxEmptyString ); m_facetNotice->SetName( "RecursiveFacetNotice" );
    m_facetDetail->Add( m_facetNotice, 0, wxEXPAND | wxTOP, FromDIP( 6 ) );
    m_facetClear = new R::LINK_BUTTON( scroll, _( "Clear facet" ), "RecursiveFacetClear" );
    m_facetDetail->Add( m_facetClear, 0, wxTOP, FromDIP( 6 ) );
    m_facetClear->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { clearFacet(); } );
    for( auto* button : m_facetStates ) button->Bind( wxEVT_RADIOBUTTON, [this]( wxCommandEvent& ) { if( !m_updating ) facetStateChanged(); } );
    for( auto* button : m_facetStrengths ) button->Bind( wxEVT_RADIOBUTTON, [this]( wxCommandEvent& ) { if( !m_updating ) facetEdited(); } );
    for( auto* entry : { m_facetValue, m_facetCandidates, m_facetReason } )
        entry->Bind( wxEVT_TEXT, [this]( wxCommandEvent& ) { if( !m_updating ) facetEdited(); } );
    // Enter keeps a typed value and returns to the overview.
    m_facetValue->Bind( wxEVT_TEXT_ENTER, [this]( wxCommandEvent& ) { if( m_facetProblem.empty() ) closeFacet( true ); } );
    fields->Add( m_facetDetail, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, FromDIP( 12 ) );
    auto* header = new wxPanel( inspector ); auto* heading = new wxBoxSizer( wxVERTICAL );
    m_owner = new wxStaticText( header, wxID_ANY, wxEmptyString, wxDefaultPosition, wxDefaultSize, wxST_ELLIPSIZE_END );
    m_owner->SetName( "RecursiveOwnerCaption" );
    m_owner->SetFont( GetFont().Bold().Larger() ); heading->Add( m_owner, 0, wxEXPAND | wxALL, FromDIP( 12 ) );
    m_savedVersion = new wxStaticText( header, wxID_ANY, wxEmptyString ); m_savedVersion->SetName( "RecursiveSavedVersion" );
    heading->Add( m_savedVersion, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, FromDIP( 12 ) );
    m_openDiagram = new wxButton( header, wxID_ANY, _( "Open diagram" ) ); m_openDiagram->SetName( "RecursiveOpenDiagram" );
    heading->Add( m_openDiagram, 0, wxLEFT | wxRIGHT | wxBOTTOM, FromDIP( 12 ) );
    header->SetSizer( heading ); properties->Add( header, 0, wxEXPAND );
    // Round A3 option 1 (owner decision nf53af9d74841b7d3): a selected connection shows its caption in an editable field and
    // then only the details it has, each as its own row that can be removed again. Built here so they follow the block's
    // component choices in the scrolled inspector and its tab order.
    auto* captionColumn = new wxBoxSizer( wxVERTICAL );
    m_captionLabel = new wxStaticText( scroll, wxID_ANY, _( "Caption" ) );
    // One bold heading style for every section of the inspector, the connection's as well (design QA round 2, P3 17).
    m_captionLabel->SetFont( GetFont().Bold() );
    captionColumn->Add( m_captionLabel, 0, wxBOTTOM, FromDIP( 4 ) );
    m_connectionCaption = new wxTextCtrl( scroll, wxID_ANY, wxEmptyString );
    m_connectionCaption->SetName( "RecursiveConnectionCaption" ); captionColumn->Add( m_connectionCaption, 0, wxEXPAND );
    m_captionNotice = new wxStaticText( scroll, wxID_ANY, wxEmptyString ); m_captionNotice->SetName( "RecursiveCaptionNotice" );
    captionColumn->Add( m_captionNotice, 0, wxEXPAND | wxTOP, FromDIP( 4 ) );
    fields->Add( captionColumn, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, FromDIP( 12 ) );
    m_connectionCaption->Bind( wxEVT_TEXT, [this]( wxCommandEvent& ) { if( !m_updating ) captionEdited(); } );
    // One row per detail: its bold name with the action that removes the whole detail, then its value. That action is a link
    // that says what it removes ("Remove signals"), so it cannot be taken for the "×" that removes one signal (design QA round
    // 2, R2-P2-5), and it reads like Clear facet, which removes a block's facet the same way.
    auto detailRow = [&]( DETAIL detail, const char* name )
    {
        auto* row = new wxBoxSizer( wxVERTICAL ); auto* title = new wxBoxSizer( wxHORIZONTAL );
        auto* detailName = new wxStaticText( scroll, wxID_ANY, detailLabel( static_cast<int>( detail ) ) ); detailName->SetFont( GetFont().Bold() );
        title->Add( detailName, 1, wxALIGN_CENTER_VERTICAL );
        auto* remove = new R::LINK_BUTTON( scroll, wxString::Format( _( "Remove %s" ), detailLabel( static_cast<int>( detail ) ).Lower() ),
                                           ( std::string( "RecursiveDetailRemove" ) + name ).c_str() );
        remove->Bind( wxEVT_BUTTON, [this, detail]( wxCommandEvent& ) { removeDetail( detail ); } );
        title->Add( remove, 0, wxALIGN_CENTER_VERTICAL ); row->Add( title, 0, wxEXPAND );
        m_detailRows[static_cast<int>( detail )] = row; m_detailRemove[static_cast<int>( detail )] = remove;
        fields->Add( row, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, FromDIP( 12 ) );
        return row;
    };
    // Small fixed sets are one-click choices; none is chosen until the person chooses one.
    // Chosen options use the accent checked style of the drawing tools (design QA round 2, R2-P2-4). The rows wrap within the
    // width fitFacetLabels gives them before the inspector is laid out, like the facet detail's rows, so what follows a row
    // that wraps moves down with it.
    auto oneClick = [&]( wxSizer* row, wxToggleButton** buttons, int count, const char* prefix, const char* const* names,
                         const wxString* labels, DETAIL detail, const int* values )
    {
        auto* flow = new R::CHOICE_FLOW( FromDIP( 4 ) );
        for( int i = 0; i < count; ++i )
        {
            buttons[i] = new R::CHOICE_BUTTON( scroll, labels[i], wxString( prefix ) + names[i] );
            int value = values[i];
            buttons[i]->Bind( wxEVT_TOGGLEBUTTON, [this, detail, value]( wxCommandEvent& ) { if( !m_updating ) setLinkValue( detail, value ); } );
            flow->Add( buttons[i], 0, wxTOP, FromDIP( 4 ) );
        }
        row->Add( flow, 0, wxEXPAND );
        m_linkChoiceRows[static_cast<int>( detail ) - 1] = flow;
    };
    auto* signalRow = detailRow( DETAIL::SIGNALS, "Signals" );
    // The signals start 8 DIP below the row's "Remove signals", so it never sits against the first signal's own "×" (design QA
    // round 2, R2-P2-5: at least 8 pixels between the two removes).
    m_signalList = new wxBoxSizer( wxVERTICAL ); signalRow->Add( m_signalList, 0, wxEXPAND | wxTOP, FromDIP( 8 ) );
    m_signalEntry = new wxTextCtrl( scroll, wxID_ANY, wxEmptyString, wxDefaultPosition, wxDefaultSize, wxTE_PROCESS_ENTER );
    m_signalEntry->SetName( "RecursiveSignalEntry" ); m_signalEntry->SetHint( _( "Add a signal" ) );
    signalRow->Add( m_signalEntry, 0, wxEXPAND | wxTOP, FromDIP( 4 ) );
    m_signalEntry->Bind( wxEVT_TEXT_ENTER, [this]( wxCommandEvent& ) { addSignal(); } );
    m_signalEntry->Bind( wxEVT_TEXT, [this]( wxCommandEvent& ) { if( !m_updating && !m_signalProblem.empty() ) { m_signalProblem.clear(); refresh(); } } );
    m_signalNotice = new wxStaticText( scroll, wxID_ANY, wxEmptyString ); m_signalNotice->SetName( "RecursiveSignalNotice" );
    signalRow->Add( m_signalNotice, 0, wxEXPAND | wxTOP, FromDIP( 4 ) );
    {
        const char* names[] = { "FromFirst", "ToFirst", "Both" };
        const wxString labels[] = { wxS( "\u2192" ), wxS( "\u2190" ), _( "Both ways" ) };
        int values[] = { DIRECTIONS[0], DIRECTIONS[1], DIRECTIONS[2] };
        oneClick( detailRow( DETAIL::DIRECTION, "Direction" ), m_directionChoices.data(), 3, "RecursiveDirection", names, labels, DETAIL::DIRECTION, values );
    }
    {
        const char* names[] = { "Power", "Data", "Control", "Analog", "Mechanical" };
        const wxString labels[] = { _( "Power" ), _( "Data" ), _( "Control" ), _( "Analog" ), _( "Mechanical" ) };
        int values[] = { DOMAINS[0], DOMAINS[1], DOMAINS[2], DOMAINS[3], DOMAINS[4] };
        oneClick( detailRow( DETAIL::DOMAIN, "Domain" ), m_domainChoices.data(), 5, "RecursiveDomain", names, labels, DETAIL::DOMAIN, values );
    }
    {
        const char* names[] = { "Interface", "SignalGroup", "DifferentialPair", "Signal" };
        const wxString labels[] = { _( "Interface" ), _( "Signal group" ), _( "Differential pair" ), _( "Signal" ) };
        int values[] = { KINDS[0], KINDS[1], KINDS[2], KINDS[3] };
        oneClick( detailRow( DETAIL::TYPE, "Type" ), m_typeChoices.data(), 4, "RecursiveType", names, labels, DETAIL::TYPE, values );
    }
    // What an agent or a later realization states about an end beyond its block or port (pins, candidates, a selector or
    // intent) is shown as read-only text; a drawn end is already on the canvas.
    auto* endpointRow = new wxBoxSizer( wxVERTICAL ); auto* endpointTitle = new wxBoxSizer( wxHORIZONTAL );
    m_endpointHeading = new wxStaticText( scroll, wxID_ANY, _( "Endpoints" ) ); m_endpointHeading->SetFont( GetFont().Bold() );
    endpointTitle->Add( m_endpointHeading, 1, wxALIGN_CENTER_VERTICAL );
    m_endpointRemove = new R::LINK_BUTTON( scroll, _( "Remove endpoint details" ), "RecursiveDetailRemoveEndpoints" );
    m_endpointRemove->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { removeEndpointDetails(); } );
    endpointTitle->Add( m_endpointRemove, 0, wxALIGN_CENTER_VERTICAL ); endpointRow->Add( endpointTitle, 0, wxEXPAND );
    m_endpoints = new wxStaticText( scroll, wxID_ANY, wxEmptyString ); m_endpoints->SetName( "RecursiveConnectionEndpoints" );
    endpointRow->Add( m_endpoints, 0, wxEXPAND | wxLEFT | wxTOP, FromDIP( 4 ) );
    fields->Add( endpointRow, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, FromDIP( 12 ) );
    // The two quiet add actions of the approved inspector: + Add detail gives a block's component choice its first value
    // (Round A4) or adds one connection detail (Round A3); + Add requirement shows a requirement box that is hidden until
    // someone adds it (owner decision n98a3f3c41084f0ed). Blocks and connections grow the same way.
    m_addDetail = new wxButton( scroll, wxID_ANY, wxS( "+  " ) + _( "Add detail" ), wxDefaultPosition, wxDefaultSize, wxBU_LEFT );
    m_addDetail->SetName( "RecursiveAddDetail" );
    m_addDetail->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { chooseDetail(); } );
    fields->Add( m_addDetail, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, FromDIP( 12 ) );
    m_addSeparator = new wxStaticLine( scroll ); m_addSeparator->SetName( "staticLine" );
    fields->Add( m_addSeparator, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, FromDIP( 12 ) );
    for( int i = 0; i < 3; ++i )
    {
        // Each requirement appears only once it has text or the user adds it (owner decision n98a3f3c41084f0ed).
        auto* row = new wxBoxSizer( wxVERTICAL );
        auto* title = new wxBoxSizer( wxHORIZONTAL );
        // One bold heading style for the inspector's sections (design QA P3 19).
        auto* fieldLabel = new wxStaticText( scroll, wxID_ANY, FIELD_LABELS[i] ); fieldLabel->SetFont( GetFont().Bold() );
        title->Add( fieldLabel, 1, wxALIGN_CENTER_VERTICAL );
        m_history[i] = new wxButton( scroll, wxID_ANY, _( "History" ), wxDefaultPosition, wxDefaultSize, wxBU_EXACTFIT );
        m_history[i]->SetName( wxString::Format( "RecursiveFieldHistory%d", i ) );
        title->Add( m_history[i], 0 ); row->Add( title, 0, wxEXPAND | wxLEFT | wxRIGHT, FromDIP( 12 ) );
        m_fields[i] = new wxTextCtrl( scroll, wxID_ANY, wxEmptyString, wxDefaultPosition,
                FromDIP( wxSize( 320, 90 ) ), wxTE_MULTILINE );
        m_fields[i]->SetName( wxString::Format( "RecursiveRequirements%d", i ) );
        // Text sits inside the box, not against its border (design QA P2-9).
        R::PadTextBox( m_fields[i], FromDIP( 8 ), FromDIP( 6 ) );
        row->Add( m_fields[i], 0, wxEXPAND | wxALL, FromDIP( 12 ) );
        fields->Add( row, 0, wxEXPAND ); m_fieldHeadings[i] = row;
        m_fields[i]->Bind( wxEVT_TEXT, [this]( wxCommandEvent& ) { if( !m_updating ) edit(); } );
        m_history[i]->Bind( wxEVT_BUTTON, [this, i]( wxCommandEvent& ) { history( i ); } );
    }
    m_addRequirement = new wxButton( scroll, wxID_ANY, wxS( "+  " ) + _( "Add requirement" ), wxDefaultPosition, wxDefaultSize, wxBU_LEFT );
    m_addRequirement->SetName( "RecursiveAddRequirement" );
    m_addRequirement->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { chooseRequirement(); } );
    fields->Add( m_addRequirement, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, FromDIP( 12 ) );
    auto* commentsHeading = new wxBoxSizer( wxHORIZONTAL );
    auto* commentsLabel = new wxStaticText( scroll, wxID_ANY, _( "Comments" ) ); commentsLabel->SetFont( GetFont().Bold() );
    commentsHeading->Add( commentsLabel, 1, wxALIGN_CENTER_VERTICAL );
    m_commentChoice = new wxChoice( scroll, wxID_ANY, wxDefaultPosition, FromDIP( wxSize( 190, -1 ) ) );
    m_commentChoice->SetName( "RecursiveCommentSelection" ); commentsHeading->Add( m_commentChoice, 0 );
    fields->Add( commentsHeading, 0, wxEXPAND | wxLEFT | wxRIGHT, FromDIP( 12 ) );
    m_commentTargetStatus = new wxStaticText( scroll, wxID_ANY, wxEmptyString );
    m_commentTargetStatus->SetName( "RecursiveCommentTargetStatus" );
    fields->Add( m_commentTargetStatus, 0, wxEXPAND | wxLEFT | wxRIGHT | wxTOP, FromDIP( 12 ) );
    m_comments = new wxTextCtrl( scroll, wxID_ANY, wxEmptyString, wxDefaultPosition, FromDIP( wxSize( 320, 110 ) ), wxTE_MULTILINE );
    m_comments->SetName( "RecursiveComments" ); R::PadTextBox( m_comments, FromDIP( 8 ), FromDIP( 6 ) );
    fields->Add( m_comments, 0, wxEXPAND | wxALL, FromDIP( 12 ) );
    m_comments->Bind( wxEVT_TEXT, [this]( wxCommandEvent& ) { if( !m_updating ) editComment(); } );
    m_commentChoice->Bind( wxEVT_CHOICE, [this]( wxCommandEvent& )
    {
        int chosen = m_commentChoice->GetSelection();
        if( chosen >= 0 && chosen < static_cast<int>( m_commentIds.size() ) )
        { m_commentId = m_commentIds[chosen]; m_newComment = m_commentId.empty(); fillComments(); m_comments->SetFocus(); }
    } );
    scroll->SetSizer( fields ); properties->Add( scroll, 1, wxEXPAND ); inspector->SetSizer( properties );
    // A scroll bar that stays visible while the inspector holds more than it shows (design QA P2-13: GTK's overlay scroll bar
    // is hidden until the pointer moves over it, so a cut-off box gave no sign that the inspector scrolls).
    KIPLATFORM::UI::SetOverlayScrolling( scroll, false );
    // A wider or narrower inspector (the splitter, or its scroll bar showing) restores or collapses the strength labels.
    scroll->Bind( wxEVT_SIZE, [this]( wxSizeEvent& event )
    {
        event.Skip();
        CallAfter( [this] { if( !m_closing && fitFacetLabels() ) { m_inspectorScroll->Layout(); m_inspectorScroll->FitInside(); ++m_viewRevision; } } );
    } );
    m_inspectorBook->AddPage( inspector, _( "Properties" ) );
    PANEL_DIAGRAM_HISTORY::ACTIONS historyActions;
    historyActions.load = [this]( unsigned offset ) { loadDiagramHistory( offset ); };
    historyActions.inspect = [this]( SELECTION selected ) { inspectDiagramHistory( std::move( selected ) ); };
    historyActions.preview = [this]( SELECTION selected ) { previewDiagramHistory( std::move( selected ) ); };
    historyActions.restore = [this]( SELECTION selected ) { restoreDiagramHistory( std::move( selected ) ); };
    historyActions.close = [this] { closeDiagramHistory(); };
    historyActions.returnToCurrent = [this] { returnFromHistoryPreview(); };
    historyActions.retry = [this] { if( !m_process && m_diagramHistoryOpen && m_failedHistoryRequest.schema_version() )
        { m_diagramHistoryPanel->SetBusy( true ); execute( m_failedHistoryRequest ); } };
    m_diagramHistoryPanel = new PANEL_DIAGRAM_HISTORY( m_inspectorBook, std::move( historyActions ) );
    m_inspectorBook->AddPage( m_diagramHistoryPanel, _( "History" ) ); side->Add( m_inspectorBook, 1, wxEXPAND );
    // Decline and Save share the inspector's width; Save is the primary action (design QA P2-8, styled by enableSave).
    auto* actions = new wxBoxSizer( wxHORIZONTAL );
    m_decline = new wxButton( inspectorRoot, wxID_ANY, _( "&Decline" ) ); m_decline->SetName( "RecursiveDecline" );
    m_save = new wxButton( inspectorRoot, wxID_SAVE, _( "Save" ) ); m_save->SetName( "RecursiveSave" );
    // One minimum size for both, so the sizer gives them equal halves whatever their labels.
    m_decline->SetMinSize( FromDIP( wxSize( 60, 36 ) ) ); m_save->SetMinSize( FromDIP( wxSize( 60, 36 ) ) );
    // The 12 DIP gap is split between them: a sizer counts an item's border as part of its share.
    actions->Add( m_decline, 1, wxRIGHT, FromDIP( 6 ) ); actions->Add( m_save, 1, wxLEFT, FromDIP( 6 ) );
    side->Add( actions, 0, wxEXPAND | wxALL, FromDIP( 12 ) ); inspectorRoot->SetSizer( side );
    splitter->SplitVertically( diagram, inspectorRoot, FromDIP( 1100 ) );
    auto* frameSizer = new wxBoxSizer( wxVERTICAL ); frameSizer->Add( splitter, 1, wxEXPAND ); SetSizer( frameSizer ); CreateStatusBar();
    m_canvas->Bind( wxEVT_PAINT, [this]( wxPaintEvent& ) { wxAutoBufferedPaintDC dc( m_canvas ); paint( dc ); } );
    m_canvas->Bind( wxEVT_SIZE, [this]( wxSizeEvent& event )
    {
        m_rendered = false; ++m_viewRevision; updateImplementationLabel(); placePaletteAndEditor();
        // The canvas has no scrolling: a resized window re-fits while the view is still the fitted one, or
        // whenever part of the level would otherwise be out of view.
        if( m_ready && current() && !m_captionKind && m_drag == DRAG::NONE && ( m_fitted || !drawingFits() ) ) fit();
        m_canvas->Refresh(); event.Skip();
    } );
    m_canvas->Bind( wxEVT_LEFT_DOWN, &RECURSIVE_DIAGRAM_FRAME::click, this );
    m_canvas->Bind( wxEVT_LEAVE_WINDOW, [this]( wxMouseEvent& event )
    {
        m_pointerInside = false;
        // The canvas's tooltip goes with the pointer (review of design QA round 2).
        updateCanvasTip();
        if( m_tool == TOOL::CONNECT ) { m_rendered = false; m_canvas->Refresh(); }
        event.Skip();
    } );
    m_canvas->Bind( wxEVT_LEFT_DCLICK, &RECURSIVE_DIAGRAM_FRAME::click, this );
    m_canvas->Bind( wxEVT_MOTION, &RECURSIVE_DIAGRAM_FRAME::motion, this );
    // A pointer that jumps onto the canvas from elsewhere may bring only its entry, no motion: the tooltip, cursor and Connect
    // highlight follow it all the same.
    m_canvas->Bind( wxEVT_ENTER_WINDOW, [this]( wxMouseEvent& event ) { motion( event ); event.Skip(); } );
    // The release point ends a drag, even when the pointer's last motion before it arrived late or was merged away.
    m_canvas->Bind( wxEVT_LEFT_UP, [this]( wxMouseEvent& event ) { dragTo( event.GetPosition() ); release(); } );
    m_canvas->Bind( wxEVT_MOUSE_CAPTURE_LOST, [this]( wxMouseCaptureLostEvent& ) { release(); } );
    m_canvas->Bind( wxEVT_KEY_DOWN, [this]( wxKeyEvent& event )
    { if( !canvasKey( event ) ) event.Skip(); } );
    m_openDiagram->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { navigate( m_selected ); } );
    m_implementation->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { chooseImplementation(); } );
    m_diagramHistory->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { openDiagramHistory(); } );
    Bind( wxEVT_MENU, [this]( wxCommandEvent& ) { openDiagramHistory(); }, DIAGRAM_HISTORY );
    wxAcceleratorEntry historyKey( wxACCEL_CTRL, 'H', DIAGRAM_HISTORY );
    SetAcceleratorTable( wxAcceleratorTable( 1, &historyKey ) );
    m_save->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { save(); } );
    m_decline->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { decline(); } );
    Bind( wxEVT_MENU, [this]( wxCommandEvent& ) { save(); }, wxID_SAVE );
    Bind( wxEVT_MENU, [this]( wxCommandEvent& ) { reloadSaved(); }, wxID_REFRESH );
    Bind( wxEVT_MENU, [this]( wxCommandEvent& ) { Close(); }, wxID_CLOSE );
    Bind( wxEVT_MENU, [this]( wxCommandEvent& event )
    { m_paletteShown = event.IsChecked(); placePaletteAndEditor(); ++m_viewRevision; m_rendered = false; m_canvas->Refresh(); }, VIEW_PALETTE );
    Bind( wxEVT_TOOL, [this]( wxCommandEvent& ) { if( !m_back.empty() ) { auto id = m_back.back(); navigate( id, false ); if( current() && current()->selection().block_id() == id ) m_back.pop_back(); refresh(); } }, BACK );
    Bind( wxEVT_TOOL, [this]( wxCommandEvent& ) { if( m_path.size() > 1 ) navigate( m_path[m_path.size() - 2].block_id() ); }, UP );
    Bind( wxEVT_TOOL, [this]( wxCommandEvent& ) { fit(); }, FIT );
    Bind( wxEVT_TOOL, [this]( wxCommandEvent& ) { setTool( TOOL::NOTE ); }, NOTE );
    Bind( wxEVT_TOOL, [this]( wxCommandEvent& ) { undo( false ); }, wxID_UNDO );
    Bind( wxEVT_TOOL, [this]( wxCommandEvent& ) { undo( true ); }, wxID_REDO );
    Bind( wxEVT_TIMER, [this]( wxTimerEvent& ) { drain(); }, m_ioTimer.GetId() );
    Bind( wxEVT_END_PROCESS, &RECURSIVE_DIAGRAM_FRAME::completed, this );
    Bind( wxEVT_CLOSE_WINDOW, &RECURSIVE_DIAGRAM_FRAME::close, this );
    Bind( wxEVT_CHAR_HOOK, [this]( wxKeyEvent& event )
    {
        // The in-place caption editor owns typing; Enter keeps and Escape cancels the caption.
        if( m_captionKind && wxWindow::FindFocus() == m_caption )
        {
            if( event.GetKeyCode() == WXK_ESCAPE ) { finishCaption( false ); return; }
            if( event.GetKeyCode() == WXK_RETURN || event.GetKeyCode() == WXK_NUMPAD_ENTER ) { finishCaption( true ); return; }
            // Save keeps a typed caption first; a blank one refuses the save and stays open.
            if( event.ControlDown() && event.GetKeyCode() == 'S' ) { save(); return; }
            event.Skip(); return;
        }
        if( event.GetKeyCode() == WXK_ESCAPE && m_diagramHistoryOpen ) { closeDiagramHistory(); return; }
        // Escape in the new-signal entry clears it; in a blank connection caption it brings back the last caption.
        if( event.GetKeyCode() == WXK_ESCAPE && wxWindow::FindFocus() == m_signalEntry )
        { m_signalEntry->ChangeValue( wxEmptyString ); m_signalProblem.clear(); refresh(); m_signalEntry->SetFocus(); return; }
        if( event.GetKeyCode() == WXK_ESCAPE && wxWindow::FindFocus() == m_connectionCaption && !m_captionProblem.empty() )
        { m_captionProblem.clear(); m_notice.clear(); refresh(); m_connectionCaption->SetFocus(); m_connectionCaption->SelectAll(); return; }
        // Escape in a facet's detail returns to the overview and drops an entry that could not be kept.
        if( event.GetKeyCode() == WXK_ESCAPE && m_facet >= 0 && facetHasFocus() ) { closeFacet( true ); return; }
        // Tab moves between the detail's controls in order, the multi-line entries included.
        if( event.GetKeyCode() == WXK_TAB && !event.ControlDown() && !event.AltDown() && m_facet >= 0 && facetHasFocus() )
        { wxWindow::FindFocus()->Navigate( event.ShiftDown() ? wxNavigationKeyEvent::IsBackward : wxNavigationKeyEvent::IsForward ); return; }
        if( event.ControlDown() && event.GetKeyCode() == 'H' ) { openDiagramHistory(); return; }
        if( event.ControlDown() && event.GetKeyCode() == 'I' ) { chooseImplementation(); return; }
        if( event.ControlDown() && event.GetKeyCode() == 'R' ) { reloadSaved(); return; }
        if( event.ControlDown() && event.GetKeyCode() >= '1' && event.GetKeyCode() <= '5' )
        { if( m_ready && !m_process && !m_diagramHistoryOpen ) { if( event.GetKeyCode() == '5' ) { if( m_commentChoice->IsShown() ) m_commentChoice->SetFocus(); }
            else if( event.GetKeyCode() == '4' ) m_comments->SetFocus(); else revealField( event.GetKeyCode() - '1' ); } return; }
        if( event.AltDown() && event.GetKeyCode() == 'H' )
        { for( int i = 0; i < 3; ++i ) if( wxWindow::FindFocus() == m_fields[i] ) { history( i ); return; } }
        if( event.GetKeyCode() == WXK_ESCAPE && !m_process )
        {
            if( m_diagramHistoryOpen ) { closeDiagramHistory(); return; }
            release(); m_connectFrom.reset(); setTool( TOOL::SELECT ); m_canvas->SetFocus(); return;
        }
        if( event.ControlDown() && event.GetKeyCode() == 'S' ) { save(); return; }
        if( event.ControlDown() && event.GetKeyCode() == 'W' ) { Close(); return; }
        if( event.ControlDown() && event.GetKeyCode() == 'Z' ) { undo( false ); return; }
        if( event.ControlDown() && event.GetKeyCode() == 'Y' ) { undo( true ); return; }
        if( wxWindow::FindFocus() == m_canvas && !event.ControlDown() && !event.AltDown() && canvasKey( event ) ) return;
        event.StopPropagation(); event.Skip();
    } );
    placePaletteAndEditor();
    refresh(); CallAfter( [this, splitter]
    { splitter->SetSashPosition( splitter->GetClientSize().x - inspectorWidth() ); load( m_request.expected_source_token() ); } );
}

RECURSIVE_DIAGRAM_FRAME::~RECURSIVE_DIAGRAM_FRAME()
{
    m_ioTimer.Stop();
    if( m_process ) { m_process->Detach(); wxProcess::Kill( m_pid, wxSIGTERM, wxKILL_CHILDREN ); }
}

const RECURSIVE_DIAGRAM_FRAME::REVISION* RECURSIVE_DIAGRAM_FRAME::revision( const SELECTION& selection ) const
{
    for( const auto& item : m_document.graph().revisions() ) if( same( item.selection(), selection ) ) return &item;
    return nullptr;
}
const D::RequirementRevisionData* RECURSIVE_DIAGRAM_FRAME::requirements( const REVISION& item ) const
{
    for( const auto& history : m_document.graph().requirement_histories() ) if( history.state_id() == item.selection().state_id() )
        for( const auto& value : history.revisions() ) if( value.id() == item.requirement_revision_id() ) return &value;
    return nullptr;
}
const RECURSIVE_DIAGRAM_FRAME::REVISION* RECURSIVE_DIAGRAM_FRAME::current() const
{
    if( m_historyPreview ) return revision( *m_historyPreview );
    return m_preview ? revision( *m_preview ) : m_path.empty() ? nullptr : revision( m_path.back() );
}
int RECURSIVE_DIAGRAM_FRAME::version( const REVISION& item ) const
{
    int result = 1; const REVISION* found = &item;
    while( found->has_parent_revision_id() && result <= m_document.graph().revisions_size() )
    {
        SELECTION parent = found->selection(); parent.set_revision_id( found->parent_revision_id() );
        found = revision( parent ); if( !found ) return result; ++result;
    }
    return result;
}
bool RECURSIVE_DIAGRAM_FRAME::findPath( const std::string& blockId, std::vector<SELECTION>& path ) const
{
    std::vector<std::vector<SELECTION>> pending{ { m_document.graph().selected_root() } };
    size_t count = 0;
    while( !pending.empty() && count++ <= static_cast<size_t>( m_document.graph().revisions_size() ) )
    {
        auto candidate = std::move( pending.back() ); pending.pop_back();
        auto* item = revision( candidate.back() ); if( !item ) continue;
        if( item->selection().block_id() == blockId ) { path = std::move( candidate ); return true; }
        for( const auto& child : item->children() ) { auto next = candidate; next.push_back( child ); pending.push_back( std::move( next ) ); }
    }
    return false;
}

// ---- The level draft ------------------------------------------------------------------------

const D::ConnectionRevisionData* RECURSIVE_DIAGRAM_FRAME::savedConnection( const std::string& id ) const
{
    const REVISION* scope = current(); if( !scope ) return nullptr;
    for( const auto& selected : scope->local_diagram().connections() ) if( selected.connection_id() == id )
        for( const auto& archive : m_document.graph().connection_archives() ) if( archive.owner_block_id() == scope->selection().block_id() )
            for( const auto& item : archive.revisions() ) if( item.selection().revision_id() == selected.revision_id()
                && item.selection().connection_id() == selected.connection_id() && item.selection().state_id() == selected.state_id() ) return &item;
    return nullptr;
}
RECURSIVE_DIAGRAM_FRAME::DRAFT RECURSIVE_DIAGRAM_FRAME::draftFor( const REVISION& item ) const
{
    DRAFT draft; auto* saved = requirements( item ); if( !saved ) return draft;
    *draft.mutable_baseline() = item.selection(); draft.set_name( item.name() );
    *draft.mutable_children() = item.children(); draft.set_baseline_requirement_revision_id( saved->id() );
    *draft.mutable_baseline_fields() = saved->fields(); *draft.mutable_fields() = saved->fields();
    if( item.has_local_diagram() ) *draft.mutable_local_diagram() = item.local_diagram();
    if( item.has_definition() ) *draft.mutable_definition() = item.definition();
    if( item.has_component_bindings() ) *draft.mutable_component_bindings() = item.component_bindings();
    if( item.has_physical_allocation() ) *draft.mutable_physical_allocation() = item.physical_allocation();
    return draft;
}
RECURSIVE_DIAGRAM_FRAME::LINK_DRAFT RECURSIVE_DIAGRAM_FRAME::connectionDraftFor( const D::ConnectionRevisionData& item ) const
{
    LINK_DRAFT draft; const REVISION* scope = current(); if( !scope ) return draft;
    for( const auto& archive : m_document.graph().connection_archives() ) if( archive.owner_block_id() == scope->selection().block_id() )
        for( const auto& history : archive.requirement_histories() ) if( history.state_id() == item.selection().state_id() )
            for( const auto& row : history.revisions() ) if( row.id() == item.requirement_revision_id() )
            {
                *draft.mutable_baseline() = item.selection(); draft.set_name( item.name() ); draft.set_kind( item.kind() );
                *draft.mutable_endpoints() = item.endpoints(); *draft.mutable_members() = item.members();
                draft.set_baseline_requirement_revision_id( row.id() ); *draft.mutable_baseline_fields() = row.fields();
                *draft.mutable_fields() = row.fields(); draft.set_domain( item.domain() ); draft.set_direction( item.direction() );
                if( item.has_realization() ) *draft.mutable_realization() = item.realization();
                return draft;
            }
    return draft;
}
void RECURSIVE_DIAGRAM_FRAME::resetLevel()
{
    m_level.Clear(); m_undo.clear(); m_redo.clear(); m_revealed.clear(); m_revealedDetails.clear(); m_lastEffects.Clear();
    m_captionProblem.clear(); m_signalProblem.clear();
    m_facet = -1; m_facetOwner.clear(); m_facetTouched = false; m_facetProblem.clear();
    if( const REVISION* scope = current() ) *m_level.mutable_scope() = draftFor( *scope );
    m_savedLevel = m_level;
}
bool RECURSIVE_DIAGRAM_FRAME::levelChanged() const
{
    if( m_level.scope().SerializeAsString() != m_savedLevel.scope().SerializeAsString()
        || m_level.new_children_size() || m_level.new_connections_size() ) return true;
    for( const auto& child : m_level.child_drafts() )
        if( const REVISION* saved = revision( child.baseline() ); !saved || child.SerializeAsString() != draftFor( *saved ).SerializeAsString() ) return true;
    for( const auto& link : m_level.connection_drafts() )
        if( const auto* saved = savedConnection( link.baseline().connection_id() );
            !saved || link.SerializeAsString() != connectionDraftFor( *saved ).SerializeAsString() ) return true;
    return false;
}
bool RECURSIVE_DIAGRAM_FRAME::hasChanges() const { return m_preview.has_value() || levelChanged(); }
void RECURSIVE_DIAGRAM_FRAME::pushUndo() { m_undo.push_back( m_level ); m_redo.clear(); }
void RECURSIVE_DIAGRAM_FRAME::changed()
{
    m_dirty = hasChanges(); ++m_viewRevision; m_rendered = false; refresh();
}
D::NewBlockOccurrenceData* RECURSIVE_DIAGRAM_FRAME::newChild( const std::string& id )
{
    for( auto& child : *m_level.mutable_new_children() ) if( child.selection().block_id() == id ) return &child;
    return nullptr;
}
const D::NewBlockOccurrenceData* RECURSIVE_DIAGRAM_FRAME::newChild( const std::string& id ) const
{
    for( const auto& child : m_level.new_children() ) if( child.selection().block_id() == id ) return &child;
    return nullptr;
}
D::NewConnectionData* RECURSIVE_DIAGRAM_FRAME::newConnection( const std::string& id )
{
    for( auto& link : *m_level.mutable_new_connections() ) if( link.selection().connection_id() == id ) return &link;
    return nullptr;
}
const D::NewConnectionData* RECURSIVE_DIAGRAM_FRAME::newConnection( const std::string& id ) const
{
    for( const auto& link : m_level.new_connections() ) if( link.selection().connection_id() == id ) return &link;
    return nullptr;
}
RECURSIVE_DIAGRAM_FRAME::DRAFT* RECURSIVE_DIAGRAM_FRAME::editBlock( bool create )
{
    if( m_selected.empty() || m_selected == m_level.scope().baseline().block_id() ) return m_level.mutable_scope();
    for( auto& child : *m_level.mutable_child_drafts() ) if( child.baseline().block_id() == m_selected ) return &child;
    if( !create ) return nullptr;
    for( const auto& child : m_level.scope().children() ) if( child.block_id() == m_selected )
        if( const REVISION* saved = revision( child ) ) { *m_level.add_child_drafts() = draftFor( *saved ); return m_level.mutable_child_drafts( m_level.child_drafts_size() - 1 ); }
    return nullptr;
}
const RECURSIVE_DIAGRAM_FRAME::DRAFT* RECURSIVE_DIAGRAM_FRAME::selectedBlockDraft() const
{
    if( m_selected.empty() || m_selected == m_level.scope().baseline().block_id() ) return &m_level.scope();
    for( const auto& child : m_level.child_drafts() ) if( child.baseline().block_id() == m_selected ) return &child;
    return nullptr;
}
const RECURSIVE_DIAGRAM_FRAME::LINK_DRAFT* RECURSIVE_DIAGRAM_FRAME::connectionDraft( const std::string& id ) const
{
    for( const auto& link : m_level.connection_drafts() ) if( link.baseline().connection_id() == id ) return &link;
    return nullptr;
}
RECURSIVE_DIAGRAM_FRAME::LINK_DRAFT* RECURSIVE_DIAGRAM_FRAME::editConnection( bool create )
{
    for( auto& link : *m_level.mutable_connection_drafts() ) if( link.baseline().connection_id() == m_connectionId ) return &link;
    if( !create ) return nullptr;
    if( const auto* saved = savedConnection( m_connectionId ) )
    { *m_level.add_connection_drafts() = connectionDraftFor( *saved ); return m_level.mutable_connection_drafts( m_level.connection_drafts_size() - 1 ); }
    return nullptr;
}
bool RECURSIVE_DIAGRAM_FRAME::selectionIsNew() const
{
    return !m_connectionId.empty() ? newConnection( m_connectionId ) != nullptr : newChild( m_selected ) != nullptr;
}
std::string RECURSIVE_DIAGRAM_FRAME::selectedName() const
{
    if( !m_connectionId.empty() )
    {
        if( const auto* added = newConnection( m_connectionId ) ) return added->name();
        if( const auto* draft = connectionDraft( m_connectionId ) ) return draft->name();
        if( const auto* saved = savedConnection( m_connectionId ) ) return saved->name();
        return {};
    }
    if( const auto* added = newChild( m_selected ) ) return added->name();
    if( const auto* draft = selectedBlockDraft() ) return draft->name();
    for( const auto& child : m_level.scope().children() ) if( child.block_id() == m_selected ) if( const REVISION* saved = revision( child ) ) return saved->name();
    return {};
}
D::RequirementFieldsData RECURSIVE_DIAGRAM_FRAME::selectedFields() const
{
    if( !m_connectionId.empty() )
    {
        if( const auto* added = newConnection( m_connectionId ) ) return added->fields();
        if( const auto* draft = connectionDraft( m_connectionId ) ) return draft->fields();
        return savedFields();
    }
    if( const auto* added = newChild( m_selected ) ) return added->fields();
    if( const auto* draft = selectedBlockDraft() ) return draft->fields();
    return savedFields();
}
D::RequirementFieldsData RECURSIVE_DIAGRAM_FRAME::savedFields() const
{
    if( !m_connectionId.empty() )
    {
        if( const auto* saved = savedConnection( m_connectionId ) ) return connectionDraftFor( *saved ).fields();
        return {};
    }
    if( m_selected.empty() || m_selected == m_level.scope().baseline().block_id() ) return m_savedLevel.scope().fields();
    for( const auto& child : m_level.scope().children() ) if( child.block_id() == m_selected )
        if( const REVISION* saved = revision( child ) ) if( const auto* text = requirements( *saved ) ) return text->fields();
    return {};
}

// ---- Loading and the companion ------------------------------------------------------------

void RECURSIVE_DIAGRAM_FRAME::load( const std::string& expected )
{
    REQUEST request; request.set_action( D::RFA_READ ); request.set_expected_source_token( expected ); execute( std::move( request ) );
}
void RECURSIVE_DIAGRAM_FRAME::execute( REQUEST request )
{
    if( m_process ) return;
    // Contract rbg-v2 section 2.3: the editor and its companion speak exactly schema 2.
    request.set_schema_version( 2 ); request.set_repository_root( m_request.repository_root() );
    request.set_source_path( m_request.source_path() ); request.set_document_id( m_request.document_id() );
    std::string json;
    if( !google::protobuf::util::MessageToJsonString( request, &json ).ok() ) return;
    m_activeRequest = std::move( request ); m_error.clear(); m_errorCode.clear(); m_stdout.clear(); m_stderr.clear();
    m_process = std::make_unique<wxProcess>( this ); m_process->Redirect();
    wxString helper = Text( m_request.helper_path() ), dotnet = "dotnet", mode = "--diagram-file";
    const wxChar* binary[] = { helper.c_str(), mode.c_str(), nullptr };
    const wxChar* managed[] = { dotnet.c_str(), helper.c_str(), mode.c_str(), nullptr };
    m_pid = wxExecute( helper.EndsWith( ".dll" ) ? managed : binary, wxEXEC_ASYNC | wxEXEC_MAKE_GROUP_LEADER, m_process.get() );
    if( m_pid <= 0 )
    {
        m_process.reset(); m_errorCode = "companion_start_failed"; m_error = "The compiled companion could not start.";
        if( m_historyDialog ) m_historyDialog->PageFailed( _( "Could not load older changes. Try again." ) );
        if( m_diagramHistoryOpen )
        { m_failedHistoryRequest = m_activeRequest; m_diagramHistoryPanel->Fail( _( "The diagram companion could not start. Retry or return to properties." ) ); }
        refresh(); return;
    }
    m_process->GetOutputStream()->Write( json.data(), json.size() ); m_process->CloseOutput(); m_ioTimer.Start( 50 ); refresh();
}
void RECURSIVE_DIAGRAM_FRAME::drain()
{
    if( !m_process ) return;
    auto read = []( wxInputStream* stream, std::string& out )
    { if( !stream ) return; char buffer[4096]; while( stream->CanRead() ) { stream->Read( buffer, sizeof( buffer ) ); size_t count = stream->LastRead(); if( !count ) break; out.append( buffer, count ); } };
    read( m_process->GetInputStream(), m_stdout ); read( m_process->GetErrorStream(), m_stderr );
}
void RECURSIVE_DIAGRAM_FRAME::completed( wxProcessEvent& event )
{
    if( !m_process || event.GetPid() != m_pid ) return;
    drain(); m_ioTimer.Stop(); m_process.reset();
    bool olderHistory = ( m_activeRequest.action() == D::RFA_BLOCK_FIELD_HISTORY
                          || m_activeRequest.action() == D::RFA_CONNECTION_FIELD_HISTORY ) && m_activeRequest.offset() > 0;
    bool diagramHistory = m_activeRequest.action() == D::RFA_DIAGRAM_HISTORY
        || m_activeRequest.action() == D::RFA_COMPARE_DIAGRAM_HISTORY || m_activeRequest.action() == D::RFA_PREPARE_DIAGRAM_RESTORATION;
    // Closing history cancels delivery of this read. A late response must not
    // reopen a modal, replace a draft, or report an error in another scope.
    if( olderHistory && !m_historyDialog ) { refresh(); return; }
    if( diagramHistory && !m_diagramHistoryOpen )
    { refresh(); if( m_diagramHistory->IsEnabled() ) m_diagramHistory->SetFocus(); return; }
    D::RecursiveFileResult result;
    bool parsed = google::protobuf::util::JsonStringToMessage( m_stdout, &result ).ok();
    if( m_activeRequest.action() == D::RFA_SAVE_LEVEL || m_activeRequest.action() == D::RFA_MANAGE_IMPLEMENTATION ) ++m_saveCount;
    if( event.GetExitCode() != 0 || !parsed || !result.success() )
    {
        m_errorCode = parsed ? result.error_code() : "invalid_companion_response";
        m_error = parsed && !result.error_message().empty() ? result.error_message() : "The operation failed; the current draft remains open.";
        // A companion of another version refuses this editor's schema 2 requests before reading anything.
        if( m_errorCode == "unsupported_diagram_file_request" && m_activeRequest.action() == D::RFA_READ )
        { m_errorCode = "companion_version_mismatch"; m_error = "The diagram companion of this installation speaks another diagram version; nothing was read or changed."; }
        if( diagramHistory )
        {
            m_failedHistoryRequest = m_activeRequest; m_diagramHistoryPanel->Fail( Text( m_error ) );
            ++m_viewRevision; refresh(); return;
        }
        if( olderHistory )
        {
            m_historyDialog->PageFailed( m_errorCode == "recursive_block_file_changed"
                ? _( "Saved design changed. Close history and reload." )
                : _( "Could not load older changes. Try again." ) );
            refresh(); return;
        }
        if( m_activeRequest.action() == D::RFA_SAVE_LEVEL && m_errorCode == "recursive_block_file_changed"
            && !m_activeRequest.save_level().select_implementation() && m_rebaseAttempts++ < 2 )
        {
            REQUEST compare; compare.set_action( D::RFA_REBASE_LEVEL );
            *compare.mutable_rebase_level()->mutable_draft() = m_sentLevel;
            execute( std::move( compare ) ); return;
        }
        if( m_activeRequest.action() == D::RFA_PREPARE_LEVEL_EDIT )
        {
            const auto& edit = m_activeRequest.level_edit();
            if( m_errorCode == "boundary_interface_in_use" && edit.kind() == D::LECK_REMOVE_INTERFACE && !edit.detach_connections()
                && result.error_details_size() && std::all_of( result.error_details().begin(), result.error_details().end(),
                       [&]( const auto& detail ) { return detail.kind() == "connection" && detail.scope_block_id() == m_level.scope().baseline().block_id(); } ) )
            {
                int uses = result.error_details_size();
                wxString question = uses == 1 ? wxString( _( "A connection on this level uses this port. Remove it together with the port?" ) )
                                              : wxString::Format( _( "%d connections on this level use this port. Remove them together with the port?" ), uses );
                wxMessageDialog choice( this, question, _( "Remove port" ), wxYES_NO | wxNO_DEFAULT | wxICON_QUESTION );
                choice.SetYesNoLabels( uses == 1 ? _( "&Remove port and connection" ) : _( "&Remove port and connections" ), _( "&Keep port" ) );
                m_errorCode.clear(); m_error.clear();
                if( choice.ShowModal() == wxID_YES ) { removeSelection( true ); return; }
            }
            refresh(); return;
        }
        m_closeAfterSave = false; m_pendingScope.clear(); m_pendingSelected.clear(); m_pendingConnection.reset(); m_pendingImplementation.clear();
        if( m_pendingHistoryRestore )
        { m_pendingHistoryRestore.reset(); m_diagramHistoryPanel->Fail( _( "The existing draft was not saved. Return to properties to resolve it." ) ); }
        refresh(); return;
    }
    if( diagramHistory )
    {
        if( result.source_token() != m_document.source_token() )
        { m_failedHistoryRequest = m_activeRequest; m_diagramHistoryPanel->Fail( _( "The saved design changed. Close history and reload." ) ); refresh(); return; }
        ++m_viewRevision;
        if( m_activeRequest.action() == D::RFA_DIAGRAM_HISTORY )
        {
            if( !result.has_diagram_history() || result.diagram_history().document_id() != DocumentId()
                || !m_diagramHistoryPanel->SetPage( result.diagram_history() ) )
            { m_failedHistoryRequest = m_activeRequest; m_diagramHistoryPanel->Fail( _( "The history page does not match this diagram." ) ); }
            refresh(); return;
        }
        if( m_activeRequest.action() == D::RFA_COMPARE_DIAGRAM_HISTORY )
        {
            if( !result.has_diagram_comparison() || result.diagram_comparison().document_id() != DocumentId()
                || !m_diagramHistoryPanel->SetComparison( result.diagram_comparison() ) )
            { m_failedHistoryRequest = m_activeRequest; m_diagramHistoryPanel->Fail( _( "The comparison does not match this diagram." ) ); }
            refresh(); return;
        }
        const auto& retained = m_activeRequest.restoration().draft();
        const auto* historical = revision( m_activeRequest.restoration().source() );
        if( !result.has_prepared_draft() || !historical || !same( result.prepared_draft().baseline(), retained.baseline() )
            || !result.prepared_draft().has_restored_from() || !same( result.prepared_draft().restored_from(), historical->selection() )
            || result.prepared_draft().baseline_requirement_revision_id() != retained.baseline_requirement_revision_id() )
        { m_failedHistoryRequest = m_activeRequest; m_diagramHistoryPanel->Fail( _( "The restored draft does not match the requested history." ) ); refresh(); return; }
        resetLevel(); m_undo.push_back( m_level ); *m_level.mutable_scope() = result.prepared_draft();
        m_selected = m_level.scope().baseline().block_id(); m_connectionId.clear(); m_portOwner.clear(); m_portId.clear();
        m_commentId.clear(); m_newComment = false; m_dirty = hasChanges(); closeDiagramHistory(); fit(); m_canvas->SetFocus(); return;
    }
    if( m_activeRequest.action() == D::RFA_PREPARE_LEVEL_EDIT ) { levelResult( result ); return; }
    if( m_activeRequest.action() == D::RFA_REBASE_LEVEL ) { rebaseResult( result ); return; }
    if( m_activeRequest.action() == D::RFA_BLOCK_FIELD_HISTORY || m_activeRequest.action() == D::RFA_CONNECTION_FIELD_HISTORY )
    {
        bool link = m_activeRequest.action() == D::RFA_CONNECTION_FIELD_HISTORY;
        const auto& target = link ? m_activeRequest.connection().connection_id() : m_activeRequest.block().block_id();
        const auto& stateId = link ? m_activeRequest.connection().state_id() : m_activeRequest.block().state_id();
        const auto& revisionId = link ? m_activeRequest.connection().revision_id() : m_activeRequest.block().revision_id();
        if( !result.has_history() || result.source_token() != m_document.source_token()
            || result.history().document_id() != DocumentId()
            || result.history().owner_id() != target || result.history().state_id() != stateId
            || result.history().context_revision_id() != revisionId
            || result.history().field() != m_activeRequest.field()
            || result.history().offset() != static_cast<unsigned>( m_activeRequest.offset() )
            || ( olderHistory && ( result.history().context_version() != m_historyContext.context_version()
                || result.history().requirement_revision_id() != m_historyContext.requirement_revision_id()
                || result.history().saved_text() != m_historyContext.saved_text() ) ) )
        {
            m_errorCode = "field_history_context_mismatch"; m_error = "The history response belongs to another saved context.";
            if( m_historyDialog ) m_historyDialog->PageFailed( _( "Older changes did not match this history. Try again." ) );
            refresh(); return;
        }
        std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> rows = DiagramFieldHistoryRows( m_document.graph(), result.history(), link );
        if( olderHistory )
        {
            if( !m_historyDialog->AppendPage( result.history().offset(), result.history().total(), std::move( rows ) ) )
            { m_errorCode = "field_history_page_mismatch"; m_error = "The older page does not continue this exact history."; }
            refresh(); return;
        }
        // Return from this frame's pending process event before entering a
        // modal loop. wx excludes a handler already processing pending events;
        // keeping it on the stack would hold every later page's completion.
        wxWeakRef<RECURSIVE_DIAGRAM_FRAME> frame( this );
        REQUEST query = m_activeRequest;
        // Use a dedicated dispatcher, not the application-wide pending queue.
        m_historyEvents.CallAfter( [frame, result, query]
        { if( frame && !frame->IsClosing() ) frame->showHistory( result, query ); } );
        return;
    }
    if( !result.has_document() || result.document().schema_version() != 2 || result.document().document_id() != DocumentId()
        || result.document().source_path() != SourcePath() || result.document().source_token().size() != 64 )
    {
        bool version = result.has_document() && result.document().schema_version() != 2;
        m_errorCode = version ? "companion_version_mismatch" : "diagram_target_mismatch";
        m_error = version ? "The diagram companion of this installation speaks another diagram version; nothing was changed."
                          : "The loaded document does not match the requested diagram.";
        refresh(); return;
    }
    if( m_activeRequest.action() == D::RFA_MANAGE_IMPLEMENTATION )
    {
        bool present = false;
        for( const auto& state : result.document().graph().states() )
            if( state.id() == result.implementation_id() && state.block_id() == m_activeRequest.implementation().source().block_id() ) present = true;
        if( !present ) { m_errorCode = "implementation_target_mismatch"; m_error = "The management result belongs to another implementation."; refresh(); return; }
        if( m_activeRequest.implementation().action() != D::IAK_ARCHIVE ) m_pendingImplementation = result.implementation_id();
    }
    std::string previousScope = current() ? current()->selection().block_id() : "";
    std::string scope = m_pendingScope.empty() ? current() ? current()->selection().block_id() : result.document().graph().selected_root().block_id() : m_pendingScope;
    std::string selected = m_pendingSelected.empty() ? m_selected : m_pendingSelected;
    std::string selectedConnection = m_pendingConnection.value_or( m_connectionId );
    std::string portOwner = m_portOwner, portId = m_portId;
    m_document = result.document(); m_preview.reset(); m_ready = true;
    if( !findPath( scope, m_path ) ) m_path = { m_document.graph().selected_root() };
    m_pendingScope.clear(); m_pendingSelected.clear(); m_pendingConnection.reset();
    resetLevel(); m_dirty = false;
    m_selected.clear(); m_connectionId.clear(); m_portOwner.clear(); m_portId.clear(); m_commentId.clear(); m_newComment = false;
    select( selected.empty() ? m_path.back().block_id() : selected );
    if( !selectedConnection.empty() ) selectConnection( selectedConnection );
    else if( !portId.empty() ) selectPort( portOwner, portId );
    if( previousScope.empty() || previousScope != m_path.back().block_id() ) fit();
    ++m_viewRevision; refresh();
    m_canvas->SetFocus();
    if( !m_pendingImplementation.empty() )
    { auto state = std::move( m_pendingImplementation ); m_pendingImplementation.clear(); previewImplementation( state ); }
    if( m_pendingHistoryRestore ) { prepareDiagramRestoration(); return; }
    if( m_closeAfterSave ) { m_closeAfterSave = false; Close(); }
}

void RECURSIVE_DIAGRAM_FRAME::levelResult( const D::RecursiveFileResult& result )
{
    const auto& sent = m_activeRequest.level_edit();
    if( !result.has_level_edit() || !result.level_edit().has_draft() || result.source_token() != m_document.source_token()
        || !same( result.level_edit().draft().scope().baseline(), sent.draft().scope().baseline() ) )
    { m_errorCode = "level_edit_mismatch"; m_error = "The removal result belongs to another diagram level; nothing was changed."; refresh(); return; }
    m_undo.push_back( m_removalBefore ); m_redo.clear();
    // A removed port changes where the remaining unresolved ends on its owner attach (rule F2).
    auto before = layout( current(), true );
    m_level = result.level_edit().draft(); m_lastEffects = result.level_edit().effects();
    followRoutes( before );
    // Show every effect of the removal in one line; Undo restores all of them.
    wxArrayString removed; int unresolved = 0, realizations = 0;
    for( const auto& effect : m_lastEffects )
    {
        if( effect.kind() == D::LEEK_CHILD_REMOVED || effect.kind() == D::LEEK_CONNECTION_REMOVED || effect.kind() == D::LEEK_INTERFACE_REMOVED )
            removed.Add( wxS( "“" ) + Text( effect.detail() ) + wxS( "”" ) );
        else if( effect.kind() == D::LEEK_ANNOTATION_UNRESOLVED ) ++unresolved;
        else if( effect.kind() == D::LEEK_REALIZATION_STATE_CHANGED || effect.kind() == D::LEEK_REALIZATION_REMOVED ) ++realizations;
    }
    wxString summary = removed.empty() ? _( "Nothing was removed." ) : wxString::Format( _( "Removed %s." ), wxJoin( removed, ',' ) );
    summary.Replace( ",", ", " );
    if( unresolved ) summary += wxString::Format( _( " %d comments no longer have a target." ), unresolved );
    if( realizations ) summary += wxString::Format( _( " %d realizations need review." ), realizations );
    m_notice = Utf8( summary );
    if( sent.kind() == D::LECK_REMOVE_CONNECTION_MEMBERS )
    {
        // Removing signals keeps their connection selected with its other details (Round A3).
        changed(); if( m_addDetail->IsShown() ) m_addDetail->SetFocus(); else m_canvas->SetFocus();
        return;
    }
    m_selected = m_level.scope().baseline().block_id(); m_connectionId.clear(); m_portOwner.clear(); m_portId.clear(); m_commentId.clear();
    changed(); m_canvas->SetFocus();
}

void RECURSIVE_DIAGRAM_FRAME::rebaseResult( const D::RecursiveFileResult& result )
{
    const auto& merge = result.level_merge();
    if( !result.has_level_merge() || result.source_token().size() != 64 || !merge.has_original_draft()
        || !result.has_document() || result.document().document_id() != DocumentId() || result.document().schema_version() != 2
        || result.document().source_path() != SourcePath() || result.document().source_token() != result.source_token()
        || merge.original_draft().SerializeAsString() != m_activeRequest.rebase_level().draft().SerializeAsString() )
    { m_errorCode = "level_comparison_mismatch"; m_error = "The comparison does not match the retained level draft."; refresh(); return; }
    if( merge.has_candidate() )
    {
        // Save the revalidated candidate against this exact new file token; the
        // normal disk guard rejects another change between compare and save.
        std::string scope = m_level.scope().baseline().block_id();
        // The retained draft as drawn: the merge keeps this draft's routes while another writer may have moved
        // their ends, so the candidate's channel routes follow the ends before it is saved.
        auto before = layout( current(), true );
        m_document = result.document();
        if( !findPath( scope, m_path ) ) m_path = { m_document.graph().selected_root() };
        LEVEL candidate = merge.candidate();
        resetLevel(); m_level = std::move( candidate ); m_dirty = true;
        followRoutes( before );
        if( int kept = merge.presentation_overrides_size(); kept == 1 )
            m_notice = Utf8( _( "Your layout kept a position that was also moved in the saved design." ) );
        else if( kept > 1 )
            m_notice = Utf8( wxString::Format( _( "Your layout kept %d positions that were also moved in the saved design." ), kept ) );
        m_rebasing = true; save(); return;
    }
    bool textOnly = merge.conflicts_size() > 0 && std::all_of( merge.conflicts().begin(), merge.conflicts().end(),
                                                               []( const auto& c ) { return c.kind() == D::LCK_REQUIREMENT_TEXT; } );
    if( !textOnly )
    {
        m_closeAfterSave = false; m_pendingScope.clear(); m_pendingSelected.clear();
        wxString list;
        for( const auto& conflict : merge.conflicts() ) list += wxS( "• " ) + Text( conflict.message() ) + wxS( "\n" );
        wxMessageDialog( this, _( "The saved design changed in ways that conflict with this draft. Your draft is still open.\n\n" ) + list,
                         _( "Resolve changes before saving" ), wxOK | wxICON_INFORMATION ).ShowModal();
        m_errorCode = "level_conflict"; m_error = "The saved design changed in ways that conflict with this draft. Your draft is still open.";
        refresh(); return;
    }
    // Text conflicts use the approved dialog, one owner at a time.
    const auto& sent = m_activeRequest.rebase_level().draft();
    D::RecursiveBlockGraphData latest = result.document().graph();
    auto latestRevision = [&]( const std::string& block ) -> const REVISION*
    {
        std::vector<std::vector<SELECTION>> pending{ { latest.selected_root() } };
        while( !pending.empty() )
        {
            auto path = std::move( pending.back() ); pending.pop_back();
            for( const auto& item : latest.revisions() ) if( same( item.selection(), path.back() ) )
            {
                if( item.selection().block_id() == block ) return &item;
                for( const auto& child : item.children() ) { auto next = path; next.push_back( child ); pending.push_back( std::move( next ) ); }
            }
        }
        return nullptr;
    };
    auto blockText = [&]( const REVISION& item ) -> const D::RequirementRevisionData*
    {
        for( const auto& history : latest.requirement_histories() ) if( history.state_id() == item.selection().state_id() )
            for( const auto& row : history.revisions() ) if( row.id() == item.requirement_revision_id() ) return &row;
        return nullptr;
    };
    std::vector<std::string> owners;
    for( const auto& conflict : merge.conflicts() )
        if( std::find( owners.begin(), owners.end(), conflict.owner_id() ) == owners.end() ) owners.push_back( conflict.owner_id() );
    std::vector<D::RequirementResolutionData> choices;
    LEVEL chosen = m_level;
    for( const auto& owner : owners )
    {
        D::RequirementMergeData view; wxString label; D::RequirementFieldsData savedText; std::string savedRevision;
        view.set_base_context_version( merge.base_context_version() ); view.set_saved_context_version( merge.saved_context_version() );
        if( merge.has_saved_origin() ) *view.mutable_saved_origin() = merge.saved_origin();
        auto* original = view.mutable_original_draft();
        const REVISION* scopeNow = latestRevision( sent.scope().baseline().block_id() );
        if( owner == sent.scope().baseline().block_id() )
        {
            *original = sent.scope(); label = Text( sent.scope().name() );
            if( scopeNow ) if( const auto* text = blockText( *scopeNow ) ) { savedText = text->fields(); savedRevision = text->id(); }
        }
        else if( const DRAFT* child = [&]() -> const DRAFT* { for( const auto& c : sent.child_drafts() ) if( c.baseline().block_id() == owner ) return &c; return nullptr; }() )
        {
            *original = *child; label = Text( child->name() );
            if( scopeNow ) for( const auto& pin : scopeNow->children() ) if( pin.block_id() == owner )
                for( const auto& item : latest.revisions() ) if( same( item.selection(), pin ) ) if( const auto* text = blockText( item ) )
                { savedText = text->fields(); savedRevision = text->id(); view.set_saved_context_version( version( item ) ); }
        }
        else
        {
            for( const auto& link : sent.connection_drafts() ) if( link.baseline().connection_id() == owner )
            {
                original->mutable_baseline()->set_block_id( owner ); original->mutable_baseline()->set_state_id( link.baseline().state_id() );
                original->mutable_baseline()->set_revision_id( link.baseline().revision_id() );
                original->set_baseline_requirement_revision_id( link.baseline_requirement_revision_id() );
                *original->mutable_baseline_fields() = link.baseline_fields(); *original->mutable_fields() = link.fields(); label = Text( link.name() );
                if( scopeNow ) for( const auto& pin : scopeNow->local_diagram().connections() ) if( pin.connection_id() == owner )
                    for( const auto& archive : latest.connection_archives() ) if( archive.owner_block_id() == scopeNow->selection().block_id() )
                        for( const auto& item : archive.revisions() ) if( item.selection().revision_id() == pin.revision_id() )
                            for( const auto& history : archive.requirement_histories() ) if( history.state_id() == item.selection().state_id() )
                                for( const auto& row : history.revisions() ) if( row.id() == item.requirement_revision_id() )
                                { savedText = row.fields(); savedRevision = row.id(); }
            }
        }
        view.mutable_saved_draft()->set_baseline_requirement_revision_id( savedRevision ); *view.mutable_saved_draft()->mutable_fields() = savedText;
        for( const auto& conflict : merge.conflicts() ) if( conflict.owner_id() == owner )
        {
            auto* row = view.add_conflicts(); row->set_field( conflict.field() ); row->set_baseline( conflict.baseline() );
            row->set_draft( conflict.draft() ); row->set_saved( conflict.saved() );
        }
        DIALOG_DIAGRAM_CONFLICT dialog( this, label, view );
        if( dialog.ShowModal() != wxID_OK )
        {
            m_closeAfterSave = false; m_pendingScope.clear(); m_pendingSelected.clear();
            m_errorCode = "requirement_conflict"; m_error = "The saved design changed. Your draft is still open."; refresh(); return;
        }
        for( auto choice : dialog.Resolutions() )
        {
            choice.set_document_id( DocumentId() );
            // Keep the user's chosen or composed text as draft work even if the
            // saved file advances again before the companion can revalidate it.
            int which = static_cast<int>( choice.field() ) - 1;
            if( which >= 0 && which < 3 )
            {
                D::RequirementFieldsData* fields = nullptr; google::protobuf::RepeatedPtrField<D::FieldRestorationData>* restores = nullptr;
                if( owner == chosen.scope().baseline().block_id() ) { fields = chosen.mutable_scope()->mutable_fields(); restores = chosen.mutable_scope()->mutable_restored_fields(); }
                for( auto& c : *chosen.mutable_child_drafts() ) if( c.baseline().block_id() == owner ) { fields = c.mutable_fields(); restores = c.mutable_restored_fields(); }
                for( auto& c : *chosen.mutable_connection_drafts() ) if( c.baseline().connection_id() == owner ) { fields = c.mutable_fields(); restores = c.mutable_restored_fields(); }
                if( fields ) { setField( fields, which, choice.text() ); dropRestoration( restores, which ); }
            }
            choices.push_back( std::move( choice ) );
        }
    }
    REQUEST resolve; resolve.set_action( D::RFA_REBASE_LEVEL ); resolve.set_expected_source_token( result.source_token() );
    *resolve.mutable_rebase_level()->mutable_draft() = sent;
    for( const auto& choice : choices ) *resolve.mutable_rebase_level()->add_resolutions() = choice;
    m_undo.push_back( m_level ); m_redo.clear(); m_level = std::move( chosen );
    m_dirty = true; ++m_viewRevision; execute( std::move( resolve ) );
}

// ---- Inspector ------------------------------------------------------------------------------

void RECURSIVE_DIAGRAM_FRAME::refresh()
{
    m_updating = true; bool available = m_ready && !m_process && !m_diagramHistoryOpen;
    bool link = !m_connectionId.empty();
    bool isNew = selectionIsNew();
    wxString path;
    for( const auto& step : m_path ) if( auto* item = revision( step ) ) { if( !path.empty() ) path += wxS( "  ›  " ); path += Text( item->name() ); }
    if( m_historyPreview ) if( auto* preview = revision( *m_historyPreview ) )
        path += wxS( " · " ) + wxString::Format( _( "Preview v%d — read only" ), version( *preview ) );
    m_breadcrumb->SetLabel( path.empty() ? _( "Loading diagram…" ) : path );
    m_breadcrumb->SetToolTip( path );
    updateImplementationLabel();
    m_implementation->Enable( available );
    m_diagramHistory->Enable( m_ready && !m_process && !m_diagramHistoryOpen );
    // A connection's title names what it is; its caption is the editable field below (Round A3).
    m_owner->SetLabel( !m_ready ? wxString() : link ? _( "Connection" ) : Text( selectedName() ) );
    m_owner->SetToolTip( m_ready ? Text( selectedName() ) : wxString() );
    // Grow with definition: a block or connection drawn in this draft shows only its caption.
    m_savedVersion->SetLabel( wxString() );
    const REVISION* selected = nullptr;
    if( m_ready && !link && !isNew )
    {
        if( m_selected == m_level.scope().baseline().block_id() ) selected = revision( m_level.scope().baseline() );
        else for( const auto& child : m_level.scope().children() ) if( child.block_id() == m_selected ) selected = revision( child );
    }
    if( selected ) m_savedVersion->SetLabel( wxString::Format( m_preview && selected == current() ? _( "Preview design: v%d" ) : _( "Selected design: v%d" ), version( *selected ) ) );
    if( selected && selected == current() && m_level.scope().has_restored_from() ) if( auto* source = revision( m_level.scope().restored_from() ) )
        m_savedVersion->SetLabel( wxString::Format( _( "Draft from v%d · Saved v%d" ), version( *source ), version( *selected ) ) );
    const D::ConnectionRevisionData* savedLink = link && !isNew ? savedConnection( m_connectionId ) : nullptr;
    if( savedLink && current() )
        for( const auto& archive : m_document.graph().connection_archives() ) if( archive.owner_block_id() == current()->selection().block_id() )
        {
            auto* item = savedLink; int number = 1;
            while( item && item->has_parent_revision_id() && number <= archive.revisions_size() )
            {
                const D::ConnectionRevisionData* parent = nullptr;
                for( const auto& row : archive.revisions() ) if( row.selection().revision_id() == item->parent_revision_id() ) { parent = &row; break; }
                item = parent; ++number;
            }
            m_savedVersion->SetLabel( wxString::Format( _( "Selected connection: v%d" ), number ) ); break;
        }
    m_savedVersion->Show( !m_savedVersion->GetLabel().empty() );
    bool child = !link && m_ready && current() && m_selected != current()->selection().block_id();
    m_openDiagram->Show( child && !isNew );
    fillConnection( available );
    auto current_ = selectedFields(), saved = savedFields();
    std::string key = link ? m_connectionId : m_selected;
    bool anyHidden = false;
    for( int i = 0; i < 3; ++i )
    {
        bool shown = m_ready && ( !field( current_, i ).empty() || !field( saved, i ).empty() || ( m_revealed.count( key ) && m_revealed.at( key )[i] ) );
        if( m_fieldHeadings[i]->AreAnyItemsShown() != shown || !m_ready ) m_fieldHeadings[i]->ShowItems( shown );
        anyHidden |= !shown;
        m_fields[i]->Enable( available ); m_fields[i]->ChangeValue( Text( field( current_, i ) ) );
        m_history[i]->Show( shown && !isNew ); m_history[i]->Enable( available );
    }
    fillFacets( available );
    bool detailsLeft = false, anyDetail = m_facetHeading->IsShown();
    if( m_ready && !link ) if( const auto* definition = selectedDefinition() )
        for( int facet = 0; facet < R::FACETS; ++facet ) detailsLeft |= facet != m_facet && !R::HasValue( R::Facet( *definition, facet ) );
    if( LINK_DETAILS details; m_ready && link && linkDetails( details ) )
        for( int which = 0; which < DETAILS; ++which )
        {
            bool shown = detailShown( details, static_cast<DETAIL>( which ) );
            anyDetail |= shown;
            detailsLeft |= !shown && !( which == static_cast<int>( DETAIL::SIGNALS ) && details.kind == D::DCK_SIGNAL );
        }
    m_addRequirement->Show( m_ready && anyHidden ); m_addRequirement->Enable( available );
    // A connection's details are edited in its level draft, never in a read-only history preview.
    m_addDetail->Show( m_ready && detailsLeft ); m_addDetail->Enable( available && !( link && m_historyPreview ) );
    m_addSeparator->Show( m_ready && ( detailsLeft || anyDetail ) );
    m_openDiagram->Enable( available && child && !isNew );
    bool writable = m_document.source_writable() || !m_ready;
    enableSave( available && m_dirty && writable, available && m_dirty );
    m_toolbar->EnableTool( BACK, available && !m_back.empty() ); m_toolbar->EnableTool( UP, available && m_path.size() > 1 );
    m_toolbar->EnableTool( wxID_UNDO, available && !m_undo.empty() );
    m_toolbar->EnableTool( wxID_REDO, available && !m_redo.empty() );
    m_toolbar->EnableTool( FIT, m_ready && !m_process );
    m_toolbar->EnableTool( NOTE, available && !m_historyPreview );
    bool drawing = drawingAvailable();
    for( auto& [tool, button] : m_strip ) { button->SetValue( tool == m_tool ); button->Enable( drawing ); }
    m_stripDelete->Enable( drawing && canDelete() );
    m_palette->SetState( m_tool, drawing, canDelete(), available && !m_undo.empty() );
    showStatus();
    fillComments(); m_owner->GetParent()->Layout(); m_owner->GetParent()->GetParent()->Layout();
    m_inspectorScroll->Layout(); m_inspectorScroll->FitInside();
    m_updating = false; m_rendered = false; m_canvas->Refresh();
}
void RECURSIVE_DIAGRAM_FRAME::enableSave( bool save, bool decline )
{
    // Save is the primary action while it is available: an accent fill with a label at 4.5:1 or more; unavailable, it keeps
    // the theme's own disabled look (design QA P2-8).
    m_decline->Enable( decline );
    const int style = save ? 1 : 0;
    if( m_save->IsEnabled() == save && m_saveStyle == style ) return;
    m_save->Enable( save ); m_saveStyle = style;
    if( save )
    {
        R::ACCENT_FILL fill = R::AccentFill( wxSystemSettings::GetColour( wxSYS_COLOUR_HIGHLIGHT ), m_save->GetParent()->GetBackgroundColour() );
        m_save->SetBackgroundColour( fill.fill ); m_save->SetForegroundColour( fill.text );
    }
    else { m_save->SetBackgroundColour( wxNullColour ); m_save->SetForegroundColour( wxNullColour ); }
    m_save->Refresh();
}
void RECURSIVE_DIAGRAM_FRAME::showStatus()
{
    // One status line for every path that changes the draft, so typing never hides why Save is unavailable.
    wxString status = !m_error.empty() ? Text( m_error ) : m_process ? _( "Working…" )
        : !m_notice.empty() ? Text( m_notice )
        : m_captionKind ? _( "Type a name and press Enter, or press Escape to cancel." )
        : m_tool == TOOL::CONNECT && m_connectFrom ? _( "Click a port to finish connection" )
        : m_tool == TOOL::CONNECT ? _( "Click a block or port to start a connection." )
        : m_tool == TOOL::ADD_BLOCK ? _( "Click the diagram where the new block goes." )
        : m_tool == TOOL::ADD_PORT ? _( "Click a block edge or the level boundary to place a port." )
        : m_tool == TOOL::NOTE ? _( "Click the diagram to place a comment." )
        : m_ready && !m_document.source_writable() ? _( "This diagram file is read-only; changes cannot be saved." )
        : m_dirty ? _( "Unsaved changes" ) : wxString();
    SetStatusText( status );
}
bool RECURSIVE_DIAGRAM_FRAME::confirmChange()
{
    if( m_process ) return false;
    if( !m_dirty ) return true;
    wxMessageDialog choice( this, _( "Save changes before leaving this draft?" ), _( "Unsaved changes" ), wxYES_NO | wxCANCEL | wxICON_QUESTION );
    choice.SetYesNoLabels( _( "Save" ), _( "Decline" ) );
    int answer = choice.ShowModal();
    if( answer == wxID_YES ) save();
    else if( answer == wxID_NO ) load();
    else { m_pendingScope.clear(); m_pendingSelected.clear(); m_pendingConnection.reset(); m_pendingImplementation.clear(); }
    return false;
}
void RECURSIVE_DIAGRAM_FRAME::select( const std::string& requested )
{
    // Selecting another element of the same level never prompts: they share one draft (section 9.1).
    if( !m_ready || m_process || !current() ) return;
    std::string id = requested;
    bool present = id == m_level.scope().baseline().block_id();
    for( const auto& child : m_level.scope().children() ) present |= child.block_id() == id;
    if( !present ) id = m_level.scope().baseline().block_id();
    if( m_selected == id && m_connectionId.empty() && m_portId.empty() ) return;
    // Another element's properties start at the top of the inspector, with its caption and component choices.
    if( m_selected != id || !m_connectionId.empty() ) m_inspectorScroll->Scroll( 0, 0 );
    m_selected = id; m_connectionId.clear(); m_portOwner.clear(); m_portId.clear(); m_commentId.clear(); m_newComment = false;
    m_captionProblem.clear(); m_signalProblem.clear(); m_signalEntry->ChangeValue( wxEmptyString );
    ++m_viewRevision; refresh();
}
void RECURSIVE_DIAGRAM_FRAME::selectConnection( const std::string& id )
{
    if( !m_ready || m_process || !current() || m_connectionId == id ) return;
    bool present = false;
    for( const auto& root : m_level.scope().local_diagram().connections() ) present |= root.connection_id() == id;
    if( !present || ( !newConnection( id ) && !savedConnection( id ) ) ) return;
    m_inspectorScroll->Scroll( 0, 0 );
    m_selected = m_level.scope().baseline().block_id(); m_connectionId = id; m_portOwner.clear(); m_portId.clear();
    m_commentId.clear(); m_newComment = false;
    if( m_notice == Utf8( m_captionProblem ) ) m_notice.clear();
    m_captionProblem.clear(); m_signalProblem.clear(); m_signalEntry->ChangeValue( wxEmptyString );
    ++m_viewRevision; refresh();
}
void RECURSIVE_DIAGRAM_FRAME::selectPort( const std::string& owner, const std::string& id )
{
    if( !m_ready || m_process || !current() ) return;
    select( owner );
    m_portOwner = owner; m_portId = id; ++m_viewRevision; refresh();
}
void RECURSIVE_DIAGRAM_FRAME::navigate( std::string id, bool remember )
{
    // Callers commonly pass m_selected or a selection inside m_path. This
    // operation replaces both, so the destination must be owned, not borrowed.
    if( !m_ready || m_process || !current() || m_diagramHistoryOpen || current()->selection().block_id() == id || newChild( id ) ) return;
    finishCaption( false );
    if( m_preview )
    { m_pendingScope = id; m_pendingSelected = id; m_pendingConnection = ""; confirmChange(); return; }
    std::vector<SELECTION> path; if( !findPath( id, path ) ) return;
    m_pendingScope = id; m_pendingSelected = id;
    m_pendingConnection = "";
    if( !confirmChange() ) return;
    if( remember ) m_back.push_back( current()->selection().block_id() );
    m_views[current()->selection().block_id()] = { m_scale, m_origin, m_selected };
    m_path = std::move( path ); m_selected.clear(); m_connectionId.clear(); m_portOwner.clear(); m_portId.clear();
    m_pendingScope.clear(); m_pendingSelected.clear(); m_pendingConnection.reset(); m_connectFrom.reset();
    resetLevel(); m_dirty = false;
    select( id );
    if( auto saved = m_views.find( id ); saved != m_views.end() ) { m_scale = saved->second.scale; m_origin = saved->second.origin; m_fitted = false; select( saved->second.selected ); }
    else fit();
    ++m_viewRevision; refresh();
    CallAfter( [this] { if( !m_closing ) m_canvas->SetFocus(); } );
}
void RECURSIVE_DIAGRAM_FRAME::chooseImplementation()
{
    if( !m_ready || m_process || !current() || m_diagramHistoryOpen ) return;
    wxMenu menu; const int reserved = m_document.graph().states_size() * 2 + 8;
    int firstId = wxWindow::NewControlId( reserved ); int index = 0;
    for( const auto& state : m_document.graph().states() ) if( state.block_id() == current()->selection().block_id() && !state.archived() )
    {
        int id = firstId + index++; auto* item = menu.AppendCheckItem( id, wxString::Format( _( "Preview %s" ), Text( state.name() ) ) );
        item->Check( current()->selection().state_id() == state.id() );
        menu.Bind( wxEVT_MENU, [this, stateId = state.id()]( wxCommandEvent& ) { previewImplementation( stateId ); }, id );
    }
    bool clean = !levelChanged();
    menu.AppendSeparator();
    auto action = [&]( wxMenu& target, const wxString& label, D::ImplementationActionKind kind, const std::string& stateId, bool enabled )
    {
        int id = firstId + index++; auto* item = target.Append( id, label ); item->Enable( enabled );
        target.Bind( wxEVT_MENU, [this, kind, stateId]( wxCommandEvent& ) { manageImplementation( kind, stateId ); }, id );
    };
    std::string selectedState = current()->selection().state_id();
    action( menu, _( "&New implementation…" ), D::IAK_NEW, selectedState, clean );
    action( menu, _( "&Duplicate implementation…" ), D::IAK_DUPLICATE, selectedState, clean );
    action( menu, _( "&Rename implementation…" ), D::IAK_RENAME, selectedState, clean );
    action( menu, _( "Remo&ve implementation…" ), D::IAK_ARCHIVE, selectedState, clean && selectedState != m_path.back().state_id() );
    auto* removed = new wxMenu();
    for( const auto& state : m_document.graph().states() ) if( state.block_id() == current()->selection().block_id() && state.archived() )
        action( *removed, Text( state.name() ), D::IAK_RESTORE, state.id(), clean );
    if( removed->GetMenuItemCount() ) menu.AppendSubMenu( removed, _( "Restore re&moved implementation" ) ); else delete removed;
    wxPoint position = ScreenToClient( m_implementation->ClientToScreen( wxPoint( 0, m_implementation->GetSize().y ) ) );
    PopupMenu( &menu, position );
    wxWindow::UnreserveControlId( firstId, reserved );
}
void RECURSIVE_DIAGRAM_FRAME::manageImplementation( D::ImplementationActionKind action, std::string stateId )
{
    if( !m_ready || m_process || !current() || levelChanged() ) return;
    const D::BlockDesignStateData* state = nullptr;
    for( const auto& item : m_document.graph().states() ) if( item.id() == stateId && item.block_id() == current()->selection().block_id() ) state = &item;
    if( !state ) return;
    wxString name;
    if( action == D::IAK_NEW || action == D::IAK_DUPLICATE || action == D::IAK_RENAME )
    {
        wxString title = action == D::IAK_NEW ? _( "New implementation" ) : action == D::IAK_DUPLICATE ? _( "Duplicate implementation" ) : _( "Rename implementation" );
        wxString initial = action == D::IAK_NEW ? _( "New implementation" ) : action == D::IAK_DUPLICATE ? Text( state->name() ) + _( " copy" ) : Text( state->name() );
        if( !m_errorCode.empty() && m_activeRequest.action() == D::RFA_MANAGE_IMPLEMENTATION
            && m_activeRequest.implementation().action() == action && m_activeRequest.implementation().source().state_id() == stateId )
            initial = Text( m_activeRequest.implementation().name() );
        wxTextEntryDialog dialog( this, _( "Implementation name:" ), title, initial );
        dialog.SetName( "DiagramImplementationName" );
        while( true )
        {
            if( dialog.ShowModal() != wxID_OK ) return;
            name = dialog.GetValue().Strip( wxString::both );
            if( name.empty() ) { wxMessageBox( _( "Enter an implementation name." ), _( "Invalid implementation name" ), wxOK | wxICON_ERROR, this ); continue; }
            bool duplicate = false;
            for( const auto& other : m_document.graph().states() )
                if( other.block_id() == state->block_id() && ( action != D::IAK_RENAME || other.id() != stateId ) && name.CmpNoCase( Text( other.name() ) ) == 0 ) duplicate = true;
            if( duplicate ) { wxMessageBox( wxString::Format( _( "An implementation named \"%s\" already exists for this block. Choose another name." ), name ),
                    _( "Invalid implementation name" ), wxOK | wxICON_ERROR, this ); continue; }
            break;
        }
    }
    else if( action == D::IAK_ARCHIVE )
    {
        wxMessageDialog dialog( this, wxString::Format( _( "Remove \"%s\" from implementation choices? Its saved history will be kept." ), Text( state->name() ) ),
                _( "Remove implementation" ), wxYES_NO | wxNO_DEFAULT | wxICON_QUESTION );
        dialog.SetYesNoLabels( _( "&Remove" ), _( "&Cancel" ) ); if( dialog.ShowModal() != wxID_YES ) return;
    }
    REQUEST request; request.set_action( D::RFA_MANAGE_IMPLEMENTATION ); request.set_expected_source_token( m_document.source_token() );
    auto* operation = request.mutable_implementation(); operation->set_action( action ); *operation->mutable_expected_root() = m_document.graph().selected_root();
    operation->mutable_source()->set_block_id( state->block_id() ); operation->mutable_source()->set_state_id( stateId );
    operation->mutable_source()->set_revision_id( state->head_revision_id() );
    if( action == D::IAK_NEW || action == D::IAK_DUPLICATE )
    { operation->set_new_state_id( FreshId() ); operation->set_new_revision_id( FreshId() ); operation->set_new_requirement_revision_id( FreshId() ); }
    else operation->set_change_id( FreshId() );
    operation->set_name( Utf8( name ) ); EditorOrigin( operation->mutable_origin(), "Manage implementation" );
    m_pendingScope = current()->selection().block_id(); m_pendingSelected = m_pendingScope; m_pendingConnection = "";
    execute( std::move( request ) );
}
void RECURSIVE_DIAGRAM_FRAME::reloadSaved()
{
    if( m_process ) return;
    finishCaption( false );
    if( m_diagramHistoryOpen ) closeDiagramHistory();
    if( !m_ready ) { load(); return; }
    m_pendingScope = current()->selection().block_id(); m_pendingSelected = m_selected; m_pendingConnection = m_connectionId;
    if( !confirmChange() ) return;
    load();
}
void RECURSIVE_DIAGRAM_FRAME::updateImplementationLabel()
{
    if( !m_implementation ) return;
    wxString implementation;
    if( current() ) for( const auto& state : m_document.graph().states() ) if( state.id() == current()->selection().state_id() )
    { implementation = Text( state.name() ) + wxString::Format( " · v%d", version( *current() ) ); break; }
    wxString full = m_preview ? _( "Preview: " ) + implementation : implementation.empty() ? _( "Implementation" ) : implementation;
    wxClientDC dc( m_implementation ); dc.SetFont( m_implementation->GetFont() );
    int maximum = std::max( FromDIP( 150 ), std::min( FromDIP( 450 ), m_implementation->GetParent()->GetClientSize().x / 2 ) );
    wxString label = wxControl::Ellipsize( full, dc, wxELLIPSIZE_MIDDLE, maximum - FromDIP( 24 ) );
    wxSize minimum( std::min( maximum, dc.GetTextExtent( label ).x + FromDIP( 24 ) ), -1 );
    m_implementation->SetLabel( label ); m_implementation->SetToolTip( full );
    if( m_implementation->GetMinSize() != minimum )
    { m_implementation->SetMinSize( minimum ); m_implementation->GetParent()->Layout(); }
}
void RECURSIVE_DIAGRAM_FRAME::previewImplementation( const std::string& stateId )
{
    if( !m_ready || m_process || !current() || current()->selection().state_id() == stateId ) return;
    const D::BlockDesignStateData* alternative = nullptr;
    for( const auto& state : m_document.graph().states() ) if( state.id() == stateId && state.block_id() == current()->selection().block_id() && !state.archived() ) alternative = &state;
    if( !alternative ) return;
    if( levelChanged() )
    { m_pendingImplementation = stateId; m_pendingScope = current()->selection().block_id(); m_pendingSelected = m_pendingScope; m_pendingConnection = ""; confirmChange(); return; }
    SELECTION selection; selection.set_block_id( alternative->block_id() ); selection.set_state_id( stateId ); selection.set_revision_id( alternative->head_revision_id() );
    auto* target = revision( selection ); if( !target ) return;
    if( same( selection, m_path.back() ) ) m_preview.reset(); else m_preview = selection;
    m_connectionId.clear(); m_portOwner.clear(); m_portId.clear(); resetLevel(); m_selected = selection.block_id();
    m_dirty = hasChanges(); ++m_viewRevision; refresh(); fit(); m_canvas->SetFocus();
}
void RECURSIVE_DIAGRAM_FRAME::revealField( int which )
{
    if( which < 0 || which > 2 || !m_ready ) return;
    std::string key = m_connectionId.empty() ? m_selected : m_connectionId;
    m_revealed[key][which] = true;
    refresh(); m_fields[which]->SetFocus();
}
void RECURSIVE_DIAGRAM_FRAME::chooseRequirement()
{
    if( !m_ready || m_process || m_diagramHistoryOpen ) return;
    wxMenu menu; const int first = wxWindow::NewControlId( 3 );
    for( int i = 0; i < 3; ++i ) if( !m_fields[i]->IsShown() )
    {
        menu.Append( first + i, FIELD_LABELS[i] );
        menu.Bind( wxEVT_MENU, [this, i]( wxCommandEvent& ) { revealField( i ); }, first + i );
    }
    wxPoint position = ScreenToClient( m_addRequirement->ClientToScreen( wxPoint( 0, m_addRequirement->GetSize().y ) ) );
    PopupMenu( &menu, position );
    wxWindow::UnreserveControlId( first, 3 );
}
void RECURSIVE_DIAGRAM_FRAME::chooseDetail()
{
    if( !m_ready || m_process || m_diagramHistoryOpen ) return;
    if( !m_connectionId.empty() )
    {
        if( m_historyPreview ) return;
        // A connection offers only the details it does not have yet, in a fixed order (Round A3).
        LINK_DETAILS details; if( !linkDetails( details ) ) return;
        wxMenu menu; const int first = wxWindow::NewControlId( DETAILS );
        for( int which = 0; which < DETAILS; ++which )
        {
            auto detail = static_cast<DETAIL>( which );
            if( detailShown( details, detail ) || ( detail == DETAIL::SIGNALS && details.kind == D::DCK_SIGNAL ) ) continue;
            menu.Append( first + which, detailLabel( which ) );
            menu.Bind( wxEVT_MENU, [this, detail]( wxCommandEvent& ) { revealDetail( detail ); }, first + which );
        }
        wxPoint position = ScreenToClient( m_addDetail->ClientToScreen( wxPoint( 0, m_addDetail->GetSize().y ) ) );
        PopupMenu( &menu, position );
        wxWindow::UnreserveControlId( first, DETAILS );
        return;
    }
    const auto* definition = selectedDefinition();
    if( !definition ) return;
    // The block's facets that have no value yet; choosing one opens its detail to give it a first value.
    wxMenu menu; const int first = wxWindow::NewControlId( R::FACETS );
    for( int facet = 0; facet < R::FACETS; ++facet )
    {
        if( facet == m_facet || R::HasValue( R::Facet( *definition, facet ) ) ) continue;
        menu.Append( first + facet, R::FacetLabel( facet ) );
        menu.Bind( wxEVT_MENU, [this, facet]( wxCommandEvent& ) { openFacet( facet, false ); }, first + facet );
    }
    wxPoint position = ScreenToClient( m_addDetail->ClientToScreen( wxPoint( 0, m_addDetail->GetSize().y ) ) );
    PopupMenu( &menu, position );
    wxWindow::UnreserveControlId( first, R::FACETS );
}
// ---- Connection details (Round A3) ----------------------------------------------------------
// Owner decisions nf53af9d74841b7d3 and n98a3f3c41084f0ed: a connection is first only its caption. Add detail offers the
// details the format-2 model stores that it does not have yet: its signals (members), direction, domain and type. Each one
// becomes its own row, and removing the row returns the connection to how it was without it. Details an agent wrote into
// the file show the same way once the editor reads the file. Every edit changes the level draft only.

bool RECURSIVE_DIAGRAM_FRAME::linkDetails( LINK_DETAILS& out ) const
{
    out = {};
    if( !m_ready || m_connectionId.empty() || !current() ) return false;
    const std::string& id = m_connectionId;
    std::vector<const D::NewConnectionData*> drawn;
    for( const auto& added : m_level.new_connections() ) if( added.has_member_of() && added.member_of() == id ) drawn.push_back( &added );
    if( const auto* added = newConnection( id ); added && !added->has_member_of() )
    {
        out.kind = added->kind(); out.domain = added->domain(); out.direction = added->direction();
        out.endpoints.assign( added->endpoints().begin(), added->endpoints().end() );
        for( const auto* signal : drawn ) out.signals.push_back( { signal->selection().connection_id(), signal->name(), true, signal->kind() } );
        return true;
    }
    const LINK_DRAFT* draft = connectionDraft( id );
    const D::ConnectionRevisionData* saved = savedConnection( id );
    if( !draft && !saved ) return false;
    out.kind = draft ? draft->kind() : saved->kind(); out.domain = draft ? draft->domain() : saved->domain();
    out.direction = draft ? draft->direction() : saved->direction();
    const auto& endpoints = draft ? draft->endpoints() : saved->endpoints();
    out.endpoints.assign( endpoints.begin(), endpoints.end() );
    const D::ConnectionArchiveData* archive = nullptr;
    for( const auto& item : m_document.graph().connection_archives() ) if( item.owner_block_id() == current()->selection().block_id() ) archive = &item;
    for( const auto& member : draft ? draft->members() : saved->members() )
    {
        auto added = std::find_if( drawn.begin(), drawn.end(), [&]( const auto* signal ) { return sameConnection( signal->selection(), member ); } );
        if( added != drawn.end() ) { out.signals.push_back( { member.connection_id(), ( *added )->name(), true, ( *added )->kind() } ); continue; }
        const D::ConnectionRevisionData* item = nullptr;
        if( archive ) for( const auto& row : archive->revisions() ) if( sameConnection( row.selection(), member ) ) item = &row;
        out.signals.push_back( { member.connection_id(), item ? item->name() : member.connection_id(), false, item ? item->kind() : D::DCK_UNKNOWN } );
    }
    return true;
}
bool RECURSIVE_DIAGRAM_FRAME::HasDetail( const LINK_DETAILS& details, DETAIL detail )
{
    switch( detail )
    {
    case DETAIL::SIGNALS: return !details.signals.empty();
    case DETAIL::DIRECTION: return details.direction != D::DCDR_UNSPECIFIED;
    case DETAIL::DOMAIN: return details.domain != D::DD_UNSPECIFIED;
    default: return details.kind != D::DCK_ABSTRACT && details.kind != D::DCK_UNKNOWN;
    }
}
bool RECURSIVE_DIAGRAM_FRAME::detailShown( const LINK_DETAILS& details, DETAIL detail ) const
{
    if( HasDetail( details, detail ) ) return true;
    // A single signal has no signals of its own, so an empty Signals row does not apply to it (whether it became a signal
    // here, through undo or redo, or in the file).
    if( detail == DETAIL::SIGNALS && details.kind == D::DCK_SIGNAL ) return false;
    auto revealed = m_revealedDetails.find( m_connectionId );
    return revealed != m_revealedDetails.end() && revealed->second[static_cast<int>( detail )];
}
bool RECURSIVE_DIAGRAM_FRAME::EndpointDefined( const D::DiagramEndpointBindingData& endpoint )
{
    return endpoint.kind() == D::DEK_PIN || endpoint.kind() == D::DEK_CANDIDATES || endpoint.kind() == D::DEK_COMPATIBLE
           || !endpoint.intent().empty();
}
D::DiagramEndpointBindingData RECURSIVE_DIAGRAM_FRAME::PlainEndpoint( const D::DiagramEndpointBindingData& endpoint )
{
    D::DiagramEndpointBindingData drawn; drawn.set_block_id( endpoint.block_id() );
    if( endpoint.has_interface_id() ) { drawn.set_kind( D::DEK_INTERFACE ); drawn.set_interface_id( endpoint.interface_id() ); }
    else drawn.set_kind( D::DEK_UNRESOLVED );
    return drawn;
}
wxString RECURSIVE_DIAGRAM_FRAME::endpointName( const D::DiagramEndpointBindingData& endpoint ) const
{
    // An end on the level's own boundary is named by its port; an end on a block by the block, and its port when it has one.
    const std::string& scope = m_level.scope().baseline().block_id();
    auto portName = [&]( const google::protobuf::RepeatedPtrField<D::DiagramBoundaryInterfaceData>& ports ) -> wxString
    {
        if( !endpoint.has_interface_id() ) return wxString();
        for( const auto& port : ports ) if( port.id() == endpoint.interface_id() ) return Text( port.name() );
        return wxString();
    };
    if( endpoint.block_id() == scope )
    {
        wxString port = portName( m_level.scope().local_diagram().interfaces() );
        return port.empty() ? Text( m_level.scope().name() ) : port;
    }
    wxString block, port;
    if( const auto* added = newChild( endpoint.block_id() ) ) { block = Text( added->name() ); port = portName( added->interfaces() ); }
    else
    {
        for( const auto& child : m_level.child_drafts() ) if( child.baseline().block_id() == endpoint.block_id() )
        { block = Text( child.name() ); port = portName( child.local_diagram().interfaces() ); }
        if( block.empty() ) for( const auto& child : m_level.scope().children() ) if( child.block_id() == endpoint.block_id() )
            if( const REVISION* saved = revision( child ) ) { block = Text( saved->name() ); port = portName( saved->local_diagram().interfaces() ); }
    }
    if( block.empty() ) return _( "Unavailable end" );
    return port.empty() ? block : block + wxS( " \u00b7 " ) + port;
}
void RECURSIVE_DIAGRAM_FRAME::setLinkValue( DETAIL detail, int value )
{
    if( !m_ready || m_process || m_diagramHistoryOpen || m_historyPreview || m_connectionId.empty() ) return;
    LINK_DETAILS details; if( !linkDetails( details ) ) return;
    int now = detail == DETAIL::DIRECTION ? static_cast<int>( details.direction ) : detail == DETAIL::DOMAIN ? static_cast<int>( details.domain )
                                                                                                          : static_cast<int>( details.kind );
    // A choice that is already made stays made; the remove button takes a detail away.
    if( now == value || detail == DETAIL::SIGNALS ) { refresh(); return; }
    // The model's own rules: a single signal has no signals of its own, and a pair has exactly two signals.
    if( detail == DETAIL::TYPE && ( ( value == D::DCK_SIGNAL && !details.signals.empty() ) || ( value == D::DCK_DIFFERENTIAL_PAIR
            && ( details.signals.size() != 2 || std::any_of( details.signals.begin(), details.signals.end(), []( const SIGNAL& s ) { return s.kind != D::DCK_SIGNAL; } ) ) ) ) )
    { refresh(); return; }
    pushUndo();
    if( detail == DETAIL::TYPE && value == D::DCK_SIGNAL )
    {
        // The empty Signals row the person added goes with anything typed into it: a single signal has no signals.
        m_revealedDetails[m_connectionId][static_cast<int>( DETAIL::SIGNALS )] = false;
        bool wasUpdating = m_updating; m_updating = true; m_signalEntry->ChangeValue( wxEmptyString ); m_updating = wasUpdating;
        m_signalProblem.clear();
    }
    auto apply = [&]( auto* link )
    {
        if( detail == DETAIL::DIRECTION ) link->set_direction( static_cast<D::DiagramConnectionDirection>( value ) );
        else if( detail == DETAIL::DOMAIN ) link->set_domain( static_cast<D::DiagramDomain>( value ) );
        else link->set_kind( static_cast<D::DiagramConnectionKind>( value ) );
    };
    if( auto* added = newConnection( m_connectionId ) ) apply( added ); else if( auto* draft = editConnection( true ) ) apply( draft );
    m_notice.clear(); m_lastEffects.Clear(); changed();
}
void RECURSIVE_DIAGRAM_FRAME::revealDetail( DETAIL detail )
{
    if( !m_ready || m_process || m_diagramHistoryOpen || m_historyPreview || m_connectionId.empty() ) return;
    auto& shown = m_revealedDetails[m_connectionId];
    shown[static_cast<int>( detail )] = true;
    ++m_viewRevision; refresh();
    // Focus moves into the new row once the menu that added it has closed.
    CallAfter( [this, detail]
               {
                   if( m_closing ) return;
                   if( detail == DETAIL::SIGNALS ) { if( m_signalEntry->IsShown() ) m_signalEntry->SetFocus(); }
                   else if( detail == DETAIL::DIRECTION ) m_directionChoices[0]->SetFocus();
                   else if( detail == DETAIL::DOMAIN ) m_domainChoices[0]->SetFocus();
                   else m_typeChoices[0]->SetFocus();
               } );
}
void RECURSIVE_DIAGRAM_FRAME::removeDetail( DETAIL detail )
{
    if( !m_ready || m_process || m_diagramHistoryOpen || m_historyPreview || m_connectionId.empty() ) return;
    LINK_DETAILS details; if( !linkDetails( details ) ) return;
    m_revealedDetails[m_connectionId][static_cast<int>( detail )] = false;
    if( detail == DETAIL::SIGNALS ) { m_signalEntry->ChangeValue( wxEmptyString ); m_signalProblem.clear(); }
    if( !HasDetail( details, detail ) ) { ++m_viewRevision; refresh(); }
    else if( detail == DETAIL::SIGNALS )
    {
        std::vector<std::string> ids;
        for( const auto& signal : details.signals ) ids.push_back( signal.id );
        removeSignals( ids );
    }
    else setLinkValue( detail, detail == DETAIL::TYPE ? static_cast<int>( D::DCK_ABSTRACT ) : 0 );
    if( !m_process ) { if( m_addDetail->IsShown() ) m_addDetail->SetFocus(); else m_connectionCaption->SetFocus(); }
}
void RECURSIVE_DIAGRAM_FRAME::addSignal()
{
    if( !m_ready || m_process || m_diagramHistoryOpen || m_historyPreview || m_connectionId.empty() ) return;
    LINK_DETAILS details; if( !linkDetails( details ) ) return;
    wxString name = m_signalEntry->GetValue().Strip( wxString::both );
    if( name.empty() || details.kind == D::DCK_SIGNAL || details.kind == D::DCK_DIFFERENTIAL_PAIR ) return;
    for( const auto& signal : details.signals ) if( Text( signal.name ) == name )
    {
        m_signalProblem = wxString::Format( _( "%s is already a signal of this connection." ), wxS( "\u201c" ) + name + wxS( "\u201d" ) );
        ++m_viewRevision; refresh(); m_signalEntry->SetFocus(); return;
    }
    // A new signal runs between the blocks and ports its connection is drawn on and starts as its name only: a pin, candidates,
    // a selector or intent stated for the connection's ends were never stated for this signal.
    pushUndo();
    D::NewConnectionData signal;
    signal.mutable_selection()->set_connection_id( FreshId() ); signal.mutable_selection()->set_state_id( FreshId() );
    signal.mutable_selection()->set_revision_id( FreshId() ); signal.set_requirement_revision_id( FreshId() );
    signal.set_implementation_name( "Initial" ); signal.set_name( Utf8( name ) ); signal.set_kind( D::DCK_SIGNAL ); signal.mutable_fields();
    for( const auto& endpoint : details.endpoints ) *signal.add_endpoints() = PlainEndpoint( endpoint );
    signal.set_member_of( m_connectionId );
    if( !newConnection( m_connectionId ) ) if( auto* draft = editConnection( true ) ) *draft->add_members() = signal.selection();
    *m_level.add_new_connections() = std::move( signal );
    m_signalProblem.clear(); m_notice.clear(); m_lastEffects.Clear();
    bool wasUpdating = m_updating; m_updating = true; m_signalEntry->ChangeValue( wxEmptyString ); m_updating = wasUpdating;
    changed(); m_signalEntry->SetFocus();
}
void RECURSIVE_DIAGRAM_FRAME::removeSignals( const std::vector<std::string>& ids )
{
    if( !m_ready || m_process || m_diagramHistoryOpen || m_historyPreview || m_connectionId.empty() || ids.empty() ) return;
    LINK_DETAILS details; if( !linkDetails( details ) ) return;
    // A pair keeps exactly two signals; its type changes first.
    if( details.kind == D::DCK_DIFFERENTIAL_PAIR ) return;
    auto listed = [&]( const std::string& id ) { return std::find( ids.begin(), ids.end(), id ) != ids.end(); };
    bool saved = std::any_of( details.signals.begin(), details.signals.end(), [&]( const SIGNAL& s ) { return listed( s.id ) && !s.drawn; } );
    if( !saved )
    {
        // Signals drawn in this draft simply go.
        pushUndo();
        auto* added = m_level.mutable_new_connections();
        for( int n = added->size() - 1; n >= 0; --n )
            if( added->Get( n ).has_member_of() && added->Get( n ).member_of() == m_connectionId && listed( added->Get( n ).selection().connection_id() ) )
                added->DeleteSubrange( n, 1 );
        if( auto* draft = editConnection( false ) )
            for( int n = draft->members_size() - 1; n >= 0; --n ) if( listed( draft->members( n ).connection_id() ) ) draft->mutable_members()->DeleteSubrange( n, 1 );
        // The exact inverse of adding them: a connection draft that adding a signal opened, and that is now again the saved
        // connection, goes with them.
        if( const auto* savedLink = savedConnection( m_connectionId ) )
        {
            const std::string savedDraft = connectionDraftFor( *savedLink ).SerializeAsString();
            auto* drafts = m_level.mutable_connection_drafts();
            for( int n = drafts->size() - 1; n >= 0; --n )
                if( drafts->Get( n ).baseline().connection_id() == m_connectionId && drafts->Get( n ).SerializeAsString() == savedDraft )
                    drafts->DeleteSubrange( n, 1 );
        }
        m_notice.clear(); m_lastEffects.Clear(); changed(); return;
    }
    // A saved signal leaves through the companion's one removal cascade (contract rbg-v2 section 4.7): notes and
    // realizations on it are shown as effects, and Undo restores everything.
    std::vector<SELECTION> path; if( !findPath( m_level.scope().baseline().block_id(), path ) ) return;
    m_removalBefore = m_level;
    REQUEST request; request.set_action( D::RFA_PREPARE_LEVEL_EDIT ); request.set_expected_source_token( m_document.source_token() );
    auto* edit = request.mutable_level_edit(); *edit->mutable_expected_root() = m_document.graph().selected_root();
    for( const auto& step : path ) *edit->add_block_path() = step;
    *edit->mutable_draft() = m_level; edit->set_kind( D::LECK_REMOVE_CONNECTION_MEMBERS ); edit->set_connection_id( m_connectionId );
    for( const auto& id : ids ) edit->add_member_ids( id );
    EditorOrigin( edit->mutable_origin(), "Remove signals" );
    execute( std::move( request ) );
}
void RECURSIVE_DIAGRAM_FRAME::removeEndpointDetails()
{
    if( !m_ready || m_process || m_diagramHistoryOpen || m_historyPreview || m_connectionId.empty() ) return;
    LINK_DETAILS details; if( !linkDetails( details ) ) return;
    if( std::none_of( details.endpoints.begin(), details.endpoints.end(), EndpointDefined ) ) return;
    // Each end keeps the block or port it is drawn on; what was stated beyond that goes, on the connection and on the signals
    // drawn for it in this draft. A saved signal's own ends change only through its member path.
    auto plain = []( google::protobuf::RepeatedPtrField<D::DiagramEndpointBindingData>* endpoints )
    {
        for( auto& endpoint : *endpoints ) if( EndpointDefined( endpoint ) ) endpoint = PlainEndpoint( endpoint );
    };
    pushUndo();
    if( auto* added = newConnection( m_connectionId ) ) plain( added->mutable_endpoints() );
    else if( auto* draft = editConnection( true ) ) plain( draft->mutable_endpoints() );
    for( auto& signal : *m_level.mutable_new_connections() )
        if( signal.has_member_of() && signal.member_of() == m_connectionId ) plain( signal.mutable_endpoints() );
    m_notice.clear(); m_lastEffects.Clear(); changed();
    if( m_addDetail->IsShown() ) m_addDetail->SetFocus(); else m_connectionCaption->SetFocus();
}
void RECURSIVE_DIAGRAM_FRAME::captionEdited()
{
    if( !m_ready || m_process || m_diagramHistoryOpen || m_historyPreview || m_connectionId.empty() ) return;
    wxString value = m_connectionCaption->GetValue().Strip( wxString::both );
    auto notice = [this]( const wxString& problem )
    {
        m_captionProblem = problem; m_captionNotice->SetLabel( problem ); m_captionNotice->Show( !problem.empty() );
        m_captionNotice->Wrap( std::max( FromDIP( 200 ), m_inspectorScroll->GetClientSize().x - FromDIP( 24 ) ) );
        m_inspectorScroll->Layout(); m_inspectorScroll->FitInside(); ++m_viewRevision;
    };
    // A connection is at least its caption: a blank one is not stored, and Save explains why.
    if( value.empty() ) { notice( _( "Type a caption for this connection." ) ); return; }
    if( !m_captionProblem.empty() ) { if( m_notice == Utf8( m_captionProblem ) ) m_notice.clear(); notice( wxString() ); }
    std::string caption = Utf8( value );
    if( caption == selectedName() ) return;
    LEVEL before = m_level;
    if( auto* added = newConnection( m_connectionId ) ) added->set_name( caption );
    else if( auto* draft = editConnection( true ) ) draft->set_name( caption );
    m_undo.push_back( std::move( before ) ); m_redo.clear(); m_dirty = hasChanges(); ++m_viewRevision;
    // Like requirement text, the field is not refilled while typing; the canvas shows the new caption.
    enableSave( m_dirty && m_document.source_writable(), m_dirty );
    m_toolbar->EnableTool( wxID_UNDO, true ); m_toolbar->EnableTool( wxID_REDO, false );
    m_palette->SetState( m_tool, drawingAvailable(), canDelete(), true );
    showStatus();
    m_rendered = false; m_canvas->Refresh();
}
void RECURSIVE_DIAGRAM_FRAME::fillConnection( bool available )
{
    LINK_DETAILS details; bool link = linkDetails( details );
    bool editable = available && !m_historyPreview;
    bool wasUpdating = m_updating; m_updating = true;
    m_captionLabel->Show( link ); m_connectionCaption->Show( link );
    if( link && m_captionProblem.empty() && m_connectionCaption->GetValue() != Text( selectedName() ) ) m_connectionCaption->ChangeValue( Text( selectedName() ) );
    m_connectionCaption->Enable( editable );
    bool dark = [&] { wxColour window = wxSystemSettings::GetColour( wxSYS_COLOUR_WINDOW ); return window.Red() + window.Green() + window.Blue() < 384; }();
    wxColour problem = dark ? wxColour( 255, 145, 135 ) : wxColour( 176, 0, 32 );
    int wrap = std::max( FromDIP( 200 ), m_inspectorScroll->GetClientSize().x - FromDIP( 24 ) );
    m_captionNotice->SetForegroundColour( problem ); m_captionNotice->SetLabel( m_captionProblem );
    m_captionNotice->Show( link && !m_captionProblem.empty() ); m_captionNotice->Wrap( wrap );
    for( int which = 0; which < DETAILS; ++which )
    {
        bool shown = link && detailShown( details, static_cast<DETAIL>( which ) );
        if( m_detailRows[which]->AreAnyItemsShown() != shown ) m_detailRows[which]->ShowItems( shown );
        m_detailRemove[which]->Enable( editable );
    }
    // Signals: one line per signal with its own remove button, then the entry for the next one.
    bool pair = details.kind == D::DCK_DIFFERENTIAL_PAIR;
    wxString pairTip = _( "A differential pair keeps exactly two signals; choose another type first." );
    bool signalsShown = link && detailShown( details, DETAIL::SIGNALS );
    auto* scroll = m_inspectorScroll;
    while( m_signalLines.size() < details.signals.size() )
    {
        size_t index = m_signalLines.size();
        auto* row = new wxBoxSizer( wxHORIZONTAL );
        auto* name = new wxStaticText( scroll, wxID_ANY, wxEmptyString, wxDefaultPosition, wxDefaultSize, wxST_ELLIPSIZE_END );
        name->SetName( wxString::Format( "RecursiveSignal%zu", index ) );
        auto* remove = new wxButton( scroll, wxID_ANY, wxS( "\u00d7" ), wxDefaultPosition, wxDefaultSize, wxBU_EXACTFIT | wxBORDER_NONE );
        remove->SetName( wxString::Format( "RecursiveSignalRemove%zu", index ) );
        remove->Bind( wxEVT_BUTTON, [this, index]( wxCommandEvent& )
                      {
                          LINK_DETAILS now;
                          if( linkDetails( now ) && index < now.signals.size() ) removeSignals( { now.signals[index].id } );
                      } );
        row->Add( name, 1, wxALIGN_CENTER_VERTICAL | wxLEFT, FromDIP( 10 ) ); row->Add( remove, 0, wxALIGN_CENTER_VERTICAL );
        m_signalList->Add( row, 0, wxEXPAND );
        // The lines keep the row's tab order: its remove button, each signal's remove button, then the entry.
        remove->MoveAfterInTabOrder( index ? m_signalLines.back().remove : m_detailRemove[static_cast<int>( DETAIL::SIGNALS )] );
        m_signalEntry->MoveAfterInTabOrder( remove );
        m_signalLines.push_back( { name, remove, row } );
    }
    for( size_t i = 0; i < m_signalLines.size(); ++i )
    {
        bool shown = signalsShown && i < details.signals.size();
        m_signalLines[i].name->Show( shown ); m_signalLines[i].remove->Show( shown );
        if( !shown ) continue;
        wxString value = Text( details.signals[i].name );
        if( m_signalLines[i].name->GetLabelText() != value ) m_signalLines[i].name->SetLabelText( value );
        m_signalLines[i].name->SetToolTip( value );
        // Each signal's own "×" is named for the signal it removes, on hover and for assistive technology (design QA round 2,
        // R2-P2-5), apart from the row's "Remove signals".
        const wxString removeName = wxString::Format( _( "Remove signal %s" ), value );
        m_signalLines[i].remove->SetToolTip( pair ? pairTip : removeName );
        R::SetAccessibleRole( m_signalLines[i].remove, nullptr, removeName );
        m_signalLines[i].remove->Enable( editable && !pair );
    }
    m_signalEntry->Show( signalsShown ); m_signalEntry->Enable( editable && !pair );
    m_signalEntry->SetToolTip( pair ? pairTip : _( "Type a signal name and press Enter." ) );
    m_signalNotice->SetForegroundColour( problem ); m_signalNotice->SetLabel( m_signalProblem );
    m_signalNotice->Show( signalsShown && !m_signalProblem.empty() ); m_signalNotice->Wrap( wrap );
    m_detailRemove[static_cast<int>( DETAIL::SIGNALS )]->Enable( editable && !( pair && !details.signals.empty() ) );
    // The link says what it removes; only a reason why it is unavailable needs a tooltip (design QA P3 9).
    if( pair && !details.signals.empty() ) m_detailRemove[static_cast<int>( DETAIL::SIGNALS )]->SetToolTip( pairTip );
    else m_detailRemove[static_cast<int>( DETAIL::SIGNALS )]->UnsetToolTip();
    // Direction is stated between the connection's own ends: from its first end to the others, the reverse, or both ways.
    if( details.endpoints.size() >= 2 )
    {
        wxString first = endpointName( details.endpoints[0] );
        wxString others = details.endpoints.size() == 2 ? endpointName( details.endpoints[1] ) : _( "the others" );
        wxString labels[] = { first + wxS( " \u2192 " ) + others, others + wxS( " \u2192 " ) + first, _( "Both ways" ) };
        // No tooltip that only repeats the label and covers the next choice (design QA round 2, P3 19).
        for( int i = 0; i < 3; ++i ) if( m_directionChoices[i]->GetLabel() != labels[i] ) m_directionChoices[i]->SetLabel( labels[i] );
    }
    for( int i = 0; i < 3; ++i ) { m_directionChoices[i]->SetValue( details.direction == DIRECTIONS[i] ); m_directionChoices[i]->Enable( editable ); }
    for( int i = 0; i < 5; ++i ) { m_domainChoices[i]->SetValue( details.domain == DOMAINS[i] ); m_domainChoices[i]->Enable( editable ); }
    bool signalMembers = std::all_of( details.signals.begin(), details.signals.end(), []( const SIGNAL& s ) { return s.kind == D::DCK_SIGNAL; } );
    for( int i = 0; i < 4; ++i )
    {
        bool allowed = KINDS[i] == D::DCK_SIGNAL ? details.signals.empty()
                     : KINDS[i] == D::DCK_DIFFERENTIAL_PAIR ? details.signals.size() == 2 && signalMembers : true;
        m_typeChoices[i]->SetValue( details.kind == KINDS[i] );
        m_typeChoices[i]->Enable( editable && ( allowed || details.kind == KINDS[i] ) );
        // An unavailable type says why (design QA round 2, P3 20); an available one needs no tooltip that repeats its label.
        wxString reason = KINDS[i] == D::DCK_SIGNAL && !allowed ? _( "A single signal has no signals of its own; remove its signals first." )
                        : KINDS[i] == D::DCK_DIFFERENTIAL_PAIR && !allowed ? _( "A differential pair has exactly two signals." ) : wxString();
        if( reason.empty() ) m_typeChoices[i]->UnsetToolTip(); else m_typeChoices[i]->SetToolTip( reason );
    }
    // What an end says beyond its block or port, one line per end that says more.
    wxString endpoints;
    for( const auto& endpoint : details.endpoints ) if( EndpointDefined( endpoint ) )
    {
        wxString line = endpointName( endpoint ) + wxS( ": " );
        switch( endpoint.kind() )
        {
        case D::DEK_PIN: line += wxString::Format( _( "pin %s" ), Text( endpoint.pin().pin() ) ); break;
        case D::DEK_CANDIDATES: line += wxString::Format( _( "%d candidate pins" ), endpoint.candidates_size() ); break;
        case D::DEK_COMPATIBLE:
        {
            wxString parts;
            auto part = [&]( const std::string& value ) { if( value.empty() ) return; if( !parts.empty() ) parts += wxS( ", " ); parts += Text( value ); };
            part( endpoint.selector().role() ); part( endpoint.selector().protocol() );
            for( const auto& function : endpoint.selector().required_functions() ) part( function );
            line += parts.empty() ? _( "a compatible pin" ) : wxString::Format( _( "a pin for %s" ), parts );
            break;
        }
        default: line = endpointName( endpoint ); break;
        }
        if( !endpoint.intent().empty() ) line += wxS( " \u2014 " ) + Text( endpoint.intent() );
        if( !endpoints.empty() ) endpoints += wxS( "\n" );
        endpoints += line;
    }
    m_endpointHeading->Show( link && !endpoints.empty() ); m_endpoints->Show( link && !endpoints.empty() );
    m_endpointRemove->Show( link && !endpoints.empty() ); m_endpointRemove->Enable( editable );
    // Text a person or agent wrote is shown as written, never as mnemonics.
    if( m_endpoints->GetLabelText() != endpoints ) m_endpoints->SetLabelText( endpoints );
    m_endpoints->Wrap( wrap );
    m_updating = wasUpdating;
}

// ---- Component choices (Round A4) -----------------------------------------------------------
// Owner decisions n0b2a908b00e78823 and n98a3f3c41084f0ed: a block shows a chip for each chosen or candidate
// facet; the inspector lists only the facets that have a value; one facet's detail edits its state, value,
// reason and strength; a facet can return to unknown or be cleared. A chosen name never creates pins, a
// footprint or a native component (decision na7aa99408263431e): only the definition in the draft changes.

const D::BlockDefinitionData* RECURSIVE_DIAGRAM_FRAME::selectedDefinition() const
{
    if( !m_ready || !m_connectionId.empty() || !current() ) return nullptr;
    if( const auto* added = newChild( m_selected ) ) return &added->definition();
    if( const auto* draft = selectedBlockDraft() ) return &draft->definition();
    for( const auto& child : m_level.scope().children() ) if( child.block_id() == m_selected )
        if( const REVISION* saved = revision( child ) ) return &saved->definition();
    return nullptr;
}
const D::BlockDefinitionData* RECURSIVE_DIAGRAM_FRAME::savedDefinition() const
{
    if( !m_ready || !m_connectionId.empty() || !current() || newChild( m_selected ) ) return nullptr;
    if( const auto* draft = selectedBlockDraft() )
        return revision( draft->baseline() ) ? &revision( draft->baseline() )->definition() : nullptr;
    for( const auto& child : m_level.scope().children() ) if( child.block_id() == m_selected )
        if( const REVISION* saved = revision( child ) ) return &saved->definition();
    return nullptr;
}
void RECURSIVE_DIAGRAM_FRAME::storeFacet( int facet, const D::DefinitionTextChoiceData* choice )
{
    auto store = []( auto* owner, int which, const D::DefinitionTextChoiceData* value )
    {
        if( value ) { *R::MutableFacet( owner->mutable_definition(), which ) = *value; return; }
        if( !owner->has_definition() ) return;
        R::ClearFacet( owner->mutable_definition(), which );
        // A definition that records nothing is no definition, as the saved block had none.
        if( R::EmptyDefinition( owner->definition() ) ) owner->clear_definition();
    };
    if( auto* added = newChild( m_selected ) ) store( added, facet, choice );
    else if( DRAFT* draft = editBlock( true ) ) store( draft, facet, choice );
}
bool RECURSIVE_DIAGRAM_FRAME::facetFromForm( D::DefinitionTextChoiceData& choice, wxString& problem ) const
{
    int state = facetState(), strength = facetStrength();
    choice.Clear();
    choice.set_strength( static_cast<kiapi::automation::structure::v1::StructuralGuidanceStrength>( strength ) );
    if( state == 0 )
    {
        wxString value = m_facetValue->GetValue().Strip( wxString::both );
        if( value.empty() ) { problem = _( "Type the chosen value, or choose another state." ); return false; }
        choice.set_state( D::DCSD_SELECTED ); choice.add_values( R::Utf8( value ) );
    }
    else if( state == 1 )
    {
        choice.set_state( D::DCSD_CANDIDATES );
        wxArrayString lines = wxSplit( m_facetCandidates->GetValue(), '\n', '\0' );
        for( auto line : lines )
        {
            line = line.Strip( wxString::both ); std::string value = R::Utf8( line );
            if( !value.empty() && std::find( choice.values().begin(), choice.values().end(), value ) == choice.values().end() ) choice.add_values( value );
        }
        if( choice.values().empty() ) { problem = _( "List the candidates, one per line." ); return false; }
    }
    else
    {
        wxString reason = m_facetReason->GetValue().Strip( wxString::both );
        if( reason.empty() ) { problem = _( "Say why this is unknown." ); return false; }
        choice.set_state( D::DCSD_UNKNOWN ); choice.set_unknown_reason( R::Utf8( reason ) );
    }
    // The sources, the condition it applies under and the verification recorded with a facet describe that exact
    // choice. A changed state, value or unknown reason is a new statement: it keeps none of them (the inspector does
    // not show them, so the person could not see that they no longer fit). A changed strength alone keeps them.
    // The entry is compared with the draft's facet and then with the saved revision's, so an edit that is typed back
    // to the saved statement (a keystroke and Backspace) gets the saved sources, condition and verification again.
    auto sameStatement = [&]( const D::DefinitionTextChoiceData* base )
    {
        return base && base->state() == choice.state() && base->values().size() == choice.values().size()
               && std::equal( base->values().begin(), base->values().end(), choice.values().begin() )
               && base->unknown_reason() == choice.unknown_reason();
    };
    const auto* drafted = selectedDefinition(); const auto* saved = savedDefinition();
    const D::DefinitionTextChoiceData* base = nullptr;
    if( m_facet >= 0 && drafted && sameStatement( R::Facet( *drafted, m_facet ) ) ) base = R::Facet( *drafted, m_facet );
    else if( m_facet >= 0 && saved && sameStatement( R::Facet( *saved, m_facet ) ) ) base = R::Facet( *saved, m_facet );
    if( base )
    {
        // Everything but the strength comes from the matching statement, so a facet typed back to its saved value
        // and strength is byte-for-byte the saved facet and leaves nothing to save.
        choice = *base;
        choice.set_strength( static_cast<kiapi::automation::structure::v1::StructuralGuidanceStrength>( strength ) );
    }
    else choice.set_verification( kiapi::automation::structure::v1::SV_UNVERIFIED );
    problem.clear(); return true;
}
void RECURSIVE_DIAGRAM_FRAME::fillFacetForm()
{
    // The detail shows the draft's facet, or a first chosen value for a facet that has none yet.
    const auto* definition = selectedDefinition();
    const auto* choice = definition && m_facet >= 0 ? R::Facet( *definition, m_facet ) : nullptr;
    if( !R::HasValue( choice ) ) return;
    bool wasUpdating = m_updating; m_updating = true;
    int state = choice->state() == D::DCSD_SELECTED ? 0 : choice->state() == D::DCSD_CANDIDATES ? 1 : 2;
    setFacetState( state ); setFacetStrength( static_cast<int>( choice->strength() ) );
    auto set = []( wxTextCtrl* control, const wxString& value ) { if( control->GetValue() != value ) control->ChangeValue( value ); };
    if( state == 0 ) set( m_facetValue, R::ChoiceValue( *choice, wxS( "\n" ) ) );
    if( state == 1 ) set( m_facetCandidates, R::ChoiceValue( *choice, wxS( "\n" ) ) );
    if( state == 2 ) set( m_facetReason, R::Text( choice->unknown_reason() ) );
    m_updating = wasUpdating;
}
void RECURSIVE_DIAGRAM_FRAME::showFacetFields()
{
    bool open = m_facet >= 0; int state = facetState();
    m_facetValue->Show( open && state == 0 ); m_facetCandidates->Show( open && state == 1 ); m_facetReason->Show( open && state == 2 );
    m_facetValueLabel->SetLabel( state == 1 ? _( "Candidates" ) : state == 2 ? _( "Reason" ) : _( "Value" ) );
}
void RECURSIVE_DIAGRAM_FRAME::fillFacets( bool available )
{
    const auto* definition = selectedDefinition();
    // The detail belongs to one block: selecting anything else closes it and drops an entry that could not be kept.
    if( m_facet >= 0 && ( !definition || m_selected != m_facetOwner ) )
    { m_facet = -1; m_facetOwner.clear(); m_facetTouched = false; m_facetProblem.clear(); }
    bool any = false;
    for( int facet = 0; facet < R::FACETS; ++facet )
    {
        const auto* choice = definition ? R::Facet( *definition, facet ) : nullptr;
        bool shown = R::HasValue( choice );
        if( shown ) m_facetRows[facet]->SetChoice( *choice, facet == m_facet );
        if( m_facetRows[facet]->IsShown() != shown ) m_facetRows[facet]->Show( shown );
        m_facetRows[facet]->Enable( available ); any |= shown;
    }
    m_facetHeading->Show( any );
    bool open = m_facet >= 0;
    m_facetDetail->ShowItems( open );
    m_facetGap->Show( any && !open );
    // "Back to facet overview" only once there is an overview to go back to (design QA P3 21); Escape always returns.
    m_facetBack->Show( open && any );
    // While a facet's detail is open, the requirement boxes and Comments are two lines high, as sketch A4 draws them beside a
    // facet's detail, so at the default window size the whole detail and Comments, with its 12 DIP margin below it, fit the
    // inspector's view (design QA P2-13 and round 2, P3 10: Comments was cut off at the view's lower edge). Each box keeps its
    // text 6 DIP from its top and bottom (P2-9).
    const int twoLines = 2 * m_comments->GetCharHeight() + 2 * FromDIP( 6 ) + FromDIP( 4 );
    m_comments->SetMinSize( wxSize( -1, open ? twoLines : FromDIP( 110 ) ) );
    for( wxTextCtrl* field : m_fields ) field->SetMinSize( wxSize( field->GetMinSize().x, open ? twoLines : FromDIP( 90 ) ) );
    if( open )
    {
        if( !m_facetTouched ) fillFacetForm();
        m_facetTitle->SetLabel( R::FacetLabel( m_facet ) );
        const auto* choice = R::Facet( *definition, m_facet );
        m_facetClear->Show( R::HasValue( choice ) );
        m_facetNotice->SetLabel( m_facetProblem ); m_facetNotice->Show( !m_facetProblem.empty() );
        bool dark = [&] { wxColour window = wxSystemSettings::GetColour( wxSYS_COLOUR_WINDOW ); return window.Red() + window.Green() + window.Blue() < 384; }();
        m_facetNotice->SetForegroundColour( dark ? wxColour( 255, 145, 135 ) : wxColour( 176, 0, 32 ) );
        m_facetNotice->Wrap( std::max( FromDIP( 200 ), m_inspectorScroll->GetClientSize().x - FromDIP( 24 ) ) );
    }
    showFacetFields(); fitFacetLabels();
    for( wxWindow* control : facetControls() ) control->Enable( available );
}
void RECURSIVE_DIAGRAM_FRAME::openFacet( int facet, bool focusState )
{
    if( !m_ready || m_process || m_diagramHistoryOpen || !m_connectionId.empty() || facet < 0 || facet >= R::FACETS || !selectedDefinition() ) return;
    finishCaption( false );
    m_facet = facet; m_facetOwner = m_selected; m_facetTouched = false; m_facetProblem.clear();
    const auto* choice = R::Facet( *selectedDefinition(), facet );
    bool wasUpdating = m_updating; m_updating = true;
    // A facet without a value opens ready for its first chosen value.
    m_facetValue->ChangeValue( wxEmptyString ); m_facetCandidates->ChangeValue( wxEmptyString ); m_facetReason->ChangeValue( wxEmptyString );
    setFacetState( 0 ); setFacetStrength( 0 );
    m_updating = wasUpdating;
    ++m_viewRevision; refresh();
    // Focus moves once the click or menu that opened the detail has finished with it.
    bool state = focusState && R::HasValue( choice );
    CallAfter( [this, facet, state]
               {
                   if( m_closing || m_facet != facet ) return;
                   if( state ) m_facetStates[facetState()]->SetFocus();
                   else if( m_facetValue->IsShown() ) m_facetValue->SetFocus();
                   else if( m_facetCandidates->IsShown() ) m_facetCandidates->SetFocus();
                   else m_facetReason->SetFocus();
               } );
}
void RECURSIVE_DIAGRAM_FRAME::closeFacet( bool focusRow )
{
    if( m_facet < 0 ) return;
    int facet = m_facet;
    if( !m_facetProblem.empty() && m_notice == Utf8( m_facetProblem ) ) m_notice.clear();
    m_facet = -1; m_facetOwner.clear(); m_facetTouched = false; m_facetProblem.clear();
    ++m_viewRevision; refresh();
    if( !focusRow ) return;
    if( m_facetRows[facet]->IsShown() ) m_facetRows[facet]->SetFocus();
    else if( m_addDetail->IsShown() ) m_addDetail->SetFocus();
    else m_canvas->SetFocus();
}
bool RECURSIVE_DIAGRAM_FRAME::facetHasFocus() const
{
    wxWindow* focus = wxWindow::FindFocus();
    for( wxWindow* control : facetControls() ) if( focus == control ) return true;
    return false;
}
std::vector<wxWindow*> RECURSIVE_DIAGRAM_FRAME::facetControls() const
{
    std::vector<wxWindow*> controls{ m_facetBack };
    controls.insert( controls.end(), m_facetStates.begin(), m_facetStates.end() );
    controls.insert( controls.end(), { m_facetValue, m_facetCandidates, m_facetReason } );
    controls.insert( controls.end(), m_facetStrengths.begin(), m_facetStrengths.end() );
    controls.push_back( m_facetClear );
    return controls;
}
int RECURSIVE_DIAGRAM_FRAME::facetState() const
{
    for( int i = 0; i < 3; ++i ) if( m_facetStates[i]->GetValue() ) return i;
    return 0;
}
int RECURSIVE_DIAGRAM_FRAME::facetStrength() const
{
    for( int i = 0; i < 3; ++i ) if( m_facetStrengths[i]->GetValue() ) return i;
    return 0;
}
void RECURSIVE_DIAGRAM_FRAME::setFacetState( int state )
{
    if( state >= 0 && state < 3 && !m_facetStates[state]->GetValue() ) m_facetStates[state]->SetValue( true );
}
void RECURSIVE_DIAGRAM_FRAME::setFacetStrength( int strength )
{
    if( strength >= 0 && strength < 3 && !m_facetStrengths[strength]->GetValue() ) m_facetStrengths[strength]->SetValue( true );
}
int RECURSIVE_DIAGRAM_FRAME::fullStrengthRow() const
{
    // The full labels in one row: each choice's indicator and padding plus its full label's text, and the gaps between them.
    const auto full = strengthLabels( false );
    int needed = 2 * FromDIP( FACET_CHOICE_GAP );
    for( int i = 0; i < 3; ++i )
    {
        wxRadioButton* button = m_facetStrengths[i]; button->InvalidateBestSize();
        needed += button->GetBestSize().x - button->GetTextExtent( button->GetLabel() ).x + button->GetTextExtent( full[i] ).x;
    }
    return needed;
}
int RECURSIVE_DIAGRAM_FRAME::inspectorWidth() const
{
    // Design QA round 2, R2-P2-7: the inspector's default width counts its scroll bar's gutter. A facet's detail makes the
    // inspector scroll at the default window size, and the scroll bar, which stays visible since P2-13, took the room the full
    // strength labels need, so they collapsed to Info, Pref. and Req. there. The default width now holds the full row with the
    // rule's 4 DIP to spare, the detail's 12 DIP margins, the scroll bar and the splitter's sash, and 4 DIP more so a scroll bar
    // a pixel wider than the toolkit reports cannot tip it; the short labels stay the fallback of a narrower inspector.
    const int scrollBar = std::max( 0, wxSystemSettings::GetMetric( wxSYS_VSCROLL_X, m_inspectorScroll ) );
    const int needed = fullStrengthRow() + FromDIP( 4 ) + 2 * FromDIP( 12 ) + scrollBar + m_splitter->GetSashSize() + FromDIP( 4 );
    return std::max( FromDIP( 400 ), needed );
}
bool RECURSIVE_DIAGRAM_FRAME::fitFacetLabels()
{
    const auto full = strengthLabels( false ), brief = strengthLabels( true );
    int available = m_inspectorScroll->GetClientSize().x - 2 * FromDIP( 12 ), needed = fullStrengthRow();
    // A few pixels to spare, so the full labels show only where they fit with room left over.
    bool collapse = needed + FromDIP( 4 ) > available, changed = false;
    for( int i = 0; i < 3; ++i )
    {
        const wxString& label = collapse ? brief[i] : full[i];
        if( m_facetStrengths[i]->GetLabel() == label ) continue;
        m_facetStrengths[i]->SetLabel( label ); m_facetStrengths[i]->InvalidateBestSize(); changed = true;
    }
    // Both rows wrap within the width the inspector gives them. Set before the inspector is laid out, it makes each row
    // as tall as the lines it places, so the controls below a row that wraps move down and none lies over a choice.
    changed |= m_facetStateRow->SetWrapWidth( available );
    changed |= m_facetStrengthRow->SetWrapWidth( available );
    // A connection's direction, domain and type rows wrap within the same width.
    for( auto* row : m_linkChoiceRows ) if( row ) changed |= row->SetWrapWidth( available );
    return changed;
}
void RECURSIVE_DIAGRAM_FRAME::facetStateChanged()
{
    if( m_facet < 0 ) return;
    int state = facetState();
    bool wasUpdating = m_updating; m_updating = true;
    // Switching between chosen and candidate carries the value across; an unknown facet needs its own reason.
    wxString value = m_facetValue->GetValue().Strip( wxString::both ), candidates = m_facetCandidates->GetValue().Strip( wxString::both );
    if( state == 1 && candidates.empty() ) m_facetCandidates->ChangeValue( value );
    if( state == 0 && value.empty() ) m_facetValue->ChangeValue( candidates.BeforeFirst( '\n' ).Strip( wxString::both ) );
    m_updating = wasUpdating;
    showFacetFields(); m_inspectorScroll->Layout(); m_inspectorScroll->FitInside();
    facetEdited();
    // Focus moves to the entry the state asks for once the drop-down list has closed and returned focus.
    CallAfter( [this, state]
               {
                   if( m_closing || m_facet < 0 || facetState() != state ) return;
                   if( state == 0 ) m_facetValue->SetFocus(); else if( state == 1 ) m_facetCandidates->SetFocus(); else m_facetReason->SetFocus();
               } );
}
void RECURSIVE_DIAGRAM_FRAME::facetEdited()
{
    if( !m_ready || m_process || m_diagramHistoryOpen || m_facet < 0 || !selectedDefinition() ) return;
    D::DefinitionTextChoiceData choice; wxString problem;
    if( !facetFromForm( choice, problem ) )
    {
        // Nothing is stored until the entry can be kept; the detail says what is missing.
        m_facetTouched = true; m_facetProblem = problem;
        m_facetNotice->SetLabel( problem ); m_facetNotice->Show();
        m_facetNotice->Wrap( std::max( FromDIP( 200 ), m_inspectorScroll->GetClientSize().x - FromDIP( 24 ) ) );
        m_inspectorScroll->Layout(); m_inspectorScroll->FitInside(); ++m_viewRevision; return;
    }
    if( !m_facetProblem.empty() && m_notice == Utf8( m_facetProblem ) ) { m_notice.clear(); showStatus(); }
    m_facetTouched = false; m_facetProblem.clear();
    if( m_facetNotice->IsShown() ) { m_facetNotice->Hide(); m_inspectorScroll->Layout(); m_inspectorScroll->FitInside(); }
    const auto* existing = R::Facet( *selectedDefinition(), m_facet );
    if( existing && existing->SerializeAsString() == choice.SerializeAsString() ) return;
    LEVEL before = m_level;
    storeFacet( m_facet, &choice );
    if( before.SerializeAsString() == m_level.SerializeAsString() ) return;
    m_undo.push_back( std::move( before ) ); m_redo.clear(); m_dirty = hasChanges(); ++m_viewRevision;
    // Like requirement text, do not refill the detail while typing: it would move the caret.
    bool wasUpdating = m_updating; m_updating = true;
    const auto* definition = selectedDefinition(); bool any = false;
    for( int facet = 0; facet < R::FACETS; ++facet )
    {
        const auto* item = R::Facet( *definition, facet ); bool shown = R::HasValue( item );
        if( shown ) m_facetRows[facet]->SetChoice( *item, facet == m_facet );
        if( m_facetRows[facet]->IsShown() != shown ) m_facetRows[facet]->Show( shown );
        any |= shown;
    }
    m_facetHeading->Show( any ); m_facetClear->Show( true );
    m_updating = wasUpdating;
    m_inspectorScroll->Layout(); m_inspectorScroll->FitInside();
    enableSave( m_dirty && m_document.source_writable(), m_dirty );
    m_toolbar->EnableTool( wxID_UNDO, true ); m_toolbar->EnableTool( wxID_REDO, false );
    m_palette->SetState( m_tool, drawingAvailable(), canDelete(), true );
    showStatus();
    m_rendered = false; m_canvas->Refresh();
}
void RECURSIVE_DIAGRAM_FRAME::clearFacet()
{
    if( !m_ready || m_process || m_diagramHistoryOpen || m_facet < 0 || !selectedDefinition() ) return;
    if( !R::HasValue( R::Facet( *selectedDefinition(), m_facet ) ) ) { closeFacet( true ); return; }
    pushUndo(); storeFacet( m_facet, nullptr );
    m_dirty = hasChanges(); closeFacet( false ); changed();
    if( m_addDetail->IsShown() ) m_addDetail->SetFocus(); else m_canvas->SetFocus();
}
bool RECURSIVE_DIAGRAM_FRAME::facetLinkOffered() const
{
    // One rule for drawing, reporting and pressing the canvas Review facets link: the canvas takes no presses while
    // the whole-diagram history is open, and a previewed past revision is read only.
    return !m_diagramHistoryOpen && !m_historyPreview;
}
void RECURSIVE_DIAGRAM_FRAME::reviewFacets( const std::string& block )
{
    if( !m_ready || m_process || !facetLinkOffered() ) return;
    finishCaption( false ); select( block );
    if( m_selected != block ) return;
    if( const auto* definition = selectedDefinition() )
        for( int facet = 0; facet < R::FACETS; ++facet )
            if( R::HasValue( R::Facet( *definition, facet ) ) ) { openFacet( facet, true ); return; }
}
wxFont RECURSIVE_DIAGRAM_FRAME::chipFont() const
{
    wxFont small = GetFont(); small.SetPointSize( std::max( 8, small.GetPointSize() - 2 ) ); return small;
}
wxColour RECURSIVE_DIAGRAM_FRAME::linkColour() const
{
    // The canvas's Review facets link and the inspector's links share one colour (design QA P2-10).
    return R::LINK_BUTTON::LinkColour( wxSystemSettings::GetColour( wxSYS_COLOUR_WINDOW ) );
}
std::vector<std::pair<std::string, R::BLOCK_CHIPS>> RECURSIVE_DIAGRAM_FRAME::drawnChips() const
{
    std::vector<std::pair<std::string, R::BLOCK_CHIPS>> result;
    if( !m_ready || !current() ) return result;
    auto drawn = layout( current(), !m_historyPreview );
    wxClientDC dc( m_canvas ); wxFont small = chipFont(), caption = GetFont().Bold().Larger();
    for( const auto& node : drawn.Nodes() )
    {
        wxRect inner = wxRect( toScreen( drawn.Rect( node.id ) ) ).Deflate( 8 );
        auto names = portNames( dc, drawn, node.id );
        auto chips = R::LayoutChips( dc, node, inner, small, R::CaptionRect( dc, node, inner, caption ), names );
        // As drawn: no Review facets link while the whole-diagram history is open (see paint).
        if( !facetLinkOffered() ) chips.link.reset();
        if( chips.shown ) result.emplace_back( node.id, std::move( chips ) );
    }
    return result;
}

void RECURSIVE_DIAGRAM_FRAME::edit()
{
    if( !m_ready || m_process || m_diagramHistoryOpen ) return;
    LEVEL before = m_level;
    auto current_ = selectedFields();
    for( int i = 0; i < 3; ++i )
    {
        std::string value = Utf8( m_fields[i]->GetValue() );
        if( field( current_, i ) == value ) continue;
        if( !m_connectionId.empty() )
        {
            if( auto* added = newConnection( m_connectionId ) ) setField( added->mutable_fields(), i, value );
            else if( auto* draft = editConnection( true ) ) { setField( draft->mutable_fields(), i, value ); dropRestoration( draft->mutable_restored_fields(), i ); }
        }
        else if( auto* added = newChild( m_selected ) ) setField( added->mutable_fields(), i, value );
        else if( auto* draft = editBlock( true ) ) { setField( draft->mutable_fields(), i, value ); dropRestoration( draft->mutable_restored_fields(), i ); }
    }
    if( before.SerializeAsString() == m_level.SerializeAsString() ) return;
    m_undo.push_back( std::move( before ) ); m_redo.clear(); m_dirty = hasChanges(); ++m_viewRevision;
    // Do not refill text controls while typing: it would move the caret.
    enableSave( m_dirty && m_document.source_writable(), m_dirty );
    m_toolbar->EnableTool( wxID_UNDO, true ); m_toolbar->EnableTool( wxID_REDO, false );
    m_palette->SetState( m_tool, drawingAvailable(), canDelete(), true );
    showStatus();
}
void RECURSIVE_DIAGRAM_FRAME::fillComments()
{
    bool wasUpdating = m_updating; m_updating = true;
    bool link = !m_connectionId.empty();
    const auto& notes = m_historyPreview && current() ? current()->local_diagram().annotations() : m_level.scope().local_diagram().annotations();
    const std::string target = link ? m_connectionId : m_selected;
    D::DiagramAnnotationTargetKind kind = link ? D::DAT_CONNECTION : D::DAT_BLOCK;
    m_commentIds.clear(); m_commentChoice->Clear();
    const D::DiagramAnnotationData* selectedNote = nullptr;
    // Comments opens on the selected element's own first comment, or on a new one when it has none (mockup audit M1-1).
    // The level's free-space notes and unresolved comments stay in the list, but opening a block never picks one of them:
    // typing would otherwise rewrite the level's note, and the note would be drawn as the selected comment.
    if( m_commentId.empty() && !m_newComment )
        for( const auto& note : notes ) if( note.target_kind() == kind && note.target_id() == target ) { m_commentId = note.id(); break; }
    for( const auto& note : notes ) if( ( note.target_kind() == kind && note.target_id() == target )
        || ( !link && ( note.target_kind() == D::DAT_CANVAS || note.has_unresolved_reason() ) ) )
    {
        wxString title = Text( note.text() ).BeforeFirst( '\n' );
        if( title.length() > 36 ) title = title.Left( 36 ) + wxS( "…" );
        if( title.empty() ) title = _( "Sketch comment" );
        if( note.has_unresolved_reason() ) title = _( "Unresolved target: " ) + title;
        m_commentChoice->Append( title ); m_commentIds.push_back( note.id() );
        if( note.id() == m_commentId ) { selectedNote = &note; m_commentChoice->SetSelection( m_commentIds.size() - 1 ); }
    }
    m_commentChoice->Append( _( "New comment" ) ); m_commentIds.emplace_back();
    if( !selectedNote ) m_commentChoice->SetSelection( m_commentIds.size() - 1 );
    wxString value = selectedNote ? Text( selectedNote->text() ) : wxString();
    m_commentTargetStatus->Show( selectedNote && selectedNote->has_unresolved_reason() );
    m_commentTargetStatus->SetLabel( selectedNote && selectedNote->has_unresolved_reason() ? Text( selectedNote->unresolved_reason() ) : wxString() );
    m_commentTargetStatus->Wrap( std::max( FromDIP( 200 ), m_inspectorScroll->GetClientSize().x - FromDIP( 24 ) ) );
    if( m_comments->GetValue() != value ) m_comments->ChangeValue( value );
    bool available = m_ready && !m_process && !m_diagramHistoryOpen && !m_historyPreview;
    m_comments->Enable( available ); m_commentChoice->Enable( available );
    m_commentChoice->Show( m_commentIds.size() > 1 );
    m_updating = wasUpdating;
}
void RECURSIVE_DIAGRAM_FRAME::editComment()
{
    if( !m_ready || m_process || m_diagramHistoryOpen || m_historyPreview ) return;
    bool link = !m_connectionId.empty();
    LEVEL before = m_level;
    auto* notes = m_level.mutable_scope()->mutable_local_diagram()->mutable_annotations();
    D::DiagramAnnotationData* selectedNote = nullptr; int index = -1;
    for( int i = 0; i < notes->size(); ++i ) if( notes->Get( i ).id() == m_commentId ) { selectedNote = notes->Mutable( i ); index = i; break; }
    std::string value = Utf8( m_comments->GetValue() );
    if( ( selectedNote && selectedNote->text() == value ) || ( !selectedNote && value.empty() ) ) return;
    if( selectedNote && value.empty() && selectedNote->strokes_size() == 0 )
    { notes->DeleteSubrange( index, 1 ); m_commentId.clear(); m_newComment = true; }
    else
    {
        if( !selectedNote )
        {
            selectedNote = notes->Add(); selectedNote->set_id( FreshId() ); selectedNote->set_role( D::DAR_COMMENT ); selectedNote->set_units( "diagram-unit" );
            selectedNote->set_target_kind( link ? D::DAT_CONNECTION : D::DAT_BLOCK );
            selectedNote->set_target_id( link ? m_connectionId : m_selected ); m_commentId = selectedNote->id(); m_newComment = false;
        }
        selectedNote->set_text( value ); EditorOrigin( selectedNote->mutable_origin(), "Edit diagram comment" );
    }
    m_undo.push_back( std::move( before ) ); m_redo.clear(); m_dirty = hasChanges();
    ++m_viewRevision; enableSave( m_dirty && m_document.source_writable(), m_dirty );
    m_toolbar->EnableTool( wxID_UNDO, true ); m_toolbar->EnableTool( wxID_REDO, false );
    fillComments(); m_inspectorScroll->Layout(); m_inspectorScroll->FitInside(); showStatus();
    m_rendered = false; m_canvas->Refresh();
}
void RECURSIVE_DIAGRAM_FRAME::save()
{
    if( !m_ready || m_process || ( m_diagramHistoryOpen && !m_pendingHistoryRestore ) ) return;
    // A caption being typed is part of the draft. A blank one cannot be kept, so nothing is saved and the
    // caption editor stays open with its notice.
    if( m_captionKind ) { finishCaption( true ); if( m_captionKind ) return; }
    // A facet entry that cannot be kept yet is part of what the person is doing: nothing is saved and the
    // detail stays open with its notice, as for a blank caption.
    if( m_facet >= 0 && m_facetTouched && !m_facetProblem.empty() )
    {
        m_notice = Utf8( m_facetProblem ); refresh();
        if( m_facetValue->IsShown() ) m_facetValue->SetFocus(); else if( m_facetCandidates->IsShown() ) m_facetCandidates->SetFocus(); else m_facetReason->SetFocus();
        return;
    }
    // A blank connection caption is not kept (Round A3): nothing is saved and the caption field says why.
    if( !m_connectionId.empty() && !m_captionProblem.empty() )
    { m_notice = Utf8( m_captionProblem ); refresh(); m_connectionCaption->SetFocus(); return; }
    // A signal typed but not yet added is part of what the person is doing: it is added first, or the save waits.
    if( !m_connectionId.empty() && m_signalEntry->IsShown() && !m_signalEntry->GetValue().Strip( wxString::both ).empty() )
    {
        addSignal();
        if( !m_signalProblem.empty() ) { m_notice = Utf8( m_signalProblem ); refresh(); m_signalEntry->SetFocus(); return; }
    }
    if( !m_dirty ) return;
    if( !m_document.source_writable() )
    { m_errorCode = "diagram_file_read_only"; m_error = "This diagram file is read-only; changes cannot be saved."; refresh(); return; }
    // A save the user asks for may rebase again; the automatic save of a rebased candidate may not.
    if( !m_rebasing ) m_rebaseAttempts = 0;
    m_rebasing = false;
    std::vector<SELECTION> path; if( !findPath( m_level.scope().baseline().block_id(), path ) ) return;
    // Only edited children and connections travel; an unchanged draft creates nothing.
    LEVEL sent = m_level; sent.clear_child_drafts(); sent.clear_connection_drafts();
    for( const auto& child : m_level.child_drafts() )
        if( const REVISION* saved = revision( child.baseline() ); !saved || child.SerializeAsString() != draftFor( *saved ).SerializeAsString() )
            *sent.add_child_drafts() = child;
    for( const auto& link : m_level.connection_drafts() )
        if( const auto* saved = savedConnection( link.baseline().connection_id() ); !saved || link.SerializeAsString() != connectionDraftFor( *saved ).SerializeAsString() )
            *sent.add_connection_drafts() = link;
    REQUEST request; request.set_action( D::RFA_SAVE_LEVEL ); request.set_expected_source_token( m_document.source_token() );
    auto* save = request.mutable_save_level(); *save->mutable_expected_root() = m_document.graph().selected_root(); *save->mutable_draft() = sent;
    for( const auto& step : path ) *save->add_block_path() = step;
    for( size_t i = 1; i < path.size(); ++i ) save->add_ancestor_revision_ids( FreshId() );
    save->set_new_revision_id( FreshId() ); save->set_new_requirement_revision_id( FreshId() );
    for( const auto& child : sent.child_drafts() )
    { auto* ids = save->add_child_revisions(); ids->set_object_id( child.baseline().block_id() ); ids->set_new_revision_id( FreshId() ); ids->set_new_requirement_revision_id( FreshId() ); }
    for( const auto& link : sent.connection_drafts() )
    { auto* ids = save->add_connection_revisions(); ids->set_object_id( link.baseline().connection_id() ); ids->set_new_revision_id( FreshId() ); ids->set_new_requirement_revision_id( FreshId() ); }
    save->set_select_implementation( m_preview.has_value() );
    EditorOrigin( save->mutable_origin(), m_preview ? "Choose implementation" : "Edit diagram level" );
    m_sentLevel = sent;
    execute( std::move( request ) );
}
void RECURSIVE_DIAGRAM_FRAME::decline()
{
    if( m_ready && !m_process && m_dirty && !m_diagramHistoryOpen )
    { finishCaption( false ); m_notice.clear(); m_pendingScope = current()->selection().block_id(); m_pendingSelected = m_selected; load(); }
}

void RECURSIVE_DIAGRAM_FRAME::openDiagramHistory()
{
    if( !m_ready || m_process || m_diagramHistoryOpen || !current() ) return;
    release(); finishCaption( false ); setTool( TOOL::SELECT );
    SELECTION context = current()->selection(); const auto* saved = revision( context ); if( !saved ) return;
    m_diagramHistoryOpen = true; m_failedHistoryRequest.Clear();
    m_diagramHistoryPanel->Begin( context, Text( saved->name() ), version( *saved ) );
    m_inspectorBook->SetSelection( 1 ); ++m_viewRevision; loadDiagramHistory( 0 );
}
void RECURSIVE_DIAGRAM_FRAME::loadDiagramHistory( unsigned offset )
{
    if( !m_diagramHistoryOpen || m_process ) return;
    REQUEST query; query.set_action( D::RFA_DIAGRAM_HISTORY ); query.set_expected_source_token( m_document.source_token() );
    *query.mutable_block() = m_diagramHistoryPanel->Context(); query.set_offset( static_cast<int>( offset ) ); query.set_limit( 50 );
    m_diagramHistoryPanel->SetBusy( true ); ++m_viewRevision; execute( std::move( query ) );
}
void RECURSIVE_DIAGRAM_FRAME::inspectDiagramHistory( SELECTION selected )
{
    if( !m_diagramHistoryOpen || m_process ) return;
    REQUEST query; query.set_action( D::RFA_COMPARE_DIAGRAM_HISTORY ); query.set_expected_source_token( m_document.source_token() );
    *query.mutable_block() = m_diagramHistoryPanel->Context(); *query.mutable_inspected_block() = std::move( selected );
    m_diagramHistoryPanel->SetBusy( true ); ++m_viewRevision; execute( std::move( query ) );
}
void RECURSIVE_DIAGRAM_FRAME::previewDiagramHistory( SELECTION selected )
{
    if( !m_diagramHistoryOpen || m_process || !revision( selected ) ) return;
    if( !m_historyView ) m_historyView = VIEW{ m_scale, m_origin, m_selected };
    m_historyPreview = std::move( selected ); m_diagramHistoryPanel->SetPreviewing( true );
    ++m_viewRevision; refresh(); fit();
}
void RECURSIVE_DIAGRAM_FRAME::returnFromHistoryPreview()
{
    if( !m_historyPreview ) return;
    m_historyPreview.reset();
    if( m_historyView ) { m_scale = m_historyView->scale; m_origin = m_historyView->origin; m_fitted = false; m_historyView.reset(); }
    m_diagramHistoryPanel->SetPreviewing( false ); ++m_viewRevision; refresh();
}
void RECURSIVE_DIAGRAM_FRAME::closeDiagramHistory()
{
    if( !m_diagramHistoryOpen ) return;
    returnFromHistoryPreview(); m_diagramHistoryOpen = false; m_pendingHistoryRestore.reset(); m_failedHistoryRequest.Clear();
    m_inspectorBook->SetSelection( 0 ); ++m_viewRevision; refresh();
    // A cancelled read can still own the companion process. Its History button
    // stays disabled until completion; never leave GTK focus on that control.
    if( m_diagramHistory->IsEnabled() ) m_diagramHistory->SetFocus(); else m_canvas->SetFocus();
}
void RECURSIVE_DIAGRAM_FRAME::restoreDiagramHistory( SELECTION selected )
{
    if( !m_diagramHistoryOpen || m_process ) return;
    m_pendingHistoryRestore = std::move( selected );
    if( levelChanged() )
    {
        wxMessageDialog choice( this, _( "Save your existing edits before preparing the earlier diagram?" ),
            _( "Unsaved changes" ), wxYES_NO | wxCANCEL | wxICON_QUESTION );
        choice.SetYesNoLabels( _( "&Save" ), _( "&Decline" ) );
        int answer = choice.ShowModal();
        if( answer == wxID_YES ) { returnFromHistoryPreview(); save(); return; }
        if( answer == wxID_NO ) { returnFromHistoryPreview(); load(); return; }
        m_pendingHistoryRestore.reset(); return;
    }
    prepareDiagramRestoration();
}
void RECURSIVE_DIAGRAM_FRAME::prepareDiagramRestoration()
{
    if( !m_pendingHistoryRestore || !m_diagramHistoryOpen || m_process ) return;
    returnFromHistoryPreview();
    SELECTION source = *m_pendingHistoryRestore;
    const auto* saved = current() ? revision( current()->selection() ) : nullptr;
    if( !saved || saved->selection().block_id() != source.block_id() || saved->selection().state_id() != source.state_id() )
    {
        m_pendingHistoryRestore.reset(); m_diagramHistoryPanel->Fail( _( "The diagram implementation changed. Close history and review the current design." ) ); return;
    }
    REQUEST request; request.set_action( D::RFA_PREPARE_DIAGRAM_RESTORATION ); request.set_expected_source_token( m_document.source_token() );
    *request.mutable_restoration()->mutable_draft() = draftFor( *saved ); *request.mutable_restoration()->mutable_source() = source;
    m_diagramHistoryPanel->SetBusy( true ); execute( std::move( request ) );
}
void RECURSIVE_DIAGRAM_FRAME::history( int which )
{
    if( !m_ready || m_process || m_diagramHistoryOpen || selectionIsNew() ) return;
    REQUEST request; request.set_expected_source_token( m_document.source_token() );
    if( m_connectionId.empty() )
    {
        SELECTION target = m_level.scope().baseline();
        for( const auto& child : m_level.scope().children() ) if( child.block_id() == m_selected ) target = child;
        request.set_action( D::RFA_BLOCK_FIELD_HISTORY ); *request.mutable_block() = target;
    }
    else
    {
        const auto* saved = savedConnection( m_connectionId ); if( !saved ) return;
        request.set_action( D::RFA_CONNECTION_FIELD_HISTORY ); *request.mutable_block() = m_path.back(); *request.mutable_connection() = saved->selection();
    }
    request.set_field( static_cast<D::RequirementFieldKind>( which + 1 ) ); request.set_limit( 200 ); execute( std::move( request ) );
}

void RECURSIVE_DIAGRAM_FRAME::showHistory( const D::RecursiveFileResult& result, const REQUEST& query )
{
    if( m_process || m_historyDialog || m_closing ) return;
    bool link = query.action() == D::RFA_CONNECTION_FIELD_HISTORY;
    const auto& page = result.history();
    std::string owner = link ? query.connection().connection_id() : query.block().block_id();
    int which = static_cast<int>( page.field() ) - 1;
    bool stillSelected = link ? m_connectionId == owner : m_connectionId.empty() && m_selected == owner;
    if( result.source_token() != m_document.source_token() || page.owner_id() != owner || !stillSelected || which < 0 || which > 2 ) return;
    std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> rows = DiagramFieldHistoryRows( m_document.graph(), page, link );
    DIALOG_DIAGRAM_FIELD_HISTORY dialog( this, FIELD_LABELS[which], m_owner->GetLabel(),
            wxString::Format( "v%u", page.context_version() ), Text( page.saved_text() ), std::move( rows ) );
    m_historyContext = page;
    dialog.ConfigurePaging( page.total(), [this, next = query]( size_t offset ) mutable
    { next.set_offset( static_cast<int>( offset ) ); execute( next ); } );
    m_historyDialog = &dialog;
    int action = dialog.ShowModal();
    m_historyDialog = nullptr; m_historyContext.Clear();
    if( action == wxID_OK && dialog.RestoredEntry() )
    {
        const auto& row = *dialog.RestoredEntry();
        if( field( selectedFields(), which ) != Utf8( row.text ) )
        {
            pushUndo();
            D::RequirementFieldsData* fields = nullptr; google::protobuf::RepeatedPtrField<D::FieldRestorationData>* restores = nullptr;
            if( link ) { if( auto* draft = editConnection( true ) ) { fields = draft->mutable_fields(); restores = draft->mutable_restored_fields(); } }
            else if( auto* draft = editBlock( true ) ) { fields = draft->mutable_fields(); restores = draft->mutable_restored_fields(); }
            if( fields )
            {
                setField( fields, which, Utf8( row.text ) ); dropRestoration( restores, which );
                auto* restored = restores->Add(); restored->set_field( page.field() ); restored->set_source_revision_id( row.revisionId );
            }
            m_dirty = hasChanges(); ++m_viewRevision;
        }
    }
    refresh();
    if( action == wxID_OK ) m_fields[which]->SetFocus(); else m_history[which]->SetFocus();
}
void RECURSIVE_DIAGRAM_FRAME::undo( bool redo )
{
    if( m_process || m_diagramHistoryOpen ) return;
    finishCaption( false ); release();
    auto& from = redo ? m_redo : m_undo; auto& to = redo ? m_undo : m_redo;
    if( from.empty() ) return; to.push_back( m_level ); m_level = std::move( from.back() ); from.pop_back();
    // Keep the selection only while its element still exists in the restored draft.
    if( !m_connectionId.empty() )
    {
        bool present = false;
        for( const auto& root : m_level.scope().local_diagram().connections() ) present |= root.connection_id() == m_connectionId;
        if( !present ) m_connectionId.clear();
    }
    bool block = m_selected == m_level.scope().baseline().block_id();
    for( const auto& child : m_level.scope().children() ) block |= child.block_id() == m_selected;
    if( !block ) { m_selected = m_level.scope().baseline().block_id(); m_portOwner.clear(); m_portId.clear(); }
    m_notice.clear(); m_lastEffects.Clear(); m_captionProblem.clear(); m_signalProblem.clear();
    if( m_facet >= 0 )
    {
        const auto* definition = selectedDefinition();
        m_facetTouched = false; m_facetProblem.clear();
        if( !definition || !R::HasValue( R::Facet( *definition, m_facet ) ) ) { m_facet = -1; m_facetOwner.clear(); }
    }
    m_dirty = hasChanges(); ++m_viewRevision; refresh();
}
void RECURSIVE_DIAGRAM_FRAME::close( wxCloseEvent& event )
{
    if( m_diagramHistoryOpen && event.CanVeto() ) { closeDiagramHistory(); event.Veto(); return; }
    if( m_process ) { event.Veto(); return; }
    finishCaption( false );
    if( m_dirty && event.CanVeto() ) { m_closeAfterSave = true; if( !confirmChange() ) { if( !m_process ) m_closeAfterSave = false; event.Veto(); return; } }
    m_closing = true; Destroy();
}

// ---- Observation --------------------------------------------------------------------------

HANDLER_RESULT<D::RecursiveDiagramObservation> RECURSIVE_DIAGRAM_FRAME::Observe( const D::ObserveRecursiveDiagramEditor& request )
{
    wxASSERT( wxIsMainThread() );
    auto failure = []( kiapi::common::ApiStatusCode code, const std::string& message )
    {
        kiapi::common::ApiResponseStatus error; error.set_status( code ); error.set_error_message( message );
        return tl::unexpected( error );
    };
    auto known = request; known.DiscardUnknownFields();
    if( known.SerializeAsString() != request.SerializeAsString() || request.document_id() != DocumentId()
        || request.expected_source_token().size() != 64 || request.views_size() < 1 || request.views_size() > 8 )
        return failure( kiapi::common::AS_BAD_REQUEST, "Provide this exact diagram, observed source/view revision and one to eight supported view requests" );
    if( !m_ready || m_process || m_drag != DRAG::NONE || m_captionKind || m_closing || !current() )
        return failure( kiapi::common::AS_BUSY, "The diagram is not at an idle rendering checkpoint" );
    if( request.expected_source_token() != m_document.source_token() || request.expected_view_revision() != m_viewRevision )
        return failure( kiapi::common::AS_BAD_REQUEST, "The diagram view changed; inspect its current source token and view revision before observing it" );
    std::set<std::string> ids; uint64_t pixels = 0;
    for( const auto& view : request.views() )
    {
        if( view.view_id().empty() || view.view_id().size() > 128 || !ids.insert( view.view_id() ).second
            || view.pixel_width() < 64 || view.pixel_width() > 4096 || view.pixel_height() < 64 || view.pixel_height() > 4096
            || ( view.has_selection() && !revision( view.selection() ) ) )
            return failure( kiapi::common::AS_BAD_REQUEST, "View IDs must be distinct, image dimensions 64 through 4096 pixels, and selected diagram revisions exact" );
        pixels += static_cast<uint64_t>( view.pixel_width() ) * view.pixel_height();
        if( pixels > 16 * 1024 * 1024 )
            return failure( kiapi::common::AS_BAD_REQUEST, "Request at most sixteen million pixels per observation; use additional observations for more views" );
        if( view.has_viewport() )
        {
            const auto& box = view.viewport();
            if( !std::isfinite( box.x() ) || !std::isfinite( box.y() ) || !std::isfinite( box.width() ) || !std::isfinite( box.height() )
                || std::abs( box.x() ) > 1000000000 || std::abs( box.y() ) > 1000000000
                || box.width() <= 0 || box.height() <= 0 || box.width() > 2100000000 || box.height() > 2100000000 )
                return failure( kiapi::common::AS_BAD_REQUEST, "Use finite positive viewport dimensions within the supported diagram-unit range" );
        }
    }
    // No yield, input processing or asynchronous paint occurs inside this scope.
    // Restore every temporary rendering parameter even when PNG encoding fails.
    struct RESTORE_VIEW
    {
        RECURSIVE_DIAGRAM_FRAME& frame;
        double scale;
        wxPoint2DDouble origin;
        std::optional<SELECTION> historical;
        std::string selected, connection, comment, portOwner, port;
        bool rendered;
        ~RESTORE_VIEW()
        {
            frame.m_scale = scale; frame.m_origin = origin; frame.m_historyPreview = historical;
            frame.m_selected = selected; frame.m_connectionId = connection; frame.m_commentId = comment;
            frame.m_portOwner = portOwner; frame.m_portId = port; frame.m_rendered = rendered;
        }
    } restore{ *this, m_scale, m_origin, m_historyPreview, m_selected, m_connectionId, m_commentId, m_portOwner, m_portId, m_rendered };
    D::RecursiveDiagramObservation result;
    result.set_document_id( DocumentId() ); result.set_source_token( m_document.source_token() ); result.set_view_revision( m_viewRevision );
    *result.mutable_editor() = State();
    for( const auto& view : request.views() )
    {
        m_historyPreview = view.has_selection() ? std::optional<SELECTION>( view.selection() ) : restore.historical;
        m_selected = view.has_selection() ? "" : restore.selected;
        m_connectionId = view.has_selection() ? "" : restore.connection;
        m_commentId = view.has_selection() ? "" : restore.comment;
        m_portOwner = view.has_selection() ? "" : restore.portOwner; m_portId = view.has_selection() ? "" : restore.port;
        auto* scope = current(); if( !scope ) return failure( kiapi::common::AS_BAD_REQUEST, "The requested diagram revision is unavailable" );
        bool draft = !m_historyPreview;
        auto drawn = layout( scope, draft );
        R::RECT extent = drawn.Bounds();
        auto bounds = view.has_viewport() ? wxRect2DDouble( view.viewport().x(), view.viewport().y(), view.viewport().width(), view.viewport().height() )
            : wxRect2DDouble( extent.x / double( R::QUANTUM ), extent.y / double( R::QUANTUM ), extent.w / double( R::QUANTUM ), extent.h / double( R::QUANTUM ) );
        m_scale = std::min( view.pixel_width() / bounds.m_width, view.pixel_height() / bounds.m_height );
        m_origin = { bounds.m_x - ( view.pixel_width() / m_scale - bounds.m_width ) / 2,
                     bounds.m_y - ( view.pixel_height() / m_scale - bounds.m_height ) / 2 };
        // The wxDC backend accepts integer device coordinates. Reject an
        // unrepresentable zoom instead of overflowing and drawing false geometry.
        auto representable = [&]( double x, double y )
        { return std::isfinite( ( x - m_origin.m_x ) * m_scale ) && std::isfinite( ( y - m_origin.m_y ) * m_scale )
            && std::abs( ( x - m_origin.m_x ) * m_scale ) < 10000000 && std::abs( ( y - m_origin.m_y ) * m_scale ) < 10000000; };
        if( !std::isfinite( m_scale ) || m_scale <= 0 || !representable( extent.x / double( R::QUANTUM ), extent.y / double( R::QUANTUM ) )
            || !representable( extent.Right() / double( R::QUANTUM ), extent.Bottom() / double( R::QUANTUM ) ) )
            return failure( kiapi::common::AS_BAD_REQUEST, "This zoom exceeds the native drawing range; request a wider viewport" );
        wxBitmap bitmap( static_cast<int>( view.pixel_width() ), static_cast<int>( view.pixel_height() ), 32 );
        if( !bitmap.IsOk() ) return failure( kiapi::common::AS_BAD_REQUEST, "The native image buffer could not be created" );
        { wxMemoryDC dc( bitmap ); dc.SetFont( GetFont() ); paint( dc ); dc.SelectObject( wxNullBitmap ); }
        wxMemoryOutputStream output;
        if( !bitmap.ConvertToImage().SaveFile( output, wxBITMAP_TYPE_PNG ) || output.GetSize() > 64 * 1024 * 1024 )
            return failure( kiapi::common::AS_BAD_REQUEST, "The native diagram image could not be encoded within the supported output size" );
        std::string png( output.GetSize(), '\0' ); output.CopyTo( png.data(), png.size() );
        auto* rendered = result.add_views(); rendered->set_view_id( view.view_id() ); rendered->set_units( "diagram-unit" );
        rendered->set_coordinate_system( "x-right/y-down" );
        rendered->mutable_viewport()->set_x( m_origin.m_x ); rendered->mutable_viewport()->set_y( m_origin.m_y );
        rendered->mutable_viewport()->set_width( view.pixel_width() / m_scale ); rendered->mutable_viewport()->set_height( view.pixel_height() / m_scale );
        rendered->set_pixel_width( view.pixel_width() ); rendered->set_pixel_height( view.pixel_height() ); rendered->set_png( std::move( png ) );
        *rendered->mutable_diagram() = *scope;
        if( draft && same( m_level.scope().baseline(), scope->selection() ) )
        {
            // The current canvas includes its unsaved level draft.
            rendered->mutable_diagram()->set_name( m_level.scope().name() );
            *rendered->mutable_diagram()->mutable_children() = m_level.scope().children();
            *rendered->mutable_diagram()->mutable_local_diagram() = m_level.scope().local_diagram();
            *rendered->mutable_requirements() = m_level.scope().fields();
        }
        else if( auto* fields = requirements( *scope ) ) *rendered->mutable_requirements() = fields->fields();
        rendered->set_contains_unsaved_draft( draft && m_dirty );
        drawn.Report( rendered->mutable_resolved_layout() );
        for( const auto& child : draft ? m_level.scope().children() : scope->children() ) if( auto* item = revision( child ) ) *rendered->add_children() = *item;
        for( const auto& archive : m_document.graph().connection_archives() ) if( archive.owner_block_id() == scope->selection().block_id() )
        {
            std::vector<D::ConnectionSelectionData> pending( scope->local_diagram().connections().begin(), scope->local_diagram().connections().end() );
            std::set<std::string> seen;
            while( !pending.empty() )
            {
                auto selected = pending.back(); pending.pop_back(); if( !seen.insert( selected.revision_id() ).second ) continue;
                for( const auto& item : archive.revisions() ) if( item.selection().SerializeAsString() == selected.SerializeAsString() )
                { *rendered->add_connections() = item; pending.insert( pending.end(), item.members().begin(), item.members().end() ); break; }
            }
        }
    }
    return result;
}
D::RecursiveDiagramEditorState RECURSIVE_DIAGRAM_FRAME::State() const
{
    D::RecursiveDiagramEditorState result; result.set_document_id( DocumentId() ); result.set_source_path( SourcePath() ); result.set_source_token( m_document.source_token() );
    result.set_ready( m_ready ); result.set_busy( m_process != nullptr ); result.set_dirty( m_dirty ); result.set_rendered( m_rendered );
    result.set_view_revision( m_viewRevision ); result.set_completed_save_count( m_saveCount ); result.set_error_code( m_errorCode ); result.set_error_message( m_error );
    for( const auto& step : m_path ) *result.add_diagram_path() = step;
    if( m_ready )
    {
        *result.mutable_level_draft() = m_level;
        // draft (13) is the selected block's draft; connection_draft (14) the selected connection's.
        if( const auto* added = m_connectionId.empty() ? newChild( m_selected ) : nullptr )
        {
            auto* mirror = result.mutable_draft(); *mirror->mutable_baseline() = added->selection(); mirror->set_name( added->name() );
            mirror->set_baseline_requirement_revision_id( added->requirement_revision_id() );
            mirror->mutable_baseline_fields(); *mirror->mutable_fields() = added->fields();
            *mirror->mutable_local_diagram()->mutable_interfaces() = added->interfaces();
            if( added->has_definition() ) *mirror->mutable_definition() = added->definition();
        }
        else if( const auto* draft = m_connectionId.empty() ? selectedBlockDraft() : &m_level.scope() ) *result.mutable_draft() = *draft;
        else for( const auto& child : m_level.scope().children() ) if( child.block_id() == m_selected )
            if( const REVISION* saved = revision( child ) ) *result.mutable_draft() = draftFor( *saved );
        if( !m_connectionId.empty() )
        {
            if( const auto* added = newConnection( m_connectionId ) )
            {
                auto* mirror = result.mutable_connection_draft(); *mirror->mutable_baseline() = added->selection(); mirror->set_name( added->name() );
                mirror->set_kind( added->kind() ); *mirror->mutable_endpoints() = added->endpoints();
                mirror->set_baseline_requirement_revision_id( added->requirement_revision_id() ); mirror->mutable_baseline_fields();
                *mirror->mutable_fields() = added->fields(); mirror->set_domain( added->domain() ); mirror->set_direction( added->direction() );
                for( const auto& signal : m_level.new_connections() )
                    if( signal.has_member_of() && signal.member_of() == m_connectionId ) *mirror->add_members() = signal.selection();
            }
            else if( const auto* draft = connectionDraft( m_connectionId ) ) *result.mutable_connection_draft() = *draft;
            else if( const auto* saved = savedConnection( m_connectionId ) ) *result.mutable_connection_draft() = connectionDraftFor( *saved );
        }
        result.set_stored_schema_version( m_document.stored_schema_version() );
        result.set_source_writable( m_document.source_writable() );
    }
    result.set_selected_annotation_id( m_commentId );
    result.set_implementation_preview( m_preview.has_value() );
    result.set_navigation_input_revision( m_navigationInputRevision );
    result.set_canvas_origin_x( m_origin.m_x ); result.set_canvas_origin_y( m_origin.m_y ); result.set_canvas_scale( m_scale );
    result.set_canvas_pixel_width( std::max( 0, m_canvas->GetClientSize().x ) );
    result.set_canvas_pixel_height( std::max( 0, m_canvas->GetClientSize().y ) );
    wxPoint corner = m_canvas->GetScreenPosition() - GetScreenPosition();
    result.set_canvas_window_x( corner.x ); result.set_canvas_window_y( corner.y );
    result.set_canvas_tool( R::ToolName( m_tool ) );
    result.set_selected_interface_id( m_portId ); result.set_selected_interface_owner_id( m_portOwner );
    result.set_palette_shown( m_paletteShown );
    static const char* CAPTIONS[] = { "", "block", "connection", "port", "rename" };
    result.set_caption_editor( m_captionKind >= 0 && m_captionKind <= 4 ? CAPTIONS[m_captionKind] : "" );
    if( m_tool == TOOL::CONNECT && m_connectFrom ) result.set_canvas_hint( "Click a port to finish connection" );
    *result.mutable_last_effects() = m_lastEffects;
    for( int i = 0; i < 3; ++i ) if( m_ready && m_fields[i]->IsShown() ) result.add_shown_requirement_fields( static_cast<D::RequirementFieldKind>( i + 1 ) );
    result.set_notice( m_notice );
    if( auto* status = GetStatusBar() ) result.set_status_text( Utf8( status->GetStatusText() ) );
    result.set_dragging( m_drag != DRAG::NONE );
    // Round A4: chips and Review facets links as drawn, the facet overview and the open detail.
    {
        wxPoint origin = m_canvas->GetScreenPosition() - GetScreenPosition();
        wxRect visible( wxPoint( 0, 0 ), m_canvas->GetClientSize() );
        bool usable = m_ready && !m_process && !m_diagramHistoryOpen && !m_historyPreview;
        auto place = [&]( D::DiagramControlRect* out, const std::string& name, const wxRect& rect, bool enabled )
        {
            out->set_name( name ); out->set_x( origin.x + rect.x ); out->set_y( origin.y + rect.y );
            out->set_width( rect.width ); out->set_height( rect.height ); out->set_shown( visible.Contains( rect ) ); out->set_enabled( enabled );
        };
        const auto drawnChipsCache = drawnChips();
        // The level as drawn, kept for the whole report: a "+N more" chip's tooltip reads its block's definition from it.
        std::optional<R::LEVEL_LAYOUT> chipLevel;
        if( m_ready && current() ) chipLevel.emplace( layout( current(), !m_historyPreview ) );
        for( const auto& [block, chips] : drawnChipsCache )
        {
            auto* row = result.add_block_chips(); row->set_block_id( block ); row->set_hidden_chips( chips.hidden );
            for( const auto& chip : chips.chips )
            {
                auto* item = row->add_chips(); item->set_facet( R::FacetName( chip.facet ) ); item->set_state( chip.state );
                item->set_text( Utf8( chip.text ) ); place( item->mutable_rect(), "DiagramFacetChip", chip.rect, false );
            }
            if( chips.link ) place( row->mutable_review_facets(), "DiagramReviewFacets", *chips.link, usable );
            for( const auto& mark : chips.marks )
            {
                auto* item = row->add_marks(); item->set_facet( R::FacetName( mark.facet ) ); item->set_state( mark.state );
                place( item->mutable_rect(), "DiagramChoiceMark", mark.rect, false );
            }
            if( chips.more )
            {
                place( row->mutable_more(), "DiagramMoreChips", *chips.more, false );
                row->mutable_more()->set_label( Utf8( wxString::Format( _( "+%u more" ), chips.hidden ) ) );
                if( const R::NODE* node = chipLevel ? chipLevel->Node( block ) : nullptr )
                    row->mutable_more()->set_tooltip( Utf8( R::MoreToolTip( *node, chips ) ) );
            }
            place( row->mutable_caption(), "DiagramBlockCaption", chips.caption, false );
            for( const auto& name : chips.portNames ) place( row->add_port_names(), "DiagramBlockPortName", name, false );
        }
        for( int facet = 0; facet < R::FACETS; ++facet ) if( m_facetRows[facet]->IsShown() ) result.add_shown_facets( R::FacetName( facet ) );
        result.set_facet_editor( m_facet >= 0 ? R::FacetName( m_facet ) : "" );
        result.set_facet_notice( Utf8( m_facetProblem ) );
        // Round A3: the selected connection's detail rows and signals as shown, and each connection's arrowheads as drawn.
        for( int which = 0; which < DETAILS; ++which ) if( m_ready && m_detailRows[which]->AreAnyItemsShown() ) result.add_shown_connection_details( DETAIL_NAMES[which] );
        if( m_ready && m_endpoints->IsShown() ) result.add_shown_connection_details( "endpoints" );
        for( const auto& line : m_signalLines ) if( line.name->IsShown() ) result.add_shown_signals( Utf8( line.name->GetLabelText() ) );
        result.set_connection_notice( Utf8( !m_captionProblem.empty() ? m_captionProblem : m_signalProblem ) );
        std::map<std::string, D::DiagramCanvasConnectionMarks*> marks;
        for( const auto& arrow : drawnArrows() )
        {
            auto*& mark = marks[arrow.connection];
            if( !mark ) { mark = result.add_connection_marks(); mark->set_connection_id( arrow.connection ); }
            if( std::find( mark->arrow_endpoints().begin(), mark->arrow_endpoints().end(), arrow.endpoint ) == mark->arrow_endpoints().end() )
                mark->add_arrow_endpoints( arrow.endpoint );
            wxRect box( wxPoint( std::min( arrow.tip.x, arrow.from.x ), std::min( arrow.tip.y, arrow.from.y ) ),
                        wxPoint( std::max( arrow.tip.x, arrow.from.x ), std::max( arrow.tip.y, arrow.from.y ) ) );
            box.Inflate( FromDIP( 5 ) );
            place( mark->add_arrows(), "DiagramArrow", box, false );
        }
        for( auto& [id, mark] : marks ) std::sort( mark->mutable_arrow_endpoints()->begin(), mark->mutable_arrow_endpoints()->end() );
        // Round A1: the level frame and the names of its boundary ports as drawn, so a journey can check that fitting keeps
        // them beside the palette and inside the canvas.
        if( m_ready && current() )
        {
            auto drawn = layout( current(), !m_historyPreview );
            if( auto frame = drawn.Frame() ) place( result.mutable_level_frame(), "DiagramLevelFrame", toScreen( *frame ), false );
            for( const auto& name : boundaryNames( drawn ) )
            {
                auto* row = result.add_boundary_port_names(); place( row, "DiagramBoundaryPortName", name.rect, false ); row->set_label( name.name );
                row->set_object_id( name.owner + "/" + name.id );
            }
            // Each canvas note with the lines it shows, measured as the canvas paints them.
            wxClientDC dc( m_canvas ); dc.SetFont( GetFont() );
            const auto& notes = visibleNotes();
            for( int i = 0; i < notes.size(); ++i )
            {
                const auto& note = notes.Get( i );
                if( note.target_kind() != D::DAT_CANVAS && !note.has_position() ) continue;
                wxRect box = noteRect( note, i );
                auto* row = result.add_canvas_notes(); row->set_annotation_id( note.id() ); place( row->mutable_rect(), "DiagramCanvasNote", box, false );
                box.Deflate( 8 );
                for( const wxString& line : R::NoteLines( dc, R::Text( note.text() ), box.width, box.GetBottom() - box.y ) ) row->add_lines( R::Utf8( line ) );
            }
            // Design QA fixes, as drawn: every port's square and the Connect tool's highlight (P2-6), every connection caption
            // (P2-7), every block's caption and version line (P2-7) and the selected block's handles (P1-1).
            auto target = connectTarget( drawn );
            for( const auto& port : portMarks( drawn ) )
            {
                auto* row = result.add_port_marks(); place( row, "DiagramPortMark", port.rect, false );
                row->set_label( R::Utf8( port.name ) ); row->set_object_id( port.owner + "/" + port.id );
                row->set_active( target && target->port && target->owner == port.owner && target->id == port.id );
            }
            if( target ) { place( result.mutable_connect_target(), "DiagramConnectTarget", target->rect, false ); result.mutable_connect_target()->set_label( R::Utf8( target->name ) ); }
            for( const auto& caption : connectionCaptions( drawn, m_canvas->GetClientSize() ) )
            {
                auto* row = result.add_connection_captions(); place( row, "DiagramConnectionCaption", caption.rect, false );
                row->set_shown( caption.shown && visible.Contains( caption.rect ) ); row->set_label( R::Utf8( caption.text ) );
                row->set_object_id( caption.connection );
                // A shortened caption shows its whole text on hover (design QA round 2, R2-P2-1).
                if( caption.text != caption.full ) row->set_tooltip( R::Utf8( caption.full ) );
                // A caption drawn where it covers something, because no clear place was found, is reported as not enabled.
                row->set_enabled( caption.clear );
            }
            wxFont captionFont = GetFont().Bold().Larger();
            for( const auto& node : drawn.Nodes() )
            {
                wxRect box = toScreen( drawn.Rect( node.id ) ), inner = wxRect( box ).Deflate( 8 );
                auto* row = result.add_block_texts(); row->set_block_id( node.id ); place( row->mutable_block(), "DiagramBlock", box, false );
                R::CAPTION caption = R::BlockCaption( dc, node, inner, captionFont );
                if( !caption.rect.IsEmpty() )
                { place( row->mutable_caption(), "DiagramBlockCaption", caption.rect, false ); row->mutable_caption()->set_label( R::Utf8( caption.text ) ); }
                dc.SetFont( GetFont() );
                bool chips = std::any_of( drawnChipsCache.begin(), drawnChipsCache.end(), [&]( const auto& entry ) { return entry.first == node.id; } );
                if( !chips && !node.isNew )
                {
                    wxString version = wxString::Format( "v%d", node.version );
                    if( wxRect line = R::VersionRect( dc, version, inner, caption.rect ); !line.IsEmpty() )
                    { place( row->mutable_version_line(), "DiagramBlockVersion", line, false ); row->mutable_version_line()->set_label( R::Utf8( version ) ); }
                }
            }
            for( const auto& handle : selectionHandles( drawn ) ) place( result.add_selection_handles(), "DiagramSelectionHandle", handle, false );
            // The Connect tool's preview and hint as drawn (design QA round 2, R2-P2-2 and P3 1).
            const auto preview = connectPreview( drawn );
            for( const wxPoint& point : preview ) { auto* row = result.add_connect_preview(); row->set_x( origin.x + point.x ); row->set_y( origin.y + point.y ); }
            if( auto hint = connectHint( drawn, preview ) )
            { place( result.mutable_connect_hint(), "DiagramConnectHint", *hint, false ); result.mutable_connect_hint()->set_label( "Click a port to finish connection" ); }
        }
    }
    result.set_canvas_presses( m_canvasPresses );
    for( const auto& [block, view] : m_views )
    { auto* row = result.add_level_viewports(); row->set_block_id( block ); row->set_origin_x( view.origin.m_x ); row->set_origin_y( view.origin.m_y ); row->set_scale( view.scale ); }
    if( auto* canvas = current() )
    {
        if( !m_views.count( canvas->selection().block_id() ) )
        { auto* row = result.add_level_viewports(); row->set_block_id( canvas->selection().block_id() ); row->set_origin_x( m_origin.m_x ); row->set_origin_y( m_origin.m_y ); row->set_scale( m_scale ); }
        *result.mutable_canvas_diagram() = *canvas; unsigned resolved = 0;
        if( !m_historyPreview && m_ready && same( m_level.scope().baseline(), canvas->selection() ) )
        {
            *result.mutable_canvas_diagram()->mutable_children() = m_level.scope().children();
            *result.mutable_canvas_diagram()->mutable_local_diagram() = m_level.scope().local_diagram();
        }
        for( const auto& child : result.canvas_diagram().children() ) if( revision( child ) || newChild( child.block_id() ) ) ++resolved;
        result.set_resolved_canvas_children( resolved );
    }
    if( m_diagramHistoryOpen )
    {
        auto* history = result.mutable_diagram_history(); *history->mutable_context() = m_diagramHistoryPanel->Context();
        if( auto selected = m_diagramHistoryPanel->Inspected() ) *history->mutable_inspected() = *selected;
        if( m_historyPreview ) *history->mutable_preview() = *m_historyPreview;
        history->set_loaded_count( m_diagramHistoryPanel->LoadedCount() ); history->set_total_count( m_diagramHistoryPanel->TotalCount() );
        history->set_busy( m_diagramHistoryPanel->Busy() ); history->set_error_message( m_diagramHistoryPanel->Error() );
    }
    if( m_historyDialog )
    {
        auto* history = result.mutable_field_history();
        history->set_owner_id( m_historyContext.owner_id() ); history->set_state_id( m_historyContext.state_id() );
        history->set_context_revision_id( m_historyContext.context_revision_id() ); history->set_field( m_historyContext.field() );
        history->set_loaded_count( static_cast<unsigned>( m_historyDialog->LoadedCount() ) );
        history->set_total_count( static_cast<unsigned>( m_historyDialog->TotalCount() ) );
        history->set_inspected_revision_id( m_historyDialog->InspectedRevision() );
        history->set_loading( m_historyDialog->IsLoading() ); history->set_error_message( Utf8( m_historyDialog->PageError() ) );
        for( const wxString& row : m_historyDialog->RowLabels() ) history->add_row_labels( Utf8( row ) );
        for( const wxString& row : m_historyDialog->ShownRowLabels() ) history->add_shown_row_labels( Utf8( row ) );
    }
    if( m_preview ) *result.mutable_preview_selection() = *m_preview;
    // Rendered controls, so journeys drive the real toolbar strip, palette and inspector.
    wxPoint window = GetScreenPosition();
    auto control = [&]( const std::string& name, const wxRect& rect, bool shown, bool enabled, bool active, const wxString& label,
                        wxWindow* item = nullptr )
    {
        auto* row = result.add_controls(); row->set_name( name ); row->set_x( rect.x - window.x ); row->set_y( rect.y - window.y );
        row->set_width( rect.width ); row->set_height( rect.height ); row->set_shown( shown ); row->set_enabled( enabled ); row->set_active( active );
        row->set_label( Utf8( label ) );
        if( item ) row->set_tooltip( Utf8( item->GetToolTipText() ) );
        // What assistive technology reads from a button, a one-click choice or a facet row, from the toolkit itself.
        if( item && ( dynamic_cast<wxAnyButton*>( item ) || dynamic_cast<wxRadioButton*>( item ) || dynamic_cast<R::FACET_ROW*>( item ) ) )
            if( auto accessible = R::AccessibleOf( item ) )
            {
                auto* out = row->mutable_accessible();
                out->set_role( accessible->role ); out->set_name( accessible->name ); out->set_checked( accessible->checked );
            }
    };
    std::vector<wxWindow*> windows{ m_caption, m_addRequirement, m_addDetail, m_save, m_decline, m_openDiagram, m_owner, m_canvas, m_stripDelete,
                                    m_endpoints, m_comments, m_inspectorScroll, m_connectionCaption, m_signalEntry, m_savedVersion };
    windows.insert( windows.end(), m_detailRemove.begin(), m_detailRemove.end() );
    windows.push_back( m_endpointRemove );
    windows.insert( windows.end(), m_directionChoices.begin(), m_directionChoices.end() );
    windows.insert( windows.end(), m_domainChoices.begin(), m_domainChoices.end() );
    windows.insert( windows.end(), m_typeChoices.begin(), m_typeChoices.end() );
    for( const auto& line : m_signalLines ) { windows.push_back( line.name ); windows.push_back( line.remove ); }
    for( auto& [tool, button] : m_strip ) windows.push_back( button );
    for( auto* child : m_palette->GetChildren() ) windows.push_back( child );
    for( int i = 0; i < 3; ++i ) { windows.push_back( m_fields[i] ); windows.push_back( m_history[i] ); }
    for( auto* row : m_facetRows ) windows.push_back( row );
    for( wxWindow* detail : facetControls() ) windows.push_back( detail );
    for( auto* item : windows )
    {
        if( item->GetName().empty() || item->GetName() == "staticLine" ) continue;
        auto* toggle = dynamic_cast<wxToggleButton*>( item );
        auto* radio = dynamic_cast<wxRadioButton*>( item );
        bool labelled = radio || dynamic_cast<wxAnyButton*>( item ) || dynamic_cast<R::FACET_ROW*>( item ) || item == m_owner
                        || dynamic_cast<wxStaticText*>( item );
        // The connection's caption field and new-signal entry report the text they show (Round A3).
        auto* text = dynamic_cast<wxStaticText*>( item );
        // A facet row reports its label as shown and read out, without the escaping of an "&" in its value.
        auto* facetRow = dynamic_cast<R::FACET_ROW*>( item );
        wxString label = item == m_connectionCaption ? m_connectionCaption->GetValue() : item == m_signalEntry ? m_signalEntry->GetValue()
                       : text ? text->GetLabelText() : facetRow ? facetRow->GetLabelText() : labelled ? item->GetLabel() : wxString();
        control( Utf8( item->GetName() ), wxRect( item->GetScreenPosition(), item->GetSize() ), item->IsShownOnScreen(), item->IsEnabled(),
                 ( toggle && toggle->GetValue() ) || ( radio && radio->GetValue() ), label, item );
    }
    // The splitter's sash between the canvas and the inspector, which widens or narrows the inspector.
    if( m_splitter->IsSplit() )
    {
        wxPoint sash = m_splitter->ClientToScreen( wxPoint( m_splitter->GetSashPosition(), 0 ) );
        control( "RecursiveInspectorSash", wxRect( sash, wxSize( m_splitter->GetSashSize(), m_splitter->GetClientSize().y ) ),
                 m_splitter->IsShownOnScreen(), m_splitter->IsEnabled(), false, wxString() );
    }
    if( auto* focused = wxWindow::FindFocus(); focused && wxGetTopLevelParent( focused ) == this )
        result.set_focused_control( Utf8( focused->GetName() ) );
    // How often the computed connection paths were laid out and how long that took, read after everything above has drawn.
    const R::ROUTE_STATS routes = R::RouteStats();
    result.set_route_layouts( routes.layouts ); result.set_slowest_route_layout_micros( routes.slowestMicros );
    result.set_latest_route_layout_micros( routes.latestMicros );
    result.set_drag_positions( m_dragPositions );
    // The tooltip as it is set on the canvas window itself, not the editor's own note of it.
    result.set_canvas_tooltip( Utf8( m_canvas->GetToolTipText() ) );
    result.set_canvas_motions( m_canvasMotions );
    {
        wxPoint corner = m_canvas->GetScreenPosition() - GetScreenPosition();
        result.mutable_canvas_pointer()->set_x( corner.x + m_pointer.x ); result.mutable_canvas_pointer()->set_y( corner.y + m_pointer.y );
    }
    return result;
}

namespace
{
auto controlFailure( kiapi::common::ApiStatusCode code, const std::string& message )
{
    kiapi::common::ApiResponseStatus error; error.set_status( code ); error.set_error_message( message );
    return tl::unexpected( error );
}
}

RECURSIVE_DIAGRAM_CONTROL::RECURSIVE_DIAGRAM_CONTROL( wxWindow* parent ) : m_parent( parent )
{
    registerHandler<D::OpenRecursiveDiagramEditor, D::RecursiveDiagramEditorState>( &RECURSIVE_DIAGRAM_CONTROL::open );
    registerHandler<D::ReadRecursiveDiagramEditor, D::RecursiveDiagramEditorState>( &RECURSIVE_DIAGRAM_CONTROL::read );
    registerHandler<D::ObserveRecursiveDiagramEditor, D::RecursiveDiagramObservation>( &RECURSIVE_DIAGRAM_CONTROL::observe );
    Pgm().GetApiServer().RegisterHandler( this );
}

RECURSIVE_DIAGRAM_CONTROL::~RECURSIVE_DIAGRAM_CONTROL()
{
    Pgm().GetApiServer().DeregisterHandler( this );
}

bool RECURSIVE_DIAGRAM_CONTROL::CloseEditors()
{
    for( auto& editor : m_editors )
        if( editor && !editor->IsClosing() && !editor->IsBeingDeleted() && !editor->Close() )
            return false;
    return true;
}

RECURSIVE_DIAGRAM_FRAME* RECURSIVE_DIAGRAM_CONTROL::openEditor( const std::string& documentId ) const
{
    for( const auto& editor : m_editors )
        if( editor && !editor->IsClosing() && !editor->IsBeingDeleted() && editor->DocumentId() == documentId )
            return editor.get();
    return nullptr;
}

HANDLER_RESULT<D::RecursiveDiagramEditorState> RECURSIVE_DIAGRAM_CONTROL::open( const HANDLER_CONTEXT<D::OpenRecursiveDiagramEditor>& ctx )
{
    const auto& request = ctx.Request;
    auto known = request; known.DiscardUnknownFields();
    // Contract rbg-v2 section 2.4: only schema 2 opens; an older MCP gets AS_BAD_REQUEST.
    if( known.ByteSizeLong() != request.ByteSizeLong() || request.schema_version() != 2
        || !KIID::SniffTest( wxString::FromUTF8( request.document_id() ) ) || request.expected_source_token().size() != 64
        || !wxFileName( wxString::FromUTF8( request.source_path() ) ).IsAbsolute()
        || !wxFileName( wxString::FromUTF8( request.helper_path() ) ).IsAbsolute()
        || !wxFileName::FileExists( wxString::FromUTF8( request.helper_path() ) )
        || !wxFileName::DirExists( wxString::FromUTF8( request.repository_root() ) ) )
        return controlFailure( kiapi::common::AS_BAD_REQUEST, "An exact schema 2 diagram target and compiled companion are required" );
    if( auto* editor = openEditor( request.document_id() ) )
    {
        if( editor->SourcePath() != request.source_path() )
            return controlFailure( kiapi::common::AS_BUSY, "This diagram is already open from another source" );
        editor->Show(); editor->Raise(); return editor->State();
    }
    auto* frame = new RECURSIVE_DIAGRAM_FRAME( m_parent, request );
    m_editors.emplace_back( frame ); frame->Show(); frame->Raise(); return frame->State();
}

HANDLER_RESULT<D::RecursiveDiagramEditorState> RECURSIVE_DIAGRAM_CONTROL::read( const HANDLER_CONTEXT<D::ReadRecursiveDiagramEditor>& ctx )
{
    if( auto* editor = openEditor( ctx.Request.document_id() ) ) return editor->State();
    return controlFailure( kiapi::common::AS_BAD_REQUEST, "The explicitly identified recursive diagram is not open" );
}

HANDLER_RESULT<D::RecursiveDiagramObservation> RECURSIVE_DIAGRAM_CONTROL::observe( const HANDLER_CONTEXT<D::ObserveRecursiveDiagramEditor>& ctx )
{
    if( auto* editor = openEditor( ctx.Request.document_id() ) ) return editor->Observe( ctx.Request );
    return controlFailure( kiapi::common::AS_BAD_REQUEST, "The explicitly identified recursive diagram is not open" );
}
