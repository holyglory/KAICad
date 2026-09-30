/* Real detached copper preparation, not full DRC-job qualification. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <api/pcb_drc_run_inputs.h>
#include <advanced_config.h>
#include <board.h>
#include <board_design_settings.h>
#include <cli_progress_reporter.h>
#include <drc/drc_engine.h>
#include <drawing_sheet/ds_data_model.h>
#include <footprint_library_adapter.h>
#include <footprint.h>
#include <libraries/library_manager.h>
#include <netinfo.h>
#include <pad.h>
#include <pcb_track.h>
#include <pcb_generator.h>
#include <generators/pcb_via_stitch.h>
#include <pcb_io/kicad_sexpr/pcb_io_kicad_sexpr.h>
#include <pcbnew_utils/board_test_utils.h>
#include <richio.h>
#include <router/pns_routing_settings.h>
#include <settings/settings_manager.h>
#include <zone.h>
#include <teardrop/teardrop_parameters.h>
#include <algorithm>

namespace
{
using STATUS = PCB_DRC_COPPER_PREPARATION::STATUS;
std::string structure( BOARD& board )
{
    PCB_IO_KICAD_SEXPR io;
    STRING_FORMATTER output;
    io.FormatBoardToFormatter( &output, &board, nullptr, false );
    return output.GetString();
}

struct COPPER_FIXTURE
{
    LIBRARY_MANAGER libraries;
    FOOTPRINT_LIBRARY_ADAPTER adapter{ libraries };
    DS_DATA_MODEL drawing;
    PNS::ROUTING_SETTINGS routing{ nullptr, "tools.pns" };
    PCB_DRC_CAPTURE_CONTEXT context{ adapter, drawing, KIID(), &routing };
    COPPER_FIXTURE() { drawing.ClearList(); drawing.AllowVoidList( true ); }

    std::unique_ptr<PCB_DRC_RUN_INPUTS> capture( BOARD& source, bool rules = true )
    {
        auto inputs = PCB_DRC_RUN_INPUTS::Capture( source, context );
        if( rules && inputs )
        {
            auto& board = inputs->GetBoard();
            auto& design = board.GetDesignSettings();
            design.m_DRCEngine = std::make_shared<DRC_ENGINE>( &board, &design );
            inputs->InitializeEngine( *design.m_DRCEngine );
        }
        return inputs;
    }

    ZONE* addZone( BOARD& board )
    {
        auto* zone = new ZONE( &board );
        zone->SetLayer( F_SilkS );
        zone->AppendCorner( { 0, 0 }, -1 );
        zone->AppendCorner( { 10000000, 0 }, -1 );
        zone->AppendCorner( { 10000000, 10000000 }, -1 );
        zone->AppendCorner( { 0, 10000000 }, -1 );
        zone->SetIslandRemovalMode( ISLAND_REMOVAL_MODE::NEVER );
        board.Add( zone );
        return zone;
    }
};
}

BOOST_FIXTURE_TEST_SUITE( DrcCopperPreparation, COPPER_FIXTURE )

BOOST_AUTO_TEST_CASE( FillsOnlyThePrivateCopyAndDoesNotRepeatCompletedPreparation )
{
    BOARD source;
    auto* zone = addZone( source );
    const auto before = structure( source );
    const int revision = source.GetTimeStamp();
    auto inputs = capture( source );
    BOOST_REQUIRE( inputs );
    const auto& result = inputs->PrepareCopper();
    BOOST_REQUIRE_MESSAGE( result.status == STATUS::COMPLETED, result.error );
    auto* filled = static_cast<ZONE*>( inputs->GetBoard().ResolveItem( zone->m_Uuid, true ) );
    BOOST_REQUIRE( filled );
    BOOST_REQUIRE( filled->GetFilledPolysList( F_SilkS ) );
    BOOST_CHECK_GT( filled->GetFilledPolysList( F_SilkS )->TotalVertices(), 0 );
    BOOST_CHECK_EQUAL( structure( source ), before );
    BOOST_CHECK_EQUAL( source.GetTimeStamp(), revision );
    const int preparedRevision = inputs->GetBoard().GetTimeStamp();
    const auto prepared = structure( inputs->GetBoard() );
    BOOST_CHECK( inputs->PrepareCopper().status == STATUS::COMPLETED );
    BOOST_CHECK_EQUAL( inputs->GetBoard().GetTimeStamp(), preparedRevision );
    BOOST_CHECK_EQUAL( structure( inputs->GetBoard() ), prepared );
}

BOOST_AUTO_TEST_CASE( RegeneratesDirtyNativeTuningWithoutChangingTheSource )
{
    SETTINGS_MANAGER settings;
    std::unique_ptr<BOARD> source;
    KI_TEST::LoadBoard( settings, "issue17971/issue17971", source );
    const KIID identity( "24d674f0-f4fd-411b-8bf7-cc6e9297d826" );
    for( auto* generator : source->Generators() ) generator->ClearDirty();
    auto* pattern = dynamic_cast<PCB_GENERATOR*>( source->ResolveItem( identity, true ) );
    BOOST_REQUIRE( pattern );
    pattern->MarkDirty();
    const auto before = structure( *source );
    const int revision = source->GetTimeStamp();
    auto inputs = capture( *source );
    BOOST_REQUIRE( inputs );
    const auto& result = inputs->PrepareCopper();
    BOOST_REQUIRE_MESSAGE( result.status == STATUS::COMPLETED, result.error );
    BOOST_CHECK( std::find( result.regenerated.begin(), result.regenerated.end(), identity )
                    != result.regenerated.end() );
    BOOST_CHECK_EQUAL( structure( *source ), before );
    BOOST_CHECK_EQUAL( source->GetTimeStamp(), revision );
    BOOST_CHECK( pattern->IsDirty() );
    BOOST_CHECK( !inputs->GetBoard().Tracks().empty() );
}

BOOST_AUTO_TEST_CASE( MissingInputsCancellationFailureAndFreshCaptureRecovery )
{
    BOARD source;
    addZone( source );
    const auto before = structure( source );
    context.routingSettings = nullptr;
    auto missingRouting = capture( source );
    BOOST_REQUIRE( missingRouting );
    BOOST_CHECK( missingRouting->PrepareCopper().status == STATUS::FAILED );
    BOOST_CHECK( !missingRouting->PrepareCopper().error.empty() );
    context.routingSettings = &routing;
    auto missingRules = capture( source, false );
    BOOST_REQUIRE( missingRules );
    BOOST_CHECK( missingRules->PrepareCopper().status == STATUS::FAILED );

    class REPORTER : public CLI_PROGRESS_REPORTER
    {
    public:
        bool stop = false;
        bool fail = false;
        bool IsCancelled() const override { return stop; }
        bool KeepRefreshing( bool = false ) override { return !stop; }
        void Report( const wxString& ) override
        { if( fail ) throw std::runtime_error( "fixture progress sink failure" ); }
    } reporter;
    auto cancelled = capture( source );
    BOOST_REQUIRE( cancelled );
    reporter.stop = true;
    BOOST_CHECK( cancelled->PrepareCopper( &reporter ).status == STATUS::CANCELLED );
    reporter.stop = false;
    BOOST_CHECK( cancelled->PrepareCopper( &reporter ).status == STATUS::CANCELLED );

    auto failed = capture( source );
    BOOST_REQUIRE( failed );
    reporter.fail = true;
    const auto& failure = failed->PrepareCopper( &reporter );
    BOOST_CHECK( failure.status == STATUS::FAILED );
    BOOST_CHECK_EQUAL( failure.error, "fixture progress sink failure" );
    reporter.fail = false;
    auto recovered = capture( source );
    BOOST_REQUIRE( recovered );
    BOOST_CHECK( recovered->PrepareCopper( &reporter ).status == STATUS::COMPLETED );
    BOOST_CHECK_EQUAL( structure( source ), before );
}

BOOST_AUTO_TEST_CASE( IterationLimitDoesNotBecomeSuccessfulPreparation )
{
    auto& enabled = const_cast<ADVANCED_CFG&>( ADVANCED_CFG::GetCfg() ).m_ZoneFillIterativeRefill;
    struct RESTORE { bool& value; bool old; ~RESTORE() { value = old; } } restore{ enabled, enabled };
    enabled = true;
    SETTINGS_MANAGER settings;
    std::unique_ptr<BOARD> source;
    KI_TEST::LoadBoard( settings, "zone_refill_convergence_limit", source );
    const auto before = structure( *source );
    auto inputs = capture( *source );
    BOOST_REQUIRE( inputs );
    BOOST_CHECK( inputs->PrepareCopper().status == STATUS::NOT_CONVERGED );
    BOOST_CHECK_EQUAL( structure( *source ), before );
}

BOOST_AUTO_TEST_CASE( RebuildsAndFillsRealPadTeardropsWithoutAddingThemToTheSource )
{
    BOARD source;
    auto* net = new NETINFO_ITEM( &source, "SUPPLY", 1 );
    source.Add( net );
    auto* footprint = new FOOTPRINT( &source );
    auto* pad = new PAD( footprint );
    pad->SetAttribute( PAD_ATTRIB::SMD );
    pad->SetLayerSet( LSET( { F_Cu } ) );
    pad->SetShape( PADSTACK::ALL_LAYERS, PAD_SHAPE::CIRCLE );
    pad->SetSize( PADSTACK::ALL_LAYERS, { 2000000, 2000000 } );
    pad->SetPosition( { 5000000, 5000000 } );
    pad->SetNetCode( 1 );
    pad->GetTeardropParams().m_Enabled = true;
    pad->GetTeardropParams().m_TdOnPadsInZones = true;
    footprint->Add( pad );
    source.Add( footprint );
    auto* track = new PCB_TRACK( &source );
    track->SetStart( pad->GetPosition() );
    track->SetEnd( { 10000000, 5000000 } );
    track->SetWidth( 250000 );
    track->SetLayer( F_Cu );
    track->SetNetCode( 1 );
    source.Add( track );
    source.BuildConnectivity();
    const auto before = structure( source );
    BOOST_CHECK( source.Zones().empty() );
    auto inputs = capture( source );
    BOOST_REQUIRE( inputs );
    const auto& result = inputs->PrepareCopper();
    BOOST_REQUIRE_MESSAGE( result.status == STATUS::COMPLETED, result.error );
    unsigned filledTeardrops = 0;
    for( ZONE* zone : inputs->GetBoard().Zones() )
    {
        if( !zone->IsTeardropArea() || !zone->IsOnLayer( F_Cu ) ) continue;
        BOOST_REQUIRE( zone->GetFilledPolysList( F_Cu ) );
        BOOST_CHECK_GT( zone->GetFilledPolysList( F_Cu )->TotalVertices(), 0 );
        ++filledTeardrops;
    }
    BOOST_CHECK_GT( filledTeardrops, 0 );
    BOOST_CHECK( source.Zones().empty() );
    BOOST_CHECK_EQUAL( structure( source ), before );
}

BOOST_AUTO_TEST_CASE( CopperFillDrivesViaStitchingAndTheGeneratedViasStayPrivate )
{
    BOARD source;
    source.Add( new NETINFO_ITEM( &source, "GND", 1 ) );
    for( PCB_LAYER_ID layer : { F_Cu, B_Cu } )
    {
        ZONE* zone = addZone( source );
        zone->SetLayer( layer );
        zone->SetNetCode( 1 );
    }
    auto* stitch = new PCB_VIA_STITCH( &source );
    SHAPE_POLY_SET outline;
    outline.NewOutline();
    outline.Append( 2000000, 2000000 );
    outline.Append( 8000000, 2000000 );
    outline.Append( 8000000, 8000000 );
    outline.Append( 2000000, 8000000 );
    stitch->SetOutline( outline );
    stitch->SetPitch( 2000000 );
    stitch->SetLayout( PCB_VIA_STITCH_LAYOUT::STAGGERED );
    stitch->SetMode( PCB_VIA_STITCH_MODE::STITCH );
    stitch->SetNetCode( 1 );
    stitch->ViaTemplate()->SetWidth( PADSTACK::ALL_LAYERS, 600000 );
    stitch->ViaTemplate()->SetDrill( 300000 );
    source.Add( stitch );
    const auto before = structure( source );
    auto inputs = capture( source );
    BOOST_REQUIRE( inputs );
    const auto& result = inputs->PrepareCopper();
    BOOST_REQUIRE_MESSAGE( result.status == STATUS::COMPLETED, result.error );
    BOOST_CHECK( std::find( result.regenerated.begin(), result.regenerated.end(), stitch->m_Uuid )
                    != result.regenerated.end() );
    auto* generated = dynamic_cast<PCB_VIA_STITCH*>( inputs->GetBoard().ResolveItem( stitch->m_Uuid, true ) );
    BOOST_REQUIRE( generated );
    BOOST_CHECK_GT( generated->GetBoardItems().size(), 0 );
    BOOST_CHECK_GT( inputs->GetBoard().Tracks().size(), 0 );
    for( ZONE* zone : inputs->GetBoard().Zones() )
    {
        BOOST_REQUIRE( zone->GetFilledPolysList( zone->GetFirstLayer() ) );
        BOOST_CHECK_GT( zone->GetFilledPolysList( zone->GetFirstLayer() )->TotalVertices(), 0 );
    }
    BOOST_CHECK( stitch->GetBoardItems().empty() );
    BOOST_CHECK( source.Tracks().empty() );
    BOOST_CHECK_EQUAL( structure( source ), before );
}

BOOST_AUTO_TEST_SUITE_END()
