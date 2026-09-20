/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#include "panel_diagram_history.h"
#include <wx/button.h>
#include <wx/datetime.h>
#include <wx/dc.h>
#include <wx/settings.h>
#include <wx/sizer.h>
#include <wx/stattext.h>
#include <wx/textctrl.h>
#include <wx/vlbox.h>
#include <algorithm>
#include <set>
#include <vector>

namespace D = kiapi::automation::diagrams::v1;
namespace
{
wxString text( const std::string& value ) { return wxString::FromUTF8( value ); }
bool same( const D::BlockSelectionData& a, const D::BlockSelectionData& b )
{ return a.block_id() == b.block_id() && a.state_id() == b.state_id() && a.revision_id() == b.revision_id(); }
}

class DIAGRAM_HISTORY_ROWS : public wxVListBox
{
public:
    explicit DIAGRAM_HISTORY_ROWS( wxWindow* parent ) : wxVListBox( parent, wxID_ANY )
    { SetName( "DiagramHistoryRevisions" ); SetMinSize( FromDIP( wxSize( 250, 120 ) ) ); }
    std::vector<D::DiagramHistoryEntryData> entries;
protected:
    wxCoord OnMeasureItem( size_t ) const override { return GetCharHeight() * 2 + FromDIP( 18 ); }
    void OnDrawItem( wxDC& dc, const wxRect& bounds, size_t index ) const override
    {
        const auto& entry = entries[index];
        dc.SetTextForeground( wxSystemSettings::GetColour( IsSelected( index ) ? wxSYS_COLOUR_HIGHLIGHTTEXT : wxSYS_COLOUR_WINDOWTEXT ) );
        wxRect row = bounds; row.Deflate( FromDIP( 10 ), FromDIP( 6 ) );
        dc.SetFont( GetFont().Bold() );
        wxString version = wxString::Format( "v%u", entry.version() ); dc.DrawText( version, row.GetTopLeft() );
        int inset = std::max( FromDIP( 50 ), dc.GetTextExtent( version ).x + FromDIP( 12 ) );
        dc.SetFont( GetFont() );
        wxString actor = entry.is_context() ? _( "Saved" ) + wxS( " · " ) : wxString(); actor += text( entry.origin().actor() );
        dc.DrawText( wxControl::Ellipsize( actor, dc, wxELLIPSIZE_END, std::max( 1, row.width - inset ) ), row.x + inset, row.y );
        dc.DrawText( wxControl::Ellipsize( text( entry.origin().summary() ).BeforeFirst( '\n' ), dc, wxELLIPSIZE_END,
            std::max( 1, row.width - inset ) ), row.x + inset, row.y + GetCharHeight() + FromDIP( 3 ) );
    }
};

PANEL_DIAGRAM_HISTORY::PANEL_DIAGRAM_HISTORY( wxWindow* parent, ACTIONS actions ) :
    wxPanel( parent ), m_actions( std::move( actions ) )
{
    SetName( "DiagramHistoryPanel" ); const int gap = FromDIP( 12 );
    auto* layout = new wxBoxSizer( wxVERTICAL ); auto* heading = new wxBoxSizer( wxHORIZONTAL );
    auto* close = new wxButton( this, wxID_ANY, _( "Back" ), wxDefaultPosition, wxDefaultSize, wxBU_EXACTFIT );
    close->SetName( "DiagramHistoryClose" ); close->SetToolTip( _( "Back to properties" ) );
    heading->Add( close, 0, wxRIGHT, gap );
    m_title = new wxStaticText( this, wxID_ANY, _( "History" ), wxDefaultPosition, wxDefaultSize, wxST_ELLIPSIZE_MIDDLE );
    m_title->SetFont( GetFont().Bold().Larger() ); heading->Add( m_title, 1, wxALIGN_CENTER_VERTICAL );
    layout->Add( heading, 0, wxEXPAND | wxALL, gap );
    m_saved = new wxStaticText( this, wxID_ANY, wxEmptyString );
    layout->Add( m_saved, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, gap );
    m_rows = new DIAGRAM_HISTORY_ROWS( this ); layout->Add( m_rows, 1, wxEXPAND | wxLEFT | wxRIGHT, gap );
    auto* paging = new wxBoxSizer( wxHORIZONTAL ); m_count = new wxStaticText( this, wxID_ANY, wxEmptyString );
    paging->Add( m_count, 1, wxALIGN_CENTER_VERTICAL );
    m_more = new wxButton( this, wxID_ANY, _( "Load older" ), wxDefaultPosition, wxDefaultSize, wxBU_EXACTFIT );
    m_more->SetName( "DiagramHistoryOlder" ); paging->Add( m_more, 0 );
    layout->Add( paging, 0, wxEXPAND | wxALL, gap );
    m_details = new wxTextCtrl( this, wxID_ANY, wxEmptyString, wxDefaultPosition, FromDIP( wxSize( 250, 180 ) ), wxTE_MULTILINE | wxTE_READONLY );
    m_details->SetName( "DiagramHistoryComparison" ); m_details->SetMinSize( FromDIP( wxSize( 220, 100 ) ) );
    layout->Add( m_details, 1, wxEXPAND | wxLEFT | wxRIGHT, gap );
    m_failure = new wxStaticText( this, wxID_ANY, wxEmptyString ); m_failure->SetName( "DiagramHistoryError" );
    layout->Add( m_failure, 0, wxEXPAND | wxALL, gap );
    m_retry = new wxButton( this, wxID_ANY, _( "Retry" ) ); m_retry->SetName( "DiagramHistoryRetry" );
    layout->Add( m_retry, 0, wxLEFT | wxRIGHT | wxBOTTOM, gap );
    auto* actionsRow = new wxBoxSizer( wxHORIZONTAL );
    m_preview = new wxButton( this, wxID_ANY, _( "&Preview" ) ); m_preview->SetName( "DiagramHistoryPreview" );
    m_restore = new wxButton( this, wxID_ANY, _( "Restore as &draft" ) ); m_restore->SetName( "DiagramHistoryRestore" );
    actionsRow->Add( m_preview, 0, wxRIGHT, gap ); actionsRow->Add( m_restore, 1 );
    layout->Add( actionsRow, 0, wxEXPAND | wxALL, gap );
    m_return = new wxButton( this, wxID_ANY, _( "Return to &current" ) ); m_return->SetName( "DiagramHistoryReturn" );
    layout->Add( m_return, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, gap );
    SetSizer( layout );
    close->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { m_actions.close(); } );
    m_rows->Bind( wxEVT_LISTBOX, [this]( wxCommandEvent& ) { select(); } );
    m_rows->Bind( wxEVT_LISTBOX_DCLICK, [this]( wxCommandEvent& ) { select(); } );
    m_more->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { m_actions.load( LoadedCount() ); } );
    m_retry->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { m_actions.retry(); } );
    m_preview->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { if( auto selected = Inspected() ) m_actions.preview( *selected ); } );
    m_restore->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { if( auto selected = Inspected() ) m_actions.restore( *selected ); } );
    m_return->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { m_actions.returnToCurrent(); } );
    updateActions();
}

void PANEL_DIAGRAM_HISTORY::Begin( SELECTION context, const wxString& name, int version )
{
    m_context = std::move( context ); m_total = 0; m_rows->entries.clear(); m_rows->SetItemCount( 0 );
    m_title->SetLabel( name + wxS( " — " ) + _( "History" ) ); m_title->SetToolTip( m_title->GetLabel() );
    m_saved->SetLabel( wxString::Format( _( "Saved diagram: v%d" ), version ) );
    m_details->ChangeValue( wxEmptyString ); m_comparisonReady = false; m_previewing = false; SetBusy( true );
}

std::optional<PANEL_DIAGRAM_HISTORY::SELECTION> PANEL_DIAGRAM_HISTORY::Inspected() const
{
    int selected = m_rows->GetSelection();
    return selected < 0 || static_cast<size_t>( selected ) >= m_rows->entries.size() ? std::nullopt
        : std::optional<SELECTION>( m_rows->entries[selected].selection() );
}
unsigned PANEL_DIAGRAM_HISTORY::LoadedCount() const { return static_cast<unsigned>( m_rows->entries.size() ); }
void PANEL_DIAGRAM_HISTORY::SetBusy( bool busy ) { m_busy = busy; if( busy ) m_error.clear(); updateActions(); }
void PANEL_DIAGRAM_HISTORY::Fail( const wxString& message ) { m_busy = false; m_error = message.ToStdString( wxConvUTF8 ); updateActions(); }
void PANEL_DIAGRAM_HISTORY::SetPreviewing( bool preview ) { m_previewing = preview; updateActions(); }

bool PANEL_DIAGRAM_HISTORY::SetPage( const PAGE& page )
{
    bool valid = same( page.context(), m_context ) && page.offset() == LoadedCount() && page.total() >= LoadedCount()
        && page.entries_size() > 0 && page.entries_size() <= 200
        && static_cast<unsigned>( page.entries_size() ) <= page.total() - LoadedCount()
        && ( LoadedCount() == 0 || page.total() == m_total );
    std::set<std::string> ids;
    for( const auto& entry : m_rows->entries ) ids.insert( entry.selection().revision_id() );
    for( const auto& entry : page.entries() )
        valid = entry.selection().block_id() == m_context.block_id() && entry.selection().state_id() == m_context.state_id()
            && !entry.selection().revision_id().empty() && ids.insert( entry.selection().revision_id() ).second && valid;
    if( !valid ) { Fail( _( "This page does not match the open history." ) ); return false; }
    bool initial = m_rows->entries.empty(); m_total = page.total();
    m_rows->entries.insert( m_rows->entries.end(), page.entries().begin(), page.entries().end() );
    m_rows->SetItemCount( m_rows->entries.size() ); m_rows->Refresh(); m_busy = false; m_error.clear();
    if( initial ) { m_rows->SetSelection( 0 ); select(); } else updateActions();
    return true;
}

void PANEL_DIAGRAM_HISTORY::select()
{
    m_comparisonReady = false;
    if( auto selected = Inspected() ) { m_details->ChangeValue( _( "Loading comparison…" ) ); m_actions.inspect( *selected ); }
    updateActions();
}

bool PANEL_DIAGRAM_HISTORY::SetComparison( const COMPARISON& comparison )
{
    auto selected = Inspected();
    if( !selected || !same( comparison.context(), m_context ) || !same( comparison.inspected(), *selected ) )
    { Fail( _( "The comparison belongs to another diagram revision." ) ); return false; }
    wxString details = wxString::Format( _( "Revision v%u" ), comparison.inspected_version() ) + wxS( "\n" );
    details += text( comparison.inspected_origin().actor() ) + wxS( "\n" );
    if( comparison.inspected_origin().has_recorded_at() )
        details += wxDateTime( static_cast<time_t>( comparison.inspected_origin().recorded_at().seconds() ) )
            .Format( "%Y-%m-%d %H:%M UTC", wxDateTime::UTC ) + wxS( "\n" );
    if( !comparison.inspected_origin().summary().empty() ) details += wxS( "\n" ) + text( comparison.inspected_origin().summary() ) + wxS( "\n" );
    details += wxS( "\n" ) + wxString::Format( _( "Changes in saved v%u" ), comparison.context_version() ) + wxS( "\n" );
    if( comparison.changes_size() == 0 ) details += _( "No diagram content changes." );
    for( const auto& change : comparison.changes() )
    {
        wxString verb;
        switch( change.kind() )
        {
        case D::DHCK_ADDED: verb = _( "Added" ); break;
        case D::DHCK_REMOVED: verb = _( "Removed" ); break;
        case D::DHCK_REORDERED: verb = _( "Reordered" ); break;
        default: verb = _( "Changed" ); break;
        }
        wxString name = text( change.name() );
        switch( change.category() )
        {
        case D::DHCC_NAME: name = _( "Diagram name" ); break;
        case D::DHCC_REQUIREMENT:
            name = change.field() == D::RFK_GENERAL ? _( "General requirements" )
                : change.field() == D::RFK_SCHEMATIC ? _( "Schematic requirements" ) : _( "Routing requirements" ); break;
        case D::DHCC_BLOCK: if( name.empty() ) name = _( "Blocks" ); break;
        case D::DHCC_CONNECTION: if( name.empty() ) name = _( "Connections" ); break;
        case D::DHCC_INTERFACE: if( name.empty() ) name = _( "Interfaces" ); break;
        case D::DHCC_COMMENT: if( name.empty() ) name = _( "Comments" ); break;
        default: break;
        }
        details += verb + wxS( ": " ) + name + wxS( "\n" );
    }
    m_details->ChangeValue( details ); m_details->SetInsertionPoint( 0 );
    m_comparisonReady = true; m_busy = false; m_error.clear(); updateActions(); m_rows->SetFocus(); return true;
}

void PANEL_DIAGRAM_HISTORY::updateActions()
{
    bool selected = Inspected().has_value();
    m_rows->Enable( !m_busy ); m_more->Show( LoadedCount() < m_total ); m_more->Enable( !m_busy );
    m_count->SetLabel( m_busy ? _( "Loading…" ) : wxString::Format( _( "%u of %u revisions" ), LoadedCount(), m_total ) );
    m_count->Show( m_busy || m_total > 50 );
    m_failure->SetLabel( text( m_error ) ); m_failure->Wrap( std::max( FromDIP( 200 ), GetClientSize().x - FromDIP( 24 ) ) );
    m_failure->Show( !m_error.empty() ); m_retry->Show( !m_error.empty() ); m_retry->Enable( !m_busy );
    m_preview->Enable( selected && m_comparisonReady && !m_busy );
    m_restore->Enable( selected && m_comparisonReady && !m_busy && !same( *Inspected(), m_context ) );
    m_return->Show( m_previewing ); m_return->Enable( !m_busy ); Layout();
}
