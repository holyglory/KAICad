/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#ifndef KICAD_DIALOG_DIAGRAM_FIELD_HISTORY_H
#define KICAD_DIALOG_DIAGRAM_FIELD_HISTORY_H

#include <api/common/types/diagram_revision_types.pb.h>
#include <dialog_shim.h>
#include <algorithm>
#include <cmath>
#include <functional>
#include <optional>
#include <string>
#include <utility>
#include <vector>
#include <wx/button.h>
#include <wx/colour.h>
#include <wx/settings.h>
#include <wx/textctrl.h>
#include <wx/vlbox.h>
#if defined( __WXGTK__ )
#include <dlfcn.h>
#endif

class wxStaticText;

/** Colour and text-box helpers shared by the per-level editor and its history and conflict dialogs. The dialogs are also
 * built without the editor (into qa_diagram_field_history), so the helpers live here, inline. */
namespace DIAGRAM_LOOK
{
inline double Channel( unsigned char aValue )
{
    double v = aValue / 255.0;
    return v <= 0.03928 ? v / 12.92 : std::pow( ( v + 0.055 ) / 1.055, 2.4 );
}
inline double Luminance( const wxColour& aColour )
{
    return 0.2126 * Channel( aColour.Red() ) + 0.7152 * Channel( aColour.Green() ) + 0.0722 * Channel( aColour.Blue() );
}
/// The WCAG 2 contrast ratio of two colours, from 1 to 21.
inline double Contrast( const wxColour& aA, const wxColour& aB )
{
    double x = Luminance( aA ), y = Luminance( aB );
    return ( std::max( x, y ) + 0.05 ) / ( std::min( x, y ) + 0.05 );
}
struct HSL { double h = 0, s = 0, l = 0; };
inline HSL ToHsl( const wxColour& aColour )
{
    double r = aColour.Red() / 255.0, g = aColour.Green() / 255.0, b = aColour.Blue() / 255.0;
    double high = std::max( { r, g, b } ), low = std::min( { r, g, b } );
    HSL result; result.l = ( high + low ) / 2;
    if( high == low ) return result;
    double d = high - low;
    result.s = result.l > 0.5 ? d / ( 2 - high - low ) : d / ( high + low );
    if( high == r ) result.h = ( g - b ) / d + ( g < b ? 6 : 0 );
    else if( high == g ) result.h = ( b - r ) / d + 2;
    else result.h = ( r - g ) / d + 4;
    result.h /= 6;
    return result;
}
inline wxColour FromHsl( const HSL& aHsl )
{
    auto hue = []( double p, double q, double t )
    {
        if( t < 0 ) t += 1;
        if( t > 1 ) t -= 1;
        if( t < 1.0 / 6 ) return p + ( q - p ) * 6 * t;
        if( t < 0.5 ) return q;
        if( t < 2.0 / 3 ) return p + ( q - p ) * ( 2.0 / 3 - t ) * 6;
        return p;
    };
    double l = std::clamp( aHsl.l, 0.0, 1.0 ), r = l, g = l, b = l;
    if( aHsl.s > 0 )
    {
        double q = l < 0.5 ? l * ( 1 + aHsl.s ) : l + aHsl.s - l * aHsl.s, p = 2 * l - q;
        r = hue( p, q, aHsl.h + 1.0 / 3 ); g = hue( p, q, aHsl.h ); b = hue( p, q, aHsl.h - 1.0 / 3 );
    }
    auto byte = []( double v ) { return static_cast<unsigned char>( std::lround( std::clamp( v, 0.0, 1.0 ) * 255 ) ); };
    return wxColour( byte( r ), byte( g ), byte( b ) );
}
/// A filled accent control on aSurface: a fill with its hue that stands 3:1 or more from the surface, and a label
/// colour (white where the fill allows it, otherwise near-black) with 4.5:1 or more on the fill.
struct ACCENT { wxColour fill, text; };
inline ACCENT AccentFill( const wxColour& aAccent, const wxColour& aSurface )
{
    const wxColour white( 255, 255, 255 ), ink( 26, 26, 26 );
    HSL base = ToHsl( aAccent );
    std::optional<ACCENT> inked;
    for( int step = 0; step <= 100; ++step )
        for( int sign : { 1, -1 } )
        {
            if( step == 0 && sign < 0 ) continue;
            HSL moved = base; moved.l += sign * step * 0.01;
            if( moved.l < 0.1 || moved.l > 0.9 ) continue;
            wxColour fill = FromHsl( moved );
            // 3:1 with a little to spare, so the rendered fill never lands just under it.
            if( Contrast( fill, aSurface ) < 3.2 ) continue;
            if( Contrast( white, fill ) >= 4.5 ) return { fill, white };
            if( !inked && Contrast( ink, fill ) >= 4.5 ) inked = ACCENT{ fill, ink };
        }
    if( inked ) return *inked;
    return { aAccent, Contrast( white, aAccent ) >= Contrast( ink, aAccent ) ? white : ink };
}
/// The primary action of a dialog or panel, styled like the editor's Save (design QA P2-8; mockup audit M1-4, the approved
/// history and conflict mockups): while it is available, an accent fill with a label of 4.5:1 or more on it; unavailable,
/// the theme's own disabled look.
inline void StylePrimary( wxButton* aButton, bool aAvailable )
{
    aButton->Enable( aAvailable );
    if( aAvailable )
    {
        ACCENT fill = AccentFill( wxSystemSettings::GetColour( wxSYS_COLOUR_HIGHLIGHT ), aButton->GetParent()->GetBackgroundColour() );
        aButton->SetBackgroundColour( fill.fill ); aButton->SetForegroundColour( fill.text );
    }
    else { aButton->SetBackgroundColour( wxNullColour ); aButton->SetForegroundColour( wxNullColour ); }
    aButton->Refresh();
}
/// Keeps a text box's text aHorizontal pixels from its sides and aVertical from its top and bottom (design QA P2-9; mockup
/// audit M1-5). wxGTK 3.2 applies SetMargins to single-line entries only; a multi-line entry is a GtkTextView inside the
/// GtkScrolledWindow that GetHandle() returns, so its margins are set on it through the GTK the toolkit already runs on,
/// looked up at run time so no GTK header is needed. A missing function leaves the text where GTK puts it.
inline void PadTextBox( wxTextCtrl* aControl, int aHorizontal, int aVertical )
{
    if( !aControl->IsMultiLine() ) { aControl->SetMargins( aHorizontal, aVertical ); return; }
#if defined( __WXGTK__ )
    using CHILD = void* ( * )( void* );
    using MARGIN = void ( * )( void*, int );
    static const auto child = reinterpret_cast<CHILD>( dlsym( RTLD_DEFAULT, "gtk_bin_get_child" ) );
    static const auto left = reinterpret_cast<MARGIN>( dlsym( RTLD_DEFAULT, "gtk_text_view_set_left_margin" ) );
    static const auto right = reinterpret_cast<MARGIN>( dlsym( RTLD_DEFAULT, "gtk_text_view_set_right_margin" ) );
    static const auto top = reinterpret_cast<MARGIN>( dlsym( RTLD_DEFAULT, "gtk_text_view_set_top_margin" ) );
    static const auto bottom = reinterpret_cast<MARGIN>( dlsym( RTLD_DEFAULT, "gtk_text_view_set_bottom_margin" ) );
    void* view = child && aControl->GetHandle() ? child( aControl->GetHandle() ) : nullptr;
    if( !view ) return;
    if( left ) left( view, aHorizontal );
    if( right ) right( view, aHorizontal );
    if( top ) top( view, aVertical );
    if( bottom ) bottom( view, aVertical );
#else
    aControl->SetMargins( aHorizontal, aVertical );
#endif
}
/// Gives aWindow aName for assistive technology (GTK's ATK object; elsewhere nothing changes): the history lists draw their
/// rows themselves, so the list is named after the row selected in it, whole (mockup audit P3 item 10).
inline void NameForAssistiveTechnology( wxWindow* aWindow, const wxString& aName )
{
#if defined( __WXGTK__ )
    using ACCESSIBLE = void* ( * )( void* );
    using NAME = void ( * )( void*, const char* );
    static const auto accessibleOf = reinterpret_cast<ACCESSIBLE>( dlsym( RTLD_DEFAULT, "gtk_widget_get_accessible" ) );
    static const auto setName = reinterpret_cast<NAME>( dlsym( RTLD_DEFAULT, "atk_object_set_name" ) );
    if( !accessibleOf || !setName || !aWindow || !aWindow->GetHandle() ) return;
    if( void* accessible = accessibleOf( aWindow->GetHandle() ) ) setName( accessible, aName.utf8_str() );
#else
    wxUnusedVar( aWindow ); wxUnusedVar( aName );
#endif
}
/// The fill that marks the words in which two versions of a text differ (mockup audit M1-6; the approved conflict mockup
/// highlights the phrases "top edge" and "bottom edge", the editor the words that differ, "top" and "bottom"): an amber tint that keeps aText at 7:1 or more on it, pale on a light surface and
/// deep on a dark one.
inline wxColour DifferenceFill( const wxColour& aText, const wxColour& aSurface )
{
    const bool dark = Luminance( aSurface ) < 0.18;
    HSL tint{ 45.0 / 360, 1.0, dark ? 0.24 : 0.84 };
    for( int step = 0; step < 60 && Contrast( aText, FromHsl( tint ) ) < 7.0; ++step ) tint.l += dark ? -0.01 : 0.01;
    return FromHsl( tint );
}
/// The ranges [start, end) of the words of aText that aOther does not share, by the longest common run of their words;
/// neighbouring words that differ form one range with the spaces between them. Texts of more than 600 words are not
/// compared, and give no ranges.
inline std::vector<std::pair<long, long>> DifferingWords( const wxString& aText, const wxString& aOther )
{
    auto words = []( const wxString& text )
    {
        std::vector<std::pair<long, long>> result;
        long start = -1;
        for( long i = 0; i <= static_cast<long>( text.length() ); ++i )
        {
            bool space = i == static_cast<long>( text.length() ) || text[i] == ' ' || text[i] == '\t' || text[i] == '\n' || text[i] == '\r';
            if( !space && start < 0 ) start = i;
            if( space && start >= 0 ) { result.emplace_back( start, i ); start = -1; }
        }
        return result;
    };
    const auto mine = words( aText ), theirs = words( aOther );
    std::vector<std::pair<long, long>> ranges;
    if( mine.size() > 600 || theirs.size() > 600 ) return ranges;
    auto word = []( const wxString& text, const std::pair<long, long>& at ) { return text.Mid( at.first, at.second - at.first ); };
    const size_t n = mine.size(), m = theirs.size();
    std::vector<std::vector<unsigned short>> common( n + 1, std::vector<unsigned short>( m + 1, 0 ) );
    for( size_t i = n; i-- > 0; )
        for( size_t j = m; j-- > 0; )
            common[i][j] = word( aText, mine[i] ) == word( aOther, theirs[j] ) ? common[i + 1][j + 1] + 1
                                                                                : std::max( common[i + 1][j], common[i][j + 1] );
    std::vector<bool> shared( n, false );
    for( size_t i = 0, j = 0; i < n && j < m; )
    {
        if( word( aText, mine[i] ) == word( aOther, theirs[j] ) ) { shared[i] = true; ++i; ++j; }
        else if( common[i + 1][j] >= common[i][j + 1] ) ++i;
        else ++j;
    }
    for( size_t i = 0; i < n; ++i )
    {
        if( shared[i] ) continue;
        if( !ranges.empty() && i > 0 && !shared[i - 1] ) ranges.back().second = mine[i].second;
        else ranges.emplace_back( mine[i].first, mine[i].second );
    }
    return ranges;
}
}

/** One row of a history list in the approved diagram history's two-line pattern (mockup 01-diagram-history): the version in
 * bold on the left; beside it the saved marker and the author; then a second line (the diagram history's change summary, the
 * field history's earlier implementation). */
struct DIAGRAM_HISTORY_ROW
{
    wxString version;
    /// "Saved · Author", or the author alone.
    wxString first;
    /// The second line, or empty.
    wxString second;
};

/** A history row as laid out in a row rectangle of a list, in the list's client pixels (mockup audit M1-12). The saved marker
 * and the author are never cut: where they are wider than the row they continue on the next line, broken between words (and
 * inside a word only when the word alone is wider than the row). Only the second line ends in "…", when it is wider. */
struct DIAGRAM_HISTORY_ROW_LAYOUT
{
    wxString version;
    wxRect versionRect;
    std::vector<wxString> lines;
    std::vector<wxRect> lineRects;
    /// The version and every line lie whole inside the row.
    bool fits = true;
};
/// aRow laid out in aBounds, a row of aList, with aList's font (the version in its bold weight) and its DIP scale.
DIAGRAM_HISTORY_ROW_LAYOUT LayHistoryRow( const wxWindow* aList, const wxRect& aBounds, const DIAGRAM_HISTORY_ROW& aRow );
/// The height of aRow in a list aWidth pixels wide: two lines at least, as the approved rows, and one more for each line the
/// saved marker and the author need.
int HistoryRowHeight( const wxWindow* aList, int aWidth, const DIAGRAM_HISTORY_ROW& aRow );
/// Draws a laid-out row, in the selection's text colour when aSelected.
void DrawHistoryRow( wxDC& aDC, const wxWindow* aList, const DIAGRAM_HISTORY_ROW_LAYOUT& aLayout, bool aSelected );

/** A list of history rows drawn in that pattern, each as tall as its lines need at the list's width. Selection, keyboard and
 * the listbox events are the platform list's (wxVListBox). */
class DIAGRAM_HISTORY_LIST : public wxVListBox
{
public:
    DIAGRAM_HISTORY_LIST( wxWindow* aParent, const wxString& aName );
    void SetRows( std::vector<DIAGRAM_HISTORY_ROW> aRows );
    const std::vector<DIAGRAM_HISTORY_ROW>& Rows() const { return m_rows; }
    /// Row aIndex as drawn at the list's current width, in client pixels (its rectangle whether or not it is scrolled into view).
    DIAGRAM_HISTORY_ROW_LAYOUT RowLayout( size_t aIndex ) const;
    /// Row aIndex's rectangle in client pixels at the current scroll position.
    wxRect RowRect( size_t aIndex ) const;

protected:
    wxCoord OnMeasureItem( size_t aIndex ) const override;
    void OnDrawItem( wxDC& aDC, const wxRect& aRect, size_t aIndex ) const override;

private:
    std::vector<DIAGRAM_HISTORY_ROW> m_rows;
    int m_width = -1;
};

struct DIAGRAM_FIELD_HISTORY_ENTRY
{
    std::string revisionId;
    wxString revisionLabel;
    wxString actor;
    wxString text;
    wxString sourceDescription;
    bool saved = false;
    /// The name of the earlier implementation the row was saved in, empty for a row of the implementation shown; the
    /// revision label then reads "name · vN".
    wxString implementation;
};

/** The rows of one field-history page, newest first. An implementation made from another one continues that
 * implementation's field history, and a row's version counts the diagram revisions of the implementation it was saved
 * in. A row saved in an earlier implementation therefore names that implementation ("Initial approach · v3") so it is
 * not mistaken for a version of this one; a row of this implementation shows its version alone ("v3").
 * @param aConnection true for a connection's or member's history, false for a block's. */
std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> DiagramFieldHistoryRows(
        const kiapi::automation::diagrams::v1::RecursiveBlockGraphData& aGraph,
        const kiapi::automation::diagrams::v1::FieldHistoryPageData& aPage, bool aConnection );

/** Read-only history comparison. Accepting returns an exact source revision for
 * the caller's draft; this dialog never writes a file or starts an AI agent. */
class DIALOG_DIAGRAM_FIELD_HISTORY : public DIALOG_SHIM
{
public:
    DIALOG_DIAGRAM_FIELD_HISTORY( wxWindow* aParent, const wxString& aFieldLabel,
            const wxString& aOwnerPath, const wxString& aSavedRevisionLabel,
            const wxString& aSavedText, std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> aEntries,
            std::function<void( const std::string& )> aOpenSource = {} );

    const std::optional<std::string>& RestoreRevision() const { return m_restoreRevision; }
    const DIAGRAM_FIELD_HISTORY_ENTRY* RestoredEntry() const;

    // Pages append to the exact comparison opened by the caller. A failed or
    // cancelled read must never clear inspected rows or select another revision.
    void ConfigurePaging( size_t aTotal, std::function<void( size_t )> aLoadOlder );
    bool AppendPage( size_t aOffset, size_t aTotal,
                     std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> aEntries );
    void PageFailed( const wxString& aMessage );
    size_t LoadedCount() const { return m_entries.size(); }
    size_t TotalCount() const { return m_total; }
    bool IsLoading() const { return m_loading; }
    std::string InspectedRevision() const;
    wxString PageError() const;
    /// Every loaded row's whole text: its revision label (with the name of the earlier implementation it was saved in), the
    /// saved marker and the author.
    std::vector<wxString> RowLabels() const;
    /// Every loaded row as the list shows it at its current width, one line per line drawn: the version, the saved marker and
    /// the author (on more lines where the row is narrower), then the earlier implementation's name (mockup audit M1-12).
    std::vector<wxString> ShownRowLabels() const;
    /// Every loaded row as the list draws it: its version and lines, where they are on screen, and whether they lie whole
    /// inside the row and the list (mockup audit M1-12).
    struct SHOWN_ROW
    {
        wxString version;
        std::vector<wxString> lines;
        wxRect rect, versionRect;
        std::vector<wxRect> lineRects;
        bool fits = false, shown = false;
    };
    std::vector<SHOWN_ROW> ShownRows() const;

    /// Lays the dialog out and then fits the selected row's heading to the width its column got.
    bool Layout() override;

private:
    void updateSelection();
    void fitHeading();
    void updatePaging();
    /// The list's rows for every loaded entry, in the diagram history's two-line pattern.
    void showRows();

    std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> m_entries;
    const std::function<void( const std::string& )> m_openSource;
    std::function<void( size_t )> m_loadOlder;
    size_t m_total = 0;
    bool m_loading = false;
    bool m_showPageCount = false;
    std::optional<std::string> m_restoreRevision;
    DIAGRAM_HISTORY_LIST* m_history = nullptr;
    wxStaticText* m_pageStatus;
    wxStaticText* m_pageError;
    wxButton* m_older;
    wxStaticText* m_selectedHeading = nullptr;
    // The selected row's heading in two parts: the earlier implementation's name (possibly empty) and "vN · Author —
    // Selected text". Only the name is shortened when the heading is too narrow, so the version and author stay readable.
    wxString m_headingImplementation;
    wxString m_headingRest;
    wxTextCtrl* m_selectedText;
    wxButton* m_source;
    wxButton* m_restore;
};

#endif
