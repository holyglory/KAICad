/* Typed refill outcomes for verification without changing legacy UI acceptance. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <advanced_config.h>
#include <board.h>
#include <cli_progress_reporter.h>
#include <pcbnew_utils/board_test_utils.h>
#include <settings/settings_manager.h>
#include <zone.h>
#include <zone_filler.h>
#include <atomic>

BOOST_AUTO_TEST_SUITE( ZoneFillOutcome )

BOOST_AUTO_TEST_CASE( CompletionCancellationAndFailureAreDistinctAndRecoverable )
{
    BOARD board;
    auto* zone = new ZONE( &board );
    zone->SetLayer( F_SilkS );
    zone->AppendCorner( { 0, 0 }, -1 );
    zone->AppendCorner( { 10000000, 0 }, -1 );
    zone->AppendCorner( { 10000000, 10000000 }, -1 );
    zone->AppendCorner( { 0, 10000000 }, -1 );
    zone->SetIslandRemovalMode( ISLAND_REMOVAL_MODE::NEVER );
    board.Add( zone );
    class REPORTER : public CLI_PROGRESS_REPORTER
    {
    public:
        bool IsCancelled() const override { return cancelled.load(); }
        bool KeepRefreshing( bool = false ) override { return !cancelled.load(); }
        void Report( const wxString& ) override
        { if( fail ) throw std::runtime_error( "fixture progress sink failure" ); }
        std::atomic_bool cancelled = false;
        bool fail = false;
    } reporter;
    ZONE_FILLER filler( &board, nullptr );
    BOOST_CHECK( filler.LastOutcome() == ZONE_FILLER::OUTCOME::NOT_RUN );
    filler.SetProgressReporter( &reporter );
    BOOST_REQUIRE( filler.Fill( { zone } ) );
    BOOST_CHECK( filler.LastOutcome() == ZONE_FILLER::OUTCOME::COMPLETED );
    BOOST_REQUIRE( zone->GetFilledPolysList( F_SilkS ) );
    BOOST_CHECK_GT( zone->GetFilledPolysList( F_SilkS )->TotalVertices(), 0 );

    reporter.cancelled = true;
    BOOST_CHECK( !filler.Fill( { zone } ) );
    BOOST_CHECK( filler.LastOutcome() == ZONE_FILLER::OUTCOME::CANCELLED );
    reporter.cancelled = false;
    reporter.fail = true;
    BOOST_CHECK_THROW( filler.Fill( { zone } ), std::runtime_error );
    BOOST_CHECK( filler.LastOutcome() == ZONE_FILLER::OUTCOME::FAILED );
    reporter.fail = false;
    BOOST_REQUIRE( filler.Fill( { zone } ) );
    BOOST_CHECK( filler.LastOutcome() == ZONE_FILLER::OUTCOME::COMPLETED );
    BOOST_CHECK_GT( zone->GetFilledPolysList( F_SilkS )->TotalVertices(), 0 );
}

BOOST_AUTO_TEST_CASE( IterationLimitIsNotACompletedVerificationFill )
{
    auto& config = const_cast<ADVANCED_CFG&>( ADVANCED_CFG::GetCfg() );
    struct RESTORE
    {
        bool& value; bool previous;
        ~RESTORE() { value = previous; }
    } restore{ config.m_ZoneFillIterativeRefill, config.m_ZoneFillIterativeRefill };
    config.m_ZoneFillIterativeRefill = true;
    SETTINGS_MANAGER manager;
    std::unique_ptr<BOARD> board;
    KI_TEST::LoadBoard( manager, "zone_refill_convergence_limit", board );
    std::vector<ZONE*> zones;
    for( auto* zone : board->Zones() ) zones.push_back( zone );
    BOOST_REQUIRE( !zones.empty() );
    ZONE_FILLER filler( board.get(), nullptr );
    // Existing UI behavior accepts the best fill and warns; verification must
    // additionally inspect the explicit non-converged outcome.
    BOOST_CHECK( filler.Fill( zones ) );
    BOOST_CHECK( filler.LastOutcome() == ZONE_FILLER::OUTCOME::NOT_CONVERGED );
}

BOOST_AUTO_TEST_SUITE_END()
