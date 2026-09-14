/* Internal worker lifecycle, not qualification of complete design verification. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <advanced_config.h>
#include <api/pcb_drc_job_manager.h>
#include <api/pcb_drc_run_inputs.h>
#include <api/native_state_digest.h>
#include <board.h>
#include <pcb_track.h>
#include <netinfo.h>
#include <netclass.h>
#include <footprint.h>
#include <pad.h>
#include <footprint_library_adapter.h>
#include <libraries/library_manager.h>
#include <drawing_sheet/ds_data_model.h>
#include <drawing_sheet/ds_data_item.h>
#include <board_design_settings.h>
#include <drc/drc_item.h>
#include <pcb_marker.h>
#include <pcbnew_utils/board_test_utils.h>
#include <project.h>
#include <project/project_file.h>
#include <project/net_settings.h>
#include <settings/settings_manager.h>
#include <json_common.h>
#include <router/pns_routing_settings.h>
#include <zone.h>
#include <fstream>
#include <google/protobuf/util/message_differencer.h>
#include <chrono>
#include <thread>

using namespace kiapi::automation::v1;
using google::protobuf::util::MessageDifferencer;

struct DRC_CAPTURE_FIXTURE
{
    LIBRARY_MANAGER libraries;
    FOOTPRINT_LIBRARY_ADAPTER adapter{ libraries };
    DS_DATA_MODEL drawing;
    PCB_DRC_CAPTURE_CONTEXT context{ adapter, drawing, KIID() };
    DRC_CAPTURE_FIXTURE() { drawing.ClearList(); drawing.AllowVoidList( true ); }
};

BOOST_FIXTURE_TEST_SUITE( PcbDrcJobLifecycle, DRC_CAPTURE_FIXTURE )

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

    PCB_DRC_JOB_MANAGER jobs;
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
    PCB_DRC_JOB_MANAGER jobs;
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
    PCB_DRC_JOB_MANAGER jobs;
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
    PCB_DRC_JOB_MANAGER jobs;
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
    PCB_DRC_JOB_MANAGER jobs;
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
    PCB_DRC_JOB_MANAGER jobs;
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
    PCB_DRC_JOB_MANAGER jobs;
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
    auto state = jobs.Read( query, *board, epoch );
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds( 30 );
    while( state && !state->worker_finished() && std::chrono::steady_clock::now() < deadline )
    {
        std::this_thread::sleep_for( std::chrono::milliseconds( 5 ) );
        state = jobs.Read( query, *board, epoch );
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
    PCB_DRC_JOB_MANAGER jobs;
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
