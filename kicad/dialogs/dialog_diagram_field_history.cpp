/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#include "dialog_diagram_field_history.h"

#include <algorithm>
#include <set>
#include <utility>
#include <wx/button.h>
#include <wx/listbox.h>
#include <wx/sizer.h>
#include <wx/statline.h>
#include <wx/stattext.h>
#include <wx/textctrl.h>


DIALOG_DIAGRAM_FIELD_HISTORY::DIALOG_DIAGRAM_FIELD_HISTORY( wxWindow* aParent,
        const wxString& aFieldLabel, const wxString& aOwnerPath,
        const wxString& aSavedRevisionLabel, const wxString& aSavedText,
        std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> aEntries,
        std::function<void( const std::string& )> aOpenSource ) :
        DIALOG_SHIM( aParent, wxID_ANY, _( "Requirement history" ), wxDefaultPosition,
                    wxDefaultSize, wxDEFAULT_DIALOG_STYLE | wxRESIZE_BORDER ),
        m_entries( std::move( aEntries ) ), m_openSource( std::move( aOpenSource ) )
{
    SetName( "DiagramFieldHistory" );
    wxFont bodyFont = GetFont();
    bodyFont.SetPointSize( std::max( 12, bodyFont.GetPointSize() ) );
    SetFont( bodyFont );
    const int gap = FromDIP( 18 );
    auto* outer = new wxBoxSizer( wxVERTICAL );
    auto* heading = new wxStaticText( this, wxID_ANY,
            wxString::Format( _( "%s — History" ), aFieldLabel ) );
    wxFont headingFont = GetFont();
    headingFont.SetWeight( wxFONTWEIGHT_BOLD );
    headingFont.SetPointSize( headingFont.GetPointSize() + 2 );
    heading->SetFont( headingFont );
    outer->Add( heading, 0, wxEXPAND | wxLEFT | wxRIGHT | wxTOP, gap );
    auto* context = new wxStaticText( this, wxID_ANY,
            wxString::Format( _( "%s · Saved diagram %s" ), aOwnerPath, aSavedRevisionLabel ),
            wxDefaultPosition, wxDefaultSize, wxST_ELLIPSIZE_MIDDLE );
    context->SetToolTip( context->GetLabel() );
    outer->Add( context, 0, wxEXPAND | wxALL, gap );

    auto* comparison = new wxBoxSizer( wxHORIZONTAL );
    m_history = new wxListBox( this, wxID_ANY, wxDefaultPosition, FromDIP( wxSize( 200, 300 ) ),
                              0, nullptr, wxLB_SINGLE | wxLB_HSCROLL );
    m_history->SetName( "DiagramFieldHistoryRevisions" );
    OptOut( m_history );
    m_history->SetMinSize( FromDIP( wxSize( 200, 200 ) ) );
    appendRows( m_entries );
    auto* revisionColumn = new wxBoxSizer( wxVERTICAL );
    revisionColumn->Add( m_history, 1, wxEXPAND );
    m_pageStatus = new wxStaticText( this, wxID_ANY, wxEmptyString );
    m_pageStatus->SetName( "DiagramFieldHistoryPageStatus" );
    revisionColumn->Add( m_pageStatus, 0, wxTOP, gap / 2 );
    m_pageError = new wxStaticText( this, wxID_ANY, wxEmptyString, wxDefaultPosition,
                                  FromDIP( wxSize( 200, -1 ) ), wxST_NO_AUTORESIZE );
    m_pageError->SetName( "DiagramFieldHistoryPageError" );
    revisionColumn->Add( m_pageError, 0, wxEXPAND | wxTOP, gap / 2 );
    m_older = new wxButton( this, wxID_ANY, _( "Load &older" ) );
    m_older->SetName( "DiagramFieldHistoryOlder" );
    revisionColumn->Add( m_older, 0, wxEXPAND | wxTOP, gap / 2 );
    comparison->Add( revisionColumn, 0, wxEXPAND | wxRIGHT, gap );

    auto* texts = new wxBoxSizer( wxVERTICAL );
    m_selectedHeading = new wxStaticText( this, wxID_ANY, wxEmptyString );
    m_selectedHeading->SetFont( GetFont().Bold() );
    texts->Add( m_selectedHeading, 0, wxEXPAND | wxBOTTOM, gap / 2 );
    m_selectedText = new wxTextCtrl( this, wxID_ANY, wxEmptyString, wxDefaultPosition,
            FromDIP( wxSize( 340, 130 ) ), wxTE_MULTILINE | wxTE_READONLY );
    m_selectedText->SetName( "DiagramFieldHistorySelectedText" );
    OptOut( m_selectedText );
    m_selectedText->SetMinSize( FromDIP( wxSize( 220, 70 ) ) );
    texts->Add( m_selectedText, 1, wxEXPAND );
    m_source = new wxButton( this, wxID_ANY, _( "View source instruction" ),
                            wxDefaultPosition, wxDefaultSize, wxBU_EXACTFIT );
    m_source->SetName( "DiagramFieldHistorySource" );
    texts->Add( m_source, 0, wxTOP | wxBOTTOM, gap / 2 );
    texts->Add( new wxStaticLine( this ), 0, wxEXPAND | wxTOP | wxBOTTOM, gap / 2 );
    auto* savedHeading = new wxStaticText( this, wxID_ANY,
            wxString::Format( _( "%s — Saved text" ), aSavedRevisionLabel ) );
    savedHeading->SetFont( GetFont().Bold() );
    texts->Add( savedHeading, 0, wxEXPAND | wxBOTTOM, gap / 2 );
    auto* savedText = new wxTextCtrl( this, wxID_ANY, aSavedText, wxDefaultPosition,
            FromDIP( wxSize( 340, 130 ) ), wxTE_MULTILINE | wxTE_READONLY );
    savedText->SetName( "DiagramFieldHistorySavedText" );
    OptOut( savedText );
    savedText->SetMinSize( FromDIP( wxSize( 220, 70 ) ) );
    texts->Add( savedText, 1, wxEXPAND );
    comparison->Add( texts, 1, wxEXPAND );
    outer->Add( comparison, 1, wxEXPAND | wxLEFT | wxRIGHT, gap );

    auto* actions = new wxBoxSizer( wxHORIZONTAL );
    actions->AddStretchSpacer();
    auto* close = new wxButton( this, wxID_CANCEL, _( "Close" ) );
    close->SetName( "DiagramFieldHistoryClose" );
    actions->Add( close, 0, wxRIGHT, gap );
    m_restore = new wxButton( this, wxID_OK, _( "Use selected text in draft" ) );
    m_restore->SetName( "DiagramFieldHistoryRestore" );
    m_restore->SetDefault();
    actions->Add( m_restore, 0 );
    outer->Add( actions, 0, wxEXPAND | wxALL, gap );
    SetSizer( outer );

    m_history->Bind( wxEVT_LISTBOX, [this]( wxCommandEvent& ) { updateSelection(); } );
    // A double-click is inspection, not permission to replace a draft field.
    m_history->Bind( wxEVT_LISTBOX_DCLICK, [this]( wxCommandEvent& ) { updateSelection(); } );
    m_older->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& )
    {
        if( !m_loadOlder || m_loading || m_entries.size() >= m_total ) return;
        // Keep keyboard navigation and Escape on a live control while the
        // triggering button is disabled (and later hidden on the last page).
        m_history->SetFocus();
        m_loading = true; m_pageError->SetLabel( wxEmptyString ); updatePaging();
        m_loadOlder( m_entries.size() );
    } );
    m_source->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& )
    {
        int selected = m_history->GetSelection();
        if( m_openSource && selected != wxNOT_FOUND && !m_entries[selected].sourceDescription.IsEmpty() )
            m_openSource( m_entries[selected].revisionId );
    } );
    m_restore->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& )
    {
        int selected = m_history->GetSelection();
        if( selected == wxNOT_FOUND ) return;
        m_restoreRevision = m_entries[selected].revisionId;
        EndModal( wxID_OK );
    } );
    Bind( wxEVT_CHAR_HOOK, [this]( wxKeyEvent& event )
    {
        if( event.GetKeyCode() == WXK_ESCAPE ) { EndModal( wxID_CANCEL ); return; }
        if( event.ControlDown() && ( event.GetKeyCode() == 'S' || event.GetKeyCode() == 'Z'
                                    || event.GetKeyCode() == 'Y' ) ) return;
        event.StopPropagation(); event.Skip();
    } );
    Bind( wxEVT_SHOW, [this]( wxShowEvent& event )
    {
        if( event.IsShown() ) m_restoreRevision.reset();
        event.Skip();
    } );

    if( !m_entries.empty() ) m_history->SetSelection( 0 );
    m_total = m_entries.size(); updatePaging();
    updateSelection();
    finishDialogSettings();
    wxSize minimum = GetSizer()->CalcMin();
    minimum.IncTo( FromDIP( wxSize( 590, 440 ) ) );
    SetMinClientSize( minimum );
    wxSize preferred = FromDIP( wxSize( 740, 520 ) );
    preferred.IncTo( minimum );
    SetClientSize( preferred );
    m_history->SetFocus();
}


void DIALOG_DIAGRAM_FIELD_HISTORY::appendRows( const std::vector<DIAGRAM_FIELD_HISTORY_ENTRY>& entries )
{
    for( const auto& entry : entries )
    {
        // The saved marker must remain visible when a long actor name scrolls.
        wxString label = entry.revisionLabel;
        if( entry.saved ) label += wxS( " · " ) + _( "Saved" );
        label += wxS( " · " ) + entry.actor;
        m_history->Append( label );
    }
}


const DIAGRAM_FIELD_HISTORY_ENTRY* DIALOG_DIAGRAM_FIELD_HISTORY::RestoredEntry() const
{
    if( m_restoreRevision )
        for( const auto& entry : m_entries )
            if( entry.revisionId == *m_restoreRevision ) return &entry;
    return nullptr;
}


std::string DIALOG_DIAGRAM_FIELD_HISTORY::InspectedRevision() const
{
    int selected = m_history->GetSelection();
    return selected == wxNOT_FOUND ? "" : m_entries[selected].revisionId;
}


wxString DIALOG_DIAGRAM_FIELD_HISTORY::PageError() const { return m_pageError->GetLabel(); }


void DIALOG_DIAGRAM_FIELD_HISTORY::ConfigurePaging( size_t total, std::function<void( size_t )> loadOlder )
{
    m_showPageCount = total > m_entries.size();
    m_total = std::max( total, m_entries.size() ); m_loadOlder = std::move( loadOlder ); updatePaging();
}


bool DIALOG_DIAGRAM_FIELD_HISTORY::AppendPage( size_t offset, size_t total,
                                              std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> entries )
{
    bool valid = m_loading && offset == m_entries.size() && total == m_total
                 && !entries.empty() && entries.size() <= 200 && entries.size() <= total - offset;
    std::set<std::string> ids;
    for( const auto& row : m_entries ) ids.insert( row.revisionId );
    for( const auto& row : entries ) valid = !row.revisionId.empty() && ids.insert( row.revisionId ).second && valid;
    if( !valid ) { PageFailed( _( "Older changes did not match this history. Try again." ) ); return false; }
    int selected = m_history->GetSelection();
    m_history->Freeze(); appendRows( entries );
    m_entries.insert( m_entries.end(), entries.begin(), entries.end() );
    m_history->SetSelection( selected ); m_history->Thaw();
    m_loading = false; m_pageError->SetLabel( wxEmptyString ); updatePaging();
    return true;
}


void DIALOG_DIAGRAM_FIELD_HISTORY::PageFailed( const wxString& message )
{
    m_loading = false; m_pageError->SetLabel( message );
    m_pageError->Wrap( FromDIP( 200 ) ); updatePaging();
}


void DIALOG_DIAGRAM_FIELD_HISTORY::updatePaging()
{
    bool more = m_entries.size() < m_total;
    m_pageStatus->SetLabel( wxString::Format( _( "%zu of %zu changes" ), m_entries.size(), m_total ) );
    m_pageStatus->Show( m_showPageCount );
    m_pageError->Show( !m_pageError->GetLabel().IsEmpty() );
    m_older->Show( more ); m_older->Enable( more && m_loadOlder && !m_loading );
    m_older->SetLabel( m_loading ? _( "Loading…" ) : _( "Load &older" ) );
    Layout();
}


void DIALOG_DIAGRAM_FIELD_HISTORY::updateSelection()
{
    int selected = m_history->GetSelection();
    const bool available = selected != wxNOT_FOUND;
    m_restore->Enable( available );
    m_history->Enable( !m_entries.empty() );
    if( available )
    {
        const auto& entry = m_entries[selected];
        m_selectedHeading->SetLabel( wxString::Format( _( "%s — Selected text" ), entry.revisionLabel ) );
        m_selectedText->ChangeValue( entry.text );
        m_restore->SetLabel( wxString::Format( _( "Use %s text in draft" ), entry.revisionLabel ) );
        m_source->Show( m_openSource && !entry.sourceDescription.IsEmpty() );
        m_source->SetToolTip( entry.sourceDescription );
    }
    else
    {
        m_selectedHeading->SetLabel( _( "No field history available" ) );
        m_selectedText->ChangeValue( wxEmptyString );
        m_source->Hide();
    }
    Layout();
}
