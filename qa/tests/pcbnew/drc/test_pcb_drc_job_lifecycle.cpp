/* Internal worker lifecycle, not qualification of complete design verification. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <advanced_config.h>
#include <api/pcb_drc_job_manager.h>
#include <api/pcb_drc_run_inputs.h>
#include <api/native_state_digest.h>
#include <board.h>
#include <board_commit.h>
#include <tool/tool_manager.h>
#include <pcb_track.h>
#include <netinfo.h>
#include <netclass.h>
#include <footprint.h>
#include <pad.h>
#include <footprint_library_adapter.h>
#include <libraries/library_manager.h>
#include <libraries/library_table.h>
#include <pcb_io/kicad_sexpr/pcb_io_kicad_sexpr.h>
#include <drawing_sheet/ds_data_model.h>
#include <drawing_sheet/ds_data_item.h>
#include <board_design_settings.h>
#include <drc/drc_item.h>
#include <drc/drc_library_inputs.h>
#include <drc/drc_rule_parser.h>
#include <pcb_marker.h>
#include <pcbnew_utils/board_test_utils.h>
#include <project.h>
#include <project/project_file.h>
#include <project/net_settings.h>
#include <settings/settings_manager.h>
#include <json_common.h>
#include <ki_exception.h>
#include <router/pns_routing_settings.h>
#include <zone.h>
#include <fstream>
#include <google/protobuf/util/message_differencer.h>
#include <chrono>
#include <thread>
#include <wx/app.h>
#include <wx/evtloop.h>
#include <wx/fswatcher.h>

using namespace kiapi::automation::v1;
using google::protobuf::util::MessageDifferencer;

struct DRC_CAPTURE_FIXTURE
{
    LIBRARY_MANAGER libraries;
    FOOTPRINT_LIBRARY_ADAPTER adapter{ libraries };
    DS_DATA_MODEL drawing;
    PCB_DRC_CAPTURE_CONTEXT context{ adapter, drawing, KIID() };
    bool libraryOwnerAvailable = true;
    // Library rereads caused by native file notifications (one per owner lookup).
    int libraryResolutions = 0;
    DRC_CAPTURE_FIXTURE() { drawing.ClearList(); drawing.AllowVoidList( true ); }
    PCB_DRC_JOB_MANAGER::AUXILIARY_OBSERVER auxiliaryObserver()
    {
        return [this]( BOARD& ) -> tl::expected<std::string, std::string>
        { return PCB_DRC_AUXILIARY_BASELINE::Capture( context ).Fingerprint(); };
    }
    // Simulates the native editor owner: it runs on this thread and detaches every
    // board before replacing or destroying it (see PCB_EDIT_FRAME::SetBoard).
    void EnableEvents( PCB_DRC_JOB_MANAGER& jobs )
    {
        jobs.EnableNativeEvents( [this]( BOARD& ) -> FOOTPRINT_LIBRARY_ADAPTER*
                                 {
                                     ++libraryResolutions;
                                     return libraryOwnerAvailable ? &adapter : nullptr;
                                 } );
    }
    static void LoseEvents( PCB_DRC_JOB_MANAGER& jobs, BOARD& board ) { jobs.InputEventsLost( &board ); }
    // Delivers a notification through the owner's real handler, as the native
    // watcher does on the owner thread.
    static void DeliverFileEvent( PCB_DRC_JOB_MANAGER& jobs, wxFileSystemWatcherEvent& event )
    { jobs.fileEvent( event ); }
    static int FileSubscribers( const PCB_DRC_JOB_MANAGER& jobs, const std::filesystem::path& directory )
    { return jobs.fileSubscribers( wxString::FromUTF8( directory.string() ) ); }
    static int NativeWatches( const PCB_DRC_JOB_MANAGER& jobs ) { return jobs.nativeWatches(); }
    static void Detach( PCB_DRC_JOB_MANAGER& jobs, BOARD& board ) { jobs.DetachBoard( &board ); }
    static size_t WatchCount( const PCB_DRC_JOB_MANAGER& jobs ) { return jobs.m_watches.size(); }
    // A recovery checkpoint of the editor owner: window activation, or with
    // settingsChanged a settings notification (PCB_EDIT_FRAME::CommonSettingsChanged).
    static void Observe( PCB_DRC_JOB_MANAGER& jobs, BOARD& board, const std::string& epoch,
                         const PCB_DRC_JOB_MANAGER::LIBRARY_OBSERVER& observer = {},
                         const PCB_DRC_JOB_MANAGER::SCHEMATIC_OBSERVER& schematic = {},
                         bool settingsChanged = false )
    { jobs.ObserveInputs( board, epoch, schematic, observer, settingsChanged ); }
    // Why a notification or checkpoint made the receipt stale, without observing
    // anything; empty while it is live.
    static std::string Invalidation( const PCB_DRC_JOB_MANAGER& jobs, const PcbDrcJobState& state )
    { return jobs.invalidation( state.job_id() ); }
    // Counts the observations of the live project settings and custom rules file.
    static void CountProjectObservations( PCB_DRC_JOB_MANAGER& jobs, int& count )
    {
        jobs.m_observeProject = [&count]( const BOARD& board )
        {
            ++count;
            return PCB_DRC_PROJECT_BASELINE::Observe( board );
        };
    }
    static void AddTracks( BOARD& board, int count )
    {
        for( int i = 0; i < count; ++i )
        {
            auto* track = new PCB_TRACK( &board );
            track->SetStart( { i * 10000, 0 } ); track->SetEnd( { i * 10000, 1000000 } );
            track->SetWidth( 10000 ); track->SetLayer( F_Cu ); board.Add( track );
        }
    }

    static StartPcbDrcJob Request( BOARD& board, const std::string& epoch )
    {
        StartPcbDrcJob request;
        request.mutable_document()->set_type( kiapi::common::types::DOCTYPE_PCB );
        request.mutable_document()->set_board_filename( board.GetFileName().ToStdString() );
        request.set_process_epoch( epoch ); request.set_operation_id( KIID().AsStdString() );
        request.mutable_expected_revision()->set_epoch( board.m_Uuid.AsStdString() );
        request.mutable_expected_revision()->set_sequence( board.GetTimeStamp() );
        return request;
    }

    static ReadPcbDrcJob Query( const PcbDrcJobState& state )
    {
        ReadPcbDrcJob query;
        query.mutable_document()->CopyFrom( state.document() );
        query.set_process_epoch( state.process_epoch() ); query.set_job_id( state.job_id() );
        return query;
    }

    static PcbDrcJobState Wait( PCB_DRC_JOB_MANAGER& jobs, BOARD& board, const PcbDrcJobState& start,
                              const PCB_DRC_JOB_MANAGER::LIBRARY_OBSERVER& observer = {} )
    {
        const auto query = Query( start );
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds( 30 );
        auto current = jobs.Read( query, board, start.process_epoch(), {}, observer );
        while( current && !current->worker_finished() && std::chrono::steady_clock::now() < deadline )
        {
            BOOST_CHECK_EQUAL( current->findings_size(), 0 );
            std::this_thread::sleep_for( std::chrono::milliseconds( 5 ) );
            current = jobs.Read( query, board, start.process_epoch(), {}, observer );
        }
        BOOST_REQUIRE_MESSAGE( current.has_value(), ( current ? "" : current.error() ) );
        BOOST_REQUIRE( current->worker_finished() );
        return *current;
    }

    static void DispatchFiles( wxEventLoopBase& loop )
    {
        // Deliver native filesystem notifications without a job read. The
        // assertions after this bounded window, not elapsed time, prove success.
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds( 1 );
        while( std::chrono::steady_clock::now() < deadline )
        {
            loop.DispatchTimeout( 10 );
            wxTheApp->ProcessPendingEvents();
        }
    }
};

BOOST_FIXTURE_TEST_SUITE( PcbDrcJobLifecycle, DRC_CAPTURE_FIXTURE )

BOOST_AUTO_TEST_CASE( HeadlessBoardReplacementDoesNotRetainNativeListeners )
{
    PCB_DRC_JOB_MANAGER jobs( auxiliaryObserver() );
    const std::string epoch = KIID().AsStdString();
    // A worker owns only its snapshot. Destroying the source board while it runs
    // leaves a stale receipt without findings once the worker has stopped.
    auto busy = std::make_unique<BOARD>();
    busy->SetFileName( "headless-replacement.kicad_pcb" );
    AddTracks( *busy, 4000 );
    auto running = jobs.Start( Request( *busy, epoch ), *busy, epoch, context );
    BOOST_REQUIRE_MESSAGE( running.has_value(), ( running ? "" : running.error() ) );
    BOOST_CHECK_EQUAL( WatchCount( jobs ), 0 );
    busy.reset();
    auto board = std::make_unique<BOARD>();
    board->SetFileName( "headless-replacement.kicad_pcb" );
    auto orphan = jobs.Read( Query( *running ), *board, epoch );
    BOOST_REQUIRE_MESSAGE( orphan.has_value(), ( orphan ? "" : orphan.error() ) );
    BOOST_CHECK( orphan->cancellation_requested() );
    BOOST_CHECK_EQUAL( orphan->findings_size(), 0 );
    if( !orphan->worker_finished() )
        BOOST_CHECK( orphan->status() == PDRCJS_QUEUED || orphan->status() == PDRCJS_RUNNING );
    const auto quiesced = Wait( jobs, *board, *running );
    BOOST_CHECK( quiesced.status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( quiesced.error_code(), "document_changed" );
    BOOST_CHECK_EQUAL( quiesced.findings_size(), 0 );

    auto first = jobs.Start( Request( *board, epoch ), *board, epoch, context );
    BOOST_REQUIRE_MESSAGE( first.has_value(), ( first ? "" : first.error() ) );
    BOOST_REQUIRE( Wait( jobs, *board, *first ).status() == PDRCJS_COMPLETED );
    BOOST_CHECK_EQUAL( WatchCount( jobs ), 0 );
    // Keep a failing old implementation safe while recording its regression.
    if( WatchCount( jobs ) ) Detach( jobs, *board );
    board.reset();
    auto replacement = std::make_unique<BOARD>();
    replacement->SetFileName( "headless-replacement.kicad_pcb" );
    auto old = jobs.Read( Query( *first ), *replacement, epoch );
    BOOST_REQUIRE( old );
    BOOST_CHECK( old->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( old->findings_size(), 0 );
    auto second = jobs.Start( Request( *replacement, epoch ), *replacement, epoch, context );
    BOOST_REQUIRE_MESSAGE( second.has_value(), ( second ? "" : second.error() ) );
    BOOST_REQUIRE( Wait( jobs, *replacement, *second ).status() == PDRCJS_COMPLETED );
    BOOST_CHECK_EQUAL( WatchCount( jobs ), 0 );
    if( WatchCount( jobs ) ) Detach( jobs, *replacement );
    replacement.reset(); // The job owner is destroyed afterwards, with no source listener.
}

BOOST_AUTO_TEST_CASE( NativeCommitInvalidatesBeforeReadAndPreservesIndependentOwners )
{
    BOARD board, other;
    board.SetFileName( "native-edit.kicad_pcb" ); other.SetFileName( "independent.kicad_pcb" );
    auto* track = new PCB_TRACK( &board );
    track->SetStart( { 0, 0 } ); track->SetEnd( { 1000000, 0 } ); track->SetWidth( 100000 );
    board.Add( track );
    TOOL_MANAGER tools;
    tools.SetEnvironment( &board, nullptr, nullptr, nullptr, nullptr );
    tools.RegisterTool( new KI_TEST::DUMMY_TOOL );
    int observations = 0;
    const auto ownerThread = std::this_thread::get_id();
    PCB_DRC_JOB_MANAGER jobs( [&]( BOARD& ) -> tl::expected<std::string, std::string>
    {
        BOOST_CHECK( std::this_thread::get_id() == ownerThread );
        ++observations;
        return PCB_DRC_AUXILIARY_BASELINE::Capture( context ).Fingerprint();
    } );
    EnableEvents( jobs );
    PCB_DRC_JOB_MANAGER independent( auxiliaryObserver() );
    EnableEvents( independent );
    const std::string epoch = KIID().AsStdString();
    auto request = Request( board, epoch );
    auto started = jobs.Start( request, board, epoch, context );
    auto separate = independent.Start( Request( other, epoch ), other, epoch, context );
    BOOST_REQUIRE( started ); BOOST_REQUIRE( separate );
    BOOST_REQUIRE( Wait( jobs, board, *started ).status() == PDRCJS_COMPLETED );
    BOOST_REQUIRE( Wait( independent, other, *separate ).status() == PDRCJS_COMPLETED );
    board.OnBoardSelectionChanged(); board.OnRatsnestChanged();
    BOOST_CHECK( jobs.Read( Query( *started ), board, epoch )->status() == PDRCJS_COMPLETED );

    BOARD_COMMIT commit( &tools, true, false );
    commit.Modify( track ); track->SetEnd( { 2000000, 0 } );
    commit.Push( "Native edit", SKIP_UNDO | SKIP_SET_DIRTY | SKIP_TEARDROPS );
    observations = 0;
    auto stale = jobs.Read( Query( *started ), board, epoch );
    BOOST_REQUIRE( stale );
    BOOST_CHECK( stale->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( stale->error_code(), "document_changed" );
    BOOST_CHECK_EQUAL( stale->findings_size(), 0 );
    BOOST_CHECK_EQUAL( observations, 0 ); // The commit already invalidated it.
    BOOST_CHECK( independent.Read( Query( *separate ), other, epoch )->status() == PDRCJS_COMPLETED );

    BOARD_COMMIT restore( &tools, true, false );
    restore.Modify( track ); track->SetEnd( { 1000000, 0 } );
    restore.Push( "Restore geometry", SKIP_UNDO | SKIP_SET_DIRTY | SKIP_TEARDROPS );
    auto replay = jobs.ReadOperation( request, board, epoch );
    BOOST_REQUIRE( replay ); BOOST_REQUIRE( replay->has_value() );
    BOOST_CHECK( ( **replay ).status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( ( **replay ).findings_size(), 0 );
}

BOOST_AUTO_TEST_CASE( NativeChangeCancelsActiveWorkerWithoutPrematureTerminalAcknowledgement )
{
    BOARD board;
    board.SetFileName( "native-cancel.kicad_pcb" );
    AddTracks( board, 4000 );
    PCB_DRC_JOB_MANAGER jobs( auxiliaryObserver() );
    EnableEvents( jobs );
    const auto epoch = KIID().AsStdString();
    auto started = jobs.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE( started );
    board.OnItemChanged( board.Tracks().front() );
    auto current = jobs.Read( Query( *started ), board, epoch );
    BOOST_REQUIRE( current );
    BOOST_CHECK( current->cancellation_requested() );
    BOOST_CHECK_EQUAL( current->findings_size(), 0 );
    if( !current->worker_finished() )
        BOOST_CHECK( current->status() == PDRCJS_QUEUED || current->status() == PDRCJS_RUNNING );
    const auto terminal = Wait( jobs, board, *started );
    BOOST_CHECK( terminal.status() == PDRCJS_STALE );
    BOOST_CHECK( terminal.worker_finished() );
    BOOST_CHECK_EQUAL( terminal.findings_size(), 0 );
}

BOOST_AUTO_TEST_CASE( LostEventsRequireFreshObservationAndDetachedBoardsCannotReviveReceipts )
{
    auto board = std::make_unique<BOARD>();
    board->SetFileName( "lost-events.kicad_pcb" );
    int observations = 0;
    PCB_DRC_JOB_MANAGER jobs( [&]( BOARD& ) -> tl::expected<std::string, std::string>
    {
        ++observations;
        return PCB_DRC_AUXILIARY_BASELINE::Capture( context ).Fingerprint();
    } );
    EnableEvents( jobs );
    const auto epoch = KIID().AsStdString();
    const auto request = Request( *board, epoch );
    auto started = jobs.Start( request, *board, epoch, context );
    BOOST_REQUIRE( started );
    BOOST_REQUIRE( Wait( jobs, *board, *started ).status() == PDRCJS_COMPLETED );
    LoseEvents( jobs, *board );
    observations = 0;
    auto stale = jobs.Read( Query( *started ), *board, epoch );
    BOOST_REQUIRE( stale );
    BOOST_CHECK( stale->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( stale->error_code(), "input_events_lost" );
    BOOST_CHECK_EQUAL( observations, 0 );
    Observe( jobs, *board, epoch );
    auto replay = jobs.ReadOperation( request, *board, epoch );
    BOOST_REQUIRE( replay ); BOOST_REQUIRE( replay->has_value() );
    BOOST_CHECK( ( **replay ).status() == PDRCJS_STALE );
    auto fresh = jobs.Start( Request( *board, epoch ), *board, epoch, context );
    BOOST_REQUIRE( fresh );
    BOOST_CHECK_GT( observations, 0 );
    BOOST_REQUIRE( Wait( jobs, *board, *fresh ).status() == PDRCJS_COMPLETED );
    Detach( jobs, *board );
    board.reset(); // The native owner can replace/delete the source before the manager.
    BOARD replacement;
    auto old = jobs.Read( Query( *fresh ), replacement, epoch );
    BOOST_REQUIRE( old );
    BOOST_CHECK( old->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( old->findings_size(), 0 );
}

BOOST_AUTO_TEST_CASE( NativeLibraryEventsInvalidateAndReadsRejectChangesBeforeEventDispatch )
{
    wxConsoleEventLoop loop;
    wxEventLoopActivator active( &loop );
    KI_TEST::TEMPORARY_DIRECTORY scratch( "drc_events_" + KIID().AsStdString(), "" );
    const auto libPath = scratch.GetPath() / "local.pretty";
    std::filesystem::create_directory( libPath );
    const wxString uri = wxString::FromUTF8( libPath.string() );
    FOOTPRINT original( nullptr );
    original.SetFPID( LIB_ID( "EventLibrary", "Part" ) );
    PCB_IO_KICAD_SEXPR io;
    io.FootprintSave( uri, &original );
    LIBRARY_TABLE table( wxFileName( wxString::FromUTF8( ( scratch.GetPath() / "fp-lib-table" ).string() ) ),
                         LIBRARY_TABLE_SCOPE::PROJECT, LIBRARY_TABLE_TYPE::FOOTPRINT );
    table.SetType( LIBRARY_TABLE_TYPE::FOOTPRINT ); table.SetOk();
    auto& row = table.InsertRow();
    row.SetNickname( "EventLibrary" ); row.SetType( "KiCad" ); row.SetURI( uri );
    // A second library the board also uses: a notification must recheck only the
    // library it names, never every library.
    const auto otherPath = scratch.GetPath() / "other.pretty";
    std::filesystem::create_directory( otherPath );
    const wxString otherUri = wxString::FromUTF8( otherPath.string() );
    FOOTPRINT otherPart( nullptr );
    otherPart.SetFPID( LIB_ID( "OtherLibrary", "Part" ) );
    io.FootprintSave( otherUri, &otherPart );
    auto& otherRow = table.InsertRow();
    otherRow.SetNickname( "OtherLibrary" ); otherRow.SetType( "KiCad" ); otherRow.SetURI( otherUri );
    BOOST_REQUIRE( table.Save().has_value() );
    libraries.LoadProjectTables( wxString::FromUTF8( scratch.GetPath().string() ),
                                  { LIBRARY_TABLE_TYPE::FOOTPRINT } );
    auto loaded = adapter.LoadOne( "EventLibrary" );
    BOOST_REQUIRE( loaded && loaded->load_status == LOAD_STATUS::LOADED );
    auto otherLoaded = adapter.LoadOne( "OtherLibrary" );
    BOOST_REQUIRE( otherLoaded && otherLoaded->load_status == LOAD_STATUS::LOADED );
    BOARD board;
    board.SetFileName( wxString::FromUTF8( ( scratch.GetPath() / "events.kicad_pcb" ).string() ) );
    auto* placed = static_cast<FOOTPRINT*>( original.Clone() );
    placed->SetParent( &board ); board.Add( placed );
    auto* otherPlaced = static_cast<FOOTPRINT*>( otherPart.Clone() );
    otherPlaced->SetParent( &board ); board.Add( otherPlaced );
    int libraryReads = 0, auxiliaryReads = 0, projectObservations = 0;
    PCB_DRC_JOB_MANAGER::LIBRARY_OBSERVER observer = [&]( BOARD& source )
            -> tl::expected<std::string, std::string>
    {
        ++libraryReads;
        if( !libraryOwnerAvailable ) return tl::unexpected( "Native footprint library owner is unavailable" );
        return DRC_LIBRARY_INPUTS::Capture( source, adapter )->ContentFingerprint();
    };
    PCB_DRC_JOB_MANAGER jobs( [&]( BOARD& ) -> tl::expected<std::string, std::string>
    {
        ++auxiliaryReads;
        return PCB_DRC_AUXILIARY_BASELINE::Capture( context ).Fingerprint();
    } );
    EnableEvents( jobs );
    CountProjectObservations( jobs, projectObservations );
    const auto epoch = KIID().AsStdString();
    auto request = Request( board, epoch );
    auto start = jobs.Start( request, board, epoch, context );
    BOOST_REQUIRE_MESSAGE( start.has_value(), ( start ? "" : start.error() ) );
    BOOST_CHECK_EQUAL( start->input_warnings_size(), 0 );
    BOOST_REQUIRE( Wait( jobs, board, *start, observer ).status() == PDRCJS_COMPLETED );
    libraryReads = 0;
    for( int i = 0; i < 10; ++i )
        BOOST_CHECK( jobs.Read( Query( *start ), board, epoch, {}, observer )->status() == PDRCJS_COMPLETED );
    // Native watch registration cannot rule out a queued change. Until a safe
    // completeness barrier exists, every live read must retain content fallback.
    BOOST_CHECK_EQUAL( libraryReads, 10 );
    original.SetLibDescription( "changed before event dispatch" );
    io.FootprintSave( uri, &original );
    auto queued = jobs.Read( Query( *start ), board, epoch, {}, observer );
    BOOST_REQUIRE( queued );
    BOOST_CHECK( queued->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( queued->error_code(), "library_inputs_changed" );
    BOOST_CHECK_EQUAL( queued->findings_size(), 0 );
    original.SetLibDescription( "" ); io.FootprintSave( uri, &original );
    auto queuedReplay = jobs.ReadOperation( request, board, epoch, {}, observer );
    BOOST_REQUIRE( queuedReplay ); BOOST_REQUIRE( queuedReplay->has_value() );
    BOOST_CHECK( ( **queuedReplay ).status() == PDRCJS_STALE );
    DispatchFiles( loop );
    request = Request( board, epoch );
    start = jobs.Start( request, board, epoch, context );
    BOOST_REQUIRE( start );
    BOOST_REQUIRE( Wait( jobs, board, *start, observer ).status() == PDRCJS_COMPLETED );
    { std::ofstream file( scratch.GetPath() / "unrelated.txt" ); file << "unrelated"; }
    DispatchFiles( loop );
    BOOST_CHECK( jobs.Read( Query( *start ), board, epoch, {}, observer )->status() == PDRCJS_COMPLETED );
    // Native serialization generates fresh UUIDs; unchanged content must stay valid.
    io.FootprintSave( uri, &original );
    DispatchFiles( loop );
    BOOST_CHECK( jobs.Read( Query( *start ), board, epoch, {}, observer )->status() == PDRCJS_COMPLETED );

    // Change OtherLibrary in memory only, so no notification names it. A
    // same-content rewrite of EventLibrary must recheck EventLibrary alone: the
    // receipt stays live for the next read, whose full comparison then finds the
    // in-memory change. A recheck of every library would have made it stale early.
    auto otherTableRow = adapter.GetRow( "OtherLibrary" );
    BOOST_REQUIRE( otherTableRow );
    ( *otherTableRow )->SetDisabled( true );
    io.FootprintSave( uri, &original );
    DispatchFiles( loop );
    auxiliaryReads = 0;
    auto observed = jobs.Read( Query( *start ), board, epoch, {}, observer );
    BOOST_REQUIRE( observed );
    BOOST_CHECK_EQUAL( auxiliaryReads, 1 );
    BOOST_CHECK( observed->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( observed->error_code(), "library_inputs_changed" );
    BOOST_CHECK_EQUAL( observed->findings_size(), 0 );
    ( *otherTableRow )->SetDisabled( false );
    request = Request( board, epoch );
    start = jobs.Start( request, board, epoch, context );
    BOOST_REQUIRE( start );
    BOOST_CHECK_EQUAL( start->input_warnings_size(), 0 );
    BOOST_REQUIRE( Wait( jobs, board, *start, observer ).status() == PDRCJS_COMPLETED );
    // A changed OtherLibrary definition reaches the receipt through its own notification.
    otherPart.SetLibDescription( "changed other library" ); io.FootprintSave( otherUri, &otherPart );
    DispatchFiles( loop );
    auxiliaryReads = 0;
    auto otherStale = jobs.Read( Query( *start ), board, epoch, {}, observer );
    BOOST_REQUIRE( otherStale );
    BOOST_CHECK( otherStale->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( otherStale->error_code(), "library_inputs_changed" );
    BOOST_CHECK_EQUAL( auxiliaryReads, 0 );
    otherPart.SetLibDescription( "" ); io.FootprintSave( otherUri, &otherPart );
    DispatchFiles( loop );
    request = Request( board, epoch );
    start = jobs.Start( request, board, epoch, context );
    BOOST_REQUIRE( start );
    BOOST_REQUIRE( Wait( jobs, board, *start, observer ).status() == PDRCJS_COMPLETED );

    const auto file = libPath / "Part.kicad_mod";
    const auto timestamp = std::filesystem::last_write_time( file );
    original.SetLibDescription( "changed native content" ); io.FootprintSave( uri, &original );
    std::filesystem::last_write_time( file, timestamp );
    DispatchFiles( loop ); // No Start/Read/Cancel call between the edit and delivery.
    auxiliaryReads = 0;
    auto stale = jobs.Read( Query( *start ), board, epoch, {}, observer );
    BOOST_REQUIRE( stale );
    BOOST_CHECK( stale->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( stale->error_code(), "library_inputs_changed" );
    BOOST_CHECK_EQUAL( stale->findings_size(), 0 );
    BOOST_CHECK_EQUAL( auxiliaryReads, 0 );
    original.SetLibDescription( "" ); io.FootprintSave( uri, &original );
    DispatchFiles( loop );
    auto replay = jobs.ReadOperation( request, board, epoch, {}, observer );
    BOOST_REQUIRE( replay ); BOOST_REQUIRE( replay->has_value() );
    BOOST_CHECK( ( **replay ).status() == PDRCJS_STALE );
    auto fresh = jobs.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE( fresh );
    BOOST_CHECK( Wait( jobs, board, *fresh, observer ).status() == PDRCJS_COMPLETED );
    libraryOwnerAvailable = false;
    auto unavailable = jobs.Read( Query( *fresh ), board, epoch, {}, observer );
    BOOST_REQUIRE( unavailable );
    BOOST_CHECK( unavailable->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( unavailable->error_code(), "library_inputs_changed" );
    BOOST_CHECK_EQUAL( unavailable->findings_size(), 0 );
    libraryOwnerAvailable = true;
    Observe( jobs, board, epoch, observer );
    BOOST_CHECK( jobs.Read( Query( *fresh ), board, epoch, {}, observer )->status() == PDRCJS_STALE );

    // A window-activation checkpoint observes each live input once for every
    // receipt, and rereads no library whose native notifications cover it while its
    // library configuration is unchanged.
    auto covered = jobs.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE( covered );
    BOOST_CHECK_EQUAL( covered->input_warnings_size(), 0 );
    BOOST_REQUIRE( Wait( jobs, board, *covered, observer ).status() == PDRCJS_COMPLETED );
    auto alsoCovered = jobs.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE( alsoCovered );
    BOOST_CHECK_EQUAL( alsoCovered->input_warnings_size(), 0 );
    BOOST_REQUIRE( Wait( jobs, board, *alsoCovered, observer ).status() == PDRCJS_COMPLETED );
    BOOST_CHECK_EQUAL( WatchCount( jobs ), 2 );
    libraryReads = 0; auxiliaryReads = 0; libraryResolutions = 0; projectObservations = 0;
    Observe( jobs, board, epoch, observer );
    BOOST_CHECK_EQUAL( libraryReads, 0 );
    // One owner lookup serves the library configuration comparison of both receipts.
    // It loads no footprint: a checkpoint reads library content only through the
    // observer, which libraryReads counts.
    BOOST_CHECK_EQUAL( libraryResolutions, 1 );
    BOOST_CHECK_EQUAL( auxiliaryReads, 1 );
    BOOST_CHECK_EQUAL( projectObservations, 1 );
    BOOST_CHECK_EQUAL( Invalidation( jobs, *covered ), "" );
    BOOST_CHECK_EQUAL( Invalidation( jobs, *alsoCovered ), "" );
    // Reads still compare every live input before exposing results (n456d6b796cd7a9a3).
    libraryReads = 0; projectObservations = 0;
    BOOST_CHECK( jobs.Read( Query( *covered ), board, epoch, {}, observer )->status() == PDRCJS_COMPLETED );
    BOOST_CHECK( jobs.Read( Query( *alsoCovered ), board, epoch, {}, observer )->status() == PDRCJS_COMPLETED );
    BOOST_CHECK_EQUAL( libraryReads, 2 );
    BOOST_CHECK_EQUAL( projectObservations, 2 );

    // A changed library file whose notification has not run yet: window activation
    // leaves both receipts to that notification instead of rereading the library.
    original.SetLibDescription( "changed for both receipts" );
    io.FootprintSave( uri, &original );
    libraryReads = 0;
    Observe( jobs, board, epoch, observer );
    BOOST_CHECK_EQUAL( libraryReads, 0 );
    BOOST_CHECK_EQUAL( Invalidation( jobs, *covered ), "" );
    BOOST_CHECK_EQUAL( Invalidation( jobs, *alsoCovered ), "" );

    // One notification of a changed library rereads that library once, not once per
    // receipt, and makes both receipts stale before any read.
    wxFileSystemWatcherEvent modified( wxFSW_EVENT_MODIFY, wxFileName( wxString::FromUTF8( ( libPath / "Part.kicad_mod" ).string() ) ),
                                       wxFileName( wxString::FromUTF8( ( libPath / "Part.kicad_mod" ).string() ) ) );
    libraryResolutions = 0; auxiliaryReads = 0;
    DeliverFileEvent( jobs, modified );
    BOOST_CHECK_EQUAL( libraryResolutions, 1 );
    for( const auto& receipt : { *covered, *alsoCovered } )
    {
        auto changed = jobs.Read( Query( receipt ), board, epoch, {}, observer );
        BOOST_REQUIRE( changed );
        BOOST_CHECK( changed->status() == PDRCJS_STALE );
        BOOST_CHECK_EQUAL( changed->error_code(), "library_inputs_changed" );
        BOOST_CHECK_EQUAL( changed->findings_size(), 0 );
    }
    BOOST_CHECK_EQUAL( auxiliaryReads, 0 ); // The notification, not the reads, made them stale.
    original.SetLibDescription( "" );
    io.FootprintSave( uri, &original );
    DispatchFiles( loop );

    // A settings notification (Configure Paths, preferences) may have changed library
    // configuration in memory, so it compares library content for every receipt,
    // once, whatever their rows look like.
    auto settingsFirst = jobs.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE( settingsFirst );
    BOOST_REQUIRE( Wait( jobs, board, *settingsFirst, observer ).status() == PDRCJS_COMPLETED );
    auto settingsSecond = jobs.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE( settingsSecond );
    BOOST_REQUIRE( Wait( jobs, board, *settingsSecond, observer ).status() == PDRCJS_COMPLETED );
    libraryReads = 0; libraryResolutions = 0;
    Observe( jobs, board, epoch, observer, {}, true );
    BOOST_CHECK_EQUAL( libraryReads, 1 );
    BOOST_CHECK_EQUAL( libraryResolutions, 0 );
    BOOST_CHECK_EQUAL( Invalidation( jobs, *settingsFirst ), "" ); // Same content: still current.
    BOOST_CHECK_EQUAL( Invalidation( jobs, *settingsSecond ), "" );
    original.SetLibDescription( "changed before a settings checkpoint" );
    io.FootprintSave( uri, &original ); // Its notification has not run yet.
    libraryReads = 0;
    Observe( jobs, board, epoch, observer, {}, true );
    BOOST_CHECK_EQUAL( libraryReads, 1 );
    BOOST_CHECK_EQUAL( Invalidation( jobs, *settingsFirst ), "library_inputs_changed" );
    BOOST_CHECK_EQUAL( Invalidation( jobs, *settingsSecond ), "library_inputs_changed" );
    original.SetLibDescription( "" );
    io.FootprintSave( uri, &original );
    DispatchFiles( loop );

    // Library configuration changed in memory produces no file notification: a row
    // disabled, or pointed at another folder as a changed path variable does. Window
    // activation compares each covered receipt's library rows without loading a
    // footprint, finds the change, and then compares the library content once.
    auto eventRow = adapter.GetRow( "EventLibrary" );
    BOOST_REQUIRE( eventRow );
    const std::vector<std::pair<std::function<void()>, std::function<void()>>> rowChanges = {
        { [&] { ( *eventRow )->SetDisabled( true ); }, [&] { ( *eventRow )->SetDisabled( false ); } },
        { [&] { ( *eventRow )->SetURI( otherUri ); }, [&] { ( *eventRow )->SetURI( uri ); } } };
    for( const auto& [change, undo] : rowChanges )
    {
        auto receipt = jobs.Start( Request( board, epoch ), board, epoch, context );
        BOOST_REQUIRE( receipt );
        BOOST_CHECK_EQUAL( receipt->input_warnings_size(), 0 );
        BOOST_REQUIRE( Wait( jobs, board, *receipt, observer ).status() == PDRCJS_COMPLETED );
        change();
        libraryReads = 0; libraryResolutions = 0;
        Observe( jobs, board, epoch, observer );
        BOOST_CHECK_EQUAL( libraryResolutions, 1 );
        BOOST_CHECK_EQUAL( libraryReads, 1 );
        BOOST_CHECK_EQUAL( Invalidation( jobs, *receipt ), "library_inputs_changed" );
        auxiliaryReads = 0;
        auto rowStale = jobs.Read( Query( *receipt ), board, epoch, {}, observer );
        BOOST_REQUIRE( rowStale );
        BOOST_CHECK( rowStale->status() == PDRCJS_STALE );
        BOOST_CHECK_EQUAL( rowStale->error_code(), "library_inputs_changed" );
        BOOST_CHECK_EQUAL( rowStale->findings_size(), 0 );
        BOOST_CHECK_EQUAL( auxiliaryReads, 0 ); // The checkpoint, not this read, made it stale.
        undo();
    }

    // Receipts without native notifications (a headless owner) are rechecked from
    // content at a checkpoint, still with one library read for all of them.
    PCB_DRC_JOB_MANAGER headless( [&]( BOARD& ) -> tl::expected<std::string, std::string>
    {
        ++auxiliaryReads;
        return PCB_DRC_AUXILIARY_BASELINE::Capture( context ).Fingerprint();
    } );
    auto first = headless.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE( first );
    BOOST_REQUIRE( Wait( headless, board, *first, observer ).status() == PDRCJS_COMPLETED );
    auto second = headless.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE( second );
    BOOST_REQUIRE( Wait( headless, board, *second, observer ).status() == PDRCJS_COMPLETED );
    libraryReads = 0; auxiliaryReads = 0;
    Observe( headless, board, epoch, observer );
    BOOST_CHECK_EQUAL( libraryReads, 1 );
    BOOST_CHECK_EQUAL( auxiliaryReads, 1 );
    BOOST_CHECK( headless.Read( Query( *first ), board, epoch, {}, observer )->status() == PDRCJS_COMPLETED );
    BOOST_CHECK( headless.Read( Query( *second ), board, epoch, {}, observer )->status() == PDRCJS_COMPLETED );
}

BOOST_AUTO_TEST_CASE( LostNativeNotificationsStaleEveryReceiptOfTheSharedWatcher )
{
    wxConsoleEventLoop loop;
    wxEventLoopActivator active( &loop );
    KI_TEST::TEMPORARY_DIRECTORY scratch( "drc_lost_events_" + KIID().AsStdString(), "" );
    // One board is checked against custom rules; another uses a footprint library in
    // a watched subfolder. Both depend on the project library table next to them.
    const auto projectPath = scratch.GetPath() / "fixture.kicad_pro";
    const auto rulesPath = scratch.GetPath() / "fixture.kicad_dru";
    { std::ofstream file( projectPath ); file << R"({"meta":{"version":3}})"; }
    { std::ofstream file( rulesPath ); file << "(version 1)\n(rule \"limit\" (constraint clearance (min 0.4mm)))\n"; }
    SETTINGS_MANAGER settings;
    const wxString projectName = wxString::FromUTF8( projectPath.string() );
    BOOST_REQUIRE( settings.LoadProject( projectName, false ) );
    BOARD rulesBoard;
    rulesBoard.SetProject( settings.GetProject( projectName ) );
    rulesBoard.SetFileName( wxString::FromUTF8( ( scratch.GetPath() / "fixture.kicad_pcb" ).string() ) );

    const auto libPath = scratch.GetPath() / "local.pretty";
    std::filesystem::create_directory( libPath );
    const wxString uri = wxString::FromUTF8( libPath.string() );
    FOOTPRINT part( nullptr );
    part.SetFPID( LIB_ID( "EventLibrary", "Part" ) );
    PCB_IO_KICAD_SEXPR io;
    io.FootprintSave( uri, &part );
    LIBRARY_TABLE table( wxFileName( wxString::FromUTF8( ( scratch.GetPath() / "fp-lib-table" ).string() ) ),
                         LIBRARY_TABLE_SCOPE::PROJECT, LIBRARY_TABLE_TYPE::FOOTPRINT );
    table.SetType( LIBRARY_TABLE_TYPE::FOOTPRINT ); table.SetOk();
    auto& row = table.InsertRow();
    row.SetNickname( "EventLibrary" ); row.SetType( "KiCad" ); row.SetURI( uri );
    BOOST_REQUIRE( table.Save().has_value() );
    libraries.LoadProjectTables( wxString::FromUTF8( scratch.GetPath().string() ),
                                  { LIBRARY_TABLE_TYPE::FOOTPRINT } );
    auto loaded = adapter.LoadOne( "EventLibrary" );
    BOOST_REQUIRE( loaded && loaded->load_status == LOAD_STATUS::LOADED );
    BOARD libraryBoard;
    libraryBoard.SetFileName( wxString::FromUTF8( ( scratch.GetPath() / "library.kicad_pcb" ).string() ) );
    auto* placed = static_cast<FOOTPRINT*>( part.Clone() );
    placed->SetParent( &libraryBoard ); libraryBoard.Add( placed );
    PCB_DRC_JOB_MANAGER::LIBRARY_OBSERVER observer = [&]( BOARD& source )
            -> tl::expected<std::string, std::string>
    { return DRC_LIBRARY_INPUTS::Capture( source, adapter )->ContentFingerprint(); };

    int observations = 0;
    PCB_DRC_JOB_MANAGER jobs( [&]( BOARD& ) -> tl::expected<std::string, std::string>
    {
        ++observations;
        return PCB_DRC_AUXILIARY_BASELINE::Capture( context ).Fingerprint();
    } );
    EnableEvents( jobs );
    const auto epoch = KIID().AsStdString();
    auto rules = jobs.Start( Request( rulesBoard, epoch ), rulesBoard, epoch, context );
    BOOST_REQUIRE_MESSAGE( rules.has_value(), ( rules ? "" : rules.error() ) );
    BOOST_CHECK_EQUAL( rules->input_warnings_size(), 0 );
    BOOST_REQUIRE( Wait( jobs, rulesBoard, *rules, observer ).status() == PDRCJS_COMPLETED );
    auto library = jobs.Start( Request( libraryBoard, epoch ), libraryBoard, epoch, context );
    BOOST_REQUIRE_MESSAGE( library.has_value(), ( library ? "" : library.error() ) );
    BOOST_CHECK_EQUAL( library->input_warnings_size(), 0 );
    BOOST_REQUIRE( Wait( jobs, libraryBoard, *library, observer ).status() == PDRCJS_COMPLETED );
    // One native watcher serves both receipts: the folder both depend on is one
    // native watch that both receipts reference.
    BOOST_CHECK_EQUAL( WatchCount( jobs ), 2 );
    BOOST_CHECK_EQUAL( FileSubscribers( jobs, scratch.GetPath() ), 2 );
    BOOST_CHECK_EQUAL( FileSubscribers( jobs, libPath ), 1 );
    BOOST_CHECK_EQUAL( NativeWatches( jobs ), 2 );

    // Renaming a watched folder ends its kernel subscription, and nothing would
    // report later changes below it. Every receipt of the shared watcher becomes
    // stale, including the rules check that never used that folder.
    std::filesystem::rename( libPath, scratch.GetPath() / "moved.pretty" );
    DispatchFiles( loop );
    observations = 0;
    auto lost = jobs.Read( Query( *rules ), rulesBoard, epoch, {}, observer );
    BOOST_REQUIRE( lost );
    BOOST_CHECK( lost->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( lost->error_code(), "input_events_lost" );
    BOOST_CHECK_EQUAL( lost->findings_size(), 0 );
    BOOST_CHECK( lost->worker_finished() );
    auto moved = jobs.Read( Query( *library ), libraryBoard, epoch, {}, observer );
    BOOST_REQUIRE( moved );
    BOOST_CHECK( moved->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( moved->findings_size(), 0 );
    BOOST_CHECK_EQUAL( observations, 0 ); // Notifications, not these reads, made both stale.
    std::filesystem::rename( scratch.GetPath() / "moved.pretty", libPath );
    DispatchFiles( loop );

    // A new check subscribes again, through a fresh native subscription.
    auto fresh = jobs.Start( Request( rulesBoard, epoch ), rulesBoard, epoch, context );
    BOOST_REQUIRE_MESSAGE( fresh.has_value(), ( fresh ? "" : fresh.error() ) );
    BOOST_CHECK_EQUAL( fresh->input_warnings_size(), 0 );
    BOOST_REQUIRE( Wait( jobs, rulesBoard, *fresh ).status() == PDRCJS_COMPLETED );
    BOOST_CHECK_EQUAL( WatchCount( jobs ), 1 );
    BOOST_CHECK_EQUAL( FileSubscribers( jobs, scratch.GetPath() ), 1 );
    BOOST_CHECK_EQUAL( FileSubscribers( jobs, libPath ), 0 );
    BOOST_CHECK_EQUAL( NativeWatches( jobs ), 1 );

    // A kernel queue overflow names no file: nothing proves that no change was missed.
    wxFileSystemWatcherEvent overflow( wxFSW_EVENT_WARNING, wxFSW_WARNING_OVERFLOW,
                                       wxS( "Event queue overflowed" ) );
    DeliverFileEvent( jobs, overflow );
    observations = 0;
    auto overflowed = jobs.Read( Query( *fresh ), rulesBoard, epoch );
    BOOST_REQUIRE( overflowed );
    BOOST_CHECK( overflowed->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( overflowed->error_code(), "input_events_lost" );
    BOOST_CHECK_EQUAL( overflowed->findings_size(), 0 );
    BOOST_CHECK_EQUAL( observations, 0 );

    // The replaced watcher still reports a real rules change to the next check.
    auto later = jobs.Start( Request( rulesBoard, epoch ), rulesBoard, epoch, context );
    BOOST_REQUIRE_MESSAGE( later.has_value(), ( later ? "" : later.error() ) );
    BOOST_CHECK_EQUAL( later->input_warnings_size(), 0 );
    BOOST_REQUIRE( Wait( jobs, rulesBoard, *later ).status() == PDRCJS_COMPLETED );
    BOOST_CHECK_EQUAL( FileSubscribers( jobs, scratch.GetPath() ), 1 );
    BOOST_CHECK_EQUAL( NativeWatches( jobs ), 1 );
    { std::ofstream file( rulesPath ); file << "(version 1)\n(rule \"limit\" (constraint clearance (min 0.5mm)))\n"; }
    DispatchFiles( loop );
    observations = 0;
    auto stale = jobs.Read( Query( *later ), rulesBoard, epoch );
    BOOST_REQUIRE( stale );
    BOOST_CHECK( stale->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( stale->error_code(), "project_inputs_changed" );
    BOOST_CHECK_EQUAL( stale->findings_size(), 0 );
    BOOST_CHECK_EQUAL( observations, 0 ); // The notification, not this read, made it stale.
}

BOOST_AUTO_TEST_CASE( RuleFileNotificationsAndMissedEventRecoveryUseFreshContent )
{
    wxConsoleEventLoop loop;
    wxEventLoopActivator active( &loop );
    KI_TEST::TEMPORARY_DIRECTORY scratch( "drc_rule_events_" + KIID().AsStdString(), "" );
    const auto projectPath = scratch.GetPath() / "fixture.kicad_pro";
    const auto rulesPath = scratch.GetPath() / "fixture.kicad_dru";
    { std::ofstream file( projectPath ); file << R"({"meta":{"version":3}})"; }
    const std::string original = "(version 1)\n(rule \"limit\" (constraint clearance (min 0.4mm)))\n";
    const std::string changed = "(version 1)\n(rule \"limit\" (constraint clearance (min 0.5mm)))\n";
    { std::ofstream file( rulesPath ); file << original; }
    SETTINGS_MANAGER settings;
    const wxString projectName = wxString::FromUTF8( projectPath.string() );
    BOOST_REQUIRE( settings.LoadProject( projectName, false ) );
    BOARD board;
    board.SetProject( settings.GetProject( projectName ) );
    board.SetFileName( wxString::FromUTF8( ( scratch.GetPath() / "fixture.kicad_pcb" ).string() ) );
    int observations = 0;
    PCB_DRC_JOB_MANAGER jobs( [&]( BOARD& ) -> tl::expected<std::string, std::string>
    {
        ++observations;
        return PCB_DRC_AUXILIARY_BASELINE::Capture( context ).Fingerprint();
    } );
    EnableEvents( jobs );
    const auto epoch = KIID().AsStdString();
    const auto request = Request( board, epoch );
    auto start = jobs.Start( request, board, epoch, context );
    BOOST_REQUIRE_MESSAGE( start.has_value(), ( start ? "" : start.error() ) );
    BOOST_CHECK_EQUAL( start->input_warnings_size(), 0 );
    BOOST_REQUIRE( Wait( jobs, board, *start ).status() == PDRCJS_COMPLETED );
    { std::ofstream file( rulesPath ); file << changed; }
    // Do not dispatch any wx event between the file edit and this status read.
    auto queued = jobs.Read( Query( *start ), board, epoch );
    BOOST_REQUIRE( queued );
    BOOST_CHECK( queued->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( queued->error_code(), "project_inputs_changed" );
    BOOST_CHECK_EQUAL( queued->findings_size(), 0 );
    { std::ofstream file( rulesPath ); file << original; }
    auto queuedReplay = jobs.ReadOperation( request, board, epoch );
    BOOST_REQUIRE( queuedReplay ); BOOST_REQUIRE( queuedReplay->has_value() );
    BOOST_CHECK( ( **queuedReplay ).status() == PDRCJS_STALE );
    DispatchFiles( loop );
    start = jobs.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE( start );
    BOOST_REQUIRE( Wait( jobs, board, *start ).status() == PDRCJS_COMPLETED );
    Observe( jobs, board, epoch ); // An unchanged recovery checkpoint preserves the receipt.
    BOOST_CHECK( jobs.Read( Query( *start ), board, epoch )->status() == PDRCJS_COMPLETED );
    const auto timestamp = std::filesystem::last_write_time( rulesPath );
    { std::ofstream file( rulesPath ); file << changed; }
    std::filesystem::last_write_time( rulesPath, timestamp );
    BOOST_REQUIRE_EQUAL( original.size(), changed.size() );
    DispatchFiles( loop );
    observations = 0;
    auto stale = jobs.Read( Query( *start ), board, epoch );
    BOOST_REQUIRE( stale );
    BOOST_CHECK( stale->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( stale->error_code(), "project_inputs_changed" );
    BOOST_CHECK_EQUAL( observations, 0 );
    BOOST_CHECK_EQUAL( stale->findings_size(), 0 );
    { std::ofstream file( rulesPath ); file << original; }
    DispatchFiles( loop );
    auto fresh = jobs.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE( fresh );
    BOOST_REQUIRE( Wait( jobs, board, *fresh ).status() == PDRCJS_COMPLETED );

    // A save rewrites the project file on disk. The check used the in-memory
    // project settings, which every read compares, and they did not change.
    { std::ofstream file( projectPath ); file << R"({"meta":{"version":3}})" << "\n"; }
    DispatchFiles( loop );
    observations = 0;
    auto saved = jobs.Read( Query( *fresh ), board, epoch );
    BOOST_REQUIRE( saved );
    BOOST_CHECK( saved->status() == PDRCJS_COMPLETED );
    BOOST_CHECK_EQUAL( observations, 1 );

    // A changed file without dispatching its event models a suspended native
    // event loop. Reactivation must perform a fresh content observation.
    const auto replacement = scratch.GetPath() / "replacement.kicad_dru";
    { std::ofstream file( replacement ); file << changed; }
    std::filesystem::rename( replacement, rulesPath );
    Observe( jobs, board, epoch );
    observations = 0;
    auto recovered = jobs.Read( Query( *fresh ), board, epoch );
    BOOST_REQUIRE( recovered );
    BOOST_CHECK( recovered->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( recovered->findings_size(), 0 );
    BOOST_CHECK_EQUAL( observations, 0 );
    auto replay = jobs.ReadOperation( request, board, epoch );
    BOOST_REQUIRE( replay ); BOOST_REQUIRE( replay->has_value() );
    BOOST_CHECK( ( **replay ).status() == PDRCJS_STALE );

    // A checkpoint observes the project settings and reads the rules file once for
    // every receipt of the board; each read observes them for its own receipt.
    { std::ofstream file( rulesPath ); file << original; }
    DispatchFiles( loop );
    auto one = jobs.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE( one );
    BOOST_REQUIRE( Wait( jobs, board, *one ).status() == PDRCJS_COMPLETED );
    auto two = jobs.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE( two );
    BOOST_REQUIRE( Wait( jobs, board, *two ).status() == PDRCJS_COMPLETED );
    int projectObservations = 0;
    CountProjectObservations( jobs, projectObservations );
    Observe( jobs, board, epoch );
    BOOST_CHECK_EQUAL( projectObservations, 1 );
    BOOST_CHECK_EQUAL( Invalidation( jobs, *one ), "" );
    BOOST_CHECK_EQUAL( Invalidation( jobs, *two ), "" );
    projectObservations = 0;
    BOOST_CHECK( jobs.Read( Query( *one ), board, epoch )->status() == PDRCJS_COMPLETED );
    BOOST_CHECK( jobs.Read( Query( *two ), board, epoch )->status() == PDRCJS_COMPLETED );
    BOOST_CHECK_EQUAL( projectObservations, 2 );
    { std::ofstream file( rulesPath ); file << changed; } // Its notification has not run yet.
    projectObservations = 0;
    Observe( jobs, board, epoch );
    BOOST_CHECK_EQUAL( projectObservations, 1 ); // One rules file read made both stale.
    BOOST_CHECK_EQUAL( Invalidation( jobs, *one ), "project_inputs_changed" );
    BOOST_CHECK_EQUAL( Invalidation( jobs, *two ), "project_inputs_changed" );
    projectObservations = 0;
    for( const auto& receipt : { *one, *two } )
    {
        auto checkpointStale = jobs.Read( Query( receipt ), board, epoch );
        BOOST_REQUIRE( checkpointStale );
        BOOST_CHECK( checkpointStale->status() == PDRCJS_STALE );
        BOOST_CHECK_EQUAL( checkpointStale->error_code(), "project_inputs_changed" );
        BOOST_CHECK_EQUAL( checkpointStale->findings_size(), 0 );
    }
    BOOST_CHECK_EQUAL( projectObservations, 0 ); // A stale receipt is never observed again.
}

BOOST_AUTO_TEST_CASE( CancellationWaitsForWorkerExitAndReplayBindsEveryArgument )
{
    BOARD board;
    board.SetFileName( "worker-fixture.kicad_pcb" );
    // Real serialization/parsing/checker work gives the immediately requested
    // cancellation a nontrivial job to stop. No fake terminal result or delay
    // is injected into production code.
    for( int i = 0; i < 4000; ++i )
    {
        auto* track = new PCB_TRACK( &board );
        track->SetStart( { i * 10000, 0 } );
        track->SetEnd( { i * 10000, 1000000 } );
        track->SetWidth( 10000 );
        track->SetLayer( F_Cu );
        board.Add( track );
    }
    const std::string epoch = KIID().AsStdString();
    StartPcbDrcJob request;
    request.mutable_document()->set_type( kiapi::common::types::DOCTYPE_PCB );
    request.mutable_document()->set_board_filename( "worker-fixture.kicad_pcb" );
    request.set_operation_id( KIID().AsStdString() );
    request.set_process_epoch( epoch );
    request.mutable_expected_revision()->set_epoch( board.m_Uuid.AsStdString() );
    request.mutable_expected_revision()->set_sequence( board.GetTimeStamp() );

    PCB_DRC_JOB_MANAGER jobs( auxiliaryObserver() );
    auto started = jobs.Start( request, board, epoch, context );
    BOOST_REQUIRE_MESSAGE( started.has_value(), ( started ? "" : started.error() ) );
    CancelPcbDrcJob cancel;
    cancel.mutable_document()->CopyFrom( request.document() );
    cancel.set_job_id( started->job_id() ); cancel.set_process_epoch( epoch );
    auto acknowledgement = jobs.Cancel( cancel, board, epoch );
    BOOST_REQUIRE( acknowledgement );
    BOOST_CHECK( acknowledgement->cancellation_requested() );
    BOOST_CHECK( !acknowledgement->results_fresh() );
    if( !acknowledgement->worker_finished() )
        BOOST_CHECK( acknowledgement->status() == PDRCJS_QUEUED || acknowledgement->status() == PDRCJS_RUNNING );

    ReadPcbDrcJob query;
    query.mutable_document()->CopyFrom( request.document() );
    query.set_job_id( started->job_id() ); query.set_process_epoch( epoch );
    auto wait = [&]
    {
        auto current = jobs.Read( query, board, epoch );
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds( 30 );
        while( current && !current->worker_finished() && std::chrono::steady_clock::now() < deadline )
        {
            BOOST_CHECK_EQUAL( current->findings_size(), 0 );
            std::this_thread::sleep_for( std::chrono::milliseconds( 5 ) );
            current = jobs.Read( query, board, epoch );
        }
        BOOST_REQUIRE( current );
        BOOST_REQUIRE( current->worker_finished() );
        return *current;
    };
    auto terminal = wait();
    BOOST_CHECK( terminal.status() == PDRCJS_CANCELLED );
    BOOST_CHECK_EQUAL( terminal.findings_size(), 0 );
    BOOST_CHECK( !terminal.results_fresh() );
    BOOST_CHECK_EQUAL( board.GetTimeStamp(), request.expected_revision().sequence() );
    BOOST_CHECK_EQUAL( board.Tracks().size(), 4000 );

    auto replay = jobs.Start( request, board, epoch, context );
    BOOST_REQUIRE( replay );
    BOOST_CHECK( MessageDifferencer::Equals( *replay, terminal ) );
    auto altered = request;
    altered.set_report_all_track_errors( !request.report_all_track_errors() );
    BOOST_CHECK( !jobs.Start( altered, board, epoch, context ) );
    altered = request; altered.set_refill_zones( true );
    BOOST_CHECK( !jobs.Start( altered, board, epoch, context ) );
    altered = request; altered.set_test_footprints( true );
    BOOST_CHECK( !jobs.Start( altered, board, epoch, context ) );
    query.set_process_epoch( KIID().AsStdString() );
    BOOST_CHECK( !jobs.Read( query, board, epoch ) );
    BOOST_CHECK( MessageDifferencer::Equals( *jobs.Cancel( cancel, board, epoch ), terminal ) );
}

BOOST_AUTO_TEST_CASE( StaleAdmissionIsRejectedAndCompletedBoardOnlyResultCannotClaimFreshness )
{
    BOARD board;
    board.SetFileName( "worker-fixture.kicad_pcb" );
    PCB_DRC_JOB_MANAGER jobs( auxiliaryObserver() );
    const std::string epoch = KIID().AsStdString();
    StartPcbDrcJob request;
    request.mutable_document()->set_type( kiapi::common::types::DOCTYPE_PCB );
    request.mutable_document()->set_board_filename( "worker-fixture.kicad_pcb" );
    request.set_process_epoch( epoch ); request.set_operation_id( KIID().AsStdString() );
    request.mutable_expected_revision()->set_epoch( board.m_Uuid.AsStdString() );
    request.mutable_expected_revision()->set_sequence( board.GetTimeStamp() + 1 );
    BOOST_CHECK( !jobs.Start( request, board, epoch, context ) );
    request.mutable_expected_revision()->set_sequence( board.GetTimeStamp() );
    auto started = jobs.Start( request, board, epoch, context );
    BOOST_REQUIRE_MESSAGE( started.has_value(), ( started ? "" : started.error() ) );
    ReadPcbDrcJob query;
    query.mutable_document()->CopyFrom( request.document() );
    query.set_process_epoch( epoch ); query.set_job_id( started->job_id() );
    auto state = jobs.Read( query, board, epoch );
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds( 30 );
    while( state && !state->worker_finished() && std::chrono::steady_clock::now() < deadline )
    {
        std::this_thread::sleep_for( std::chrono::milliseconds( 5 ) );
        state = jobs.Read( query, board, epoch );
    }
    BOOST_REQUIRE( state ); BOOST_REQUIRE( state->worker_finished() );
    BOOST_CHECK( state->status() == PDRCJS_COMPLETED );
    BOOST_CHECK_EQUAL( state->progress(), 1.0 );
    BOOST_CHECK_GT( state->findings_size(), 0 );
    bool foundOutline = false;
    for( const auto& finding : state->findings() )
    {
        if( finding.marker().error_type() == kiapi::board::DRCET_INVALID_OUTLINE )
        {
            foundOutline = true;
            BOOST_REQUIRE_GT( finding.marker().items_size(), 0 );
            BOOST_CHECK_EQUAL( finding.marker().items( 0 ).value(), board.m_Uuid.AsStdString() );
        }
    }
    BOOST_CHECK( foundOutline );
    BOOST_CHECK( !state->snapshot_complete() );
    BOOST_CHECK( !state->results_fresh() );
    board.IncrementTimeStamp();
    auto stale = jobs.Read( query, board, epoch );
    BOOST_REQUIRE( stale );
    BOOST_CHECK( stale->status() == PDRCJS_STALE );
    BOOST_CHECK( stale->worker_finished() );
    BOOST_CHECK( !stale->results_fresh() );
    BOOST_CHECK_EQUAL( stale->findings_size(), 0 );
}

BOOST_AUTO_TEST_CASE( ProjectBaselineOwnsSettingsAndDetectsContentChangesWithoutTimestampChanges )
{
    KI_TEST::TEMPORARY_DIRECTORY scratch( "drc_baseline_" + KIID().AsStdString(), "" );
    const auto projectPath = scratch.GetPath() / "fixture.kicad_pro";
    { std::ofstream file( projectPath ); file << R"({"meta":{"version":3}})"; }
    const auto rulesPath = scratch.GetPath() / "fixture.kicad_dru";
    SETTINGS_MANAGER manager;
    const wxString projectName = wxString::FromUTF8( projectPath.string() );
    BOOST_REQUIRE( manager.LoadProject( projectName, false ) );
    PROJECT* project = manager.GetProject( projectName );
    BOOST_REQUIRE( project );
    BOARD board;
    board.SetProject( project );
    const wxString boardName = wxString::FromUTF8( ( scratch.GetPath() / "fixture.kicad_pcb" ).string() );
    board.SetFileName( boardName );
    auto item = DRC_ITEM::Create( DRCE_INVALID_OUTLINE );
    item->SetItems( &board );
    auto* marker = new PCB_MARKER( item, {}, Edge_Cuts );
    board.Add( marker ); marker->SetExcluded( true, "retained explanation" );
    const int sequence = board.GetTimeStamp();
    auto inputs = PCB_DRC_RUN_INPUTS::Capture( board, context );
    BOOST_REQUIRE( inputs );
    const auto absent = inputs->ProjectBaseline();
    inputs.reset(); // A terminal receipt must not need the worker's private inputs.
    BOOST_CHECK( absent.Unchanged( board ) );
    project->GetTextVars()["CHECK_VALUE"] = "changed";
    BOOST_CHECK( !absent.Unchanged( board ) );
    project->GetTextVars().erase( "CHECK_VALUE" );
    BOOST_CHECK( absent.Unchanged( board ) );
    auto& settings = board.GetDesignSettings();
    const int minimum = settings.m_MinClearance;
    settings.m_MinClearance = minimum + 1;
    BOOST_CHECK( !absent.Unchanged( board ) );
    settings.m_MinClearance = minimum;
    BOOST_CHECK( absent.Unchanged( board ) );
    auto netclass = settings.m_NetSettings->GetDefaultNetclass();
    const int clearance = netclass->GetClearance();
    netclass->SetClearance( clearance + 1 );
    BOOST_CHECK( !absent.Unchanged( board ) );
    netclass->SetClearance( clearance );
    BOOST_CHECK( absent.Unchanged( board ) );
    marker->SetExcluded( true, "changed explanation" );
    BOOST_CHECK( !absent.Unchanged( board ) );
    marker->SetExcluded( true, "retained explanation" );
    BOOST_CHECK( absent.Unchanged( board ) );

    const std::string first = "(version 1)\n(rule \"limit\" (constraint clearance (min 0.4mm)))\n";
    const std::string second = "(version 1)\n(rule \"limit\" (constraint clearance (min 0.5mm)))\n";
    { std::ofstream file( rulesPath ); file << first; }
    BOOST_CHECK( !absent.Unchanged( board ) );
    inputs = PCB_DRC_RUN_INPUTS::Capture( board, context );
    BOOST_REQUIRE( inputs );
    const auto present = inputs->ProjectBaseline();
    inputs.reset();
    BOOST_CHECK( present.Unchanged( board ) );
    const auto modified = std::filesystem::last_write_time( rulesPath );
    const auto bytes = std::filesystem::file_size( rulesPath );
    { std::ofstream file( rulesPath ); file << second; }
    std::filesystem::last_write_time( rulesPath, modified );
    BOOST_REQUIRE_EQUAL( std::filesystem::file_size( rulesPath ), bytes );
    BOOST_CHECK( !present.Unchanged( board ) );
    { std::ofstream file( rulesPath ); file << first; }
    BOOST_CHECK( present.Unchanged( board ) );
    board.SetFileName( wxString::FromUTF8( ( scratch.GetPath() / "other.kicad_pcb" ).string() ) );
    BOOST_CHECK( !present.Unchanged( board ) );
    board.SetFileName( boardName );
    BOOST_CHECK( present.Unchanged( board ) );
    BOOST_REQUIRE( std::filesystem::remove( rulesPath ) );
    BOOST_CHECK( !present.Unchanged( board ) );
    BOOST_CHECK( absent.Unchanged( board ) );
    BOOST_REQUIRE( std::filesystem::create_directory( rulesPath ) );
    BOOST_CHECK( !present.Unchanged( board ) );
    BOOST_CHECK( !absent.Unchanged( board ) );
    BOOST_REQUIRE( std::filesystem::remove( rulesPath ) );
    BOOST_CHECK( absent.Unchanged( board ) );
    BOOST_CHECK_EQUAL( board.GetTimeStamp(), sequence );
}

BOOST_AUTO_TEST_CASE( AuxiliaryBaselineRetainsDrawingAndRoutingWithoutBorrowingPrivateInputs )
{
    BOARD board;
    PNS::ROUTING_SETTINGS routing( nullptr, "tools.pns" );
    context.routingSettings = &routing;
    auto* text = new DS_DATA_ITEM_TEXT( "Original drawing text" );
    drawing.Append( text );
    auto inputs = PCB_DRC_RUN_INPUTS::Capture( board, context );
    BOOST_REQUIRE( inputs );
    const auto baseline = inputs->AuxiliaryBaseline();
    inputs.reset();
    BOOST_CHECK( baseline.Unchanged( context ) );
    text->m_TextBase = "Changed drawing text";
    BOOST_CHECK( !baseline.Unchanged( context ) );
    text->m_TextBase = "Original drawing text";
    BOOST_CHECK( baseline.Unchanged( context ) );
    drawing.AllowVoidList( false );
    BOOST_CHECK( !baseline.Unchanged( context ) );
    drawing.AllowVoidList( true );
    BOOST_CHECK( baseline.Unchanged( context ) );
    const KIID drawingId = context.drawingIdentity;
    context.drawingIdentity = KIID();
    BOOST_CHECK( !baseline.Unchanged( context ) );
    context.drawingIdentity = drawingId;
    BOOST_CHECK( baseline.Unchanged( context ) );
    const bool shove = routing.ShoveVias();
    routing.SetShoveVias( !shove );
    BOOST_CHECK( !baseline.Unchanged( context ) );
    routing.SetShoveVias( shove );
    BOOST_CHECK( baseline.Unchanged( context ) );
    context.routingSettings = nullptr;
    BOOST_CHECK( !baseline.Unchanged( context ) );
    const auto absent = PCB_DRC_AUXILIARY_BASELINE::Capture( context );
    BOOST_CHECK( absent.Unchanged( context ) );
    context.routingSettings = &routing;
    BOOST_CHECK( !absent.Unchanged( context ) );
    BOOST_CHECK( baseline.Unchanged( context ) );
}

BOOST_AUTO_TEST_CASE( DrawingAndRouterChangesInvalidateCompletedJobsAndCannotReviveOldReceipts )
{
    BOARD board;
    board.SetFileName( "auxiliary-job.kicad_pcb" );
    PNS::ROUTING_SETTINGS routing( nullptr, "tools.pns" );
    context.routingSettings = &routing;
    auto* text = new DS_DATA_ITEM_TEXT( "Original" ); drawing.Append( text );
    bool available = true;
    PCB_DRC_JOB_MANAGER jobs( [&]( BOARD& ) -> tl::expected<std::string, std::string>
    {
        if( !available ) return tl::unexpected( "Drawing editor is unavailable" );
        return PCB_DRC_AUXILIARY_BASELINE::Capture( context ).Fingerprint();
    } );
    const std::string epoch = KIID().AsStdString();
    const int revision = board.GetTimeStamp();
    const bool initialShove = routing.ShoveVias();
    for( int change = 0; change < 3; ++change )
    {
        StartPcbDrcJob request;
        request.mutable_document()->set_type( kiapi::common::types::DOCTYPE_PCB );
        request.mutable_document()->set_board_filename( "auxiliary-job.kicad_pcb" );
        request.set_process_epoch( epoch ); request.set_operation_id( KIID().AsStdString() );
        request.mutable_expected_revision()->set_epoch( board.m_Uuid.AsStdString() );
        request.mutable_expected_revision()->set_sequence( revision );
        auto started = jobs.Start( request, board, epoch, context );
        BOOST_REQUIRE_MESSAGE( started.has_value(), ( started ? "" : started.error() ) );
        ReadPcbDrcJob query;
        query.mutable_document()->CopyFrom( request.document() );
        query.set_job_id( started->job_id() ); query.set_process_epoch( epoch );
        auto current = jobs.Read( query, board, epoch );
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds( 30 );
        while( current && !current->worker_finished() && std::chrono::steady_clock::now() < deadline )
        {
            std::this_thread::sleep_for( std::chrono::milliseconds( 5 ) );
            current = jobs.Read( query, board, epoch );
        }
        BOOST_REQUIRE( current ); BOOST_REQUIRE( current->worker_finished() );
        BOOST_REQUIRE_MESSAGE( current->status() == PDRCJS_COMPLETED, current->error_message() );
        BOOST_CHECK_GT( current->findings_size(), 0 );
        if( change == 0 ) text->m_TextBase = "Changed";
        else if( change == 1 ) routing.SetShoveVias( !initialShove );
        else available = false;
        auto stale = jobs.Read( query, board, epoch );
        BOOST_REQUIRE( stale );
        BOOST_CHECK( stale->status() == PDRCJS_STALE );
        BOOST_CHECK_EQUAL( stale->error_code(), "auxiliary_inputs_changed" );
        BOOST_CHECK_EQUAL( stale->findings_size(), 0 );
        BOOST_CHECK( !stale->results_fresh() );
        text->m_TextBase = "Original"; routing.SetShoveVias( initialShove ); available = true;
        auto replay = jobs.ReadOperation( request, board, epoch );
        BOOST_REQUIRE( replay ); BOOST_REQUIRE( replay->has_value() );
        BOOST_CHECK( replay->value().status() == PDRCJS_STALE );
        BOOST_CHECK_EQUAL( replay->value().findings_size(), 0 );
        BOOST_CHECK_EQUAL( board.GetTimeStamp(), revision );
    }
}

BOOST_AUTO_TEST_CASE( ProjectChangesClearCompletedFindingsAndOldOperationCannotResurrect )
{
    KI_TEST::TEMPORARY_DIRECTORY scratch( "drc_receipt_inputs_" + KIID().AsStdString(), "" );
    const auto projectPath = scratch.GetPath() / "fixture.kicad_pro";
    { std::ofstream file( projectPath ); file << R"({"meta":{"version":3}})"; }
    const auto rulesPath = scratch.GetPath() / "fixture.kicad_dru";
    SETTINGS_MANAGER manager;
    const wxString projectName = wxString::FromUTF8( projectPath.string() );
    BOOST_REQUIRE( manager.LoadProject( projectName, false ) );
    PROJECT* project = manager.GetProject( projectName );
    BOOST_REQUIRE( project );
    BOARD board;
    board.SetProject( project );
    board.SetFileName( wxString::FromUTF8( ( scratch.GetPath() / "fixture.kicad_pcb" ).string() ) );
    const int sequence = board.GetTimeStamp();
    const std::string epoch = KIID().AsStdString();
    PCB_DRC_JOB_MANAGER jobs( auxiliaryObserver() );
    StartPcbDrcJob request;
    request.mutable_document()->set_type( kiapi::common::types::DOCTYPE_PCB );
    request.mutable_document()->set_board_filename( "fixture.kicad_pcb" );
    request.set_process_epoch( epoch );
    request.mutable_expected_revision()->set_epoch( board.m_Uuid.AsStdString() );
    request.mutable_expected_revision()->set_sequence( sequence );
    for( bool ruleChange : { false, true } )
    {
        request.set_operation_id( KIID().AsStdString() );
        auto started = jobs.Start( request, board, epoch, context );
        BOOST_REQUIRE_MESSAGE( started.has_value(), ( started ? "" : started.error() ) );
        ReadPcbDrcJob query;
        query.mutable_document()->CopyFrom( request.document() );
        query.set_process_epoch( epoch ); query.set_job_id( started->job_id() );
        auto current = jobs.Read( query, board, epoch );
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds( 30 );
        while( current && !current->worker_finished() && std::chrono::steady_clock::now() < deadline )
        {
            std::this_thread::sleep_for( std::chrono::milliseconds( 5 ) );
            current = jobs.Read( query, board, epoch );
        }
        BOOST_REQUIRE( current ); BOOST_REQUIRE( current->worker_finished() );
        BOOST_REQUIRE_MESSAGE( current->status() == PDRCJS_COMPLETED, current->error_message() );
        BOOST_CHECK_GT( current->findings_size(), 0 );
        auto unchanged = jobs.Read( query, board, epoch );
        BOOST_REQUIRE( unchanged );
        BOOST_CHECK( MessageDifferencer::Equals( *current, *unchanged ) );
        if( ruleChange ) { std::ofstream file( rulesPath ); file << "(version 1)"; }
        else project->GetTextVars()["CHECK_VALUE"] = "changed without a board edit";
        auto stale = jobs.Read( query, board, epoch );
        BOOST_REQUIRE( stale );
        BOOST_CHECK( stale->status() == PDRCJS_STALE );
        BOOST_CHECK_EQUAL( stale->error_code(), "project_inputs_changed" );
        BOOST_CHECK_EQUAL( stale->findings_size(), 0 );
        BOOST_CHECK( !stale->results_fresh() && stale->worker_finished() );
        if( ruleChange ) BOOST_REQUIRE( std::filesystem::remove( rulesPath ) );
        else project->GetTextVars().erase( "CHECK_VALUE" );
        auto replay = jobs.ReadOperation( request, board, epoch );
        BOOST_REQUIRE( replay ); BOOST_REQUIRE( replay->has_value() );
        BOOST_CHECK( MessageDifferencer::Equals( replay->value(), *stale ) );
        auto repeated = jobs.Start( request, board, epoch, context );
        BOOST_REQUIRE( repeated );
        BOOST_CHECK( MessageDifferencer::Equals( *repeated, *stale ) );
        BOOST_CHECK_EQUAL( board.GetTimeStamp(), sequence );
    }
}

BOOST_AUTO_TEST_CASE( WorkerFindsActualCopperViolationsWithItsBoardEngineBound )
{
    BOARD board;
    board.SetFileName( "clearance-fixture.kicad_pcb" );
    board.SetCopperLayerCount( 2 );
    auto* netA = new NETINFO_ITEM( &board, "A", 1 );
    auto* netB = new NETINFO_ITEM( &board, "B", 2 );
    board.Add( netA ); board.Add( netB );
    for( int i = 0; i < 20; ++i )
    {
        auto* footprint = new FOOTPRINT( &board );
        footprint->SetPosition( { 0, i * 50000 } );
        board.Add( footprint );
        auto* pad = new PAD( footprint );
        pad->SetPadstackMode( PADSTACK::MODE::NORMAL );
        pad->SetAttribute( PAD_ATTRIB::SMD );
        pad->SetShape( PADSTACK::ALL_LAYERS, PAD_SHAPE::CIRCLE );
        pad->SetSize( PADSTACK::ALL_LAYERS, { 100000, 100000 } );
        pad->SetLayerSet( LSET( { F_Cu } ) );
        pad->SetPosition( footprint->GetPosition() );
        pad->SetNet( i % 2 ? netA : netB );
        footprint->Add( pad );
    }
    const std::string epoch = KIID().AsStdString();
    StartPcbDrcJob request;
    request.mutable_document()->set_type( kiapi::common::types::DOCTYPE_PCB );
    request.mutable_document()->set_board_filename( "clearance-fixture.kicad_pcb" );
    request.set_process_epoch( epoch ); request.set_operation_id( KIID().AsStdString() );
    request.mutable_expected_revision()->set_epoch( board.m_Uuid.AsStdString() );
    request.mutable_expected_revision()->set_sequence( board.GetTimeStamp() );
    PCB_DRC_JOB_MANAGER jobs( auxiliaryObserver() );
    auto started = jobs.Start( request, board, epoch, context );
    BOOST_REQUIRE_MESSAGE( started.has_value(), ( started ? "" : started.error() ) );
    ReadPcbDrcJob query;
    query.mutable_document()->CopyFrom( request.document() );
    query.set_process_epoch( epoch ); query.set_job_id( started->job_id() );
    auto state = jobs.Read( query, board, epoch );
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds( 30 );
    while( state && !state->worker_finished() && std::chrono::steady_clock::now() < deadline )
    {
        std::this_thread::sleep_for( std::chrono::milliseconds( 5 ) );
        state = jobs.Read( query, board, epoch );
    }
    BOOST_REQUIRE( state ); BOOST_REQUIRE( state->worker_finished() );
    BOOST_REQUIRE( state->status() == PDRCJS_COMPLETED );
    int copperViolations = 0;
    for( const auto& finding : state->findings() )
    {
        if( finding.marker().error_type() == kiapi::board::DRCET_CLEARANCE
                || finding.marker().error_type() == kiapi::board::DRCET_SHORTING_ITEMS )
            ++copperViolations;
    }
    BOOST_CHECK_GT( copperViolations, 0 );
    BOOST_CHECK( !state->snapshot_complete() && !state->results_fresh() );
    BOOST_CHECK_EQUAL( board.Footprints().size(), 20 );
    BOOST_CHECK_EQUAL( board.GetTimeStamp(), request.expected_revision().sequence() );
}

BOOST_AUTO_TEST_CASE( WorkerUsesCapturedUnsavedProjectRulesDrawingAndExclusions )
{
    KI_TEST::TEMPORARY_DIRECTORY scratch( "drc_worker_inputs_" + KIID().AsStdString(), "" );
    const auto projectPath = scratch.GetPath() / "fixture.kicad_pro";
    { std::ofstream stream( projectPath ); stream << R"({"meta":{"filename":"fixture.kicad_pro","version":3}})"; }
    const auto rulesPath = scratch.GetPath() / "fixture.kicad_dru";
    { std::ofstream stream( rulesPath ); stream << R"((version 1)
      (rule "captured unsaved limit" (constraint clearance (min ${CHECK_CLEARANCE}))))"; }
    const wxString projectName = wxString::FromUTF8( projectPath.string() );
    SETTINGS_MANAGER manager;
    BOOST_REQUIRE( manager.LoadProject( projectName, false ) );
    PROJECT* project = manager.GetProject( projectName );
    BOOST_REQUIRE( project );
    BOARD board;
    board.SetProject( project );
    board.SetFileName( wxString::FromUTF8( ( scratch.GetPath() / "fixture.kicad_pcb" ).string() ) );
    project->GetTextVars()["CHECK_CLEARANCE"] = "0.9mm";
    auto* netA = new NETINFO_ITEM( &board, "A", 1 );
    auto* netB = new NETINFO_ITEM( &board, "B", 2 );
    board.Add( netA ); board.Add( netB );
    for( int i = 0; i < 2; ++i )
    {
        auto* footprint = new FOOTPRINT( &board );
        footprint->SetPosition( { i * 1000000, 0 } );
        board.Add( footprint );
        auto* pad = new PAD( footprint );
        pad->SetNumber( "1" );
        pad->SetPadstackMode( PADSTACK::MODE::NORMAL );
        pad->SetAttribute( PAD_ATTRIB::SMD );
        pad->SetShape( PADSTACK::ALL_LAYERS, PAD_SHAPE::CIRCLE );
        pad->SetSize( PADSTACK::ALL_LAYERS, { 200000, 200000 } );
        pad->SetLayerSet( LSET( { F_Cu } ) );
        pad->SetPosition( footprint->GetPosition() );
        pad->SetNet( i ? netB : netA );
        footprint->Add( pad );
    }
    auto* text = new DS_DATA_ITEM_TEXT( "${UNRESOLVED_WORKER_SHEET_VAR}" );
    text->SetStart( 10, 10, LT_CORNER ); drawing.Append( text );
    board.GetDesignSettings().m_DRCSeverities[DRCE_UNRESOLVED_VARIABLE] = SEVERITY::RPT_SEVERITY_ERROR;
    auto outline = DRC_ITEM::Create( DRCE_INVALID_OUTLINE );
    outline->SetItems( &board );
    auto* marker = new PCB_MARKER( outline, board.GetBoundingBox().Centre(), Edge_Cuts );
    board.Add( marker ); marker->SetExcluded( true, "reviewed input outline" );
    // Deliberately leave the effective exclusion unsaved in the live marker.
    const auto projectBefore = project->GetProjectFile().CaptureCurrentState();
    const int sequence = board.GetTimeStamp();
    const std::string epoch = KIID().AsStdString();
    StartPcbDrcJob request;
    request.mutable_document()->set_type( kiapi::common::types::DOCTYPE_PCB );
    request.mutable_document()->set_board_filename( "fixture.kicad_pcb" );
    request.set_process_epoch( epoch ); request.set_operation_id( KIID().AsStdString() );
    request.mutable_expected_revision()->set_epoch( board.m_Uuid.AsStdString() );
    request.mutable_expected_revision()->set_sequence( sequence );
    PCB_DRC_JOB_MANAGER jobs( auxiliaryObserver() );
    auto started = jobs.Start( request, board, epoch, context );
    BOOST_REQUIRE_MESSAGE( started.has_value(), ( started ? "" : started.error() ) );
    ReadPcbDrcJob query;
    query.mutable_document()->CopyFrom( request.document() );
    query.set_process_epoch( epoch ); query.set_job_id( started->job_id() );
    auto state = jobs.Read( query, board, epoch );
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds( 30 );
    while( state && !state->worker_finished() && std::chrono::steady_clock::now() < deadline )
    {
        std::this_thread::sleep_for( std::chrono::milliseconds( 5 ) );
        state = jobs.Read( query, board, epoch );
    }
    BOOST_REQUIRE( state ); BOOST_REQUIRE( state->worker_finished() );
    BOOST_REQUIRE_MESSAGE( state->status() == PDRCJS_COMPLETED, state->error_message() );
    bool clearance = false, drawingFound = false, exclusion = false;
    for( const auto& finding : state->findings() )
    {
        clearance |= finding.marker().error_type() == kiapi::board::DRCET_CLEARANCE;
        if( finding.marker().error_type() == kiapi::board::DRCET_UNRESOLVED_VARIABLE )
        {
            drawingFound = true;
            BOOST_REQUIRE_GT( finding.marker().items_size(), 0 );
            // The private rendering proxy must not escape as an object identity.
            for( const auto& id : finding.marker().items() )
                BOOST_CHECK( id.value() == context.drawingIdentity.AsStdString()
                             || id.value() == niluuid.AsStdString() );
        }
        if( finding.marker().error_type() == kiapi::board::DRCET_INVALID_OUTLINE )
            exclusion |= finding.excluded() && finding.comment() == "reviewed input outline";
    }
    BOOST_CHECK( clearance ); BOOST_CHECK( drawingFound ); BOOST_CHECK( exclusion );
    BOOST_CHECK( !state->results_fresh() && !state->snapshot_complete() );
    BOOST_CHECK( project->GetProjectFile().CaptureCurrentState() == projectBefore );
    BOOST_CHECK_EQUAL( board.GetTimeStamp(), sequence );
    BOOST_CHECK_EQUAL( board.Markers().size(), 1 );
    BOOST_CHECK( !std::filesystem::exists( scratch.GetPath() / "fixture.kicad_pcb" ) );
}

// Isolated rule of the worker: custom rules that do not compile, or that declare a newer rules
// format, end the check failed with design_rules_invalid and a message naming the file and the
// item, line and offset or the version, never with findings. A new check after the rules are
// corrected completes with the real copper finding.
BOOST_AUTO_TEST_CASE( UncompilableCustomRulesFailTheCheckAndCorrectedRulesComplete )
{
    KI_TEST::TEMPORARY_DIRECTORY scratch( "drc_invalid_rules_" + KIID().AsStdString(), "" );
    const auto projectPath = scratch.GetPath() / "fixture.kicad_pro";
    { std::ofstream stream( projectPath ); stream << R"({"meta":{"filename":"fixture.kicad_pro","version":3}})"; }
    const auto rulesPath = scratch.GetPath() / "fixture.kicad_dru";
    { std::ofstream stream( rulesPath ); stream << "(version 1)\n(rule \"kept\" (constraint clearance (min 0.3mm)))\n(not_a_rule)\n"; }
    const wxString projectName = wxString::FromUTF8( projectPath.string() );
    SETTINGS_MANAGER manager;
    BOOST_REQUIRE( manager.LoadProject( projectName, false ) );
    PROJECT* project = manager.GetProject( projectName );
    BOOST_REQUIRE( project );
    BOARD board;
    board.SetProject( project );
    board.SetFileName( wxString::FromUTF8( ( scratch.GetPath() / "fixture.kicad_pcb" ).string() ) );
    auto* netA = new NETINFO_ITEM( &board, "A", 1 );
    auto* netB = new NETINFO_ITEM( &board, "B", 2 );
    board.Add( netA ); board.Add( netB );
    for( int i = 0; i < 2; ++i )
    {
        auto* footprint = new FOOTPRINT( &board );
        footprint->SetPosition( { i * 300000, 0 } );
        board.Add( footprint );
        auto* pad = new PAD( footprint );
        pad->SetNumber( "1" );
        pad->SetPadstackMode( PADSTACK::MODE::NORMAL );
        pad->SetAttribute( PAD_ATTRIB::SMD );
        pad->SetShape( PADSTACK::ALL_LAYERS, PAD_SHAPE::CIRCLE );
        pad->SetSize( PADSTACK::ALL_LAYERS, { 200000, 200000 } );
        pad->SetLayerSet( LSET( { F_Cu } ) );
        pad->SetPosition( footprint->GetPosition() );
        pad->SetNet( i ? netB : netA );
        footprint->Add( pad );
    }
    const std::string epoch = KIID().AsStdString();
    PCB_DRC_JOB_MANAGER jobs( auxiliaryObserver() );
    // Capture only reads the rules bytes, so the check is admitted and fails in its worker.
    auto started = jobs.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE_MESSAGE( started.has_value(), ( started ? "" : started.error() ) );
    const PcbDrcJobState failed = Wait( jobs, board, *started );
    BOOST_CHECK( failed.status() == PDRCJS_FAILED );
    BOOST_CHECK_EQUAL( failed.error_code(), "design_rules_invalid" );
    // The item is on line 3 of the file, starting at its second character.
    BOOST_CHECK_MESSAGE( failed.error_message().find( "'not_a_rule'" ) != std::string::npos
                         && failed.error_message().find( "fixture.kicad_dru', line 3, offset 2." ) != std::string::npos,
                         failed.error_message() );
    BOOST_CHECK_EQUAL( failed.findings_size(), 0 );
    BOOST_CHECK_LT( failed.progress(), 1.0 );
    BOOST_CHECK( !failed.results_fresh() && !failed.snapshot_complete() && !failed.cancellation_requested() );
    auto reread = jobs.Read( Query( failed ), board, epoch );
    BOOST_REQUIRE( reread );
    BOOST_CHECK( MessageDifferencer::Equals( *reread, failed ) );

    // Rules that parse but declare a newer rules format are the rules file's problem too, not an
    // internal error.
    { std::ofstream stream( rulesPath ); stream << "(version " << DRC_RULE_FILE_VERSION + 1
                                                << ")\n(rule \"kept\" (constraint clearance (min 0.3mm)))\n"; }
    auto future = jobs.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE_MESSAGE( future.has_value(), ( future ? "" : future.error() ) );
    const PcbDrcJobState newer = Wait( jobs, board, *future );
    BOOST_CHECK( newer.status() == PDRCJS_FAILED );
    BOOST_CHECK_EQUAL( newer.error_code(), "design_rules_invalid" );
    BOOST_CHECK_MESSAGE( newer.error_message().find( "fixture.kicad_dru' declares a design rules version newer than "
                                                     + std::to_string( DRC_RULE_FILE_VERSION ) ) != std::string::npos,
                         newer.error_message() );
    BOOST_CHECK_EQUAL( newer.findings_size(), 0 );
    BOOST_CHECK( !newer.results_fresh() && !newer.snapshot_complete() && !newer.cancellation_requested() );

    { std::ofstream stream( rulesPath ); stream << "(version 1)\n(rule \"kept\" (constraint clearance (min 0.3mm)))\n"; }
    auto corrected = jobs.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE_MESSAGE( corrected.has_value(), ( corrected ? "" : corrected.error() ) );
    const PcbDrcJobState completed = Wait( jobs, board, *corrected );
    BOOST_REQUIRE_MESSAGE( completed.status() == PDRCJS_COMPLETED, completed.error_code() + ": " + completed.error_message() );
    int clearance = 0;
    for( const auto& finding : completed.findings() )
        clearance += finding.marker().error_type() == kiapi::board::DRCET_CLEARANCE;
    BOOST_CHECK_EQUAL( clearance, 1 );
    BOOST_CHECK( !completed.results_fresh() && !completed.snapshot_complete() );
}

// Isolated rule: every failure message a check reports or compares is copied from the exception
// itself. IO_ERROR::what() points into a temporary that is freed before it can be read, so no
// test through a worker could tell a correct message from a lucky read of freed memory.
BOOST_AUTO_TEST_CASE( NativeExceptionMessagesAreCopiedFromTheExceptionItself )
{
    BOOST_CHECK_EQUAL( PcbDrcExceptionMessage( IO_ERROR( wxString::FromUTF8( "Library 電源 is unreadable" ),
                                                         __FILE__, __FUNCTION__, __LINE__ ) ),
                       "Library 電源 is unreadable" );
    const PARSE_ERROR parse( wxT( "Unrecognized item" ), __FILE__, __FUNCTION__, __LINE__,
                             wxT( "fixture.kicad_dru" ), "(not_a_rule)", 3, 2 );
    BOOST_CHECK_EQUAL( PcbDrcExceptionMessage( parse ), parse.Problem().ToStdString( wxConvUTF8 ) );
    BOOST_CHECK_EQUAL( PcbDrcExceptionMessage( std::runtime_error( "Native DRC inputs changed during capture" ) ),
                       "Native DRC inputs changed during capture" );
}

BOOST_AUTO_TEST_CASE( RefillRunsOnThePrivateBoardAndRequiresCapturedRoutingSettings )
{
    BOARD board;
    board.SetFileName( "refill-worker.kicad_pcb" );
    auto* zone = new ZONE( &board );
    zone->SetLayer( F_SilkS );
    zone->AppendCorner( { 0, 0 }, -1 );
    zone->AppendCorner( { 10000000, 0 }, -1 );
    zone->AppendCorner( { 10000000, 10000000 }, -1 );
    zone->AppendCorner( { 0, 10000000 }, -1 );
    zone->SetIslandRemovalMode( ISLAND_REMOVAL_MODE::NEVER );
    board.Add( zone );
    const int revision = board.GetTimeStamp();
    const auto beforeFill = zone->GetFilledPolysList( F_SilkS );
    const int vertices = beforeFill ? beforeFill->TotalVertices() : 0;
    PCB_DRC_JOB_MANAGER jobs( auxiliaryObserver() );
    const std::string epoch = KIID().AsStdString();
    StartPcbDrcJob request;
    request.mutable_document()->set_type( kiapi::common::types::DOCTYPE_PCB );
    request.mutable_document()->set_board_filename( "refill-worker.kicad_pcb" );
    request.set_operation_id( KIID().AsStdString() );
    request.set_process_epoch( epoch );
    request.mutable_expected_revision()->set_epoch( board.m_Uuid.AsStdString() );
    request.mutable_expected_revision()->set_sequence( revision );
    request.set_refill_zones( true );
    auto rejected = jobs.Start( request, board, epoch, context );
    BOOST_CHECK( !rejected );
    PNS::ROUTING_SETTINGS routing( nullptr, "tools.pns" );
    context.routingSettings = &routing;
    auto started = jobs.Start( request, board, epoch, context );
    BOOST_REQUIRE_MESSAGE( started.has_value(), ( started ? "" : started.error() ) );
    ReadPcbDrcJob query;
    query.mutable_document()->CopyFrom( request.document() );
    query.set_job_id( started->job_id() ); query.set_process_epoch( epoch );
    auto state = jobs.Read( query, board, epoch );
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds( 30 );
    while( state && !state->worker_finished() && std::chrono::steady_clock::now() < deadline )
    {
        std::this_thread::sleep_for( std::chrono::milliseconds( 5 ) );
        state = jobs.Read( query, board, epoch );
    }
    BOOST_REQUIRE( state ); BOOST_REQUIRE( state->worker_finished() );
    BOOST_REQUIRE_MESSAGE( state->status() == PDRCJS_COMPLETED, state->error_message() );
    BOOST_CHECK( !state->results_fresh() && !state->snapshot_complete() );
    BOOST_CHECK_EQUAL( board.GetTimeStamp(), revision );
    BOOST_CHECK_EQUAL( zone->GetFilledPolysList( F_SilkS ) ? zone->GetFilledPolysList( F_SilkS )->TotalVertices() : 0,
                       vertices );
    BOOST_CHECK_EQUAL( board.Zones().size(), 1 );
    auto replay = jobs.Start( request, board, epoch, context );
    BOOST_REQUIRE( replay );
    BOOST_CHECK( MessageDifferencer::Equals( *state, *replay ) );
}

BOOST_AUTO_TEST_CASE( NonConvergingRefillStopsTheJobWithoutPublishingCheckFindings )
{
    auto& enabled = const_cast<ADVANCED_CFG&>( ADVANCED_CFG::GetCfg() ).m_ZoneFillIterativeRefill;
    struct RESTORE { bool& value; bool old; ~RESTORE() { value = old; } } restore{ enabled, enabled };
    enabled = true;
    SETTINGS_MANAGER settings;
    std::unique_ptr<BOARD> board;
    KI_TEST::LoadBoard( settings, "zone_refill_convergence_limit", board );
    PCB_DRC_JOB_MANAGER jobs( auxiliaryObserver() );
    const std::string epoch = KIID().AsStdString();
    PNS::ROUTING_SETTINGS routing( nullptr, "tools.pns" );
    context.routingSettings = &routing;
    StartPcbDrcJob request;
    request.mutable_document()->set_type( kiapi::common::types::DOCTYPE_PCB );
    request.mutable_document()->set_board_filename( board->GetFileName().ToStdString() );
    request.set_operation_id( KIID().AsStdString() ); request.set_process_epoch( epoch );
    request.set_refill_zones( true );
    request.mutable_expected_revision()->set_epoch( board->m_Uuid.AsStdString() );
    request.mutable_expected_revision()->set_sequence( board->GetTimeStamp() );
    auto started = jobs.Start( request, *board, epoch, context );
    BOOST_REQUIRE_MESSAGE( started.has_value(), ( started ? "" : started.error() ) );
    ReadPcbDrcJob query;
    query.mutable_document()->CopyFrom( request.document() );
    query.set_job_id( started->job_id() ); query.set_process_epoch( epoch );
    PCB_DRC_JOB_MANAGER::LIBRARY_OBSERVER observeLibraries = [&]( BOARD& source )
            -> tl::expected<std::string, std::string>
    {
        auto captured = DRC_LIBRARY_INPUTS::Capture( source, adapter );
        if( !captured ) return tl::unexpected( "Library observation cancelled" );
        return captured->ContentFingerprint();
    };
    auto state = jobs.Read( query, *board, epoch, {}, observeLibraries );
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds( 30 );
    while( state && !state->worker_finished() && std::chrono::steady_clock::now() < deadline )
    {
        std::this_thread::sleep_for( std::chrono::milliseconds( 5 ) );
        state = jobs.Read( query, *board, epoch, {}, observeLibraries );
    }
    BOOST_REQUIRE( state ); BOOST_REQUIRE( state->worker_finished() );
    BOOST_CHECK( state->status() == PDRCJS_INCOMPLETE );
    BOOST_CHECK_EQUAL( state->error_code(), "refill_not_converged" );
    BOOST_CHECK_EQUAL( state->findings_size(), 0 );
    BOOST_CHECK( !state->results_fresh() && !state->snapshot_complete() );
    BOOST_CHECK_EQUAL( board->GetTimeStamp(), request.expected_revision().sequence() );
}

BOOST_AUTO_TEST_CASE( CapturedSchematicRunsParityAndSameRevisionElectricalChangeInvalidatesIt )
{
    BOARD board;
    board.SetFileName( "parity-worker.kicad_pcb" );
    auto& settings = board.GetDesignSettings();
    for( int code = DRCE_FIRST; code <= DRCE_LAST; ++code )
        settings.m_DRCSeverities[code] = SEVERITY::RPT_SEVERITY_IGNORE;
    settings.m_DRCSeverities[DRCE_MISSING_FOOTPRINT] = SEVERITY::RPT_SEVERITY_ERROR;
    const std::string epoch = KIID().AsStdString();
    StartPcbDrcJob request;
    request.mutable_document()->set_type( kiapi::common::types::DOCTYPE_PCB );
    request.mutable_document()->set_board_filename( "parity-worker.kicad_pcb" );
    request.mutable_document()->mutable_project()->set_path( "/fixture/project" );
    request.mutable_document()->mutable_project()->set_name( "fixture" );
    request.set_operation_id( KIID().AsStdString() ); request.set_process_epoch( epoch );
    request.set_test_footprints( true );
    request.mutable_expected_revision()->set_epoch( board.m_Uuid.AsStdString() );
    request.mutable_expected_revision()->set_sequence( board.GetTimeStamp() );
    PCB_DRC_JOB_MANAGER jobs( auxiliaryObserver() );
    BOOST_CHECK( !jobs.Start( request, board, epoch, context ) );

    SchematicParityNetlistSnapshot captured;
    captured.set_schema_version( 1 );
    auto* source = captured.mutable_source_state();
    source->mutable_document()->set_type( kiapi::common::types::DOCTYPE_SCHEMATIC );
    source->mutable_document()->mutable_project()->CopyFrom( request.document().project() );
    source->mutable_document()->mutable_sheet_path()->add_path()->set_value( KIID().AsStdString() );
    source->mutable_revision()->set_epoch( KIID().AsStdString() );
    source->mutable_revision()->set_sequence( 17 );
    source->set_native_identity( KIID().AsStdString() ); source->set_process_epoch( epoch );
    source->set_state_sha256( std::string( 64, 'a' ) );
    source->set_scope( DLS_SCHEMATIC_HIERARCHY ); source->set_project_settings_included( true );
    captured.set_native_netlist_sexpr( "(export (version E) (design) (components "
            "(comp (ref R1) (value 10k) (footprint Device:R) (sheetpath (names /) (tstamps /)) "
            "(tstamps e74fd410-f341-421a-b332-a39f523c96f6))) (libparts) (libraries) (nets))" );
    NATIVE_STATE_DIGEST digest; digest.Append( captured.native_netlist_sexpr() );
    captured.set_netlist_sha256( digest.Hex() );
    captured.add_warnings( "fixture intentional duplicate sheet names" );
    request.mutable_expected_schematic_state()->CopyFrom( *source );
    context.schematic = &captured;
    auto currentSchematic = *source;
    auto observer = [&]( const kiapi::common::types::DocumentSpecifier& document )
            -> tl::expected<DocumentLifecycleState, std::string>
    {
        BOOST_CHECK( MessageDifferencer::Equals( document, currentSchematic.document() ) );
        return currentSchematic;
    };
    auto started = jobs.Start( request, board, epoch, context );
    BOOST_REQUIRE_MESSAGE( started.has_value(), ( started ? "" : started.error() ) );
    const SchematicParityNetlistSnapshot recaptured = captured;
    // No capture object or export buffer is borrowed by the background worker.
    captured.Clear(); context.schematic = nullptr;
    ReadPcbDrcJob query;
    query.mutable_document()->CopyFrom( request.document() );
    query.set_job_id( started->job_id() ); query.set_process_epoch( epoch );
    auto state = jobs.Read( query, board, epoch, observer );
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds( 30 );
    while( state && !state->worker_finished() && std::chrono::steady_clock::now() < deadline )
    {
        std::this_thread::sleep_for( std::chrono::milliseconds( 5 ) );
        state = jobs.Read( query, board, epoch, observer );
    }
    BOOST_REQUIRE( state ); BOOST_REQUIRE( state->worker_finished() );
    BOOST_REQUIRE_MESSAGE( state->status() == PDRCJS_COMPLETED, state->error_message() );
    BOOST_REQUIRE_EQUAL( state->findings_size(), 1 );
    BOOST_CHECK( state->findings( 0 ).marker().error_type() == kiapi::board::DRCET_MISSING_FOOTPRINT );
    BOOST_CHECK( MessageDifferencer::Equals( state->checked_schematic_state(), currentSchematic ) );
    BOOST_REQUIRE_EQUAL( state->input_warnings_size(), 1 );
    BOOST_CHECK_EQUAL( state->input_warnings( 0 ), "fixture intentional duplicate sheet names" );
    BOOST_CHECK( !state->results_fresh() && !state->snapshot_complete() );

    // A recovery checkpoint observes the parity schematic once for all its receipts.
    auto again = request;
    again.set_operation_id( KIID().AsStdString() );
    captured = recaptured; context.schematic = &captured;
    auto second = jobs.Start( again, board, epoch, context );
    BOOST_REQUIRE_MESSAGE( second.has_value(), ( second ? "" : second.error() ) );
    captured.Clear(); context.schematic = nullptr;
    ReadPcbDrcJob secondQuery = query;
    secondQuery.set_job_id( second->job_id() );
    auto secondState = jobs.Read( secondQuery, board, epoch, observer );
    const auto secondDeadline = std::chrono::steady_clock::now() + std::chrono::seconds( 30 );
    while( secondState && !secondState->worker_finished() && std::chrono::steady_clock::now() < secondDeadline )
    {
        std::this_thread::sleep_for( std::chrono::milliseconds( 5 ) );
        secondState = jobs.Read( secondQuery, board, epoch, observer );
    }
    BOOST_REQUIRE( secondState ); BOOST_REQUIRE( secondState->worker_finished() );
    BOOST_REQUIRE_MESSAGE( secondState->status() == PDRCJS_COMPLETED, secondState->error_message() );
    int schematicReads = 0;
    Observe( jobs, board, epoch, {},
             [&]( const kiapi::common::types::DocumentSpecifier& document )
                     -> tl::expected<DocumentLifecycleState, std::string>
             {
                 ++schematicReads;
                 return observer( document );
             } );
    BOOST_CHECK_EQUAL( schematicReads, 1 );
    BOOST_CHECK( jobs.Read( query, board, epoch, observer )->status() == PDRCJS_COMPLETED );
    BOOST_CHECK( jobs.Read( secondQuery, board, epoch, observer )->status() == PDRCJS_COMPLETED );

    // Writer digest, not just the journal cursor, protects against untracked edits.
    currentSchematic.set_state_sha256( std::string( 64, 'b' ) );
    auto stale = jobs.Read( query, board, epoch, observer );
    BOOST_REQUIRE( stale );
    BOOST_CHECK( stale->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( stale->error_code(), "schematic_changed" );
    BOOST_CHECK_EQUAL( stale->findings_size(), 0 );
    currentSchematic = request.expected_schematic_state();
    BOOST_CHECK( jobs.Read( query, board, epoch, observer )->status() == PDRCJS_STALE );
    auto replay = jobs.ReadOperation( request, board, epoch, observer );
    BOOST_REQUIRE( replay ); BOOST_REQUIRE( replay->has_value() );
    BOOST_CHECK( ( **replay ).status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( ( **replay ).job_id(), started->job_id() );
    auto changedRequest = request; changedRequest.set_allow_duplicate_sheet_names( true );
    BOOST_CHECK( !jobs.ReadOperation( changedRequest, board, epoch, observer ) );
    BOOST_CHECK_EQUAL( board.GetTimeStamp(), request.expected_revision().sequence() );
}

BOOST_AUTO_TEST_SUITE_END()
