/* Real generator routing without a live frame or global edit listeners. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <board.h>
#include <board_commit.h>
#include <board_design_settings.h>
#include <drc/drc_engine.h>
#include <footprint.h>
#include <pad.h>
#include <pcb_track.h>
#include <pcb_generator.h>
#include <generators/pcb_tuning_pattern.h>
#include <pcbnew_utils/board_file_utils.h>
#include <pcbnew_utils/board_test_utils.h>
#include <router/pns_kicad_iface.h>
#include <router/pns_node.h>
#include <router/pns_router.h>
#include <router/pns_routing_settings.h>
#include <router/pns_segment.h>
#include <router/pns_solid.h>
#include <settings/settings_manager.h>
#include <tool/tool_manager.h>
#include <tools/generator_tool.h>

namespace
{
struct GENERATOR_CONTEXT
{
    BOARD board;
    TOOL_MANAGER manager;
    GENERATOR_TOOL* tool;

    GENERATOR_CONTEXT()
    {
        auto& design = board.GetDesignSettings();
        design.m_DRCEngine = std::make_shared<DRC_ENGINE>( &board, &design );
        design.m_DRCEngine->InitEngine( wxFileName() );
        manager.SetEnvironment( &board, nullptr, nullptr, nullptr, nullptr );
        tool = new GENERATOR_TOOL( false );
        manager.RegisterTool( tool );
        tool->InitializeSnapshot( std::make_unique<PNS::ROUTING_SETTINGS>( nullptr, "tools.pns" ) );
    }
};
}

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

BOOST_AUTO_TEST_CASE( InPlaceUpdatesAreOwnedDeferredAndReversibleInTheOuterCommit )
{
    GENERATOR_CONTEXT context, other;
    auto* track = new PCB_TRACK( &context.board );
    track->SetStart( { 1000000, 1000000 } );
    track->SetEnd( { 2000000, 1000000 } );
    track->SetWidth( 200000 );
    track->SetLocked( true );
    context.board.Add( track );
    const KIID identity = track->m_Uuid;
    auto* iface = context.tool->GetInterface();
    PNS::SEGMENT changed( SEG( { 1000000, 2000000 }, { 3000000, 2000000 } ), track->GetNet() );
    changed.SetParent( track );
    changed.SetWidth( 300000 );
    // Exercise ROUTER's remove/add-to-update classification, not just its callback.
    auto* router = context.tool->Router();
    router->SyncWorld();
    auto* original = router->GetWorld()->FindItemByParent( track );
    BOOST_REQUIRE( original );
    changed.SetLayers( original->Layers() );
    auto* branch = router->GetWorld()->Branch();
    branch->Remove( original );
    branch->Add( std::unique_ptr<PNS::SEGMENT>( changed.Clone() ) );
    router->CommitRouting( branch );
    changed.SetWidth( 900000 ); // Must not change the queued, owned copy.
    BOOST_CHECK_EQUAL( track->GetWidth(), 200000 );
    BOOST_CHECK( track->GetStart() == VECTOR2I( 1000000, 1000000 ) );

    BOARD_COMMIT wrong( &other.manager, true, false );
    BOOST_CHECK_THROW( context.tool->ApplyRouterUpdates( wrong ), std::invalid_argument );
    BOOST_CHECK( wrong.Empty() );
    BOOST_CHECK_EQUAL( track->GetWidth(), 200000 );
    BOARD_COMMIT commit( &context.manager, true, false );
    context.tool->ApplyRouterUpdates( commit );
    BOOST_CHECK_EQUAL( track->GetWidth(), 300000 );
    BOOST_CHECK( track->GetStart() == VECTOR2I( 1000000, 2000000 ) );
    BOOST_CHECK( track->m_Uuid == identity );
    BOOST_CHECK( track->IsLocked() );
    BOOST_CHECK( !commit.Empty() );
    commit.Revert();
    BOOST_CHECK_EQUAL( track->GetWidth(), 200000 );
    BOOST_CHECK( track->GetStart() == VECTOR2I( 1000000, 1000000 ) );
    BOOST_CHECK( track->m_Uuid == identity );
    context.tool->ApplyRouterUpdates( commit );
    BOOST_CHECK( commit.Empty() ); // Consuming a batch does not replay it.

    iface->UpdateItem( &changed );
    context.tool->ClearRouterChanges();
    context.tool->ApplyRouterUpdates( commit );
    BOOST_CHECK( commit.Empty() );
    BOOST_CHECK_EQUAL( track->GetWidth(), 200000 );

    changed.SetParent( nullptr );
    BOOST_CHECK_THROW( iface->UpdateItem( &changed ), std::invalid_argument );
    PCB_TRACK foreign( &other.board );
    changed.SetParent( &foreign );
    BOOST_CHECK_THROW( iface->UpdateItem( &changed ), std::invalid_argument );

    changed.SetParent( track );
    iface->UpdateItem( &changed );
    commit.Remove( track );
    context.tool->ApplyRouterUpdates( commit );
    BOOST_CHECK_EQUAL( commit.GetStatus( track ) & CHT_TYPE, CHT_REMOVE );
    BOOST_CHECK_EQUAL( track->GetWidth(), 200000 );
    commit.Revert();
}

BOOST_AUTO_TEST_CASE( PadMovementUsesTheOwningFootprintTransaction )
{
    GENERATOR_CONTEXT context;
    auto* footprint = new FOOTPRINT( &context.board );
    footprint->SetPosition( { 1000000, 2000000 } );
    auto* pad = new PAD( footprint );
    pad->SetPosition( footprint->GetPosition() );
    footprint->Add( pad );
    context.board.Add( footprint );
    const KIID identity = footprint->m_Uuid;
    PNS::SOLID moved;
    moved.SetParent( pad );
    moved.SetPos( { 3000000, 4000000 } );
    context.tool->GetInterface()->UpdateItem( &moved );
    BOOST_CHECK( footprint->GetPosition() == VECTOR2I( 1000000, 2000000 ) );
    BOARD_COMMIT commit( &context.manager, true, false );
    context.tool->ApplyRouterUpdates( commit );
    BOOST_CHECK( footprint->GetPosition() == VECTOR2I( 3000000, 4000000 ) );
    BOOST_CHECK( footprint->m_Uuid == identity );
    BOOST_CHECK_EQUAL( commit.GetStatus( footprint ) & CHT_TYPE, CHT_MODIFY );
    commit.Revert();
    BOOST_CHECK( footprint->GetPosition() == VECTOR2I( 1000000, 2000000 ) );
}

BOOST_AUTO_TEST_CASE( GeneratedItemUpdateKeepsItsAdditionAndCanBeCancelled )
{
    GENERATOR_CONTEXT context;
    PNS::SEGMENT generated( SEG( { 1000000, 1000000 }, { 2000000, 1000000 } ),
                            context.board.FindNet( 0 ) );
    generated.SetLayer( 0 );
    generated.SetWidth( 200000 );
    auto* iface = context.tool->GetInterface();
    iface->AddItem( &generated );
    auto* track = static_cast<PCB_TRACK*>( generated.Parent() );
    BOOST_REQUIRE( track );
    generated.SetWidth( 400000 );
    iface->UpdateItem( &generated );
    BOARD_COMMIT commit( &context.manager, true, false );
    commit.Add( track );
    context.tool->ApplyRouterUpdates( commit );
    BOOST_CHECK_EQUAL( track->GetWidth(), 400000 );
    BOOST_CHECK_EQUAL( commit.GetStatus( track ) & CHT_TYPE, CHT_ADD );
    commit.Revert();
    context.tool->ClearRouterChanges();
    BOOST_CHECK( context.board.Tracks().empty() );
}

BOOST_AUTO_TEST_SUITE_END()
