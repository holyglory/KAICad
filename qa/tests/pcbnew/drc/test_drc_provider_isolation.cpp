/* Native checker ownership and retained diagnostic lifetimes. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <board.h>
#include <board_design_settings.h>
#include <drc/drc_engine.h>
#include <drc/drc_item.h>
#include <drc/drc_test_provider.h>
#include <pcb_text.h>
#include <future>

BOOST_AUTO_TEST_SUITE( DrcProviderIsolation )

BOOST_AUTO_TEST_CASE( EnginesOwnDistinctProvidersAcrossReinitialization )
{
    BOARD first, second;
    DRC_ENGINE left( &first, &first.GetDesignSettings() );
    DRC_ENGINE right( &second, &second.GetDesignSettings() );
    auto checkDistinct = [&]
    {
        auto a = left.GetTestProviders();
        auto b = right.GetTestProviders();
        BOOST_REQUIRE( !a.empty() );
        BOOST_REQUIRE_EQUAL( a.size(), b.size() );
        for( size_t i = 0; i < a.size(); ++i )
        {
            BOOST_REQUIRE( a[i] != b[i] );
            BOOST_CHECK( a[i]->GetName() == b[i]->GetName() );
            BOOST_CHECK( left.GetTestProvider( a[i]->GetName() ) == a[i] );
            BOOST_CHECK( right.GetTestProvider( b[i]->GetName() ) == b[i] );
        }
    };
    left.InitEngine( wxFileName() );
    right.InitEngine( wxFileName() );
    checkDistinct();
    left.InitEngine( wxFileName() );
    checkDistinct();
}

BOOST_AUTO_TEST_CASE( ShowMatchesFactoriesCreateIndependentOwners )
{
    // No production ShowMatches providers are registered in the pinned tree.
    // Exercise that registry's allocation contract locally, not by pretending
    // its empty engine list is a real check. Actual checker behavior is below.
    class FACTORY_FIXTURE : public DRC_TEST_PROVIDER
    {
    public:
        bool Run() override
        {
            BOOST_FAIL( "The allocation-only fixture must never run a design check" );
            return false;
        }
    };
    DRC_SHOWMATCHES_PROVIDER_REGISTRY registry;
    registry.RegisterShowMatchesProvider( [] { return std::make_unique<FACTORY_FIXTURE>(); } );
    auto first = registry.CreateShowMatchesProviders();
    auto second = registry.CreateShowMatchesProviders();
    BOOST_REQUIRE_EQUAL( first.size(), 1 );
    BOOST_REQUIRE_EQUAL( second.size(), 1 );
    BOOST_CHECK( first.front().get() != second.front().get() );
}

BOOST_AUTO_TEST_CASE( SimultaneousChecksKeepBoardFindingsAndRetainedNamesSeparate )
{
    BOARD first, second;
    auto prepare = []( BOARD& board, const wxString& tag )
    {
        auto* text = new PCB_TEXT( &board );
        text->SetText( "${DRC_ERROR " + tag + "}" );
        text->SetLayer( F_SilkS );
        board.Add( text );
        auto& settings = board.GetDesignSettings();
        for( int code = DRCE_FIRST; code <= DRCE_LAST; ++code )
            settings.m_DRCSeverities[code] = SEVERITY::RPT_SEVERITY_IGNORE;
        settings.m_DRCSeverities[DRCE_GENERIC_ERROR] = SEVERITY::RPT_SEVERITY_ERROR;
        settings.m_DRCSeverities[DRCE_UNRESOLVED_VARIABLE] = SEVERITY::RPT_SEVERITY_ERROR;
    };
    prepare( first, "first-board-only" );
    prepare( second, "second-board-only" );
    std::vector<std::shared_ptr<DRC_ITEM>> leftFindings, rightFindings;
    {
        DRC_ENGINE left( &first, &first.GetDesignSettings() );
        DRC_ENGINE right( &second, &second.GetDesignSettings() );
        left.InitEngine( wxFileName() );
        right.InitEngine( wxFileName() );
        // Require actual independent ownership before attempting simultaneous runs.
        BOOST_REQUIRE( left.GetTestProvider( "miscellaneous" )
                       != right.GetTestProvider( "miscellaneous" ) );
        left.SetViolationHandler( [&]( const auto& item, const auto&, int, const auto& )
                                  { leftFindings.push_back( item ); } );
        right.SetViolationHandler( [&]( const auto& item, const auto&, int, const auto& )
                                   { rightFindings.push_back( item ); } );
        for( int pass = 0; pass < 3; ++pass )
        {
            leftFindings.clear(); rightFindings.clear();
            std::promise<void> ready;
            auto start = ready.get_future().share();
            auto a = std::async( std::launch::async, [&]
                                { start.wait(); return left.RunTests( EDA_UNITS::MM, true, false ); } );
            auto b = std::async( std::launch::async, [&]
                                { start.wait(); return right.RunTests( EDA_UNITS::MM, true, false ); } );
            ready.set_value();
            BOOST_REQUIRE( a.get() == DRC_RUN_RESULT::COMPLETED );
            BOOST_REQUIRE( b.get() == DRC_RUN_RESULT::COMPLETED );
            BOOST_REQUIRE_EQUAL( leftFindings.size(), 1 );
            BOOST_REQUIRE_EQUAL( rightFindings.size(), 1 );
            BOOST_CHECK( leftFindings.front()->GetErrorMessage( false ).Contains( "first-board-only" ) );
            BOOST_CHECK( !leftFindings.front()->GetErrorMessage( false ).Contains( "second-board-only" ) );
            BOOST_CHECK( rightFindings.front()->GetErrorMessage( false ).Contains( "second-board-only" ) );
            BOOST_CHECK( !rightFindings.front()->GetErrorMessage( false ).Contains( "first-board-only" ) );
        }
    }
    // Engine/provider destruction must not leave a dangling diagnostic pointer.
    BOOST_CHECK( leftFindings.front()->GetViolatingTestName() == "miscellaneous" );
    BOOST_CHECK( rightFindings.front()->GetViolatingTestName() == "miscellaneous" );
}

BOOST_AUTO_TEST_SUITE_END()
