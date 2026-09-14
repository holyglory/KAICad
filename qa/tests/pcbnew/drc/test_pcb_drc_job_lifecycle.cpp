/* Internal worker lifecycle, not qualification of complete design verification. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <api/pcb_drc_job_manager.h>
#include <board.h>
#include <pcb_track.h>
#include <netinfo.h>
#include <footprint.h>
#include <pad.h>
#include <google/protobuf/util/message_differencer.h>
#include <chrono>
#include <thread>

using namespace kiapi::automation::v1;
using google::protobuf::util::MessageDifferencer;

BOOST_AUTO_TEST_SUITE( PcbDrcJobLifecycle )

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
    auto started = jobs.Start( request, board, epoch );
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

    auto replay = jobs.Start( request, board, epoch );
    BOOST_REQUIRE( replay );
    BOOST_CHECK( MessageDifferencer::Equals( *replay, terminal ) );
    auto altered = request;
    altered.set_report_all_track_errors( !request.report_all_track_errors() );
    BOOST_CHECK( !jobs.Start( altered, board, epoch ) );
    altered = request; altered.set_refill_zones( true );
    BOOST_CHECK( !jobs.Start( altered, board, epoch ) );
    altered = request; altered.set_test_footprints( true );
    BOOST_CHECK( !jobs.Start( altered, board, epoch ) );
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
    BOOST_CHECK( !jobs.Start( request, board, epoch ) );
    request.mutable_expected_revision()->set_sequence( board.GetTimeStamp() );
    auto started = jobs.Start( request, board, epoch );
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
    auto started = jobs.Start( request, board, epoch );
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

BOOST_AUTO_TEST_SUITE_END()
