/* DRC cleanup must permit recovery without borrowed dangling state. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <cli_progress_reporter.h>
#include <drc/drc_item.h>
#include <drc/drc_run_scope.h>
#include <netlist_reader/pcb_netlist.h>
#include <memory>
#include <type_traits>

namespace
{
class CANCELLABLE_REPORTER : public CLI_PROGRESS_REPORTER
{
public:
    bool IsCancelled() const override { return cancelled; }
    bool cancelled = false;
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

BOOST_AUTO_TEST_SUITE_END()
