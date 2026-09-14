/* DRC cleanup must permit recovery without borrowed dangling state. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <cli_progress_reporter.h>
#include <drc/drc_item.h>
#include <drc/drc_run_scope.h>
#include <netlist_reader/pcb_netlist.h>
#include <memory>
#include <type_traits>
#include <fstream>
#include <filesystem>
#include <cli/exit_codes.h>
#include <jobs/job_pcb_drc.h>
#include <pcbnew_jobs_handler.h>
#include <pcbnew_utils/board_file_utils.h>
#include <pcbnew_utils/board_test_utils.h>
#include <json_common.h>

namespace
{
class CANCELLABLE_REPORTER : public CLI_PROGRESS_REPORTER
{
public:
    bool IsCancelled() const override { return cancelled; }
    bool KeepRefreshing( bool = false ) override
    {
        ++refreshCalls;
        return continueChecking && !cancelled;
    }
    bool cancelled = false;
    bool continueChecking = true;
    int refreshCalls = 0;
};
}

static_assert( !std::is_copy_constructible_v<DRC_RUN_SCOPE> );
static_assert( !std::is_move_constructible_v<DRC_RUN_SCOPE> );

BOOST_AUTO_TEST_SUITE( DrcRunScope )

BOOST_AUTO_TEST_CASE( MissingInputAndCancellationCannotReportCompletion )
{
    DRC_ENGINE engine;
    CANCELLABLE_REPORTER reporter;
    BOOST_CHECK( engine.RunTests( EDA_UNITS::MM, false, false ) == DRC_RUN_RESULT::INCOMPLETE );
    engine.SetProgressReporter( &reporter );
    reporter.cancelled = true;
    BOOST_CHECK( engine.RunTests( EDA_UNITS::MM, false, false ) == DRC_RUN_RESULT::CANCELLED );
    reporter.cancelled = false;
    BOOST_CHECK( engine.RunTests( EDA_UNITS::MM, false, false ) == DRC_RUN_RESULT::INCOMPLETE );
    engine.SetProgressReporter( nullptr );
}

BOOST_AUTO_TEST_CASE( SuccessAndCancellationReleaseAllBorrowedState )
{
    DRC_ENGINE engine;
    CANCELLABLE_REPORTER reporter;
    NETLIST netlist;
    bool running = false;

    for( bool cancel : { false, true } )
    {
        std::weak_ptr<int> callbackLifetime;
        {
            DRC_RUN_SCOPE run( engine, running );
            engine.SetProgressReporter( &reporter );
            engine.SetSchematicNetlist( &netlist );
            reporter.cancelled = cancel;
            auto retained = std::make_shared<int>( 1 );
            callbackLifetime = retained;
            engine.SetViolationHandler( [retained]( const auto&, const auto&, int, const auto& )
                                        { (void) retained; } );
            BOOST_CHECK( engine.IsCancelled() == cancel );
        }
        BOOST_CHECK( !running );
        BOOST_CHECK( callbackLifetime.expired() );
        BOOST_CHECK( engine.GetProgressReporter() == nullptr );
        BOOST_CHECK( engine.GetSchematicNetlist() == nullptr );
        BOOST_CHECK( !engine.IsCancelled() );
    }
}

BOOST_AUTO_TEST_CASE( ThrowingViolationSinkUnwindsBeforeASecondInvocation )
{
    DRC_ENGINE engine;
    CANCELLABLE_REPORTER reporter;
    NETLIST netlist;
    bool running = false;
    std::weak_ptr<int> callbackLifetime;

    auto failingRun = [&]
    {
        DRC_RUN_SCOPE run( engine, running );
        engine.SetProgressReporter( &reporter );
        engine.SetSchematicNetlist( &netlist );
        auto retained = std::make_shared<int>( 1 );
        callbackLifetime = retained;
        engine.SetViolationHandler( [retained]( const auto&, const auto&, int, const auto& )
                                    {
                                        (void) retained;
                                        throw std::runtime_error( "injected violation sink failure" );
                                    } );
        engine.ReportViolation( DRC_ITEM::Create( DRCE_CLEARANCE ), { 0, 0 }, F_Cu );
    };
    BOOST_CHECK_THROW( failingRun(), std::runtime_error );
    BOOST_CHECK( !running );
    BOOST_CHECK( callbackLifetime.expired() );
    BOOST_CHECK( engine.GetProgressReporter() == nullptr );
    BOOST_CHECK( engine.GetSchematicNetlist() == nullptr );

    int calls = 0;
    {
        DRC_RUN_SCOPE recovered( engine, running );
        engine.SetViolationHandler( [&]( const auto&, const auto&, int, const auto& ) { ++calls; } );
        engine.ReportViolation( DRC_ITEM::Create( DRCE_CLEARANCE ), { 0, 0 }, F_Cu );
        BOOST_CHECK_EQUAL( calls, 1 );
    }
    engine.ReportViolation( DRC_ITEM::Create( DRCE_CLEARANCE ), { 0, 0 }, F_Cu );
    BOOST_CHECK_EQUAL( calls, 1 );
    BOOST_CHECK( !running );
}

BOOST_AUTO_TEST_CASE( NestedAdmissionDoesNotClearTheActiveInvocation )
{
    DRC_ENGINE engine;
    CANCELLABLE_REPORTER reporter;
    NETLIST netlist;
    bool running = false;
    int calls = 0;
    {
        DRC_RUN_SCOPE active( engine, running );
        engine.SetProgressReporter( &reporter );
        engine.SetSchematicNetlist( &netlist );
        engine.SetViolationHandler( [&]( const auto&, const auto&, int, const auto& ) { ++calls; } );
        BOOST_CHECK_THROW( DRC_RUN_SCOPE( engine, running ), std::logic_error );
        BOOST_CHECK( running );
        BOOST_CHECK( engine.GetProgressReporter() == &reporter );
        BOOST_CHECK( engine.GetSchematicNetlist() == &netlist );
        engine.ReportViolation( DRC_ITEM::Create( DRCE_CLEARANCE ), { 0, 0 }, F_Cu );
        BOOST_CHECK_EQUAL( calls, 1 );
    }
    BOOST_CHECK( !running );
}

BOOST_AUTO_TEST_CASE( ExportJobPreservesExistingReportOnCancellationAndRecovers )
{
    namespace fs = std::filesystem;
    KI_TEST::TEMPORARY_DIRECTORY temporary( "drc_export_" + KIID().AsStdString(), "" );
    const fs::path boardPath = temporary.GetPath() / "fixture.kicad_pcb";
    const fs::path outputPath = temporary.GetPath() / "drc.json";
    fs::copy_file( fs::path( KI_TEST::GetPcbnewTestDataDir() )
                           / "drc_courtyard/overlap/empty_board.kicad_pcb", boardPath );
    auto contents = []( const fs::path& path )
    {
        std::ifstream input( path, std::ios::binary );
        return std::string( std::istreambuf_iterator<char>( input ), {} );
    };
    const std::string originalBoard = contents( boardPath );
    { std::ofstream output( outputPath, std::ios::binary ); output << "previous report"; }
    const auto written = fs::last_write_time( outputPath );

    CANCELLABLE_REPORTER reporter;
    PCBNEW_JOBS_HANDLER handler( nullptr );
    JOB_PCB_DRC job;
    job.m_filename = wxString::FromUTF8( boardPath.string() );
    job.SetConfiguredOutputPath( wxString::FromUTF8( outputPath.string() ) );
    job.m_parity = false;
    job.m_refillZones = false;
    job.m_saveBoard = false;
    job.m_format = JOB_RC::OUTPUT_FORMAT::JSON;
    job.m_exitCodeViolations = false;

    reporter.cancelled = true;
    BOOST_CHECK_EQUAL( handler.RunJob( &job, nullptr, &reporter ), CLI::EXIT_CODES::ERR_UNKNOWN );
    BOOST_CHECK( job.GetOutputs().empty() );
    BOOST_CHECK_EQUAL( contents( outputPath ), "previous report" );
    BOOST_CHECK( fs::last_write_time( outputPath ) == written );

    reporter.cancelled = false;
    reporter.continueChecking = false;
    BOOST_CHECK_EQUAL( handler.RunJob( &job, nullptr, &reporter ), CLI::EXIT_CODES::ERR_UNKNOWN );
    BOOST_CHECK_GT( reporter.refreshCalls, 0 );
    BOOST_CHECK( job.GetOutputs().empty() );
    BOOST_CHECK_EQUAL( contents( outputPath ), "previous report" );
    BOOST_CHECK( fs::last_write_time( outputPath ) == written );

    reporter.continueChecking = true;
    BOOST_CHECK_EQUAL( handler.RunJob( &job, nullptr, &reporter ), CLI::EXIT_CODES::SUCCESS );
    BOOST_CHECK_EQUAL( job.GetOutputs().size(), 1 );
    const auto report = nlohmann::json::parse( contents( outputPath ) );
    BOOST_CHECK( report.contains( "violations" ) );
    BOOST_CHECK( !report.at( "violations" ).empty() );
    BOOST_CHECK_EQUAL( contents( boardPath ), originalBoard );
}

BOOST_AUTO_TEST_SUITE_END()
