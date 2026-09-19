/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#include "dialog_diagram_field_history.h"

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
    const int gap = FromDIP( 12 );
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
    m_history->SetMinSize( FromDIP( wxSize( 170, 200 ) ) );
    for( const auto& entry : m_entries )
    {
        wxString label = entry.revisionLabel + wxS( " · " ) + entry.actor;
        if( entry.saved ) label += wxS( " · " ) + _( "Saved" );
        m_history->Append( label );
    }
    comparison->Add( m_history, 0, wxEXPAND | wxRIGHT, gap );

    auto* texts = new wxBoxSizer( wxVERTICAL );
    m_selectedHeading = new wxStaticText( this, wxID_ANY, wxEmptyString );
    m_selectedHeading->SetFont( GetFont().Bold() );
    texts->Add( m_selectedHeading, 0, wxEXPAND | wxBOTTOM, gap / 2 );
    m_selectedText = new wxTextCtrl( this, wxID_ANY, wxEmptyString, wxDefaultPosition,
            FromDIP( wxSize( 340, 130 ) ), wxTE_MULTILINE | wxTE_READONLY );
    m_selectedText->SetName( "DiagramFieldHistorySelectedText" );
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

    if( !m_entries.empty() ) m_history->SetSelection( 0 );
    updateSelection();
    SetMinClientSize( FromDIP( wxSize( 590, 380 ) ) );
    SetClientSize( FromDIP( wxSize( 740, 520 ) ) );
    finishDialogSettings();
    m_history->SetFocus();
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
