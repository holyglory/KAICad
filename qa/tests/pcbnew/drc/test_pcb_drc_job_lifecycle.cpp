/* Internal worker lifecycle, not qualification of complete design verification. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <advanced_config.h>
#include <api/api_handler_pcb.h>
#include <api/api_server.h>
#include <api/headless_pcb_context.h>
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
#include <pcb_text.h>
#include <title_block.h>
#include <pgm_base.h>
#include <pcbnew_utils/board_test_utils.h>
#include <project.h>
#include <project/project_file.h>
#include <project/net_settings.h>
#include <settings/settings_manager.h>
#include <json_common.h>
#include <ki_exception.h>
#include <router/pns_routing_settings.h>
#include <teardrop/teardrop.h>
#include <api/board/board_types.pb.h>
#include <zone.h>
#include <fstream>
#include <functional>
#include <git2.h>
#include <git/git_backend.h>
#include <git/libgit_backend.h>
#include <font/font.h>
#include <font/outline_font.h>
#include <qa_utils/wx_utils/unit_test_utils.h>
#include <text_eval/text_eval_wrapper.h>
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

    // Every unfinished check reports this gap: its findings are matched to objects of the
    // open board only once it has finished.
    static constexpr const char* PENDING_IDENTITY = "snapshot_incomplete: finding_identity_pending: ";

    // The input warnings of a job state other than that pending gap, after checking that the
    // state carries the gap exactly while its worker runs, and that an unfinished check then
    // claims neither a complete snapshot nor fresh results.
    static std::vector<std::string> Notes( const PcbDrcJobState& aState )
    {
        std::vector<std::string> notes;
        int pending = 0;
        for( const auto& warning : aState.input_warnings() )
        {
            if( warning.rfind( PENDING_IDENTITY, 0 ) == 0 ) ++pending;
            else notes.push_back( warning );
        }
        BOOST_CHECK_EQUAL( pending, aState.worker_finished() ? 0 : 1 );
        if( !aState.worker_finished() )
            BOOST_CHECK( !aState.snapshot_complete() && !aState.results_fresh() );
        return notes;
    }

    static PcbDrcJobState Wait( PCB_DRC_JOB_MANAGER& jobs, BOARD& board, const PcbDrcJobState& start,
                              const PCB_DRC_JOB_MANAGER::LIBRARY_OBSERVER& observer = {} )
    {
        const auto query = Query( start );
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds( 30 );
        Notes( start );
        auto current = jobs.Read( query, board, start.process_epoch(), {}, observer );
        while( current && !current->worker_finished() && std::chrono::steady_clock::now() < deadline )
        {
            BOOST_CHECK_EQUAL( current->findings_size(), 0 );
            Notes( *current );
            std::this_thread::sleep_for( std::chrono::milliseconds( 5 ) );
            current = jobs.Read( query, board, start.process_epoch(), {}, observer );
        }
        BOOST_REQUIRE_MESSAGE( current.has_value(), ( current ? "" : current.error() ) );
        BOOST_REQUIRE( current->worker_finished() );
        Notes( *current );
        return *current;
    }

    // Replaces the job manager's observation of the live project inputs, as a read or
    // checkpoint makes it.
    static void ObserveProjectAs( PCB_DRC_JOB_MANAGER& jobs,
                                  std::function<PCB_DRC_PROJECT_OBSERVATION( const BOARD& )> observe )
    {
        jobs.m_observeProject = std::move( observe );
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

// PGM_BASE keeps its API server protected; a derived class may name that member for any
// program. The scope gives this test process the API server identity that every native request
// handler reads, and takes it away again however the test ends.
struct TEST_API_SERVER_SCOPE
{
    struct ACCESS : PGM_BASE
    {
        static std::unique_ptr<KICAD_API_SERVER>& Of( PGM_BASE& aProgram )
        {
            return aProgram.*( &ACCESS::m_api_server );
        }
    };

    TEST_API_SERVER_SCOPE()
    {
        BOOST_REQUIRE( !ACCESS::Of( Pgm() ) );
        ACCESS::Of( Pgm() ) = std::make_unique<KICAD_API_SERVER>( false );
    }

    ~TEST_API_SERVER_SCOPE() { ACCESS::Of( Pgm() ).reset(); }
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
    BOOST_CHECK( Notes( *start ).empty() );
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
    BOOST_CHECK( Notes( *start ).empty() );
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

    BOOST_CHECK( jobs.Read( Query( *start ), board, epoch, {}, observer )->results_fresh() );
    const auto file = libPath / "Part.kicad_mod";
    const auto timestamp = std::filesystem::last_write_time( file );
    const auto bytes = std::filesystem::file_size( file );
    // Same size and modification time: only the content says the library changed. The
    // footprint's own layer changes in place, from aFrom to aTo.
    auto editInPlace = [&]( const std::string& aFrom, const std::string& aTo )
    {
        std::string content;
        {
            std::ifstream in( file, std::ios::binary );
            content.assign( std::istreambuf_iterator<char>( in ), std::istreambuf_iterator<char>() );
        }
        const auto layer = content.find( aFrom );
        BOOST_REQUIRE( layer != std::string::npos );
        content.replace( layer, aFrom.size(), aTo );
        { std::ofstream out( file, std::ios::binary | std::ios::trunc ); out << content; }
        std::filesystem::last_write_time( file, timestamp );
        BOOST_REQUIRE_EQUAL( std::filesystem::file_size( file ), bytes );
        BOOST_REQUIRE( std::filesystem::last_write_time( file ) == timestamp );
    };
    const std::string front = "(layer \"F.Cu\")", back = "(layer \"B.Cu\")";
    // A read right after the edit, before KiCad has delivered its notification: only the
    // read's own comparison of the library content can see it (n456d6b796cd7a9a3).
    editInPlace( front, back );
    auto unnotified = jobs.Read( Query( *start ), board, epoch, {}, observer );
    BOOST_REQUIRE( unnotified );
    BOOST_CHECK( unnotified->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( unnotified->error_code(), "library_inputs_changed" );
    BOOST_CHECK_EQUAL( unnotified->findings_size(), 0 );
    BOOST_CHECK( unnotified->snapshot_complete() && !unnotified->results_fresh() );
    // Put back the same way; a new check of it is fresh. Then the same edit again, now
    // delivered by its notification before any read.
    editInPlace( back, front );
    DispatchFiles( loop );
    request = Request( board, epoch );
    start = jobs.Start( request, board, epoch, context );
    BOOST_REQUIRE( start );
    BOOST_REQUIRE( Wait( jobs, board, *start, observer ).status() == PDRCJS_COMPLETED );
    BOOST_CHECK( jobs.Read( Query( *start ), board, epoch, {}, observer )->results_fresh() );
    editInPlace( front, back );
    DispatchFiles( loop ); // No Start/Read/Cancel call between the edit and delivery.
    auxiliaryReads = 0;
    auto stale = jobs.Read( Query( *start ), board, epoch, {}, observer );
    BOOST_REQUIRE( stale );
    BOOST_CHECK( stale->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( stale->error_code(), "library_inputs_changed" );
    BOOST_CHECK_EQUAL( stale->findings_size(), 0 );
    BOOST_CHECK( stale->snapshot_complete() && !stale->results_fresh() );
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
    BOOST_CHECK( Notes( *covered ).empty() );
    BOOST_REQUIRE( Wait( jobs, board, *covered, observer ).status() == PDRCJS_COMPLETED );
    auto alsoCovered = jobs.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE( alsoCovered );
    BOOST_CHECK( Notes( *alsoCovered ).empty() );
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
        BOOST_CHECK( Notes( *receipt ).empty() );
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
    BOOST_CHECK( Notes( *rules ).empty() );
    BOOST_REQUIRE( Wait( jobs, rulesBoard, *rules, observer ).status() == PDRCJS_COMPLETED );
    auto library = jobs.Start( Request( libraryBoard, epoch ), libraryBoard, epoch, context );
    BOOST_REQUIRE_MESSAGE( library.has_value(), ( library ? "" : library.error() ) );
    BOOST_CHECK( Notes( *library ).empty() );
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
    BOOST_CHECK( Notes( *fresh ).empty() );
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
    BOOST_CHECK( Notes( *later ).empty() );
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
    BOOST_CHECK( Notes( *start ).empty() );
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
    BOOST_CHECK( jobs.Read( Query( *start ), board, epoch )->results_fresh() );
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
    BOOST_CHECK( stale->snapshot_complete() && !stale->results_fresh() );
    { std::ofstream file( rulesPath ); file << original; }
    DispatchFiles( loop );
    auto fresh = jobs.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE( fresh );
    BOOST_REQUIRE( Wait( jobs, board, *fresh ).status() == PDRCJS_COMPLETED );
    BOOST_CHECK( jobs.Read( Query( *fresh ), board, epoch )->results_fresh() );

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
    BOOST_CHECK( !recovered->results_fresh() );
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
    Notes( *acknowledgement ); // Not yet stopped: no complete snapshot, and it says why.
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
    // The capture was complete; a cancelled check still has no results to be fresh, and with no
    // findings it no longer waits for their identities.
    BOOST_CHECK( terminal.snapshot_complete() );
    BOOST_CHECK( !terminal.results_fresh() );
    BOOST_CHECK( Notes( terminal ).empty() );
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

// One rule for every check (p23deb822a36256a6): a board whose every input was captured exactly
// has a complete snapshot, and its completed check is fresh until the board changes. A stale check
// keeps its complete snapshot but never fresh results.
BOOST_AUTO_TEST_CASE( StaleAdmissionIsRejectedAndACompletedCheckIsFreshUntilTheBoardChanges )
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
    BOOST_CHECK( state->snapshot_complete() );
    BOOST_CHECK( state->results_fresh() );
    BOOST_CHECK( Notes( *state ).empty() );
    auto again = jobs.Read( query, board, epoch );
    BOOST_REQUIRE( again );
    BOOST_CHECK( MessageDifferencer::Equals( *state, *again ) );
    board.IncrementTimeStamp();
    auto stale = jobs.Read( query, board, epoch );
    BOOST_REQUIRE( stale );
    BOOST_CHECK( stale->status() == PDRCJS_STALE );
    BOOST_CHECK( stale->worker_finished() );
    BOOST_CHECK( stale->snapshot_complete() );
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
    // The editor's current variant lives only in memory; switching it is no board edit.
    board.AddVariant( "Assembly" );
    BOOST_CHECK( absent.Unchanged( board ) );
    board.SetCurrentVariant( "Assembly" );
    BOOST_CHECK( !absent.Unchanged( board ) );
    board.SetCurrentVariant( wxEmptyString );
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
        BOOST_CHECK( current->snapshot_complete() && current->results_fresh() );
        if( change == 0 ) text->m_TextBase = "Changed";
        else if( change == 1 ) routing.SetShoveVias( !initialShove );
        else available = false;
        auto stale = jobs.Read( query, board, epoch );
        BOOST_REQUIRE( stale );
        BOOST_CHECK( stale->status() == PDRCJS_STALE );
        BOOST_CHECK_EQUAL( stale->error_code(), "auxiliary_inputs_changed" );
        BOOST_CHECK_EQUAL( stale->findings_size(), 0 );
        BOOST_CHECK( stale->snapshot_complete() && !stale->results_fresh() );
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
    board.AddVariant( "Assembly" );
    // A project text variable, the custom rules file, and the board's current variant, which
    // lives only in memory: none of them is a board edit.
    for( int change = 0; change < 3; ++change )
    {
        const bool ruleChange = change == 1;
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
        BOOST_CHECK( current->snapshot_complete() && current->results_fresh() );
        auto unchanged = jobs.Read( query, board, epoch );
        BOOST_REQUIRE( unchanged );
        BOOST_CHECK( MessageDifferencer::Equals( *current, *unchanged ) );
        if( ruleChange ) { std::ofstream file( rulesPath ); file << "(version 1)"; }
        else if( change == 2 ) board.SetCurrentVariant( "Assembly" );
        else project->GetTextVars()["CHECK_VALUE"] = "changed without a board edit";
        auto stale = jobs.Read( query, board, epoch );
        BOOST_REQUIRE( stale );
        BOOST_CHECK( stale->status() == PDRCJS_STALE );
        BOOST_CHECK_EQUAL( stale->error_code(), "project_inputs_changed" );
        BOOST_CHECK_EQUAL( stale->findings_size(), 0 );
        BOOST_CHECK( !stale->results_fresh() && stale->worker_finished() && stale->snapshot_complete() );
        if( ruleChange ) BOOST_REQUIRE( std::filesystem::remove( rulesPath ) );
        else if( change == 2 ) board.SetCurrentVariant( wxEmptyString );
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
    BOOST_CHECK( state->snapshot_complete() && state->results_fresh() );
    // Every finding names objects of the open board, never an identity private to the check.
    for( const auto& finding : state->findings() )
        for( const auto& id : finding.marker().items() )
            BOOST_CHECK_MESSAGE( board.ResolveItem( KIID( id.value() ), true ), "Private identity " << id.value() );
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
    BOOST_CHECK( state->results_fresh() && state->snapshot_complete() );
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
    // The rules text was captured exactly; a failed check has no results to be fresh.
    BOOST_CHECK( !failed.results_fresh() && failed.snapshot_complete() && !failed.cancellation_requested() );
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
    BOOST_CHECK( !newer.results_fresh() && newer.snapshot_complete() && !newer.cancellation_requested() );

    { std::ofstream stream( rulesPath ); stream << "(version 1)\n(rule \"kept\" (constraint clearance (min 0.3mm)))\n"; }
    auto corrected = jobs.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE_MESSAGE( corrected.has_value(), ( corrected ? "" : corrected.error() ) );
    const PcbDrcJobState completed = Wait( jobs, board, *corrected );
    BOOST_REQUIRE_MESSAGE( completed.status() == PDRCJS_COMPLETED, completed.error_code() + ": " + completed.error_message() );
    int clearance = 0;
    for( const auto& finding : completed.findings() )
        clearance += finding.marker().error_type() == kiapi::board::DRCET_CLEARANCE;
    BOOST_CHECK_EQUAL( clearance, 1 );
    BOOST_CHECK( completed.results_fresh() && completed.snapshot_complete() );
}

// Isolated rule: every failure message a check reports or compares is copied from the exception
// itself, an IO_ERROR's Problem() text rather than the pointer IO_ERROR::what() returns into the
// conversion cache of that string. With the wxWidgets 3.2 this build uses, both give the same
// text, so these checks prove the copied text is the exception's real problem text; they cannot
// tell a return to what() apart, and no sanitizer would either, because nothing is freed.
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

    // The PCB editor's document state read (kicad_document_state) follows the same rule
    // (p10897cd52e677d15). A via of no defined type is a board KiCad's own writer refuses with an
    // IO_ERROR, so reading that board's state must fail with the writer's own problem text, as the
    // rule above describes.
    KI_TEST::TEMPORARY_DIRECTORY scratch( "pcb_state_error_" + KIID().AsStdString(), "" );
    const auto projectPath = scratch.GetPath() / "fixture.kicad_pro";
    { std::ofstream file( projectPath ); file << R"({"meta":{"version":3}})"; }
    SETTINGS_MANAGER manager;
    const wxString projectName = wxString::FromUTF8( projectPath.string() );
    BOOST_REQUIRE( manager.LoadProject( projectName, false ) );
    PROJECT* project = manager.GetProject( projectName );
    BOOST_REQUIRE( project );
    auto owned = std::make_unique<BOARD>();
    BOARD& board = *owned;
    board.SetProject( project );
    board.SetFileName( wxString::FromUTF8( ( scratch.GetPath() / "fixture.kicad_pcb" ).string() ) );
    auto* via = new PCB_VIA( &board );
    via->SetViaType( VIATYPE::NOT_DEFINED );
    board.Add( via );
    std::string problem;
    try
    {
        NATIVE_STATE_DIGEST discarded;
        PCB_IO_KICAD_SEXPR().FormatBoardToFormatter( &discarded, &board, nullptr, false );
    }
    catch( const IO_ERROR& error )
    {
        problem = error.Problem().ToStdString( wxConvUTF8 );
    }
    BOOST_REQUIRE_MESSAGE( problem.find( "unknown via type" ) != std::string::npos,
                           "KiCad's board writer must refuse the undefined via: " << problem );
    auto headless = std::make_shared<HEADLESS_PCB_CONTEXT>( std::move( owned ), project, nullptr );
    API_HANDLER_PCB handler( headless );
    ReadDocumentLifecycleState read;
    read.mutable_document()->set_type( kiapi::common::types::DOCTYPE_PCB );
    read.mutable_document()->set_board_filename( "fixture.kicad_pcb" );
    kiapi::common::ApiRequest request;
    request.mutable_header()->set_client_name( "kicad.qa" );
    BOOST_REQUIRE( request.mutable_message()->PackFrom( read ) );
    API_RESULT result = [&]
    {
        TEST_API_SERVER_SCOPE server;
        return handler.Handle( request );
    }();
    BOOST_REQUIRE_MESSAGE( !result.has_value(), "A board KiCad cannot write must not report a document state." );
    BOOST_CHECK_EQUAL( result.error().status(), kiapi::common::ApiStatusCode::AS_BAD_REQUEST );
    BOOST_CHECK_EQUAL( result.error().error_message(), "Native state could not be observed: " + problem );
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
    BOOST_CHECK( state->results_fresh() && state->snapshot_complete() );
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
    // A real board file keeps every object identity in the copy; an incomplete fill has no results.
    BOOST_CHECK_MESSAGE( state->snapshot_complete(),
                         ( state->input_warnings_size() ? state->input_warnings( 0 ) : std::string() ) );
    BOOST_CHECK( !state->results_fresh() );
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
    BOOST_CHECK( state->results_fresh() && state->snapshot_complete() );

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
    BOOST_CHECK( stale->snapshot_complete() && !stale->results_fresh() );
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

// An object identity the check's copy cannot keep (here two tracks of the open board share one)
// leaves the snapshot incomplete and says why; a completed check then never claims fresh results.
BOOST_AUTO_TEST_CASE( AnIdentityTheCopyCannotKeepLeavesTheSnapshotIncompleteWithItsReason )
{
    BOARD board;
    board.SetFileName( "shared-identity.kicad_pcb" );
    AddTracks( board, 2 );
    board.Tracks().back()->SetUuidDirect( board.Tracks().front()->m_Uuid );
    PCB_DRC_JOB_MANAGER jobs( auxiliaryObserver() );
    const std::string epoch = KIID().AsStdString();
    auto started = jobs.Start( Request( board, epoch ), board, epoch, context );
    BOOST_REQUIRE_MESSAGE( started.has_value(), ( started ? "" : started.error() ) );
    BOOST_CHECK( !started->snapshot_complete() );
    const PcbDrcJobState done = Wait( jobs, board, *started );
    BOOST_REQUIRE_MESSAGE( done.status() == PDRCJS_COMPLETED, done.error_code() + ": " + done.error_message() );
    BOOST_CHECK( !done.snapshot_complete() );
    BOOST_CHECK( !done.results_fresh() );
    int explained = 0;
    for( const auto& warning : done.input_warnings() )
        explained += warning.rfind( "snapshot_incomplete: item_identity: ", 0 ) == 0;
    BOOST_CHECK_EQUAL( explained, 1 );
}

// A candidate check follows the same rule as an ordinary one: its snapshot is complete, a finding
// about the candidate names the candidate's own identity, and its completed results are fresh
// until the board changes. The candidate never reaches the open board.
BOOST_AUTO_TEST_CASE( CandidateChecksFollowTheSameCompletenessAndFreshnessRule )
{
    BOARD board;
    board.SetFileName( "candidate.kicad_pcb" );
    board.SetCopperLayerCount( 2 );
    auto* net = new NETINFO_ITEM( &board, "SIGNAL", 1 );
    board.Add( net );
    // A candidate names an existing net; the board file keeps only nets that an object uses.
    auto* anchor = new PCB_TRACK( &board );
    anchor->SetStart( { 10000000, 10000000 } ); anchor->SetEnd( { 12000000, 10000000 } );
    anchor->SetWidth( 200000 ); anchor->SetLayer( F_Cu ); anchor->SetNet( net );
    board.Add( anchor );
    const std::string epoch = KIID().AsStdString();
    auto request = Request( board, epoch );
    const KIID candidateId;
    kiapi::board::types::Track candidate;
    candidate.mutable_id()->set_value( candidateId.AsStdString() );
    candidate.mutable_start()->set_x_nm( 1000000 ); candidate.mutable_start()->set_y_nm( 1000000 );
    candidate.mutable_end()->set_x_nm( 3000000 ); candidate.mutable_end()->set_y_nm( 1000000 );
    candidate.mutable_width()->set_value_nm( 200000 );
    candidate.set_layer( kiapi::board::types::BL_F_Cu );
    candidate.mutable_net()->set_name( "SIGNAL" );
    request.add_candidate_items()->PackFrom( candidate );
    PCB_DRC_JOB_MANAGER jobs( auxiliaryObserver() );
    auto started = jobs.Start( request, board, epoch, context );
    BOOST_REQUIRE_MESSAGE( started.has_value(), ( started ? "" : started.error() ) );
    BOOST_CHECK( started->candidate_dry_run() );
    const PcbDrcJobState done = Wait( jobs, board, *started );
    BOOST_REQUIRE_MESSAGE( done.status() == PDRCJS_COMPLETED, done.error_code() + ": " + done.error_message() );
    bool namesCandidate = false;
    for( const auto& finding : done.findings() )
        for( const auto& id : finding.marker().items() )
        {
            namesCandidate |= id.value() == candidateId.AsStdString();
            BOOST_CHECK_MESSAGE( id.value() == candidateId.AsStdString() || board.ResolveItem( KIID( id.value() ), true ),
                                 "Private identity " << id.value() );
        }
    BOOST_CHECK( namesCandidate );
    BOOST_CHECK( done.snapshot_complete() && done.results_fresh() );
    BOOST_CHECK_EQUAL( done.input_warnings_size(), 0 );
    BOOST_CHECK_EQUAL( board.Tracks().size(), 1 );
    board.IncrementTimeStamp();
    auto stale = jobs.Read( Query( done ), board, epoch );
    BOOST_REQUIRE( stale );
    BOOST_CHECK( stale->status() == PDRCJS_STALE );
    BOOST_CHECK( stale->snapshot_complete() && !stale->results_fresh() );
}

// KiCad's version-control text (${VCSHASH}, vcs...()) reads the project's repository through the
// git backend KiCad installs at startup. This test process installs the same backend.
struct GIT_BACKEND_SCOPE
{
    LIBGIT_BACKEND backend;
    GIT_BACKEND_SCOPE() { backend.Init(); SetGitBackend( &backend ); }
    ~GIT_BACKEND_SCOPE() { SetGitBackend( nullptr ); backend.Shutdown(); }
};

// A git repository in a scratch directory; each Commit() writes a file and commits it on top of
// HEAD, and returns the new commit's identifier.
struct SCRATCH_REPOSITORY
{
    std::filesystem::path directory;
    git_repository* repository = nullptr;

    explicit SCRATCH_REPOSITORY( const std::filesystem::path& aDirectory ) : directory( aDirectory )
    {
        BOOST_REQUIRE_EQUAL( git_repository_init( &repository, aDirectory.string().c_str(), 0 ), 0 );
    }

    ~SCRATCH_REPOSITORY() { git_repository_free( repository ); }

    std::string Commit( const std::string& aFile, const std::string& aContent )
    {
        { std::ofstream stream( directory / aFile, std::ios::binary ); stream << aContent; }
        git_index* index = nullptr;
        BOOST_REQUIRE_EQUAL( git_repository_index( &index, repository ), 0 );
        BOOST_REQUIRE_EQUAL( git_index_add_bypath( index, aFile.c_str() ), 0 );
        BOOST_REQUIRE_EQUAL( git_index_write( index ), 0 );
        git_oid treeId;
        const int treeError = git_index_write_tree( &treeId, index );
        git_index_free( index );
        BOOST_REQUIRE_EQUAL( treeError, 0 );
        git_tree* tree = nullptr;
        BOOST_REQUIRE_EQUAL( git_tree_lookup( &tree, repository, &treeId ), 0 );
        git_signature* signature = nullptr;
        BOOST_REQUIRE_EQUAL( git_signature_now( &signature, "DRC test", "drc-test@kicad.example" ), 0 );
        git_oid head;
        git_commit* parent = nullptr;
        const bool hasParent = git_reference_name_to_id( &head, repository, "HEAD" ) == 0
                               && git_commit_lookup( &parent, repository, &head ) == 0;
        const git_commit* parents[] = { parent };
        git_oid commit;
        const int error = git_commit_create( &commit, repository, "HEAD", signature, signature, nullptr,
                                             ( "DRC test: " + aFile ).c_str(), tree, hasParent ? 1 : 0, parents );
        git_commit_free( parent );
        git_signature_free( signature );
        git_tree_free( tree );
        BOOST_REQUIRE_EQUAL( error, 0 );
        char text[GIT_OID_HEXSZ + 1];
        git_oid_tostr( text, sizeof text, &commit );
        return text;
    }
};

// The reasons an incomplete snapshot gives, each "snapshot_incomplete: <code>: <explanation>".
static std::vector<std::string> IncompleteReasons( const PcbDrcJobState& aState )
{
    std::vector<std::string> reasons;
    for( const auto& warning : aState.input_warnings() )
        if( warning.rfind( "snapshot_incomplete: ", 0 ) == 0 ) reasons.push_back( warning );
    return reasons;
}

// A text can show values that come from outside the design. A check lays out the board's texts,
// so it reads them. The date and the project's version-control revision are captured with the
// check and every read compares them again: a completed check goes stale when the date it read
// is no longer the date, or when a new commit changes the revision, and a new check is fresh. The
// time of day changes while the check runs, so a board whose text can show it leaves the snapshot
// incomplete with its reason. A text reaches them directly or through a variable's definition. A
// board whose texts show none of them does not depend on the clock at all, so its checks do not
// go stale at midnight.
BOOST_AUTO_TEST_CASE( TextShowingTheDateIsCapturedAndTheTimeOfDayLeavesTheSnapshotIncomplete )
{
    GIT_BACKEND_SCOPE git;
    KI_TEST::TEMPORARY_DIRECTORY scratch( "drc_live_text_" + KIID().AsStdString(), "" );
    const auto projectPath = scratch.GetPath() / "fixture.kicad_pro";
    { std::ofstream stream( projectPath ); stream << R"({"meta":{"filename":"fixture.kicad_pro","version":3}})"; }
    const wxString projectName = wxString::FromUTF8( projectPath.string() );
    SETTINGS_MANAGER manager;
    BOOST_REQUIRE( manager.LoadProject( projectName, false ) );
    PROJECT* project = manager.GetProject( projectName );
    BOARD board;
    board.SetProject( project );
    board.SetFileName( wxString::FromUTF8( ( scratch.GetPath() / "fixture.kicad_pcb" ).string() ) );
    auto* text = new PCB_TEXT( &board );
    text->SetText( wxS( "Checked ${STAMP}" ) );
    text->SetLayer( F_SilkS );
    text->SetPosition( { 10000000, 10000000 } );
    board.Add( text );
    auto live = [&]
    {
        const nlohmann::json settings = PCB_DRC_PROJECT_BASELINE::Observe( board ).settings;
        return settings.contains( "live_text" ) ? settings.at( "live_text" ) : nlohmann::json();
    };
    // The text refers to a variable no definition gives the clock: nothing live is read.
    BOOST_CHECK( live().is_null() );

    // Through the project's variable, the text shows the date: it is captured.
    project->GetTextVars()[wxS( "STAMP" )] = wxS( "${CURRENT_DATE}" );
    const std::string before = TITLE_BLOCK::GetCurrentDate().utf8_string();
    const nlohmann::json observed = live();
    const std::string after = TITLE_BLOCK::GetCurrentDate().utf8_string();
    BOOST_REQUIRE( observed.is_object() && observed.size() == 1 && observed.contains( "CURRENT_DATE" ) );
    BOOST_CHECK( observed.at( "CURRENT_DATE" ) == before || observed.at( "CURRENT_DATE" ) == after );
    PCB_DRC_JOB_MANAGER jobs( auxiliaryObserver() );
    const std::string epoch = KIID().AsStdString();
    auto check = [&]
    {
        auto started = jobs.Start( Request( board, epoch ), board, epoch, context );
        BOOST_REQUIRE_MESSAGE( started.has_value(), ( started ? "" : started.error() ) );
        const PcbDrcJobState done = Wait( jobs, board, *started );
        BOOST_REQUIRE_MESSAGE( done.status() == PDRCJS_COMPLETED, done.error_code() + ": " + done.error_message() );
        return done;
    };
    const PcbDrcJobState dated = check();
    BOOST_CHECK( dated.snapshot_complete() && dated.results_fresh() );
    BOOST_CHECK_EQUAL( dated.input_warnings_size(), 0 );
    // A read on a later day sees another date: the check read a date that is no longer current.
    ObserveProjectAs( jobs, []( const BOARD& aBoard )
    {
        PCB_DRC_PROJECT_OBSERVATION later = PCB_DRC_PROJECT_BASELINE::Observe( aBoard );
        later.settings["live_text"]["CURRENT_DATE"] = "1999-12-31";
        return later;
    } );
    auto nextDay = jobs.Read( Query( dated ), board, epoch );
    BOOST_REQUIRE_MESSAGE( nextDay.has_value(), ( nextDay ? "" : nextDay.error() ) );
    BOOST_CHECK( nextDay->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( nextDay->error_code(), "project_inputs_changed" );
    BOOST_CHECK_EQUAL( nextDay->findings_size(), 0 );
    BOOST_CHECK( nextDay->snapshot_complete() && !nextDay->results_fresh() );
    ObserveProjectAs( jobs, []( const BOARD& aBoard ) { return PCB_DRC_PROJECT_BASELINE::Observe( aBoard ); } );
    BOOST_CHECK( jobs.Read( Query( dated ), board, epoch )->status() == PDRCJS_STALE );
    const PcbDrcJobState redated = check();
    BOOST_CHECK( redated.snapshot_complete() && redated.results_fresh() );

    // The project's version-control revision is captured the same way, as the text resolves it.
    // The scratch project is a git repository: its revision is a real commit, and a new commit
    // makes a completed check stale.
    SCRATCH_REPOSITORY repository( scratch.GetPath() );
    const std::string first = repository.Commit( "notes.txt", "first\n" );
    project->GetTextVars()[wxS( "STAMP" )] = wxS( "${VCSSHORTHASH}" );
    auto revision = [&]
    {
        wxString token = wxS( "VCSSHORTHASH" );
        BOOST_REQUIRE( project->TextVarResolver( &token ) );
        return token.utf8_string();
    };
    BOOST_REQUIRE_EQUAL( revision(), first.substr( 0, 8 ) );
    const nlohmann::json versioned = live();
    BOOST_CHECK( versioned.is_object() && versioned.size() == 1 && versioned.contains( "VCSSHORTHASH" )
                 && versioned.at( "VCSSHORTHASH" ) == first.substr( 0, 8 ) );
    BOOST_CHECK( jobs.Read( Query( redated ), board, epoch )->status() == PDRCJS_STALE );
    const PcbDrcJobState committed = check();
    BOOST_CHECK( committed.snapshot_complete() && committed.results_fresh() );
    BOOST_CHECK_EQUAL( committed.input_warnings_size(), 0 );
    BOOST_CHECK( jobs.Read( Query( committed ), board, epoch )->results_fresh() );
    const std::string second = repository.Commit( "notes.txt", "second\n" );
    BOOST_REQUIRE_NE( first, second );
    BOOST_REQUIRE_EQUAL( revision(), second.substr( 0, 8 ) );
    auto recommitted = jobs.Read( Query( committed ), board, epoch );
    BOOST_REQUIRE_MESSAGE( recommitted.has_value(), ( recommitted ? "" : recommitted.error() ) );
    BOOST_CHECK( recommitted->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( recommitted->error_code(), "project_inputs_changed" );
    BOOST_CHECK_EQUAL( recommitted->findings_size(), 0 );
    BOOST_CHECK( recommitted->snapshot_complete() && !recommitted->results_fresh() );
    const PcbDrcJobState atSecond = check();
    BOOST_CHECK( atSecond.snapshot_complete() && atSecond.results_fresh() );
    BOOST_CHECK( live().at( "VCSSHORTHASH" ) == second.substr( 0, 8 ) );

    // The time of day, shown directly: no captured value can hold it.
    project->GetTextVars().erase( wxS( "STAMP" ) );
    text->SetText( wxS( "Checked at ${CURRENT_TIME_HH_MM_SS}" ) );
    board.IncrementTimeStamp();
    BOOST_CHECK( live().is_null() );
    const PcbDrcJobState timed = check();
    BOOST_CHECK( !timed.snapshot_complete() && !timed.results_fresh() );
    const std::vector<std::string> reasons = IncompleteReasons( timed );
    BOOST_REQUIRE_EQUAL( reasons.size(), 1 );
    BOOST_CHECK_MESSAGE( reasons.front().rfind( "snapshot_incomplete: current_time_text: ", 0 ) == 0
                         && reasons.front().find( "${CURRENT_TIME_HH_MM_SS}" ) != std::string::npos, reasons.front() );

    // Nothing that shows the clock: a check does not depend on it.
    text->SetText( wxS( "Checked" ) );
    board.IncrementTimeStamp();
    BOOST_CHECK( live().is_null() );
    const PcbDrcJobState plain = check();
    BOOST_CHECK( plain.snapshot_complete() && plain.results_fresh() );
    BOOST_CHECK_EQUAL( plain.input_warnings_size(), 0 );
}

// Board text also evaluates @{...} expressions. A check lays the result out, so what an expression
// reads from outside the design is an input of the check. today() gives the day, which the check
// captures and compares like ${CURRENT_DATE}, including through formatting functions. now() and
// random() give another value each time they are evaluated, the vcs...() functions read the
// project's version-control repository while the check runs, and a function KiCad does not know
// as one of the design alone may do either: each leaves the snapshot incomplete with the stable
// reason volatile_text_expression, never fresh. A text reaches an expression directly, through a
// project variable, a board property or a title-block field, or through a variable substituted
// into an expression; the drawing sheet's expressions count too. Expressions of the design alone,
// plain text that only looks like a call, and escaped expressions leave the check complete.
BOOST_AUTO_TEST_CASE( TextExpressionsAreCapturedOrLeaveTheSnapshotIncomplete )
{
    GIT_BACKEND_SCOPE git;
    KI_TEST::TEMPORARY_DIRECTORY scratch( "drc_text_expressions_" + KIID().AsStdString(), "" );
    const auto projectPath = scratch.GetPath() / "fixture.kicad_pro";
    { std::ofstream stream( projectPath ); stream << R"({"meta":{"filename":"fixture.kicad_pro","version":3}})"; }
    SCRATCH_REPOSITORY repository( scratch.GetPath() );
    repository.Commit( "fixture.kicad_pro", R"({"meta":{"filename":"fixture.kicad_pro","version":3}})" );
    const wxString projectName = wxString::FromUTF8( projectPath.string() );
    SETTINGS_MANAGER manager;
    BOOST_REQUIRE( manager.LoadProject( projectName, false ) );
    PROJECT* project = manager.GetProject( projectName );
    BOARD board;
    board.SetProject( project );
    board.SetFileName( wxString::FromUTF8( ( scratch.GetPath() / "fixture.kicad_pcb" ).string() ) );
    auto* text = new PCB_TEXT( &board );
    text->SetLayer( F_SilkS );
    text->SetPosition( { 10000000, 10000000 } );
    board.Add( text );
    auto live = [&]
    {
        const nlohmann::json settings = PCB_DRC_PROJECT_BASELINE::Observe( board ).settings;
        return settings.contains( "live_text" ) ? settings.at( "live_text" ) : nlohmann::json();
    };
    PCB_DRC_JOB_MANAGER jobs( auxiliaryObserver() );
    const std::string epoch = KIID().AsStdString();
    auto check = [&]( const std::string& aCase )
    {
        board.IncrementTimeStamp();
        auto started = jobs.Start( Request( board, epoch ), board, epoch, context );
        BOOST_REQUIRE_MESSAGE( started.has_value(), aCase << ": " << ( started ? "" : started.error() ) );
        const PcbDrcJobState done = Wait( jobs, board, *started );
        BOOST_REQUIRE_MESSAGE( done.status() == PDRCJS_COMPLETED,
                               aCase << ": " << done.error_code() << ": " << done.error_message() );
        return done;
    };
    auto complete = [&]( const std::string& aCase )
    {
        const PcbDrcJobState done = check( aCase );
        BOOST_CHECK_MESSAGE( done.snapshot_complete() && done.results_fresh(), aCase );
        BOOST_CHECK_MESSAGE( done.input_warnings_size() == 0,
                             aCase << ": " << ( done.input_warnings_size() ? done.input_warnings( 0 ) : "" ) );
        return done;
    };
    // The check is incomplete with exactly one reason, volatile_text_expression, naming aCall.
    auto incomplete = [&]( const std::string& aCase, const std::string& aCall, const std::string& aWhere )
    {
        const PcbDrcJobState done = check( aCase );
        BOOST_CHECK_MESSAGE( !done.snapshot_complete() && !done.results_fresh(), aCase );
        const std::vector<std::string> reasons = IncompleteReasons( done );
        BOOST_REQUIRE_MESSAGE( reasons.size() == 1, aCase << ": " << reasons.size() << " reasons" );
        BOOST_CHECK_MESSAGE( reasons.front().rfind( "snapshot_incomplete: volatile_text_expression: " + aWhere, 0 ) == 0
                             && reasons.front().find( aCall ) != std::string::npos,
                             aCase << ": " << reasons.front() );
        // A read gives the same incomplete state: the reason is part of the snapshot.
        const auto again = jobs.Read( Query( done ), board, epoch );
        BOOST_REQUIRE( again );
        BOOST_CHECK_MESSAGE( again->status() == PDRCJS_COMPLETED && !again->snapshot_complete()
                             && !again->results_fresh() && IncompleteReasons( *again ) == reasons, aCase );
        return done;
    };
    const std::string board_text = "The board's text";
    const std::string sheet_text = "The drawing sheet's text";

    // Expressions of the design alone, text that only looks like a call, and an escaped expression.
    text->SetText( wxS( "@{upper(\"checked\")} @{format(2.5, 1)} @{dateformat(datestring(\"2026-01-02\"))} "
                        "@{weekdayname(datestring(\"2026-01-02\"))} @{timeformat(0)}" ) );
    BOOST_CHECK( live().is_null() );
    complete( "pure expressions" );
    text->SetText( wxS( "Order now() at random() from vcsbranch()" ) );
    BOOST_CHECK( live().is_null() );
    complete( "plain text" );
    text->SetText( wxS( "\\@{now()} \\@{random()}" ) );
    complete( "escaped expressions" );

    // today() and date formatting over it, written in the text: the day is captured. A read on
    // a later day sees another day, and the completed check goes stale; a new check is fresh.
    text->SetText( wxS( "Made @{dateformat(today(), \"ISO\")}" ) );
    const nlohmann::json today = live();
    BOOST_REQUIRE( today.is_object() && today.size() == 1 && today.contains( "today()" ) );
    const std::string day = EXPRESSION_EVALUATOR().Evaluate( wxS( "@{today()}" ) ).utf8_string();
    BOOST_CHECK_MESSAGE( today.at( "today()" ) == day, today.dump() << " " << day );
    const PcbDrcJobState dated = complete( "today() in the text" );
    ObserveProjectAs( jobs, []( const BOARD& aBoard )
    {
        PCB_DRC_PROJECT_OBSERVATION later = PCB_DRC_PROJECT_BASELINE::Observe( aBoard );
        later.settings["live_text"]["today()"] = "0";
        return later;
    } );
    auto nextDay = jobs.Read( Query( dated ), board, epoch );
    BOOST_REQUIRE( nextDay );
    BOOST_CHECK( nextDay->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( nextDay->error_code(), "project_inputs_changed" );
    BOOST_CHECK_EQUAL( nextDay->findings_size(), 0 );
    BOOST_CHECK( nextDay->snapshot_complete() && !nextDay->results_fresh() );
    ObserveProjectAs( jobs, []( const BOARD& aBoard ) { return PCB_DRC_PROJECT_BASELINE::Observe( aBoard ); } );
    BOOST_CHECK( jobs.Read( Query( dated ), board, epoch )->status() == PDRCJS_STALE );
    // The same day through a project variable, a board property and a title-block field.
    text->SetText( wxS( "Made ${STAMP}" ) );
    project->GetTextVars()[wxS( "STAMP" )] = wxS( "@{weekdayname(today())}" );
    BOOST_CHECK( live().contains( "today()" ) );
    complete( "today() through a project variable" );
    project->GetTextVars().erase( wxS( "STAMP" ) );
    board.SetProperties( { { wxS( "BUILT" ), wxS( "@{dateformat(today())}" ) } } );
    text->SetText( wxS( "Made ${BUILT}" ) );
    BOOST_CHECK( live().contains( "today()" ) );
    complete( "today() through a board property" );
    board.SetProperties( {} );
    board.GetTitleBlock().SetComment( 1, wxS( "@{today()}" ) );
    text->SetText( wxS( "Made ${COMMENT2}" ) );
    BOOST_CHECK( live().contains( "today()" ) );
    complete( "today() through a title-block field" );
    board.GetTitleBlock().SetComment( 1, wxEmptyString );
    // A definition no text refers to is no input.
    project->GetTextVars()[wxS( "UNUSED" )] = wxS( "@{now()} ${CURRENT_TIME_HH_MM_SS}" );
    text->SetText( wxS( "Made ${ANOTHER}" ) );
    BOOST_CHECK( live().is_null() );
    complete( "an unreferenced definition" );
    project->GetTextVars().erase( wxS( "UNUSED" ) );

    // The clock: now(), directly and through a project variable, also under formatting.
    text->SetText( wxS( "At @{now()}" ) );
    BOOST_CHECK( live().is_null() );
    incomplete( "now() in the text", "now()", board_text );
    text->SetText( wxS( "At ${STAMP}" ) );
    project->GetTextVars()[wxS( "STAMP" )] = wxS( "@{timeformat(now(), \"HH:mm\")}" );
    incomplete( "now() through a project variable", "now()", board_text );

    // Chance: random(), directly and through a project variable.
    text->SetText( wxS( "Lot @{random()}" ) );
    incomplete( "random() in the text", "random()", board_text );
    text->SetText( wxS( "Lot ${STAMP}" ) );
    project->GetTextVars()[wxS( "STAMP" )] = wxS( "@{round(random() * 100)}" );
    incomplete( "random() through a project variable", "random()", board_text );

    // The version-control repository: every vcs...() function, directly and through a variable.
    for( const char* call : { "vcsbranch()", "vcsidentifier(8)", "vcsdirty()", "vcsdirtysuffix()", "vcscommitdate()",
                              "vcsnearestlabel()", "vcslabeldistance()", "vcsauthor()", "vcscommitter()",
                              "vcsfileidentifier(\"fixture.kicad_pro\")", "vcsfileauthor(\"fixture.kicad_pro\")",
                              "vcsfilecommitdate(\"fixture.kicad_pro\")" } )
    {
        const std::string expression = call;
        const std::string name = expression.substr( 0, expression.find( '(' ) ) + "()";
        text->SetText( wxString::FromUTF8( "Revision @{" + expression + "}" ) );
        incomplete( name + " in the text", name, board_text );
    }
    text->SetText( wxS( "Revision ${STAMP}" ) );
    project->GetTextVars()[wxS( "STAMP" )] = wxS( "@{concat(vcsbranch(), vcsdirtysuffix())}" );
    incomplete( "vcs...() through a project variable", "vcsbranch()", board_text );

    // A function the check does not know as one of the design alone.
    text->SetText( wxS( "@{nosuchfunction(1)}" ) );
    incomplete( "an unknown function", "nosuchfunction()", board_text );

    // A variable substituted into an expression becomes part of it.
    text->SetText( wxS( "@{${EXPR} + 1}" ) );
    project->GetTextVars()[wxS( "EXPR" )] = wxS( "now()" );
    incomplete( "now() substituted into an expression", "now()", board_text );
    project->GetTextVars()[wxS( "EXPR" )] = wxS( "2" );
    complete( "a number substituted into an expression" );
    project->GetTextVars().erase( wxS( "EXPR" ) );
    project->GetTextVars().erase( wxS( "STAMP" ) );

    // The drawing sheet: the check resolves its texts to report unresolved variables, and an
    // expression there can decide that from the clock. Plain variables always resolve there.
    text->SetText( wxS( "Plain" ) );
    auto* sheet = new DS_DATA_ITEM_TEXT( wxS( "Printed ${CURRENT_DATE} ${CURRENT_TIME_HH_MM_SS}" ) );
    drawing.Append( sheet );
    complete( "plain date and time in the drawing sheet" );
    sheet->m_TextBase = wxS( "@{upper(\"sheet\")}" );
    complete( "a pure expression in the drawing sheet" );
    sheet->m_TextBase = wxS( "@{if(now() > 0, \"${MISSING}\", \"\")}" );
    incomplete( "now() in the drawing sheet", "now()", sheet_text );
    sheet->m_TextBase = wxS( "Day ${COMMENT3}" );
    board.GetTitleBlock().SetComment( 2, wxS( "@{dateformat(today())}" ) );
    incomplete( "today() through the drawing sheet's title block", "today()", sheet_text );
    board.GetTitleBlock().SetComment( 2, wxEmptyString );
    sheet->m_TextBase = wxS( "Sheet" );

    // Nothing reads outside the design any more: a new check is complete and fresh.
    BOOST_CHECK( live().is_null() );
    complete( "no expression" );
}

// A text laid out with an outline font reads its glyphs from the font file, which KiCad opens once
// and reads lazily. A check captures the content of each font file its texts use (a font embedded
// in the board is part of the board) and every read compares it again: rewriting the file in place
// with the same size and modification time makes a completed check stale, and putting it back does
// not revive it. A font file the check cannot read leaves the snapshot incomplete with its reason.
BOOST_AUTO_TEST_CASE( FontFilesOfTheBoardTextAreCapturedAndCompared )
{
    KI_TEST::TEMPORARY_DIRECTORY scratch( "drc_fonts_" + KIID().AsStdString(), "" );
    wxFileName source( wxString::FromUTF8( KI_TEST::GetTestDataRootDir() ) );
    source.RemoveLastDir();
    source.AppendDir( wxS( "resources" ) );
    source.AppendDir( wxS( "fonts" ) );
    source.SetFullName( wxS( "NotoSans-Regular.ttf" ) );
    BOOST_REQUIRE( source.FileExists() );
    const auto fontPath = scratch.GetPath() / "NotoSans-Regular.ttf";
    std::filesystem::copy_file( std::filesystem::path( source.GetFullPath().utf8_string() ), fontPath );
    std::vector<wxString> fontFiles{ wxString::FromUTF8( fontPath.string() ) };
    KIFONT::FONT* font = KIFONT::FONT::GetFont( wxS( "Noto Sans" ), false, false, &fontFiles );
    BOOST_REQUIRE( font && font->IsOutline() );
    const wxString loaded = static_cast<KIFONT::OUTLINE_FONT*>( font )->GetFileName();
    BOOST_REQUIRE_MESSAGE( std::filesystem::equivalent( std::filesystem::path( loaded.utf8_string() ), fontPath ),
                           loaded.utf8_string() );

    BOARD board;
    board.SetFileName( wxString::FromUTF8( ( scratch.GetPath() / "fonts.kicad_pcb" ).string() ) );
    auto* text = new PCB_TEXT( &board );
    text->SetText( wxS( "Outline" ) );
    text->SetLayer( F_SilkS );
    text->SetPosition( { 10000000, 10000000 } );
    text->SetFont( font );
    board.Add( text );
    PCB_DRC_JOB_MANAGER jobs( auxiliaryObserver() );
    const std::string epoch = KIID().AsStdString();
    auto check = [&]
    {
        board.IncrementTimeStamp();
        auto started = jobs.Start( Request( board, epoch ), board, epoch, context );
        BOOST_REQUIRE_MESSAGE( started.has_value(), ( started ? "" : started.error() ) );
        const PcbDrcJobState done = Wait( jobs, board, *started );
        BOOST_REQUIRE_MESSAGE( done.status() == PDRCJS_COMPLETED, done.error_code() + ": " + done.error_message() );
        return done;
    };
    const PcbDrcJobState captured = check();
    BOOST_CHECK( captured.snapshot_complete() && captured.results_fresh() );
    BOOST_CHECK_EQUAL( captured.input_warnings_size(), 0 );
    BOOST_CHECK( jobs.Read( Query( captured ), board, epoch )->results_fresh() );

    // Rewrite one byte of the font file in place, keeping its size and modification time.
    const auto modified = std::filesystem::last_write_time( fontPath );
    const auto size = std::filesystem::file_size( fontPath );
    auto rewriteLastByte = [&]( char aXor )
    {
        std::fstream stream( fontPath, std::ios::in | std::ios::out | std::ios::binary );
        stream.seekg( static_cast<std::streamoff>( size - 1 ) );
        char last = 0;
        stream.get( last );
        stream.seekp( static_cast<std::streamoff>( size - 1 ) );
        stream.put( static_cast<char>( last ^ aXor ) );
        stream.close();
        std::filesystem::last_write_time( fontPath, modified );
        BOOST_REQUIRE_EQUAL( std::filesystem::file_size( fontPath ), size );
        BOOST_REQUIRE( std::filesystem::last_write_time( fontPath ) == modified );
    };
    rewriteLastByte( 0x01 );
    auto rewritten = jobs.Read( Query( captured ), board, epoch );
    BOOST_REQUIRE_MESSAGE( rewritten.has_value(), ( rewritten ? "" : rewritten.error() ) );
    BOOST_CHECK( rewritten->status() == PDRCJS_STALE );
    BOOST_CHECK_EQUAL( rewritten->error_code(), "project_inputs_changed" );
    BOOST_CHECK_EQUAL( rewritten->findings_size(), 0 );
    BOOST_CHECK( rewritten->snapshot_complete() && !rewritten->results_fresh() );
    rewriteLastByte( 0x01 ); // The original bytes again.
    BOOST_CHECK( jobs.Read( Query( captured ), board, epoch )->status() == PDRCJS_STALE );
    const PcbDrcJobState restored = check();
    BOOST_CHECK( restored.snapshot_complete() && restored.results_fresh() );

    // The stroke font reads no file: another font file changing is no input then.
    text->SetFont( nullptr );
    const PcbDrcJobState stroked = check();
    BOOST_CHECK( stroked.snapshot_complete() && stroked.results_fresh() );
    rewriteLastByte( 0x01 );
    BOOST_CHECK( jobs.Read( Query( stroked ), board, epoch )->results_fresh() );
    rewriteLastByte( 0x01 );

    // A font file the check cannot read: KiCad still holds the font it opened, but the check
    // cannot compare the file with what it laid out.
    text->SetFont( font );
    std::vector<char> bytes( size );
    { std::ifstream stream( fontPath, std::ios::binary ); stream.read( bytes.data(), static_cast<std::streamsize>( size ) ); }
    std::filesystem::remove( fontPath );
    const PcbDrcJobState unreadable = check();
    BOOST_CHECK( !unreadable.snapshot_complete() && !unreadable.results_fresh() );
    const std::vector<std::string> reasons = IncompleteReasons( unreadable );
    BOOST_REQUIRE_EQUAL( reasons.size(), 1 );
    BOOST_CHECK_MESSAGE( reasons.front().rfind( "snapshot_incomplete: font_file: ", 0 ) == 0
                         && reasons.front().find( "NotoSans-Regular.ttf" ) != std::string::npos, reasons.front() );
    { std::ofstream stream( fontPath, std::ios::binary ); stream.write( bytes.data(), static_cast<std::streamsize>( size ) ); }
    const PcbDrcJobState readable = check();
    BOOST_CHECK( readable.snapshot_complete() && readable.results_fresh() );
}

// Refilling zones rebuilds teardrops inside the check. A finding about a rebuilt teardrop names the
// open board's teardrop when the open board holds the same one, whether under the identity KiCad
// derives for it or under the one it received when the board was loaded. A teardrop the open board
// does not hold leaves the snapshot incomplete with its reason, instead of presenting an identity
// private to the check as an object of the open board.
BOOST_AUTO_TEST_CASE( RefilledTeardropsNameTheOpenBoardTeardropOrLeaveTheSnapshotIncomplete )
{
    KI_TEST::TEMPORARY_DIRECTORY scratch( "drc_teardrops_" + KIID().AsStdString(), "" );
    const auto projectPath = scratch.GetPath() / "fixture.kicad_pro";
    { std::ofstream stream( projectPath ); stream << R"({"meta":{"filename":"fixture.kicad_pro","version":3}})"; }
    // Rules match a teardrop as a track, so every track and teardrop fails this assertion and gets a
    // finding that names it.
    { std::ofstream stream( scratch.GetPath() / "fixture.kicad_dru" );
      stream << "(version 1)\n(rule \"every_track\" (condition \"A.Type == 'Track'\") "
                "(constraint assertion \"A.Type != 'Track'\"))\n"; }
    const wxString projectName = wxString::FromUTF8( projectPath.string() );
    SETTINGS_MANAGER manager;
    BOOST_REQUIRE( manager.LoadProject( projectName, false ) );
    BOARD board;
    board.SetProject( manager.GetProject( projectName ) );
    board.SetFileName( wxString::FromUTF8( ( scratch.GetPath() / "fixture.kicad_pcb" ).string() ) );
    board.SetCopperLayerCount( 2 );
    auto* net = new NETINFO_ITEM( &board, "SIGNAL", 1 );
    board.Add( net );
    auto* via = new PCB_VIA( &board );
    via->SetPadstackMode( PADSTACK::MODE::NORMAL );
    via->SetViaType( VIATYPE::THROUGH );
    via->SetLayerPair( F_Cu, B_Cu );
    via->SetPosition( { 10000000, 10000000 } );
    via->SetWidth( PADSTACK::ALL_LAYERS, 800000 );
    via->SetDrill( 400000 );
    via->SetNet( net );
    via->GetTeardropParams().m_Enabled = true;
    board.Add( via );
    auto* track = new PCB_TRACK( &board );
    track->SetStart( { 10000000, 10000000 } ); track->SetEnd( { 14000000, 10000000 } );
    track->SetWidth( 200000 ); track->SetLayer( F_Cu ); track->SetNet( net );
    board.Add( track );
    PNS::ROUTING_SETTINGS routing( nullptr, "tools.pns" );
    context.routingSettings = &routing;
    PCB_DRC_JOB_MANAGER jobs( auxiliaryObserver() );
    const std::string epoch = KIID().AsStdString();
    // The state the check was started with, and its completed state.
    auto refillFrom = [&]
    {
        auto request = Request( board, epoch );
        request.set_refill_zones( true );
        auto started = jobs.Start( request, board, epoch, context );
        BOOST_REQUIRE_MESSAGE( started.has_value(), ( started ? "" : started.error() ) );
        const PcbDrcJobState done = Wait( jobs, board, *started );
        BOOST_REQUIRE_MESSAGE( done.status() == PDRCJS_COMPLETED, done.error_code() + ": " + done.error_message() );
        return std::pair<PcbDrcJobState, PcbDrcJobState>( *started, done );
    };
    auto refill = [&] { return refillFrom().second; };
    // Every finding, for the failure message.
    auto describe = []( const PcbDrcJobState& state )
    {
        std::string text = std::to_string( state.findings_size() ) + " findings:";
        for( const auto& finding : state.findings() )
        {
            text += " [" + kiapi::board::DrcErrorType_Name( finding.marker().error_type() );
            for( const auto& id : finding.marker().items() ) text += " " + id.value();
            text += "]";
        }
        for( const auto& warning : state.input_warnings() ) text += " warning: " + warning;
        return text;
    };
    // The identities the assertion findings name, other than the track's own.
    auto teardropFindings = [&]( const PcbDrcJobState& state )
    {
        std::vector<std::string> named;
        for( const auto& finding : state.findings() )
            if( finding.marker().error_type() == kiapi::board::DRCET_ASSERTION_FAILURE )
                for( const auto& id : finding.marker().items() )
                    if( id.value() != track->m_Uuid.AsStdString() ) named.push_back( id.value() );
        return named;
    };

    // The open board has no teardrop: the check's rebuilt teardrop is no object of it. The check
    // never claims a complete snapshot, neither when it starts nor while it runs (Wait checks
    // each running state) nor when it completes.
    const auto [missingStart, missing] = refillFrom();
    BOOST_CHECK( !missingStart.snapshot_complete() && !missingStart.results_fresh() );
    BOOST_CHECK( missingStart.worker_finished() || Notes( missingStart ).empty() );
    const auto rebuilt = teardropFindings( missing );
    BOOST_CHECK_MESSAGE( rebuilt.size() == 1, describe( missing ) );
    BOOST_CHECK( rebuilt.empty() || !board.ResolveItem( KIID( rebuilt.front() ), true ) );
    BOOST_CHECK( !missing.snapshot_complete() && !missing.results_fresh() );
    int explained = 0;
    for( const auto& warning : missing.input_warnings() )
        explained += warning.rfind( "snapshot_incomplete: generated_item_identity: 1 finding names", 0 ) == 0;
    BOOST_CHECK_EQUAL( explained, 1 );
    BOOST_CHECK_EQUAL( board.Zones().size(), 0 );

    // KiCad's own teardrop update gives the open board the same teardrop under its derived identity.
    {
        TOOL_MANAGER tools;
        tools.SetEnvironment( &board, nullptr, nullptr, nullptr, nullptr );
        tools.RegisterTool( new KI_TEST::DUMMY_TOOL );
        BOARD_COMMIT commit( &tools, true, false );
        board.BuildConnectivity();
        TEARDROP_MANAGER teardrops( &board, &tools );
        teardrops.UpdateTeardrops( commit, nullptr, nullptr, true );
        commit.Push( wxEmptyString, SKIP_UNDO | SKIP_SET_DIRTY | SKIP_TEARDROPS );
    }
    BOOST_REQUIRE_EQUAL( board.Zones().size(), 1 );
    ZONE* live = board.Zones().front();
    BOOST_REQUIRE( live->IsTeardropArea() );
    BOOST_CHECK( rebuilt.size() == 1 && live->m_Uuid.AsStdString() == rebuilt.front() );
    const PcbDrcJobState derived = refill();
    BOOST_CHECK_MESSAGE( teardropFindings( derived ) == std::vector<std::string>{ live->m_Uuid.AsStdString() },
                         describe( derived ) );
    BOOST_CHECK( derived.snapshot_complete() && derived.results_fresh() );
    BOOST_CHECK_EQUAL( derived.input_warnings_size(), 0 );

    // A board loaded from its file gives each teardrop a new identity. The rebuilt teardrop is the
    // same one, so its finding names the open board's teardrop under that identity.
    live->SetUuid( KIID() );
    board.IncrementTimeStamp();
    const PcbDrcJobState loaded = refill();
    BOOST_CHECK_MESSAGE( teardropFindings( loaded ) == std::vector<std::string>{ live->m_Uuid.AsStdString() },
                         describe( loaded ) );
    BOOST_CHECK( loaded.snapshot_complete() && loaded.results_fresh() );
    for( const auto& finding : loaded.findings() )
        for( const auto& id : finding.marker().items() )
            BOOST_CHECK_MESSAGE( board.ResolveItem( KIID( id.value() ), true ), "Private identity " << id.value() );
    BOOST_CHECK_EQUAL( board.Zones().size(), 1 );
}

BOOST_AUTO_TEST_SUITE_END()
