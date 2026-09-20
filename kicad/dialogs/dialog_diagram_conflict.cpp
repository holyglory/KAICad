/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#include "dialog_diagram_conflict.h"
#include <algorithm>
#include <wx/button.h>
#include <wx/choice.h>
#include <wx/radiobut.h>
#include <wx/sizer.h>
#include <wx/stattext.h>
#include <wx/textctrl.h>

namespace D = kiapi::automation::diagrams::v1;
namespace
{
wxString text( const std::string& value ) { return wxString::FromUTF8( value ); }
wxString fieldName( D::RequirementFieldKind field )
{
    switch( field )
    {
    case D::RFK_GENERAL: return _( "General requirements" );
    case D::RFK_SCHEMATIC: return _( "Schematic requirements" );
    case D::RFK_ROUTING: return _( "Routing requirements" );
    default: return _( "Requirements" );
    }
}
}

DIALOG_DIAGRAM_CONFLICT::DIALOG_DIAGRAM_CONFLICT( wxWindow* parent, const wxString& owner,
        const D::RequirementMergeData& merge ) :
        DIALOG_SHIM( parent, wxID_ANY, _( "Resolve changes before saving" ), wxDefaultPosition,
                    wxDefaultSize, wxDEFAULT_DIALOG_STYLE | wxRESIZE_BORDER ), m_merge( merge )
{
    SetName( "DiagramSaveConflict" ); wxFont font = GetFont(); font.SetPointSize( std::max( 12, font.GetPointSize() ) ); SetFont( font );
    int gap = FromDIP( 16 ); auto* outer = new wxBoxSizer( wxVERTICAL );
    auto* title = new wxStaticText( this, wxID_ANY, _( "Resolve changes before saving" ) ); title->SetFont( GetFont().Bold().Larger() );
    outer->Add( title, 0, wxEXPAND | wxALL, gap );
    wxString scope = merge.conflicts_size() == 1 ? owner + wxS( " · " ) + fieldName( merge.conflicts( 0 ).field() ) : owner;
    auto* ownerText = new wxStaticText( this, wxID_ANY, scope, wxDefaultPosition, wxDefaultSize, wxST_ELLIPSIZE_MIDDLE );
    ownerText->SetToolTip( scope ); outer->Add( ownerText, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, gap );
    auto* context = new wxStaticText( this, wxID_ANY, wxString::Format(
        _( "Your draft is based on v%u. The saved diagram is now v%u." ), merge.base_context_version(), merge.saved_context_version() ) );
    outer->Add( context, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, gap );
    m_field = new wxChoice( this, wxID_ANY ); m_field->SetName( "DiagramConflictField" ); OptOut( m_field );
    for( const auto& conflict : merge.conflicts() ) m_field->Append( fieldName( conflict.field() ) );
    if( merge.conflicts_size() ) m_field->SetSelection( 0 );
    m_field->Show( merge.conflicts_size() > 1 );
    outer->Add( m_field, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, gap );
    auto addText = [&]( wxSizer* sizer, const wxString& label, const char* name, bool editable )
    {
        auto* heading = new wxStaticText( this, wxID_ANY, label, wxDefaultPosition, wxDefaultSize, wxST_ELLIPSIZE_END );
        heading->SetToolTip( label ); heading->SetFont( GetFont().Bold() );
        sizer->Add( heading, 0, wxEXPAND | wxBOTTOM, gap / 2 );
        auto* value = new wxTextCtrl( this, wxID_ANY, wxEmptyString, wxDefaultPosition,
                FromDIP( wxSize( 220, 90 ) ), wxTE_MULTILINE | ( editable ? 0 : wxTE_READONLY ) );
        value->SetName( name ); OptOut( value ); value->SetMinSize( FromDIP( wxSize( 180, 65 ) ) );
        sizer->Add( value, 1, wxEXPAND ); return value;
    };
    auto* base = new wxBoxSizer( wxVERTICAL );
    m_base = addText( base, wxString::Format( _( "Base — v%u" ), merge.base_context_version() ), "DiagramConflictBase", false );
    outer->Add( base, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, gap );
    auto* comparison = new wxBoxSizer( wxHORIZONTAL ); auto* mine = new wxBoxSizer( wxVERTICAL ); auto* latest = new wxBoxSizer( wxVERTICAL );
    m_draft = addText( mine, _( "Your draft" ), "DiagramConflictDraft", false );
    wxString latestLabel = wxString::Format( _( "Latest saved — v%u" ), merge.saved_context_version() );
    if( !merge.saved_origin().actor().empty() ) latestLabel += wxS( " · " ) + text( merge.saved_origin().actor() );
    m_saved = addText( latest, latestLabel, "DiagramConflictSaved", false );
    comparison->Add( mine, 1, wxEXPAND | wxRIGHT, gap ); comparison->Add( latest, 1, wxEXPAND );
    outer->Add( comparison, 1, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, gap );
    auto* options = new wxBoxSizer( wxHORIZONTAL );
    m_mine = new wxRadioButton( this, wxID_ANY, _( "&Use my text" ), wxDefaultPosition, wxDefaultSize, wxRB_SINGLE );
    m_latest = new wxRadioButton( this, wxID_ANY, _( "Use &saved text" ), wxDefaultPosition, wxDefaultSize, wxRB_SINGLE );
    m_custom = new wxRadioButton( this, wxID_ANY, _( "Write &merged text" ), wxDefaultPosition, wxDefaultSize, wxRB_SINGLE );
    m_mine->SetName( "DiagramConflictUseMine" ); m_latest->SetName( "DiagramConflictUseSaved" ); m_custom->SetName( "DiagramConflictWriteMerged" );
    for( auto* choice : { m_mine, m_latest, m_custom } ) { choice->SetValue( false ); OptOut( choice ); options->Add( choice, 1, wxRIGHT, gap / 2 ); }
    outer->Add( options, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, gap );
    auto* resolved = new wxBoxSizer( wxVERTICAL ); m_resolved = addText( resolved, _( "Resolved text" ), "DiagramConflictResolved", true );
    outer->Add( resolved, 1, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, gap );
    auto* actions = new wxBoxSizer( wxHORIZONTAL );
    auto* cancel = new wxButton( this, wxID_CANCEL, _( "Back to editing" ) ); cancel->SetName( "DiagramConflictBack" );
    m_save = new wxButton( this, wxID_OK, _( "Save resolved &version" ) ); m_save->SetName( "DiagramConflictSave" );
    actions->Add( cancel, 0 ); actions->AddStretchSpacer(); actions->Add( m_save, 0 ); outer->Add( actions, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, gap );
    SetSizer( outer );
    m_field->Bind( wxEVT_CHOICE, [this]( wxCommandEvent& ) { m_index = m_field->GetSelection(); displayField(); } );
    m_mine->Bind( wxEVT_RADIOBUTTON, [this]( wxCommandEvent& ) { choose( 1 ); } );
    m_latest->Bind( wxEVT_RADIOBUTTON, [this]( wxCommandEvent& ) { choose( 2 ); } );
    m_custom->Bind( wxEVT_RADIOBUTTON, [this]( wxCommandEvent& ) { choose( 3 ); } );
    m_resolved->Bind( wxEVT_TEXT, [this]( wxCommandEvent& )
    { if( !m_updating && m_index >= 0 && m_index < m_merge.conflicts_size() && m_custom->GetValue() ) m_choices[m_index] = m_resolved->GetValue().ToStdString( wxConvUTF8 ); } );
    m_save->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& )
    { if( m_choices.size() == static_cast<size_t>( m_merge.conflicts_size() ) && !m_choices.empty() ) EndModal( wxID_OK ); } );
    Bind( wxEVT_CHAR_HOOK, [this]( wxKeyEvent& event )
    { if( event.GetKeyCode() == WXK_ESCAPE ) { EndModal( wxID_CANCEL ); return; } event.StopPropagation(); event.Skip(); } );
    displayField(); finishDialogSettings();
    wxSize preferred = FromDIP( wxSize( 730, 650 ) ); preferred.IncTo( GetSizer()->CalcMin() ); SetClientSize( preferred );
}

void DIALOG_DIAGRAM_CONFLICT::displayField()
{
    m_updating = true; bool valid = m_index >= 0 && m_index < m_merge.conflicts_size();
    m_field->Enable( m_merge.conflicts_size() > 1 );
    if( valid )
    {
        const auto& conflict = m_merge.conflicts( m_index );
        m_base->ChangeValue( text( conflict.baseline() ) ); m_draft->ChangeValue( text( conflict.draft() ) ); m_saved->ChangeValue( text( conflict.saved() ) );
        int selected = m_choiceKinds.count( m_index ) ? m_choiceKinds.at( m_index ) : 0;
        m_mine->SetValue( selected == 1 ); m_latest->SetValue( selected == 2 ); m_custom->SetValue( selected == 3 );
        m_resolved->ChangeValue( m_choices.count( m_index ) ? text( m_choices.at( m_index ) ) : wxString() );
        m_resolved->Enable( selected != 0 ); m_resolved->SetEditable( selected == 3 );
    }
    for( auto* control : { m_mine, m_latest, m_custom } ) control->Enable( valid );
    m_save->Enable( valid && m_choices.size() == static_cast<size_t>( m_merge.conflicts_size() ) );
    m_updating = false; Layout();
}
void DIALOG_DIAGRAM_CONFLICT::choose( int kind )
{
    if( m_index < 0 || m_index >= m_merge.conflicts_size() ) return;
    const auto& conflict = m_merge.conflicts( m_index );
    m_choiceKinds[m_index] = kind;
    m_choices[m_index] = kind == 1 ? conflict.draft() : kind == 2 ? conflict.saved() : m_choices.count( m_index ) ? m_choices.at( m_index ) : conflict.draft();
    displayField(); if( kind == 3 ) m_resolved->SetFocus();
}
std::vector<D::RequirementResolutionData> DIALOG_DIAGRAM_CONFLICT::Resolutions() const
{
    std::vector<D::RequirementResolutionData> result;
    for( const auto& [index, value] : m_choices )
    {
        D::RequirementResolutionData choice;
        choice.set_owner_id( m_merge.original_draft().baseline().block_id() ); choice.set_state_id( m_merge.original_draft().baseline().state_id() );
        choice.set_baseline_revision_id( m_merge.original_draft().baseline_requirement_revision_id() );
        choice.set_saved_revision_id( m_merge.saved_draft().baseline_requirement_revision_id() );
        *choice.mutable_baseline() = m_merge.original_draft().baseline_fields(); *choice.mutable_draft() = m_merge.original_draft().fields();
        *choice.mutable_saved() = m_merge.saved_draft().fields(); choice.set_field( m_merge.conflicts( index ).field() ); choice.set_text( value );
        result.push_back( std::move( choice ) );
    }
    return result;
}
