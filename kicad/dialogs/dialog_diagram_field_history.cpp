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


namespace
{
/// An entry's revision label in two parts: the name of the earlier implementation it was saved in, when the label names
/// one ("Initial approach · v2"), and the version that follows it ("v2").
std::pair<wxString, wxString> NameAndVersion( const DIAGRAM_FIELD_HISTORY_ENTRY& aEntry )
{
    const wxString prefix = aEntry.implementation + wxS( " · " );
    if( !aEntry.implementation.IsEmpty() && aEntry.revisionLabel.StartsWith( prefix ) )
        return { aEntry.implementation, aEntry.revisionLabel.Mid( prefix.length() ) };
    return { wxEmptyString, aEntry.revisionLabel };
}


/// A history row in the same two parts: the earlier implementation's name, and the version, the saved marker and the author
/// ("v2 · Fixture user", "v3 · Saved · AI agent").
std::pair<wxString, wxString> RowParts( const DIAGRAM_FIELD_HISTORY_ENTRY& aEntry )
{
    auto [name, rest] = NameAndVersion( aEntry );
    // The saved marker comes before the author, so it stays visible when a long author name is cut.
    if( aEntry.saved ) rest += wxS( " · " ) + _( "Saved" );
    return { name, rest + wxS( " · " ) + aEntry.actor };
}


wxString Joined( const std::pair<wxString, wxString>& aParts )
{
    return aParts.first.IsEmpty() ? aParts.second : aParts.first + wxS( " · " ) + aParts.second;
}


/// "aName · aRest" no wider than aRoom pixels as aWidth measures it: whole when it fits; otherwise only aName is shortened
/// with "…" (or left out when not even "…" fits), so the version and author in aRest stay whole and readable (mockup audit
/// M1-3). Only when aRest alone is wider than aRoom does the control end it in "…". A room of 0 or less (not laid out yet)
/// keeps the whole text.
wxString FitName( const std::function<int( const wxString& )>& aWidth, const wxString& aName, const wxString& aRest, int aRoom )
{
    const wxString separator = wxS( " · " ), ellipsis = wxS( "…" );
    if( aName.IsEmpty() ) return aRest;
    const wxString whole = aName + separator + aRest;
    if( aRoom <= 0 || aWidth( whole ) <= aRoom ) return whole;
    wxString name = aName;
    while( !name.IsEmpty() && aWidth( name + ellipsis + separator + aRest ) > aRoom ) name.RemoveLast();
    name.Trim();
    if( !name.IsEmpty() ) return name + ellipsis + separator + aRest;
    if( aWidth( ellipsis + separator + aRest ) <= aRoom ) return ellipsis + separator + aRest;
    return aRest;
}
}


std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> DiagramFieldHistoryRows(
        const kiapi::automation::diagrams::v1::RecursiveBlockGraphData& aGraph,
        const kiapi::automation::diagrams::v1::FieldHistoryPageData& aPage, bool aConnection )
{
    // The name of the implementation a row was saved in, when that is not the implementation whose history is shown.
    auto earlierImplementation = [&]( const std::string& aContextRevision ) -> wxString
    {
        std::string stateId;
        if( aConnection )
        {
            for( const auto& archive : aGraph.connection_archives() )
                for( const auto& item : archive.revisions() )
                    if( item.selection().revision_id() == aContextRevision && item.selection().connection_id() == aPage.owner_id() )
                        stateId = item.selection().state_id();
            if( stateId.empty() || stateId == aPage.state_id() ) return wxEmptyString;
            for( const auto& archive : aGraph.connection_archives() )
                for( const auto& state : archive.states() )
                    if( state.id() == stateId ) return wxString::FromUTF8( state.name() );
            return wxEmptyString;
        }
        for( const auto& item : aGraph.revisions() )
            if( item.selection().revision_id() == aContextRevision && item.selection().block_id() == aPage.owner_id() )
                stateId = item.selection().state_id();
        if( stateId.empty() || stateId == aPage.state_id() ) return wxEmptyString;
        for( const auto& state : aGraph.states() )
            if( state.id() == stateId ) return wxString::FromUTF8( state.name() );
        return wxEmptyString;
    };
    std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> rows;
    for( const auto& entry : aPage.entries() )
    {
        // The same "name · version" form the implementation selector uses.
        wxString label = wxString::Format( "v%u", entry.context_version() );
        wxString name = earlierImplementation( entry.context_revision_id() );
        if( !name.IsEmpty() ) label = name + wxS( " · " ) + label;
        rows.push_back( { entry.requirement_revision_id(), label, wxString::FromUTF8( entry.origin().actor() ),
                          wxString::FromUTF8( entry.text() ), wxEmptyString, entry.is_saved_text(), name } );
    }
    return rows;
}


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
                              0, nullptr, wxLB_SINGLE );
    m_history->SetName( "DiagramFieldHistoryRevisions" );
    OptOut( m_history );
    m_history->SetMinSize( FromDIP( wxSize( 200, 200 ) ) );
    // A row wider than the list, such as "Initial approach · v2 · Fixture user", shortens only the earlier implementation's
    // name ("Initi… · v2 · Fixture user", fitRows), so every row's version and author stay readable; a row too wide even for
    // those ends in "…" instead of being cut off mid-word. The Use button names the implementation whole (mockup audit M1-3).
    m_rowsEllipsize = DIAGRAM_LOOK::EllipsizeRows( m_history );
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
    // The heading also names the selected row's author, which a narrow revision list can cut off. A heading too long for
    // its column shortens only an earlier implementation's name (fitHeading), so "vN · Author" always stays readable; the
    // end is cut only if even that does not fit.
    m_selectedHeading = new wxStaticText( this, wxID_ANY, wxEmptyString, wxDefaultPosition, wxDefaultSize,
                                          wxST_ELLIPSIZE_END );
    m_selectedHeading->SetName( "DiagramFieldHistorySelectedHeading" );
    m_selectedHeading->SetFont( GetFont().Bold() );
    // Shorten a long heading instead of widening the dialog.
    m_selectedHeading->SetMinSize( wxSize( FromDIP( 120 ), -1 ) );
    m_selectedHeading->Bind( wxEVT_SIZE, [this]( wxSizeEvent& event ) { fitHeading(); event.Skip(); } );
    texts->Add( m_selectedHeading, 0, wxEXPAND | wxBOTTOM, gap / 2 );
    m_selectedText = new wxTextCtrl( this, wxID_ANY, wxEmptyString, wxDefaultPosition,
            FromDIP( wxSize( 340, 130 ) ), wxTE_MULTILINE | wxTE_READONLY );
    m_selectedText->SetName( "DiagramFieldHistorySelectedText" );
    OptOut( m_selectedText );
    m_selectedText->SetMinSize( FromDIP( wxSize( 220, 70 ) ) );
    // The compared texts keep clear of their boxes' borders, as in the approved mockup (mockup audit M1-5).
    DIAGRAM_LOOK::PadTextBox( m_selectedText, FromDIP( 8 ), FromDIP( 6 ) );
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
    DIAGRAM_LOOK::PadTextBox( savedText, FromDIP( 8 ), FromDIP( 6 ) );
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
    // Whole at first; the next layout fits each row to the list (fitRows).
    for( const auto& entry : entries ) m_history->Append( Joined( RowParts( entry ) ) );
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


std::vector<wxString> DIALOG_DIAGRAM_FIELD_HISTORY::RowLabels() const
{
    std::vector<wxString> labels;
    for( const auto& entry : m_entries ) labels.push_back( Joined( RowParts( entry ) ) );
    return labels;
}


std::vector<wxString> DIALOG_DIAGRAM_FIELD_HISTORY::ShownRowLabels() const
{
    std::vector<wxString> labels;
    for( unsigned row = 0; row < m_history->GetCount(); ++row ) labels.push_back( m_history->GetString( row ) );
    return labels;
}


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
    // Using the text is the dialog's primary action, filled with the accent as the approved mockup shows it (mockup audit M1-4).
    DIAGRAM_LOOK::StylePrimary( m_restore, available );
    m_history->Enable( !m_entries.empty() );
    if( available )
    {
        const auto& entry = m_entries[selected];
        auto [name, version] = NameAndVersion( entry );
        m_headingImplementation = name;
        m_headingRest = wxString::Format( _( "%s · %s — Selected text" ), version, entry.actor );
        // The whole heading, until fitHeading shortens it to the laid-out column.
        // Set as plain text: an "&" in a name or an author is shown, not taken as a keyboard mnemonic.
        m_selectedHeading->SetLabelText( Joined( { m_headingImplementation, m_headingRest } ) );
        m_selectedHeading->SetToolTip( m_selectedHeading->GetLabelText() );
        m_selectedText->ChangeValue( entry.text );
        // Escaped like the heading: an "&" in an implementation's name is shown, not taken as a keyboard mnemonic.
        m_restore->SetLabel( wxControl::EscapeMnemonics( wxString::Format( _( "Use %s text in draft" ), entry.revisionLabel ) ) );
        m_source->Show( m_openSource && !entry.sourceDescription.IsEmpty() );
        m_source->SetToolTip( entry.sourceDescription );
    }
    else
    {
        m_headingImplementation.clear(); m_headingRest = _( "No field history available" );
        m_selectedHeading->SetLabelText( m_headingRest );
        m_selectedHeading->UnsetToolTip();
        m_selectedText->ChangeValue( wxEmptyString );
        m_source->Hide();
    }
    Layout();
}


bool DIALOG_DIAGRAM_FIELD_HISTORY::Layout()
{
    // Fitting after every layout, not only on the heading's own size event, keeps the heading right however the toolkit
    // orders a resize: the sizer has just given the heading the width it will be drawn at.
    bool laidOut = DIALOG_SHIM::Layout();
    if( m_history ) fitRows();
    if( m_selectedHeading ) fitHeading();
    return laidOut;
}


void DIALOG_DIAGRAM_FIELD_HISTORY::fitHeading()
{
    // The whole heading when it fits its column; otherwise the earlier implementation's name is shortened with "…" (or
    // left out when not even "…" fits) before anything of the version or the author is.
    wxString shown = FitName( [this]( const wxString& text ) { return m_selectedHeading->GetTextExtent( text ).x; },
                              m_headingImplementation, m_headingRest, m_selectedHeading->GetClientSize().x );
    if( m_selectedHeading->GetLabelText() != shown ) m_selectedHeading->SetLabelText( shown );
}


void DIALOG_DIAGRAM_FIELD_HISTORY::fitRows()
{
    // Each row as the heading is fitted: a row wider than the list shortens only the name of the earlier implementation it
    // was saved in, so its version and author are always shown whole (mockup audit M1-3; the approved rows read "v2 · User").
    // A row's text gets the list's width less GTK's frame, cell spacing, focus line and text padding (about 10 pixels),
    // with a few pixels to spare.
    const int room = m_history->GetClientSize().x - FromDIP( 16 );
    auto width = [this]( const wxString& text ) { return m_history->GetTextExtent( text ).x; };
    for( unsigned row = 0; row < m_entries.size() && row < m_history->GetCount(); ++row )
    {
        auto [name, rest] = RowParts( m_entries[row] );
        wxString shown = FitName( width, name, rest, room );
        if( m_history->GetString( row ) != shown ) m_history->SetString( row, shown );
    }
}
