/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#define BOOST_TEST_NO_MAIN
#include <boost/test/unit_test.hpp>
#include <qa_utils/wx_utils/unit_test_utils.h>
#include <dialogs/dialog_diagram_field_history.h>
#include <dialogs/dialog_diagram_conflict.h>
#include <dialogs/panel_diagram_history.h>
#include <api/common/types/diagram_revision_types.pb.h>
#include <nlohmann/json.hpp>
#include <wx/app.h>
#include <wx/button.h>
#include <wx/choice.h>
#include <wx/radiobut.h>
#include <wx/dcmemory.h>
#include <wx/dcscreen.h>
#include <wx/evtloop.h>
#include <wx/image.h>
#include <wx/listbox.h>
#include <wx/stopwatch.h>
#include <wx/stattext.h>
#include <wx/sizer.h>
#include <wx/vlbox.h>
#include <wx/textctrl.h>
#include <wx/timer.h>
#include <wx/uiaction.h>
#include <gtk/gtk.h>
#include <algorithm>
#include <cstdlib>
#include <tuple>
#include <exception>
#include <filesystem>
#include <fstream>

namespace
{
namespace D = kiapi::automation::diagrams::v1;

void waitFor( const std::function<bool()>& aCondition )
{
    wxEventLoop loop;
    wxEventLoopActivator active( &loop );
    wxEvtHandler events;
    wxTimer timer( &events );
    wxStopWatch time;
    bool expired = false;
    events.Bind( wxEVT_TIMER, [&]( wxTimerEvent& )
    {
        expired = time.Time() >= 5000;
        if( aCondition() || expired ) loop.Exit();
    } );
    timer.Start( 20 ); loop.Run(); timer.Stop();
    BOOST_REQUIRE( !expired );
}

template<typename T>
T* control( wxWindow* aParent, const char* aName )
{
    auto* found = dynamic_cast<T*>( wxWindow::FindWindowByName( aName, aParent ) );
    BOOST_REQUIRE( found ); return found;
}

void click( wxWindow* aControl )
{
    wxRect bounds = aControl->GetScreenRect();
    wxUIActionSimulator input;
    BOOST_REQUIRE( input.MouseMove( bounds.x + bounds.width / 2, bounds.y + bounds.height / 2 ) );
    BOOST_REQUIRE( input.MouseClick() );
    wxTheApp->Yield( true );
}

void key( int aKey )
{
    wxUIActionSimulator input;
    BOOST_REQUIRE( input.KeyDown( aKey ) ); BOOST_REQUIRE( input.KeyUp( aKey ) );
    wxTheApp->Yield( true );
}

/// The pixels of a capture, kept for measuring what was drawn (mockup audit M1-3 to M1-6).
struct SHOT
{
    int width = 0, height = 0;
    std::vector<unsigned char> rgb;
    wxColour At( int aX, int aY ) const
    {
        aX = std::clamp( aX, 0, width - 1 ); aY = std::clamp( aY, 0, height - 1 );
        const unsigned char* p = &rgb[( static_cast<size_t>( aY ) * width + aX ) * 3];
        return wxColour( p[0], p[1], p[2] );
    }
};

/// Lets GTK finish a state change's transition (a button that just became available fades in over about 200 ms).
void settle( int aMilliseconds )
{
    wxStopWatch time;
    waitFor( [&] { return time.Time() >= aMilliseconds; } );
}

SHOT capture( wxWindow* aWindow, const std::filesystem::path& aDirectory, const char* aName )
{
    // A changed control value is not a rendering checkpoint. GTK can retain the
    // preceding pixels until the next frame even after wxWindow::Update().
    GtkWidget* widget = GTK_WIDGET( aWindow->GetHandle() );
    GdkFrameClock* clock = gtk_widget_get_frame_clock( widget );
    BOOST_REQUIRE( clock );
    struct PAINT_STATE { gint64 before; bool complete = false; } state{ gdk_frame_clock_get_frame_counter( clock ) };
    g_object_ref( clock );
    gulong handler = g_signal_connect( clock, "after-paint", G_CALLBACK( +[]( GdkFrameClock* frame, gpointer data )
    {
        auto* observed = static_cast<PAINT_STATE*>( data );
        observed->complete = gdk_frame_clock_get_frame_counter( frame ) > observed->before;
    } ), &state );
    struct SIGNAL_GUARD
    {
        GdkFrameClock* clock;
        gulong handler;
        ~SIGNAL_GUARD() { g_signal_handler_disconnect( clock, handler ); g_object_unref( clock ); }
    } guard{ clock, handler };
    gtk_widget_queue_draw( widget );
    gdk_frame_clock_request_phase( clock, GDK_FRAME_CLOCK_PHASE_PAINT );
    waitFor( [&] { return state.complete; } );
    gdk_display_sync( gtk_widget_get_display( widget ) );
    GdkWindow* window = gtk_widget_get_window( widget );
    BOOST_REQUIRE( window );
    GdkPixbuf* pixels = gdk_pixbuf_get_from_window( window, 0, 0, gdk_window_get_width( window ), gdk_window_get_height( window ) );
    BOOST_REQUIRE( pixels );
    SHOT shot; shot.width = gdk_pixbuf_get_width( pixels ); shot.height = gdk_pixbuf_get_height( pixels );
    const int channels = gdk_pixbuf_get_n_channels( pixels ), stride = gdk_pixbuf_get_rowstride( pixels );
    const guchar* data = gdk_pixbuf_read_pixels( pixels );
    shot.rgb.resize( static_cast<size_t>( shot.width ) * shot.height * 3 );
    for( int y = 0; y < shot.height; ++y )
        for( int x = 0; x < shot.width; ++x )
            for( int c = 0; c < 3; ++c ) shot.rgb[( static_cast<size_t>( y ) * shot.width + x ) * 3 + c] = data[y * stride + x * channels + c];
    GError* error = nullptr;
    bool written = gdk_pixbuf_save( pixels, ( aDirectory / aName ).c_str(), "png", &error, nullptr );
    g_object_unref( pixels );
    std::string message = error ? error->message : "Native window capture failed";
    if( error ) g_error_free( error );
    BOOST_REQUIRE_MESSAGE( written, message );
    return shot;
}

/// aControl's rectangle in the capture of aWindow (whose client area the capture covers).
wxRect within( wxWindow* aWindow, wxWindow* aControl )
{
    wxRect rect = aControl->GetScreenRect(); wxPoint origin = aWindow->ClientToScreen( wxPoint( 0, 0 ) );
    rect.Offset( -origin.x, -origin.y ); return rect;
}

/// The pixel of aBox that contrasts most with aBackground.
wxColour inkIn( const SHOT& aShot, const wxRect& aBox, const wxColour& aBackground )
{
    wxColour ink = aBackground; double best = 1;
    for( int y = aBox.y; y < aBox.y + aBox.height; ++y )
        for( int x = aBox.x; x < aBox.x + aBox.width; ++x )
        {
            wxColour here = aShot.At( x, y ); double contrast = DIAGRAM_LOOK::Contrast( here, aBackground );
            if( contrast > best ) { best = contrast; ink = here; }
        }
    return ink;
}

/// A primary action as drawn (mockup audit M1-4): its fill against the surface beside it, and its label on the fill.
std::pair<double, double> primaryContrast( const SHOT& aShot, wxWindow* aWindow, wxButton* aButton )
{
    wxRect rect = within( aWindow, aButton );
    wxColour fill = aShot.At( rect.x + 6, rect.y + rect.height / 2 ), surface = aShot.At( rect.x - 4, rect.y + rect.height / 2 );
    wxColour label = inkIn( aShot, wxRect( rect.x + rect.width / 4, rect.y + 4, rect.width / 2, rect.height - 8 ), fill );
    return { DIAGRAM_LOOK::Contrast( fill, surface ), DIAGRAM_LOOK::Contrast( label, fill ) };
}

/// How many pixels a box's first line of text starts right of the box's left edge (mockup audit M1-5): the first column past
/// the border with a pixel of 2:1 or more against the box's fill, in the box's top 30 pixels; -1 when none.
int textInset( const SHOT& aShot, wxWindow* aWindow, wxTextCtrl* aBox )
{
    wxRect rect = within( aWindow, aBox );
    wxColour fill = aShot.At( rect.x + rect.width - 14, rect.y + rect.height - 8 );
    for( int x = rect.x + 2; x < rect.x + rect.width / 2; ++x )
        for( int y = rect.y + 3; y < rect.y + std::min( 30, rect.height - 3 ); ++y )
            if( DIAGRAM_LOOK::Contrast( aShot.At( x, y ), fill ) >= 2.0 ) return x - rect.x;
    return -1;
}

/// Whether the list's rows end in "…" when they are wider than the list (mockup audit M1-3), read back from GTK: its text
/// renderer ellipsizes at the end, in a fixed column no wider than the list.
bool rowsEllipsize( wxListBox* aList )
{
    GtkWidget* view = gtk_bin_get_child( GTK_BIN( aList->GetHandle() ) );
    if( !GTK_IS_TREE_VIEW( view ) ) return false;
    GtkTreeViewColumn* column = gtk_tree_view_get_column( GTK_TREE_VIEW( view ), 0 );
    if( !column ) return false;
    bool ellipsized = false;
    GList* cells = gtk_cell_layout_get_cells( GTK_CELL_LAYOUT( column ) );
    for( GList* item = cells; item; item = item->next )
        if( GTK_IS_CELL_RENDERER_TEXT( item->data ) )
        {
            PangoEllipsizeMode mode = PANGO_ELLIPSIZE_NONE; g_object_get( item->data, "ellipsize", &mode, nullptr );
            ellipsized = mode == PANGO_ELLIPSIZE_END;
        }
    g_list_free( cells );
    return ellipsized && gtk_tree_view_column_get_sizing( column ) == GTK_TREE_VIEW_COLUMN_FIXED
        && gtk_tree_view_column_get_fixed_width( column ) <= aList->GetClientSize().x;
}

/// Whether GTK draws every row of the list whole, without its own "…" (mockup audit M1-3), read back from GTK: each row is
/// displayed, and its text as the list shows it, laid out in the font the list's text renderer draws with, fits the row's
/// cell (the part of it inside the list) less the renderer's padding and focus line on both sides.
bool rowsFit( wxListBox* aList )
{
    GtkWidget* view = gtk_bin_get_child( GTK_BIN( aList->GetHandle() ) );
    if( !GTK_IS_TREE_VIEW( view ) || aList->GetCount() == 0 ) return false;
    GtkTreeViewColumn* column = gtk_tree_view_get_column( GTK_TREE_VIEW( view ), 0 );
    if( !column ) return false;
    GtkCellRenderer* renderer = nullptr;
    GList* cells = gtk_cell_layout_get_cells( GTK_CELL_LAYOUT( column ) );
    for( GList* item = cells; item; item = item->next )
        if( GTK_IS_CELL_RENDERER_TEXT( item->data ) ) renderer = GTK_CELL_RENDERER( item->data );
    g_list_free( cells );
    if( !renderer ) return false;
    gint xpad = 0, ypad = 0, focus = 0;
    gtk_cell_renderer_get_padding( renderer, &xpad, &ypad );
    gtk_widget_style_get( view, "focus-line-width", &focus, nullptr );
    gboolean fontSet = FALSE; PangoFontDescription* font = nullptr;
    g_object_get( renderer, "font-set", &fontSet, nullptr );
    if( fontSet ) g_object_get( renderer, "font-desc", &font, nullptr );
    const int visible = gtk_widget_get_allocated_width( view );
    bool fits = true;
    for( unsigned row = 0; row < aList->GetCount() && fits; ++row )
    {
        GtkTreePath* path = gtk_tree_path_new_from_indices( static_cast<gint>( row ), -1 );
        GdkRectangle cell{}; gtk_tree_view_get_cell_area( GTK_TREE_VIEW( view ), path, column, &cell );
        gtk_tree_path_free( path );
        PangoLayout* layout = gtk_widget_create_pango_layout( view, aList->GetString( row ).utf8_string().c_str() );
        if( font ) pango_layout_set_font_description( layout, font );
        int width = 0, height = 0; pango_layout_get_pixel_size( layout, &width, &height );
        g_object_unref( layout );
        const int room = std::min( cell.x + cell.width, visible ) - cell.x - 2 * xpad - 2 * focus;
        fits = cell.height > 0 && width <= room;
        if( !fits )
            BOOST_TEST_MESSAGE( "Row '" << aList->GetString( row ).utf8_string() << "' is " << width << " pixels wide; its cell leaves " << room );
    }
    if( font ) pango_font_description_free( font );
    return fits;
}

/// The pixels of aBox drawn in aFill (within a colour distance of 12), and the contrast of the text drawn on them with the
/// fill: the marks of the words that differ (mockup audit M1-6).
std::pair<int, double> marked( const SHOT& aShot, wxWindow* aWindow, wxTextCtrl* aBox, const wxColour& aFill )
{
    wxRect rect = within( aWindow, aBox ); int count = 0; wxRect marks;
    for( int y = rect.y + 2; y < rect.y + rect.height - 2; ++y )
        for( int x = rect.x + 2; x < rect.x + rect.width - 2; ++x )
        {
            wxColour here = aShot.At( x, y );
            if( std::abs( here.Red() - aFill.Red() ) + std::abs( here.Green() - aFill.Green() ) + std::abs( here.Blue() - aFill.Blue() ) > 12 ) continue;
            ++count; marks = marks.IsEmpty() ? wxRect( x, y, 1, 1 ) : marks.Union( wxRect( x, y, 1, 1 ) );
        }
    double contrast = count ? DIAGRAM_LOOK::Contrast( inkIn( aShot, marks, aFill ), aFill ) : 1;
    return { count, contrast };
}

int show( DIALOG_SHIM* aDialog, const std::function<void()>& aScenario )
{
    std::exception_ptr failure;
    bool expired = false;
    wxEvtHandler events;
    wxTimer deadline( &events );
    events.Bind( wxEVT_TIMER, [&]( wxTimerEvent& )
    { expired = true; if( aDialog->IsModal() ) aDialog->EndModal( wxID_CANCEL ); } );
    deadline.StartOnce( 15000 );
    wxTheApp->CallAfter( [&]
    {
        try { waitFor( [&] { return aDialog->IsShownOnScreen(); } ); aScenario(); }
        catch( ... ) { failure = std::current_exception(); if( aDialog->IsModal() ) aDialog->EndModal( wxID_CANCEL ); }
    } );
    int result = aDialog->ShowModal(); deadline.Stop();
    BOOST_CHECK( !expired );
    if( failure ) std::rethrow_exception( failure );
    return result;
}
}

BOOST_AUTO_TEST_SUITE( DiagramFieldHistory )

BOOST_AUTO_TEST_CASE( RenderedWholeDiagramPanelKeepsInspectionPreviewAndRestoreSeparate )
{
    const char* inputPath = std::getenv( "KICAD_FIELD_HISTORY_PAGES" );
    const char* outputPath = std::getenv( "KICAD_FIELD_HISTORY_EVIDENCE" );
    if( !inputPath || !outputPath ) return;
    BOOST_REQUIRE( KI_TEST::CanDoDisplayTests() );
    D::DiagramHistoryPageData first, second; D::DiagramHistoryComparisonData comparison;
    std::ifstream firstInput( std::filesystem::path( inputPath ) / "diagram-page-0.pb", std::ios::binary );
    std::ifstream secondInput( std::filesystem::path( inputPath ) / "diagram-page-1.pb", std::ios::binary );
    std::ifstream comparedInput( std::filesystem::path( inputPath ) / "diagram-comparison.pb", std::ios::binary );
    BOOST_REQUIRE( first.ParseFromIstream( &firstInput ) ); BOOST_REQUIRE( second.ParseFromIstream( &secondInput ) );
    BOOST_REQUIRE( comparison.ParseFromIstream( &comparedInput ) );
    auto* dialog = new DIALOG_SHIM( nullptr, wxID_ANY, "Whole diagram history component" );
    auto* layout = new wxBoxSizer( wxVERTICAL ); PANEL_DIAGRAM_HISTORY* panel = nullptr;
    D::BlockSelectionData inspected, previewed, restored; bool returned = false, closed = false; unsigned requested = 0;
    PANEL_DIAGRAM_HISTORY::ACTIONS actions;
    actions.inspect = [&]( auto selected ) { inspected = selected; };
    actions.preview = [&]( auto selected ) { previewed = selected; panel->SetPreviewing( true ); };
    actions.restore = [&]( auto selected ) { restored = selected; };
    actions.load = [&]( unsigned offset ) { requested = offset; };
    actions.retry = [] {};
    actions.returnToCurrent = [&] { returned = true; panel->SetPreviewing( false ); };
    actions.close = [&] { closed = true; dialog->EndModal( wxID_CANCEL ); };
    panel = new PANEL_DIAGRAM_HISTORY( dialog, std::move( actions ) ); layout->Add( panel, 1, wxEXPAND ); dialog->SetSizer( layout );
    dialog->SetClientSize( 400, 640 ); panel->Begin( first.context(), "System", first.context_version() );
    BOOST_REQUIRE( panel->SetPage( first ) );
    bool readOnly = false, wrongRejected = false; double restoreFill = 0, restoreLabel = 0; int detailsInset = -1;
    BOOST_CHECK_EQUAL( show( dialog, [&]
    {
        click( control<wxButton>( panel, "DiagramHistoryOlder" ) ); BOOST_CHECK_EQUAL( requested, 1 );
        BOOST_CHECK( panel->SetPage( second ) );
        auto* rows = control<wxVListBox>( panel, "DiagramHistoryRevisions" ); rows->SetFocus(); key( WXK_DOWN );
        waitFor( [&] { return inspected.revision_id() == comparison.inspected().revision_id(); } );
        readOnly = previewed.revision_id().empty() && restored.revision_id().empty(); BOOST_CHECK( readOnly );
        auto wrong = comparison; wrong.mutable_inspected()->set_revision_id( first.context().revision_id() );
        wrongRejected = !panel->SetComparison( wrong ); BOOST_CHECK( wrongRejected );
        BOOST_CHECK( !control<wxButton>( panel, "DiagramHistoryRestore" )->IsEnabled() );
        BOOST_CHECK( panel->SetComparison( comparison ) );
        wxRect client( dialog->ClientToScreen( wxPoint( 0, 0 ) ), dialog->GetClientSize() );
        BOOST_CHECK( client.Contains( control<wxButton>( panel, "DiagramHistoryRestore" )->GetScreenRect() ) );
        BOOST_CHECK( client.Contains( control<wxTextCtrl>( panel, "DiagramHistoryComparison" )->GetScreenRect() ) );
        BOOST_CHECK( control<wxButton>( panel, "DiagramHistoryRestore" )->IsEnabled() );
        settle( 300 );
        SHOT shot = capture( dialog, outputPath, "09-diagram-history-panel.png" );
        std::tie( restoreFill, restoreLabel ) = primaryContrast( shot, dialog, control<wxButton>( panel, "DiagramHistoryRestore" ) );
        detailsInset = textInset( shot, dialog, control<wxTextCtrl>( panel, "DiagramHistoryComparison" ) );
        click( control<wxButton>( panel, "DiagramHistoryPreview" ) );
        BOOST_CHECK_EQUAL( previewed.revision_id(), comparison.inspected().revision_id() );
        BOOST_CHECK( restored.revision_id().empty() );
        click( control<wxButton>( panel, "DiagramHistoryReturn" ) ); BOOST_CHECK( returned );
        click( control<wxButton>( panel, "DiagramHistoryRestore" ) );
        BOOST_CHECK_EQUAL( restored.revision_id(), comparison.inspected().revision_id() );
        click( control<wxButton>( panel, "DiagramHistoryClose" ) );
    } ), wxID_CANCEL );
    std::ofstream receipt( std::filesystem::path( outputPath ) / "diagram-panel-interaction.json" );
    receipt << nlohmann::json( { { "inspection_read_only", readOnly }, { "comparison_target_rejected", wrongRejected },
        { "preview_explicit", previewed.revision_id() == comparison.inspected().revision_id() }, { "return_explicit", returned },
        { "restore_explicit", restored.revision_id() == comparison.inspected().revision_id() }, { "cancelled", closed },
        { "restore_fill_on_panel", restoreFill }, { "restore_label_on_fill", restoreLabel }, { "details_inset", detailsInset } } ).dump( 2 );
    dialog->Destroy(); wxTheApp->ProcessPendingEvents();
}

BOOST_AUTO_TEST_CASE( RenderedPagingPreservesInspectionFailureAndCancellation )
{
    const char* inputPath = std::getenv( "KICAD_FIELD_HISTORY_PAGES" );
    const char* outputPath = std::getenv( "KICAD_FIELD_HISTORY_EVIDENCE" );
    if( !inputPath || !outputPath ) return;
    BOOST_REQUIRE( KI_TEST::CanDoDisplayTests() ); wxInitAllImageHandlers();
    D::FieldHistoryPageData first, older; D::RecursiveBlockGraphData longGraph;
    std::ifstream firstInput( std::filesystem::path( inputPath ) / "history-page-0.pb", std::ios::binary );
    std::ifstream olderInput( std::filesystem::path( inputPath ) / "history-page-200.pb", std::ios::binary );
    std::ifstream graphInput( std::filesystem::path( inputPath ) / "history-graph.pb", std::ios::binary );
    BOOST_REQUIRE( first.ParseFromIstream( &firstInput ) ); BOOST_REQUIRE( older.ParseFromIstream( &olderInput ) );
    BOOST_REQUIRE( longGraph.ParseFromIstream( &graphInput ) );
    BOOST_REQUIRE_EQUAL( first.entries_size(), 200 ); BOOST_REQUIRE_EQUAL( first.total(), 206 );
    // The editor's own row builder: every row of this single implementation's history shows its version alone.
    auto rows = [&]( const D::FieldHistoryPageData& page ) { return DiagramFieldHistoryRows( longGraph, page, false ); };
    auto makeDialog = [&]()
    {
        return new DIALOG_DIAGRAM_FIELD_HISTORY( nullptr, "General requirements", "System", "v206",
                wxString::FromUTF8( first.saved_text() ), rows( first ) );
    };
    auto* dialog = makeDialog(); size_t requested = 0; bool preserved = false, failurePreserved = false, rejected = false;
    dialog->ConfigurePaging( first.total(), [&]( size_t offset ) { requested = offset; } );
    auto* list = control<wxListBox>( dialog, "DiagramFieldHistoryRevisions" );
    auto* more = control<wxButton>( dialog, "DiagramFieldHistoryOlder" );
    auto* restore = control<wxButton>( dialog, "DiagramFieldHistoryRestore" );
    auto* close = control<wxButton>( dialog, "DiagramFieldHistoryClose" );
    std::filesystem::path evidence( outputPath ); std::filesystem::create_directories( evidence );
    BOOST_CHECK_EQUAL( show( dialog, [&]
    {
        list->SetFocus(); key( WXK_DOWN );
        waitFor( [&] { return list->GetSelection() == 1; } );
        BOOST_CHECK( list->GetString( 0 ).StartsWith( "v206 · Saved · " ) );
        auto inspected = dialog->InspectedRevision();
        click( more ); BOOST_CHECK_EQUAL( requested, 200 );
        BOOST_CHECK( dialog->IsLoading() ); BOOST_CHECK( !more->IsEnabled() );
        BOOST_CHECK( restore->IsEnabled() ); BOOST_CHECK( close->IsEnabled() );
        capture( dialog, evidence, "06-history-loading.png" );
        dialog->PageFailed( "Could not load older changes. Try again." );
        failurePreserved = dialog->LoadedCount() == 200 && dialog->InspectedRevision() == inspected && more->IsEnabled();
        BOOST_CHECK( failurePreserved );
        dialog->SetClientSize( dialog->FromDIP( wxSize( 590, 440 ) ) ); dialog->Layout();
        wxRect client( dialog->ClientToScreen( wxPoint( 0, 0 ) ), dialog->GetClientSize() );
        BOOST_CHECK( client.Contains( more->GetScreenRect() ) ); BOOST_CHECK( client.Contains( restore->GetScreenRect() ) );
        BOOST_CHECK( client.Contains( control<wxStaticText>( dialog, "DiagramFieldHistoryPageError" )->GetScreenRect() ) );
        capture( dialog, evidence, "07-history-load-error.png" );
        click( more );
        rejected = !dialog->AppendPage( 199, first.total(), rows( older ) );
        BOOST_CHECK( rejected ); BOOST_CHECK_EQUAL( dialog->LoadedCount(), 200 );
        click( more ); auto duplicate = rows( older ); duplicate[0] = rows( first )[0];
        BOOST_CHECK( !dialog->AppendPage( 200, first.total(), duplicate ) );
        BOOST_CHECK_EQUAL( dialog->LoadedCount(), 200 );
        click( more ); BOOST_CHECK( dialog->AppendPage( 200, first.total(), rows( older ) ) );
        preserved = dialog->InspectedRevision() == inspected && list->GetSelection() == 1;
        BOOST_CHECK( preserved ); BOOST_CHECK_EQUAL( dialog->LoadedCount(), 206 ); BOOST_CHECK( !more->IsShown() );
        BOOST_CHECK( dialog->PageError().IsEmpty() );
        list->SetFocus(); key( WXK_END );
        waitFor( [&] { return dialog->InspectedRevision() == older.entries( older.entries_size() - 1 ).requirement_revision_id(); } );
        BOOST_CHECK_EQUAL( control<wxTextCtrl>( dialog, "DiagramFieldHistorySavedText" )->GetValue(), wxString::FromUTF8( first.saved_text() ) );
        dialog->SetClientSize( dialog->FromDIP( wxSize( 740, 520 ) ) ); dialog->Layout();
        capture( dialog, evidence, "08-history-oldest.png" ); click( restore );
    } ), wxID_OK );
    BOOST_REQUIRE( dialog->RestoredEntry() );
    std::string restored = dialog->RestoredEntry()->revisionId;
    BOOST_CHECK_EQUAL( restored, older.entries( older.entries_size() - 1 ).requirement_revision_id() );
    size_t loaded = dialog->LoadedCount(); dialog->Destroy(); wxTheApp->ProcessPendingEvents();
    auto* cancelled = makeDialog(); bool requestedThenCancelled = false;
    cancelled->ConfigurePaging( first.total(), [&]( size_t offset ) { requestedThenCancelled = offset == 200; } );
    BOOST_CHECK_EQUAL( show( cancelled, [&]
    {
        click( control<wxButton>( cancelled, "DiagramFieldHistoryOlder" ) );
        BOOST_CHECK( cancelled->IsLoading() ); key( WXK_ESCAPE );
    } ), wxID_CANCEL );
    bool noRestore = requestedThenCancelled && !cancelled->RestoreRevision().has_value(); BOOST_CHECK( noRestore );
    cancelled->Destroy(); wxTheApp->ProcessPendingEvents();
    std::ofstream receipt( evidence / "paging-interaction.json" );
    receipt << nlohmann::json( { { "selection_preserved", preserved }, { "failure_preserved_rows", failurePreserved },
        { "malformed_page_rejected", rejected }, { "cancelled_load_without_restore", noRestore },
        { "loaded_count", loaded }, { "oldest_revision_id", restored } } ).dump( 2 );
}

BOOST_AUTO_TEST_CASE( RenderedCompareCancelRestoreAndScopeIsolation )
{
    const char* inputPath = std::getenv( "KICAD_FIELD_HISTORY_INPUT" );
    const char* outputPath = std::getenv( "KICAD_FIELD_HISTORY_EVIDENCE" );
    if( !inputPath || !outputPath )
    {
        BOOST_TEST_MESSAGE( "Run the compiled native field-history journey to supply its versioned model fixture." );
        return;
    }
    BOOST_REQUIRE( KI_TEST::CanDoDisplayTests() );
    wxInitAllImageHandlers();
    D::FieldHistoryPageData page;
    std::ifstream input( inputPath, std::ios::binary );
    BOOST_REQUIRE( input.good() ); BOOST_REQUIRE( page.ParseFromIstream( &input ) );
    BOOST_REQUIRE_EQUAL( page.field(), D::RFK_ROUTING );
    BOOST_REQUIRE_GE( page.entries_size(), 2 );
    // The diagram the page was read from, so the rows are labelled exactly as the editor labels them.
    const char* graphPath = std::getenv( "KICAD_FIELD_HISTORY_GRAPH" );
    BOOST_REQUIRE( graphPath );
    D::RecursiveBlockGraphData graph;
    std::ifstream graphInput( graphPath, std::ios::binary );
    BOOST_REQUIRE( graphInput.good() ); BOOST_REQUIRE( graph.ParseFromIstream( &graphInput ) );
    std::filesystem::path evidence( outputPath );
    std::filesystem::create_directories( evidence );
    auto text = []( const std::string& value ) { return wxString::FromUTF8( value ); };
    std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> rows = DiagramFieldHistoryRows( graph, page, false );
    auto* dialog = new DIALOG_DIAGRAM_FIELD_HISTORY( nullptr, "Routing requirements", "PSU",
            wxString::Format( "v%u", page.context_version() ), text( page.saved_text() ), rows );
    dialog->Move( wxPoint( 60, 60 ) );
    auto* list = control<wxListBox>( dialog, "DiagramFieldHistoryRevisions" );
    auto* selected = control<wxTextCtrl>( dialog, "DiagramFieldHistorySelectedText" );
    auto* saved = control<wxTextCtrl>( dialog, "DiagramFieldHistorySavedText" );
    auto* restore = control<wxButton>( dialog, "DiagramFieldHistoryRestore" );
    auto* close = control<wxButton>( dialog, "DiagramFieldHistoryClose" );
    dialog->ConfigurePaging( rows.size(), []( size_t ) { BOOST_FAIL( "A complete short history needs no further request." ); } );
    nlohmann::json rowLabels = nlohmann::json::array(), shownRows = nlohmann::json::array(), compactShownRows = nlohmann::json::array();
    std::string selectedHeading, restoreLabel;
    double restoreFill = 0, restoreInk = 0; int selectedInset = -1, savedInset = -1; bool ellipsized = false, fit = false, compactFit = false;
    auto collectShown = [&]( nlohmann::json& aRows )
    {
        for( const wxString& row : dialog->ShownRowLabels() ) aRows.push_back( row.utf8_string() );
        BOOST_CHECK_EQUAL( dialog->ShownRowLabels().size(), list->GetCount() );
    };
    int cancelled = show( dialog, [&]
    {
        BOOST_CHECK( !control<wxStaticText>( dialog, "DiagramFieldHistoryPageStatus" )->IsShown() );
        BOOST_CHECK( !control<wxButton>( dialog, "DiagramFieldHistoryOlder" )->IsShown() );
        for( const wxString& row : dialog->RowLabels() ) rowLabels.push_back( row.utf8_string() );
        capture( dialog, evidence, "01-current.png" );
        list->SetFocus(); key( WXK_DOWN );
        waitFor( [&] { return list->GetSelection() == 1 && selected->GetValue() == text( page.entries( 1 ).text() ); } );
        selectedHeading = control<wxStaticText>( dialog, "DiagramFieldHistorySelectedHeading" )->GetLabelText().utf8_string();
        restoreLabel = restore->GetLabelText().utf8_string();
        BOOST_CHECK_EQUAL( saved->GetValue(), text( page.saved_text() ) );
        BOOST_CHECK( !dialog->RestoreRevision().has_value() );
        settle( 300 );
        SHOT shot = capture( dialog, evidence, "02-earlier-text.png" );
        std::tie( restoreFill, restoreInk ) = primaryContrast( shot, dialog, restore );
        selectedInset = textInset( shot, dialog, selected ); savedInset = textInset( shot, dialog, saved );
        ellipsized = dialog->RowsEllipsize() && rowsEllipsize( list );
        // Mockup audit M1-3: every row as drawn, and whether GTK draws each whole (its version and author never cut).
        collectShown( shownRows ); fit = rowsFit( list );
        BOOST_CHECK_MESSAGE( fit, "Every history row fits the list as drawn." );
        key( WXK_ESCAPE );
    } );
    BOOST_CHECK_EQUAL( cancelled, wxID_CANCEL );
    BOOST_CHECK( !dialog->RestoreRevision().has_value() );
    bool cancelledWithoutRestore = cancelled == wxID_CANCEL && !dialog->RestoreRevision().has_value();
    bool compactFits = false, compactAuthorShown = false;
    std::string compactHeading;

    BOOST_CHECK_EQUAL( show( dialog, [&]
    {
        BOOST_CHECK( !dialog->RestoreRevision().has_value() );
        dialog->SetClientSize( dialog->FromDIP( wxSize( 590, 440 ) ) ); dialog->Layout();
        waitFor( [&] { return restore->IsShownOnScreen(); } );
        wxRect client( dialog->ClientToScreen( wxPoint( 0, 0 ) ), dialog->GetClientSize() );
        BOOST_CHECK( client.Contains( restore->GetScreenRect() ) );
        BOOST_CHECK( client.Contains( close->GetScreenRect() ) );
        BOOST_CHECK( client.Contains( saved->GetScreenRect() ) );
        compactFits = client.Contains( restore->GetScreenRect() ) && client.Contains( close->GetScreenRect() )
            && client.Contains( saved->GetScreenRect() );
        // The selected row's heading keeps its version and author whole at the compact size: only the name of the earlier
        // implementation it was saved in is shortened, and the heading as shown fits its column, so nothing of it is cut.
        auto* heading = control<wxStaticText>( dialog, "DiagramFieldHistorySelectedHeading" );
        const auto& inspected = page.entries( 1 );
        wxString shown = heading->GetLabelText();
        wxString versionAndAuthor = wxString::Format( "v%u · %s — Selected text", inspected.context_version(), text( inspected.origin().actor() ) );
        compactHeading = shown.utf8_string();
        compactAuthorShown = heading->IsShownOnScreen() && client.Contains( heading->GetScreenRect() ) && shown.EndsWith( versionAndAuthor )
            && heading->GetTextExtent( shown ).x <= heading->GetClientSize().x;
        BOOST_CHECK_MESSAGE( compactAuthorShown, "The compact heading '" << compactHeading << "' shows its version and author whole." );
        capture( dialog, evidence, "03-compact.png" );
        collectShown( compactShownRows ); compactFit = rowsFit( list );
        BOOST_CHECK_MESSAGE( compactFit, "Every history row fits the list as drawn in the compact dialog." );
        dialog->SetClientSize( dialog->FromDIP( wxSize( 740, 520 ) ) ); dialog->Layout();
        click( restore );
    } ), wxID_OK );
    BOOST_REQUIRE( dialog->RestoreRevision().has_value() );
    BOOST_CHECK_EQUAL( *dialog->RestoreRevision(), page.entries( 1 ).requirement_revision_id() );
    std::string restored = *dialog->RestoreRevision();
    dialog->SaveControlState();
    bool reopenCleared = false;
    BOOST_CHECK_EQUAL( show( dialog, [&]
    {
        BOOST_CHECK( !dialog->RestoreRevision().has_value() );
        reopenCleared = !dialog->RestoreRevision().has_value();
        click( close );
    } ), wxID_CANCEL );
    BOOST_CHECK( !dialog->RestoreRevision().has_value() );
    dialog->Destroy(); wxTheApp->ProcessPendingEvents();

    rows[0].text = "Different project's saved text";
    auto* other = new DIALOG_DIAGRAM_FIELD_HISTORY( nullptr, "Routing requirements", "Other system",
            "v1", rows[0].text, rows );
    bool scopeIsolation = false;
    BOOST_CHECK_EQUAL( show( other, [&]
    {
        BOOST_CHECK_EQUAL( control<wxTextCtrl>( other, "DiagramFieldHistorySavedText" )->GetValue(), rows[0].text );
        BOOST_CHECK_EQUAL( control<wxTextCtrl>( other, "DiagramFieldHistorySelectedText" )->GetValue(), rows[0].text );
        scopeIsolation = control<wxTextCtrl>( other, "DiagramFieldHistorySavedText" )->GetValue() == rows[0].text
            && control<wxTextCtrl>( other, "DiagramFieldHistorySelectedText" )->GetValue() == rows[0].text;
        click( control<wxButton>( other, "DiagramFieldHistoryClose" ) );
    } ), wxID_CANCEL );
    other->Destroy(); wxTheApp->ProcessPendingEvents();

    // An "&" in the name of the implementation a row was saved in is shown as written on the Use button, as in the heading,
    // not taken as a keyboard mnemonic (a manufacturer-style name such as "C&K").
    DIAGRAM_FIELD_HISTORY_ENTRY named = rows[1];
    named.implementation = wxS( "C&K approach" );
    named.revisionLabel = wxString::Format( "C&K approach · v%u", page.entries( 1 ).context_version() );
    auto* ampersand = new DIALOG_DIAGRAM_FIELD_HISTORY( nullptr, "Routing requirements", "PSU",
            wxString::Format( "v%u", page.context_version() ), text( page.saved_text() ), { named } );
    std::string ampersandLabel = control<wxButton>( ampersand, "DiagramFieldHistoryRestore" )->GetLabelText().utf8_string();
    BOOST_CHECK_EQUAL( ampersandLabel, wxString::Format( "Use C&K approach · v%u text in draft", page.entries( 1 ).context_version() ).utf8_string() );
    ampersand->Destroy(); wxTheApp->ProcessPendingEvents();
    std::ofstream receipt( evidence / "interaction.json" );
    receipt << nlohmann::json( { { "document_id", page.document_id() }, { "owner_id", page.owner_id() },
        { "context_revision_id", page.context_revision_id() }, { "restore_requirement_revision_id", restored },
        { "cancelled_without_restore", cancelledWithoutRestore }, { "reopen_cleared_restore", reopenCleared },
        { "scope_isolation", scopeIsolation }, { "compact_controls_visible", compactFits },
        { "compact_author_visible", compactAuthorShown }, { "compact_heading", compactHeading },
        { "row_labels", rowLabels }, { "shown_rows", shownRows }, { "rows_fit", fit }, { "compact_shown_rows", compactShownRows },
        { "compact_rows_fit", compactFit }, { "selected_heading", selectedHeading }, { "restore_label", restoreLabel },
        { "ampersand_restore_label", ampersandLabel }, { "restore_fill_on_dialog", restoreFill }, { "restore_label_on_fill", restoreInk },
        { "selected_text_inset", selectedInset }, { "saved_text_inset", savedInset }, { "rows_ellipsize", ellipsized } } ).dump( 2 );
    BOOST_REQUIRE( receipt.good() );
}

BOOST_AUTO_TEST_CASE( RenderedConflictRequiresExplicitChoicesAndPreservesCancel )
{
    const char* inputPath = std::getenv( "KICAD_FIELD_HISTORY_INPUT" );
    const char* outputPath = std::getenv( "KICAD_FIELD_HISTORY_EVIDENCE" );
    if( !inputPath || !outputPath ) return;
    BOOST_REQUIRE( KI_TEST::CanDoDisplayTests() );
    D::FieldHistoryPageData page; std::ifstream input( inputPath, std::ios::binary );
    BOOST_REQUIRE( page.ParseFromIstream( &input ) );
    std::filesystem::path evidence( outputPath );
    D::RequirementMergeData merge;
    merge.set_base_context_version( page.context_version() - 1 ); merge.set_saved_context_version( page.context_version() );
    auto* original = merge.mutable_original_draft(); original->mutable_baseline()->set_block_id( page.owner_id() );
    original->mutable_baseline()->set_state_id( page.state_id() ); original->mutable_baseline()->set_revision_id( page.context_revision_id() );
    original->set_baseline_requirement_revision_id( page.entries( 1 ).requirement_revision_id() );
    original->mutable_baseline_fields()->set_routing( "Keep sensing quiet." );
    original->mutable_fields()->set_routing( "Place converters at the top edge." );
    original->mutable_fields()->set_general( "Prefer a removable unit." );
    auto* latest = merge.mutable_saved_draft(); *latest->mutable_baseline() = original->baseline();
    latest->set_baseline_requirement_revision_id( page.requirement_revision_id() );
    latest->mutable_fields()->set_routing( "Place converters at the bottom edge." );
    latest->mutable_fields()->set_general( "Prefer fixed mounting." );
    auto* routing = merge.add_conflicts(); routing->set_field( D::RFK_ROUTING ); routing->set_baseline( "Keep sensing quiet." );
    routing->set_draft( original->fields().routing() ); routing->set_saved( latest->fields().routing() );
    auto* general = merge.add_conflicts(); general->set_field( D::RFK_GENERAL ); general->set_baseline( "" );
    general->set_draft( original->fields().general() ); general->set_saved( latest->fields().general() );
    auto* cancelled = new DIALOG_DIAGRAM_CONFLICT( nullptr, "PSU", merge );
    nlohmann::json draftMarked = nlohmann::json::array(), savedMarked = nlohmann::json::array();
    int draftMarks = 0, savedMarks = 0, baseInset = -1, draftInset = -1, savedInset = -1; double draftMarkInk = 1, savedMarkInk = 1;
    double saveFill = 0, saveInk = 0;
    BOOST_CHECK_EQUAL( show( cancelled, [&]
    {
        BOOST_CHECK( !control<wxButton>( cancelled, "DiagramConflictSave" )->IsEnabled() );
        BOOST_CHECK( !control<wxRadioButton>( cancelled, "DiagramConflictUseMine" )->GetValue() );
        BOOST_CHECK( !control<wxRadioButton>( cancelled, "DiagramConflictUseSaved" )->GetValue() );
        BOOST_CHECK( !control<wxRadioButton>( cancelled, "DiagramConflictWriteMerged" )->GetValue() );
        SHOT shot = capture( cancelled, evidence, "04-conflict-unresolved.png" );
        for( bool draft : { true, false } ) for( const auto& word : cancelled->MarkedWords( draft ) ) ( draft ? draftMarked : savedMarked ).push_back( word.utf8_string() );
        std::tie( draftMarks, draftMarkInk ) = marked( shot, cancelled, control<wxTextCtrl>( cancelled, "DiagramConflictDraft" ), cancelled->MarkFill() );
        std::tie( savedMarks, savedMarkInk ) = marked( shot, cancelled, control<wxTextCtrl>( cancelled, "DiagramConflictSaved" ), cancelled->MarkFill() );
        baseInset = textInset( shot, cancelled, control<wxTextCtrl>( cancelled, "DiagramConflictBase" ) );
        draftInset = textInset( shot, cancelled, control<wxTextCtrl>( cancelled, "DiagramConflictDraft" ) );
        savedInset = textInset( shot, cancelled, control<wxTextCtrl>( cancelled, "DiagramConflictSaved" ) );
        key( WXK_ESCAPE );
    } ), wxID_CANCEL );
    BOOST_CHECK( cancelled->Resolutions().empty() ); cancelled->Destroy(); wxTheApp->ProcessPendingEvents();
    auto* dialog = new DIALOG_DIAGRAM_CONFLICT( nullptr, "PSU", merge );
    bool noDefault = false, partialBlocked = false;
    BOOST_CHECK_EQUAL( show( dialog, [&]
    {
        auto* save = control<wxButton>( dialog, "DiagramConflictSave" );
        auto* resolved = control<wxTextCtrl>( dialog, "DiagramConflictResolved" );
        noDefault = !save->IsEnabled();
        click( control<wxRadioButton>( dialog, "DiagramConflictUseMine" ) );
        waitFor( [&] { return resolved->GetValue() == wxString::FromUTF8( routing->draft() ); } );
        partialBlocked = !save->IsEnabled(); BOOST_CHECK( partialBlocked );
        click( control<wxRadioButton>( dialog, "DiagramConflictWriteMerged" ) );
        resolved->SetFocus(); wxUIActionSimulator keys;
        BOOST_REQUIRE( keys.KeyDown( WXK_CONTROL ) ); BOOST_REQUIRE( keys.KeyDown( 'A' ) );
        BOOST_REQUIRE( keys.KeyUp( 'A' ) ); BOOST_REQUIRE( keys.KeyUp( WXK_CONTROL ) );
        BOOST_REQUIRE( keys.Text( "Keep both edges accessible." ) );
        waitFor( [&] { return resolved->GetValue() == "Keep both edges accessible."; } );
        auto* fields = control<wxChoice>( dialog, "DiagramConflictField" ); fields->SetFocus(); key( WXK_DOWN );
        waitFor( [&] { return fields->GetSelection() == 1; } );
        BOOST_CHECK( !control<wxRadioButton>( dialog, "DiagramConflictUseMine" )->GetValue() );
        click( control<wxRadioButton>( dialog, "DiagramConflictUseSaved" ) );
        waitFor( [&] { return save->IsEnabled() && resolved->GetValue() == "Prefer fixed mounting."; } );
        fields->SetFocus(); key( WXK_UP ); waitFor( [&] { return fields->GetSelection() == 0; } );
        BOOST_CHECK_EQUAL( resolved->GetValue(), "Keep both edges accessible." );
        settle( 300 );
        SHOT resolvedShot = capture( dialog, evidence, "05-conflict-resolved.png" );
        std::tie( saveFill, saveInk ) = primaryContrast( resolvedShot, dialog, save );
        click( save );
    } ), wxID_OK );
    auto choices = dialog->Resolutions(); BOOST_REQUIRE_EQUAL( choices.size(), 2 );
    BOOST_CHECK_EQUAL( choices[0].text(), "Keep both edges accessible." );
    BOOST_CHECK_EQUAL( choices[1].text(), "Prefer fixed mounting." );
    BOOST_CHECK_EQUAL( choices[0].baseline_revision_id(), original->baseline_requirement_revision_id() );
    BOOST_CHECK_EQUAL( choices[0].saved_revision_id(), latest->baseline_requirement_revision_id() );
    BOOST_CHECK_EQUAL( choices[0].draft().routing(), "Place converters at the top edge." );
    BOOST_CHECK_EQUAL( choices[0].saved().routing(), "Place converters at the bottom edge." );
    std::ofstream receipt( evidence / "conflict-interaction.json" );
    receipt << nlohmann::json( { { "no_default_choice", noDefault }, { "partial_resolution_blocked", partialBlocked },
        { "routing_text", choices[0].text() }, { "general_text", choices[1].text() },
        { "base_revision", choices[0].baseline_revision_id() }, { "saved_revision", choices[0].saved_revision_id() },
        { "draft_marked", draftMarked }, { "saved_marked", savedMarked }, { "draft_mark_pixels", draftMarks }, { "saved_mark_pixels", savedMarks },
        { "draft_mark_text_on_fill", draftMarkInk }, { "saved_mark_text_on_fill", savedMarkInk },
        { "base_text_inset", baseInset }, { "draft_text_inset", draftInset }, { "saved_text_inset", savedInset },
        { "save_fill_on_dialog", saveFill }, { "save_label_on_fill", saveInk } } ).dump( 2 );
    BOOST_REQUIRE( receipt.good() ); dialog->Destroy(); wxTheApp->ProcessPendingEvents();
}

BOOST_AUTO_TEST_SUITE_END()
