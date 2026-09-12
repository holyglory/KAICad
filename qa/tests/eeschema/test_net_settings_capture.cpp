/* Project net-settings capture ownership. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <api/api_sch_net_settings.h>
#include <project/project_file.h>
#include <settings/json_settings_internals.h>

BOOST_AUTO_TEST_SUITE( NetSettingsCapture )

BOOST_AUTO_TEST_CASE( CaptureKeepsDeclaredOwnersAndDoesNotSerializeEffectiveCaches )
{
    PROJECT_FILE project( "net-settings-capture.kicad_pro" );
    project.Load();
    auto settings = project.NetSettings();
    BOOST_REQUIRE( settings );
    auto power = std::make_shared<NETCLASS>( "Power", false );
    power->SetTrackWidth( 500000 );
    power->SetuViaDiameter( 300000 );
    power->SetuViaDrill( 100000 );
    power->SetLineStyle( 2 );
    settings->SetNetclass( "Power", power );
    settings->SetNetclassLabelAssignment( "/VCC", { "Power", "Unresolved" } );
    settings->SetNetclassPatternAssignment( "USB*", "Default" );
    settings->SetNetclassPatternAssignment( "V*", "Power" );
    settings->SetNetColorAssignment( "/VCC", KIGFX::COLOR4D( 1, 0, 0, 1 ) );
    settings->SetNetChainNetClass( "power-rail", "Power" );
    settings->SetNetChainClassDefinitions( { "Control" } );
    settings->SetNetChainClass( "other-chain", "Control" );
    const auto persisted = settings->CaptureCurrentState();
    const auto first = SCH_NET_SETTINGS::Capture( *settings );
    BOOST_REQUIRE( first.has_default_class() );
    BOOST_CHECK_EQUAL( first.default_class().name(), "Default" );
    BOOST_REQUIRE_EQUAL( first.classes_size(), 1 );
    BOOST_CHECK_EQUAL( first.classes( 0 ).name(), "Power" );
    BOOST_CHECK( !first.classes( 0 ).board().has_clearance() );
    BOOST_CHECK_EQUAL( first.patterns( 0 ).pattern(), "USB*" );
    BOOST_CHECK_EQUAL( first.patterns( 1 ).pattern(), "V*" );
    BOOST_CHECK_EQUAL( first.chain_netclasses().at( "power-rail" ), "Power" );
    BOOST_CHECK_EQUAL( first.label_assignments().at( "/VCC" ).names_size(), 2 );
    BOOST_REQUIRE( settings->GetEffectiveNetClass( "/VCC" ) );
    const auto afterResolution = SCH_NET_SETTINGS::Capture( *settings );
    BOOST_CHECK( SCH_NET_SETTINGS::Same( first, afterResolution ) );
    BOOST_CHECK( settings->CaptureCurrentState() == persisted );
    settings->SetNetclassLabelAssignment( "/VCC", { "Power" } );
    const auto newProjection = SCH_NET_SETTINGS::Capture( *settings );
    BOOST_CHECK( !SCH_NET_SETTINGS::Same( first, newProjection ) );
    BOOST_CHECK( SCH_NET_SETTINGS::SameDeclared( first, newProjection ) );
    BOOST_CHECK_EQUAL( settings->GetNetChainClass( "other-chain" ), "Control" );
}

BOOST_AUTO_TEST_CASE( DeclaredPreparationAndApplyPreserveIndependentProjectOwners )
{
    PROJECT_FILE project( "net-settings-prepare.kicad_pro" );
    project.Load();
    auto live = project.NetSettings();
    BOOST_REQUIRE( live );
    live->SetNetclassLabelAssignment( "/N", { "Default" } );
    live->SetNetChainClassDefinitions( { "Keep" } );
    live->SetNetChainClass( "existing", "Keep" );
    auto before = SCH_NET_SETTINGS::Capture( *live );
    auto desired = before;
    desired.mutable_default_class()->mutable_board()->mutable_track_width()->set_value_nm( 420000 );
    auto* declared = desired.add_classes();
    NETCLASS auxiliary( "Aux", false );
    auxiliary.Serialize( *declared );
    auto prepared = SCH_NET_SETTINGS::PrepareDeclared( desired );
    const auto* identity = live.get();
    const auto priorGrouping = live->GetNetChainClasses();
    const auto priorLabels = live->GetNetclassLabelAssignments();
    SCH_NET_SETTINGS::ApplyPrepared( *live, *prepared );
    BOOST_CHECK( project.NetSettings().get() == identity );
    BOOST_CHECK( live->GetNetChainClasses() == priorGrouping );
    BOOST_CHECK( live->GetNetclassLabelAssignments() == priorLabels );
    BOOST_CHECK( live->GetNetChainClassDefinitions().contains( "Keep" ) );
    BOOST_CHECK( SCH_NET_SETTINGS::SameDeclared( SCH_NET_SETTINGS::Capture( *live ), desired ) );
    auto rollback = SCH_NET_SETTINGS::PrepareDeclared( before );
    SCH_NET_SETTINGS::ApplyPrepared( *live, *rollback );
    BOOST_CHECK( SCH_NET_SETTINGS::Same( SCH_NET_SETTINGS::Capture( *live ), before ) );
    for( int invalidCase = 0; invalidCase < 3; ++invalidCase )
    {
        auto invalid = desired;
        if( invalidCase == 0 ) invalid.clear_default_class();
        if( invalidCase == 1 ) *invalid.add_classes() = invalid.classes( 0 );
        if( invalidCase == 2 ) invalid.mutable_classes( 0 )->mutable_board()->mutable_via_stack()
                ->mutable_drill()->mutable_diameter()->set_y_nm( 12 );
        BOOST_CHECK_THROW( SCH_NET_SETTINGS::PrepareDeclared( invalid ), std::runtime_error );
        BOOST_CHECK( SCH_NET_SETTINGS::Same( SCH_NET_SETTINGS::Capture( *live ), before ) );
    }
}

BOOST_AUTO_TEST_SUITE_END()
