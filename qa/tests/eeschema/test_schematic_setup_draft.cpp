/* Native Schematic Setup isolation. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <sch_setup_draft.h>
#include <project/net_settings.h>
#include <refdes_tracker.h>
#include <settings/json_settings_internals.h>
#include <api/api_sch_annotation.h>
#include <limits>

BOOST_AUTO_TEST_CASE( AnnotationPolicyPreservesAllocatedDesignators )
{
    PROJECT_FILE project( "annotation-policy.kicad_pro" );
    SCHEMATIC_SETTINGS settings( &project, "schematic" );
    project.m_SchematicSettings = &settings;
    project.Load(); settings.Load();
    const auto tracker = settings.m_refDesTracker;
    BOOST_REQUIRE( tracker );
    tracker->Insert( "R123" ); tracker->Insert( "U98" );
    const auto allocated = tracker->Serialize();
    const auto original = SCH_ANNOTATION::Capture( settings );
    std::string failure;
    BOOST_CHECK( SCH_ANNOTATION::Validate( original, failure ) );
    for( int start : { std::numeric_limits<int>::min(), -1, 0, 317, std::numeric_limits<int>::max() } )
    {
        auto desired = original;
        desired.set_start_after( start );
        desired.set_order( kiapi::schematic::types::SAO_Y_POSITION );
        desired.set_method( kiapi::schematic::types::SAM_SHEET_TIMES_1000 );
        desired.set_reuse_designators( !original.reuse_designators() );
        BOOST_REQUIRE( SCH_ANNOTATION::Validate( desired, failure ) );
        SCH_ANNOTATION::Restore( settings, desired );
        BOOST_CHECK_EQUAL( SCH_ANNOTATION::Capture( settings ).SerializeAsString(), desired.SerializeAsString() );
        BOOST_CHECK( settings.m_refDesTracker == tracker );
        BOOST_CHECK_EQUAL( tracker->Serialize(), allocated );
    }
    auto malformed = original;
    malformed.set_order( kiapi::schematic::types::SAO_UNKNOWN );
    BOOST_CHECK( !SCH_ANNOTATION::Validate( malformed, failure ) );
    malformed = original; malformed.set_method( kiapi::schematic::types::SAM_UNKNOWN );
    BOOST_CHECK( !SCH_ANNOTATION::Validate( malformed, failure ) );
    malformed = original;
    malformed.GetReflection()->MutableUnknownFields( &malformed )->AddVarint( 100, 1 );
    BOOST_CHECK( !SCH_ANNOTATION::Validate( malformed, failure ) );
    SCH_ANNOTATION::Restore( settings, original );
    BOOST_CHECK_EQUAL( SCH_ANNOTATION::Capture( settings ).SerializeAsString(), original.SerializeAsString() );
    BOOST_CHECK_EQUAL( tracker->Serialize(), allocated );
}

BOOST_AUTO_TEST_CASE( SchematicDraftDoesNotChangeLiveProjectOrReferenceTracker )
{
    PROJECT_FILE project( "detached-fixture.kicad_pro" );
    ERC_SETTINGS erc( &project, "erc" );
    SCHEMATIC_SETTINGS schematic( &project, "schematic" );
    project.m_ErcSettings = &erc; project.m_SchematicSettings = &schematic;
    project.Load(); erc.Load(); schematic.Load();
    project.m_TextVars["LOCATION"] = "cooling side";
    project.NetSettings()->SetNetChainClassDefinitions( { "Unassigned" } );
    const auto before = project.CaptureCurrentState();
    const auto stored = static_cast<const nlohmann::json&>( *project.Internals() );
    {
        SCH_SETUP_DRAFT draft( project );
        BOOST_CHECK( !draft.Changed() && draft.MatchesLive( project ) );
        BOOST_REQUIRE( draft.SchematicSettings().m_refDesTracker );
        BOOST_CHECK( draft.SchematicSettings().m_refDesTracker != schematic.m_refDesTracker );
        draft.SchematicSettings().m_refDesTracker->SetReuseRefDes( !schematic.m_refDesTracker->GetReuseRefDes() );
        draft.SchematicSettings().m_DefaultTextSize += 1;
        draft.ProjectSettings().m_TextVars["LOCATION"] = "pending";
        draft.ProjectSettings().NetSettings()->SetNetChainClassDefinitions( { "Pending" } );
        draft.ErcSettings().SetPinMapValue( 0, 0, PIN_ERROR::PP_ERROR );
        BOOST_CHECK( draft.Changed() );
        BOOST_CHECK( project.CaptureCurrentState() == before );
        project.m_TextVars["INTERVENING"] = "external edit";
        BOOST_CHECK( !draft.MatchesLive( project ) );
        project.m_TextVars.erase( "INTERVENING" );
    }
    BOOST_CHECK( project.CaptureCurrentState() == before );
    BOOST_CHECK( static_cast<const nlohmann::json&>( *project.Internals() ) == stored );
}

BOOST_AUTO_TEST_CASE( SchematicDraftAppliesOnlyItsChangedOwnersAndCanUndo )
{
    PROJECT_FILE project( "setup-delta-fixture.kicad_pro" );
    ERC_SETTINGS erc( &project, "erc" );
    SCHEMATIC_SETTINGS schematic( &project, "schematic" );
    project.m_ErcSettings = &erc; project.m_SchematicSettings = &schematic;
    project.Load(); erc.Load(); schematic.Load();
    const auto before = project.CaptureCurrentState();
    const auto store = static_cast<const nlohmann::json&>( *project.Internals() );
    const auto netOwner = project.NetSettings();
    const auto refOwner = schematic.m_refDesTracker;
    SCH_SETUP_DRAFT draft( project );
    draft.SchematicSettings().m_DefaultTextSize += 1;
    draft.SchematicSettings().m_refDesTracker->SetReuseRefDes( !refOwner->GetReuseRefDes() );
    draft.ErcSettings().SetPinMapValue( 0, 0, PIN_ERROR::PP_ERROR );
    draft.ProjectSettings().NetSettings()->SetNetChainClassDefinitions( { "Pending" } );
    draft.ProjectSettings().m_BusAliases["BUS"] = { "D0", "D1" };
    const auto after = draft.ProjectSettings().CaptureCurrentState();
    project.m_TextVars["UNRELATED"] = "preserve";
    const auto live = project.CaptureCurrentState();
    project.ApplyCurrentStateDelta( before, after );
    auto expected = after; expected["text_variables"]["UNRELATED"] = "preserve";
    BOOST_CHECK( project.CaptureCurrentState() == expected );
    BOOST_CHECK( project.NetSettings() == netOwner );
    BOOST_CHECK( project.m_SchematicSettings == &schematic && project.m_ErcSettings == &erc );
    BOOST_CHECK( schematic.m_refDesTracker == refOwner );
    project.ApplyCurrentStateDelta( after, before );
    BOOST_CHECK( project.CaptureCurrentState() == live );
    BOOST_CHECK( static_cast<const nlohmann::json&>( *project.Internals() ) == store );
}
