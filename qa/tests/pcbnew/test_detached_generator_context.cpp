/* Real generator routing without a live frame or global edit listeners. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <board.h>
#include <board_commit.h>
#include <board_design_settings.h>
#include <drc/drc_engine.h>
#include <pcb_track.h>
#include <pcb_generator.h>
#include <generators/pcb_tuning_pattern.h>
#include <pcbnew_utils/board_file_utils.h>
#include <pcbnew_utils/board_test_utils.h>
#include <router/pns_kicad_iface.h>
#include <router/pns_router.h>
#include <router/pns_routing_settings.h>
#include <settings/settings_manager.h>
#include <tool/tool_manager.h>
#include <tools/generator_tool.h>

BOOST_AUTO_TEST_SUITE( DetachedGeneratorContext )

BOOST_AUTO_TEST_CASE( IndependentRoutersOwnSettingsAndRejectMissingInputWithoutResetting )
{
    BOARD first, second;
    first.GetDesignSettings().m_DRCEngine = std::make_shared<DRC_ENGINE>( &first, &first.GetDesignSettings() );
    second.GetDesignSettings().m_DRCEngine = std::make_shared<DRC_ENGINE>( &second, &second.GetDesignSettings() );
    first.GetDesignSettings().m_DRCEngine->InitEngine( wxFileName() );
    second.GetDesignSettings().m_DRCEngine->InitEngine( wxFileName() );
    TOOL_MANAGER left, right;
    left.SetEnvironment( &first, nullptr, nullptr, nullptr, nullptr );
    right.SetEnvironment( &second, nullptr, nullptr, nullptr, nullptr );
    auto* a = new GENERATOR_TOOL( false );
    auto* b = new GENERATOR_TOOL( false );
    left.RegisterTool( a ); right.RegisterTool( b );
    auto aSettings = std::make_unique<PNS::ROUTING_SETTINGS>( nullptr, "tools.pns" );
    auto bSettings = std::make_unique<PNS::ROUTING_SETTINGS>( nullptr, "tools.pns" );
    aSettings->SetShoveVias( false ); bSettings->SetShoveVias( true );
    a->InitializeSnapshot( std::move( aSettings ) );
    b->InitializeSnapshot( std::move( bSettings ) );
    BOOST_REQUIRE( a->Router() ); BOOST_REQUIRE( b->Router() );
    BOOST_CHECK( a->Router() != b->Router() );
    BOOST_CHECK( a->GetInterface()->GetBoard() == &first );
    BOOST_CHECK( b->GetInterface()->GetBoard() == &second );
    BOOST_CHECK( a->GetInterface()->GetUnits() == EDA_UNITS::MM );
    BOOST_CHECK( !a->Router()->Settings().ShoveVias() );
    BOOST_CHECK( b->Router()->Settings().ShoveVias() );
    auto* retained = a->Router();
    BOOST_CHECK_THROW( a->InitializeSnapshot( nullptr ), std::invalid_argument );
    BOOST_CHECK( a->Router() == retained );
    BOOST_CHECK_NO_THROW( a->GetInterface()->EraseView() );
    auto replacement = std::make_unique<PNS::ROUTING_SETTINGS>( nullptr, "tools.pns" );
    replacement->SetShoveVias( true );
    a->InitializeSnapshot( std::move( replacement ) );
    BOOST_CHECK( a->Router()->Settings().ShoveVias() );
}

BOOST_AUTO_TEST_CASE( RealTuningPatternCanRegenerateWithoutAnEditorFrame )
{
    SETTINGS_MANAGER settings;
    std::unique_ptr<BOARD> board;
    KI_TEST::LoadBoard( settings, "issue17971/issue17971", board );
    auto& item = KI_TEST::RequireBoardItemWithTypeAndId( *board, PCB_GENERATOR_T,
                    KIID( "24d674f0-f4fd-411b-8bf7-cc6e9297d826" ) );
    auto& pattern = static_cast<PCB_TUNING_PATTERN&>( item );
    TOOL_MANAGER manager;
    manager.SetEnvironment( board.get(), nullptr, nullptr, nullptr, nullptr );
    auto* tool = new GENERATOR_TOOL( false );
    manager.RegisterTool( tool );
    tool->InitializeSnapshot( std::make_unique<PNS::ROUTING_SETTINGS>( nullptr, "tools.pns" ) );
    BOARD_COMMIT commit( &manager, true, false );
    pattern.EditStart( tool, board.get(), &commit );
    BOOST_REQUIRE( pattern.Update( tool, board.get(), &commit ) );
    pattern.EditFinish( tool, board.get(), &commit );
    commit.Push( "Detached tuning regeneration", SKIP_UNDO | SKIP_SET_DIRTY | SKIP_TEARDROPS );
    BOOST_CHECK( !pattern.GetBoardItems().empty() );
    BOOST_CHECK( !tool->Router()->RoutingInProgress() );
    BOOST_CHECK( !board->Tracks().empty() );
}

BOOST_AUTO_TEST_SUITE_END()
