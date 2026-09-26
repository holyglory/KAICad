/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#include "dialog_diagram_field_history.h"

#include <algorithm>
#include <set>
#include <utility>
#include <wx/button.h>
#include <wx/arrstr.h>
#include <wx/dc.h>
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


/// The lines aText takes in aRoom pixels as aWidth measures it: broken between words, and inside a word only when the word
/// alone is wider than aRoom, so nothing of it is cut (mockup audit M1-12).
std::vector<wxString> Wrapped( const std::function<int( const wxString& )>& aWidth, const wxString& aText, int aRoom )
{
    std::vector<wxString> lines;
    wxString line;
    auto place = [&]( wxString word )
    {
        // A word wider than the room on its own is broken where the room ends, at least one character a line.
        while( aWidth( word ) > aRoom && word.length() > 1 )
        {
            size_t keep = word.length() - 1;
            while( keep > 1 && aWidth( word.Left( keep ) ) > aRoom ) --keep;
            lines.push_back( word.Left( keep ) ); word = word.Mid( keep );
        }
        line = word;
    };
    for( const wxString& word : wxSplit( aText, ' ', '\0' ) )
    {
        if( word.IsEmpty() ) continue;
        if( line.IsEmpty() ) { place( word ); continue; }
        if( aWidth( line + wxS( " " ) + word ) <= aRoom ) { line += wxS( " " ) + word; continue; }
        lines.push_back( line ); place( word );
    }
    if( !line.IsEmpty() || lines.empty() ) lines.push_back( line );
    return lines;
}


/// aText ending in "…" where it is wider than aRoom pixels as aWidth measures it (at least its first character stays).
wxString EndEllipsized( const std::function<int( const wxString& )>& aWidth, const wxString& aText, int aRoom )
{
    if( aWidth( aText ) <= aRoom ) return aText;
    wxString kept = aText;
    while( kept.length() > 1 && aWidth( wxString( kept ).Trim() + wxS( "…" ) ) > aRoom ) kept.RemoveLast();
    return wxString( kept ).Trim() + wxS( "…" );
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


DIAGRAM_HISTORY_ROW_LAYOUT LayHistoryRow( const wxWindow* aList, const wxRect& aBounds, const DIAGRAM_HISTORY_ROW& aRow )
{
    // The approved diagram history's row: 10 DIP in from the sides and 6 from the top, the version in bold, its lines 50 DIP in
    // (or 12 past a wider version), 3 DIP apart.
    DIAGRAM_HISTORY_ROW_LAYOUT layout;
    const wxFont normal = aList->GetFont(), bold = normal.Bold();
    auto width = [&]( const wxString& text ) { int x = 0, y = 0; aList->GetTextExtent( text, &x, &y, nullptr, nullptr, &normal ); return x; };
    wxRect inner = aBounds; inner.Deflate( aList->FromDIP( 10 ), aList->FromDIP( 6 ) );
    int versionWidth = 0, versionHeight = 0;
    aList->GetTextExtent( aRow.version, &versionWidth, &versionHeight, nullptr, nullptr, &bold );
    layout.version = aRow.version; layout.versionRect = wxRect( inner.GetTopLeft(), wxSize( versionWidth, versionHeight ) );
    const int inset = std::max( aList->FromDIP( 50 ), versionWidth + aList->FromDIP( 12 ) );
    const int room = std::max( 1, inner.width - inset ), height = aList->GetCharHeight(), pitch = height + aList->FromDIP( 3 );
    layout.lines = Wrapped( width, aRow.first, room );
    if( !aRow.second.IsEmpty() ) layout.lines.push_back( EndEllipsized( width, aRow.second, room ) );
    for( size_t k = 0; k < layout.lines.size(); ++k )
        layout.lineRects.emplace_back( inner.x + inset, inner.y + static_cast<int>( k ) * pitch, width( layout.lines[k] ), height );
    layout.fits = inset <= inner.width && versionWidth + aList->FromDIP( 12 ) <= inset && inner.Contains( layout.versionRect.GetTopLeft() );
    for( const wxRect& line : layout.lineRects )
        layout.fits = layout.fits && line.GetRight() <= inner.GetRight() + 1 && line.GetBottom() <= aBounds.GetBottom();
    return layout;
}


int HistoryRowHeight( const wxWindow* aList, int aWidth, const DIAGRAM_HISTORY_ROW& aRow )
{
    const size_t lines = std::max<size_t>( 2, LayHistoryRow( aList, wxRect( 0, 0, aWidth, 10000 ), aRow ).lines.size() );
    const int count = static_cast<int>( lines );
    return count * aList->GetCharHeight() + ( count - 1 ) * aList->FromDIP( 3 ) + aList->FromDIP( 15 );
}


void DrawHistoryRow( wxDC& aDC, const wxWindow* aList, const DIAGRAM_HISTORY_ROW_LAYOUT& aLayout, bool aSelected )
{
    aDC.SetTextForeground( wxSystemSettings::GetColour( aSelected ? wxSYS_COLOUR_HIGHLIGHTTEXT : wxSYS_COLOUR_WINDOWTEXT ) );
    aDC.SetFont( aList->GetFont().Bold() ); aDC.DrawText( aLayout.version, aLayout.versionRect.GetTopLeft() );
    aDC.SetFont( aList->GetFont() );
    for( size_t k = 0; k < aLayout.lines.size(); ++k ) aDC.DrawText( aLayout.lines[k], aLayout.lineRects[k].GetTopLeft() );
}


DIAGRAM_HISTORY_LIST::DIAGRAM_HISTORY_LIST( wxWindow* aParent, const wxString& aName ) : wxVListBox( aParent, wxID_ANY )
{
    SetName( aName );
    // A row's height follows the list's width (its lines wrap), so every width change measures the rows again.
    Bind( wxEVT_SIZE, [this]( wxSizeEvent& aEvent )
    {
        if( GetClientSize().x != m_width ) { m_width = GetClientSize().x; RefreshAll(); }
        aEvent.Skip();
    } );
}


void DIAGRAM_HISTORY_LIST::SetRows( std::vector<DIAGRAM_HISTORY_ROW> aRows )
{
    m_rows = std::move( aRows ); SetItemCount( m_rows.size() ); RefreshAll();
}


wxRect DIAGRAM_HISTORY_LIST::RowRect( size_t aIndex ) const { return GetItemRect( aIndex ); }


DIAGRAM_HISTORY_ROW_LAYOUT DIAGRAM_HISTORY_LIST::RowLayout( size_t aIndex ) const
{
    wxRect row = GetItemRect( aIndex );
    if( row.IsEmpty() ) row = wxRect( 0, 0, GetClientSize().x, OnMeasureItem( aIndex ) );
    return LayHistoryRow( this, row, m_rows[aIndex] );
}


wxCoord DIAGRAM_HISTORY_LIST::OnMeasureItem( size_t aIndex ) const
{
    return aIndex < m_rows.size() ? HistoryRowHeight( this, GetClientSize().x, m_rows[aIndex] ) : GetCharHeight() * 2 + FromDIP( 18 );
}


void DIAGRAM_HISTORY_LIST::OnDrawItem( wxDC& aDC, const wxRect& aRect, size_t aIndex ) const
{
    if( aIndex < m_rows.size() ) DrawHistoryRow( aDC, this, LayHistoryRow( this, aRect, m_rows[aIndex] ), IsSelected( aIndex ) );
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
    // The rows use the approved diagram history's two-line pattern: the version in bold, beside it the saved marker and the
    // author, then the earlier implementation the row was saved in. The saved marker and the author continue on the next line
    // rather than being cut, so every row's version, saved marker and author stay whole at every size (mockup audit M1-3 and
    // M1-12). The list keeps its 200 DIP, which leaves the compact dialog's heading room for its version and author. The
    // heading and the Use button name the implementation whole.
    m_history = new DIAGRAM_HISTORY_LIST( this, "DiagramFieldHistoryRevisions" );
    OptOut( m_history );
    m_history->SetMinSize( FromDIP( wxSize( 200, 200 ) ) );
    showRows();
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


void DIALOG_DIAGRAM_FIELD_HISTORY::showRows()
{
    std::vector<DIAGRAM_HISTORY_ROW> rows;
    for( const auto& entry : m_entries )
    {
        auto [name, version] = NameAndVersion( entry );
        rows.push_back( { version, entry.saved ? _( "Saved" ) + wxS( " · " ) + entry.actor : entry.actor, name } );
    }
    m_history->SetRows( std::move( rows ) );
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
    for( size_t row = 0; row < m_history->GetItemCount(); ++row )
    {
        const DIAGRAM_HISTORY_ROW_LAYOUT drawn = m_history->RowLayout( row );
        wxString label = drawn.version;
        for( const wxString& line : drawn.lines ) label += wxS( "\n" ) + line;
        labels.push_back( label );
    }
    return labels;
}


std::vector<DIALOG_DIAGRAM_FIELD_HISTORY::SHOWN_ROW> DIALOG_DIAGRAM_FIELD_HISTORY::ShownRows() const
{
    std::vector<SHOWN_ROW> rows;
    const wxRect visible( wxPoint( 0, 0 ), m_history->GetClientSize() );
    auto screen = [this]( wxRect rect ) { rect.SetPosition( m_history->ClientToScreen( rect.GetPosition() ) ); return rect; };
    for( size_t index = 0; index < m_history->GetItemCount(); ++index )
    {
        const wxRect row = m_history->RowRect( index );
        const DIAGRAM_HISTORY_ROW_LAYOUT drawn = m_history->RowLayout( index );
        SHOWN_ROW shown;
        shown.version = drawn.version; shown.lines = drawn.lines;
        shown.shown = !row.IsEmpty() && visible.Contains( row );
        // Whole: every line inside its row, and the row as wide as the list's visible width (nothing under a scroll bar).
        shown.fits = drawn.fits && ( row.IsEmpty() || ( row.x >= 0 && row.GetRight() <= visible.GetRight() ) );
        shown.rect = screen( row.IsEmpty() ? wxRect( 0, 0, visible.width, 0 ) : row );
        shown.versionRect = screen( drawn.versionRect );
        for( const wxRect& line : drawn.lineRects ) shown.lineRects.push_back( screen( line ) );
        rows.push_back( std::move( shown ) );
    }
    return rows;
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
    m_history->Freeze();
    m_entries.insert( m_entries.end(), entries.begin(), entries.end() );
    showRows();
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
        // The list draws its rows itself; assistive technology reads the selected row whole from the list's name.
        DIAGRAM_LOOK::NameForAssistiveTechnology( m_history, Joined( RowParts( entry ) ) );
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
