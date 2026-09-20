/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#define BOOST_TEST_NO_MAIN
#include <boost/test/unit_test.hpp>
#include <qa_utils/wx_utils/unit_test_utils.h>
#include <dialogs/dialog_diagram_field_history.h>
#include <dialogs/dialog_diagram_conflict.h>
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
#include <wx/textctrl.h>
#include <wx/timer.h>
#include <wx/uiaction.h>
#include <gtk/gtk.h>
#include <cstdlib>
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

void capture( wxWindow* aWindow, const std::filesystem::path& aDirectory, const char* aName )
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
    GError* error = nullptr;
    bool written = gdk_pixbuf_save( pixels, ( aDirectory / aName ).c_str(), "png", &error, nullptr );
    g_object_unref( pixels );
    std::string message = error ? error->message : "Native window capture failed";
    if( error ) g_error_free( error );
    BOOST_REQUIRE_MESSAGE( written, message );
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
    std::filesystem::path evidence( outputPath );
    std::filesystem::create_directories( evidence );
    auto text = []( const std::string& value ) { return wxString::FromUTF8( value ); };
    std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> rows;
    for( const auto& entry : page.entries() )
        rows.push_back( { entry.requirement_revision_id(), wxString::Format( "v%u", entry.context_version() ),
                         text( entry.origin().actor() ), text( entry.text() ), wxEmptyString, entry.is_saved_text() } );
    auto* dialog = new DIALOG_DIAGRAM_FIELD_HISTORY( nullptr, "Routing requirements", "PSU",
            wxString::Format( "v%u", page.context_version() ), text( page.saved_text() ), rows );
    dialog->Move( wxPoint( 60, 60 ) );
    auto* list = control<wxListBox>( dialog, "DiagramFieldHistoryRevisions" );
    auto* selected = control<wxTextCtrl>( dialog, "DiagramFieldHistorySelectedText" );
    auto* saved = control<wxTextCtrl>( dialog, "DiagramFieldHistorySavedText" );
    auto* restore = control<wxButton>( dialog, "DiagramFieldHistoryRestore" );
    auto* close = control<wxButton>( dialog, "DiagramFieldHistoryClose" );
    int cancelled = show( dialog, [&]
    {
        capture( dialog, evidence, "01-current.png" );
        list->SetFocus(); key( WXK_DOWN );
        waitFor( [&] { return list->GetSelection() == 1 && selected->GetValue() == text( page.entries( 1 ).text() ); } );
        BOOST_CHECK_EQUAL( saved->GetValue(), text( page.saved_text() ) );
        BOOST_CHECK( !dialog->RestoreRevision().has_value() );
        capture( dialog, evidence, "02-earlier-text.png" );
        key( WXK_ESCAPE );
    } );
    BOOST_CHECK_EQUAL( cancelled, wxID_CANCEL );
    BOOST_CHECK( !dialog->RestoreRevision().has_value() );
    bool cancelledWithoutRestore = cancelled == wxID_CANCEL && !dialog->RestoreRevision().has_value();
    bool compactFits = false;

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
        capture( dialog, evidence, "03-compact.png" );
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
    std::ofstream receipt( evidence / "interaction.json" );
    receipt << nlohmann::json( { { "document_id", page.document_id() }, { "owner_id", page.owner_id() },
        { "context_revision_id", page.context_revision_id() }, { "restore_requirement_revision_id", restored },
        { "cancelled_without_restore", cancelledWithoutRestore }, { "reopen_cleared_restore", reopenCleared },
        { "scope_isolation", scopeIsolation }, { "compact_controls_visible", compactFits } } ).dump( 2 );
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
    BOOST_CHECK_EQUAL( show( cancelled, [&]
    {
        BOOST_CHECK( !control<wxButton>( cancelled, "DiagramConflictSave" )->IsEnabled() );
        BOOST_CHECK( !control<wxRadioButton>( cancelled, "DiagramConflictUseMine" )->GetValue() );
        BOOST_CHECK( !control<wxRadioButton>( cancelled, "DiagramConflictUseSaved" )->GetValue() );
        BOOST_CHECK( !control<wxRadioButton>( cancelled, "DiagramConflictWriteMerged" )->GetValue() );
        capture( cancelled, evidence, "04-conflict-unresolved.png" );
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
        capture( dialog, evidence, "05-conflict-resolved.png" ); click( save );
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
        { "base_revision", choices[0].baseline_revision_id() }, { "saved_revision", choices[0].saved_revision_id() } } ).dump( 2 );
    BOOST_REQUIRE( receipt.good() ); dialog->Destroy(); wxTheApp->ProcessPendingEvents();
}

BOOST_AUTO_TEST_SUITE_END()
