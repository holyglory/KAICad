/* PCB editor state must be observable without saving or dropping declarations. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <pcb_project_editor_state.h>
#include <project/net_settings.h>
#include <drc/drc_item.h>
#include <api/board/board_rules.pb.h>
#include <limits>

BOOST_AUTO_TEST_SUITE( PcbProjectEditorState )

BOOST_AUTO_TEST_CASE( NamedPresentationProjectionDoesNotChangeStoredOwners )
{
    PROJECT_FILE project( "projection" );
    BOARD board;
    project.m_LayerPresets.emplace_back( "stored" );
    project.m_Viewports.emplace_back( "stored view", BOX2D( { 1, 2 }, { 3, 4 } ) );
    project.NetSettings()->SetNetColorAssignment( "unrepresented", KIGFX::COLOR4D( 0.1, 0.2, 0.3, 1 ) );
    const auto before = project.CaptureCurrentState();
    std::vector<LAYER_PRESET> presets{ LAYER_PRESET( "edited" ) };
    presets[0].layers = LSET( { F_Cu, B_Cu } );
    presets[0].activeLayer = B_Cu;
    presets[0].flipBoard = true;
    std::vector<VIEWPORT> views{ VIEWPORT( "closer view", BOX2D( { 10, 20 }, { 30, 40 } ) ) };
    const auto captured = PCB_PROJECT_EDITOR_STATE::Capture( board, project, presets, views );
    BOOST_CHECK_EQUAL( captured["board"]["layer_presets"][0]["name"].get<std::string>(), "edited" );
    BOOST_CHECK_EQUAL( captured["board"]["layer_presets"][0]["activeLayer"].get<int>(), B_Cu );
    BOOST_CHECK( captured["board"]["layer_presets"][0]["flipBoard"].get<bool>() );
    BOOST_CHECK_EQUAL( captured["board"]["viewports"][0]["x"].get<double>(), 10 );
    BOOST_CHECK_EQUAL( captured["board"]["viewports"][0]["h"].get<double>(), 40 );
    BOOST_CHECK( captured["net_settings"]["net_colors"] == before["net_settings"]["net_colors"] );
    BOOST_CHECK( project.CaptureCurrentState() == before );
    BOOST_CHECK( PCB_PROJECT_EDITOR_STATE::Capture( board, project, presets, views ) == captured );
    const auto empty = PCB_PROJECT_EDITOR_STATE::Capture( board, project, {}, {} );
    BOOST_CHECK( empty["board"]["layer_presets"].empty() );
    BOOST_CHECK( empty["board"]["viewports"].empty() );
    BOOST_CHECK( project.CaptureCurrentState() == before );
    views[0].rect.SetWidth( std::numeric_limits<double>::quiet_NaN() );
    BOOST_CHECK_THROW( PCB_PROJECT_EDITOR_STATE::Capture( board, project, presets, views ), std::runtime_error );
    BOOST_CHECK( project.CaptureCurrentState() == before );
    JSON_SETTINGS clearing( "", SETTINGS_LOC::NONE, 0, false, false, false );
    clearing.Set<nlohmann::json>( "presets", nlohmann::json::array() );
    clearing.Set<nlohmann::json>( "views", nlohmann::json::array() );
    PARAM_LAYER_PRESET presetReader( "presets", &presets );
    PARAM_VIEWPORT viewReader( "views", &views );
    presetReader.Load( clearing ); viewReader.Load( clearing );
    BOOST_CHECK( presets.empty() && views.empty() );
}

BOOST_AUTO_TEST_CASE( ExclusionsKeepUnrepresentedBindingsAndRespectExplicitRemovalAndComments )
{
    BOARD board;
    PCB_MARKER absent( DRC_ITEM::Create( DRCE_CLEARANCE ), { 100, 100 } );
    absent.SetExcluded( true, "future binding" );
    const auto retained = DRC_EXCLUSION::FromMarker( absent );
    board.GetDesignSettings().m_DrcExclusions.insert( retained );
    auto* visible = new PCB_MARKER( DRC_ITEM::Create( DRCE_CLEARANCE ), { 200, 200 } );
    visible->SetExcluded( true, "old note" );
    board.Add( visible );
    const auto original = DRC_EXCLUSION::FromMarker( *visible );
    board.GetDesignSettings().m_DrcExclusions.insert( original );
    visible->SetExcluded( true, "new note" );
    auto projected = PCB_PROJECT_EDITOR_STATE::Exclusions( board );
    BOOST_REQUIRE_EQUAL( projected.size(), 2 );
    BOOST_CHECK( projected.contains( retained ) );
    BOOST_CHECK_EQUAL( projected.find( original )->GetComment().ToStdString( wxConvUTF8 ), "new note" );
    BOOST_CHECK_EQUAL( board.GetDesignSettings().m_DrcExclusions.find( original )->GetComment().ToStdString( wxConvUTF8 ), "old note" );
    visible->SetExcluded( false );
    projected = PCB_PROJECT_EDITOR_STATE::Exclusions( board );
    BOOST_REQUIRE_EQUAL( projected.size(), 1 );
    BOOST_CHECK( projected.contains( retained ) );
    board.RecordDRCExclusions();
    BOOST_CHECK( board.GetDesignSettings().m_DrcExclusions == projected );
    auto markers = board.ResolveDRCExclusions( false );
    BOOST_CHECK( markers.empty() );
    BOOST_CHECK( board.GetDesignSettings().m_DrcExclusions.contains( retained ) );
}

BOOST_AUTO_TEST_CASE( BoardLevelExclusionSurvivesNewBoardIdentityWithoutGuessingOtherIds )
{
    BOARD first;
    auto item = DRC_ITEM::Create( DRCE_INVALID_OUTLINE );
    item->SetItems( &first );
    auto* marker = new PCB_MARKER( item, { 100, 200 }, Edge_Cuts );
    first.Add( marker );
    marker->SetExcluded( true, "reviewed board outline" );
    const auto exclusion = DRC_EXCLUSION::FromMarker( *marker );
    BOOST_REQUIRE_EQUAL( exclusion.ToProto().marker().items_size(), 1 );
    BOOST_CHECK_EQUAL( exclusion.ToProto().marker().items( 0 ).value(), niluuid.AsStdString() );
    BOOST_CHECK( item->GetMainItemID() == first.m_Uuid );

    // Serialize through the actual project JSON representation, not just a
    // copied in-memory pointer. The ordinary unknown object must stay unresolved.
    nlohmann::json saved = exclusion;
    BOARD second;
    BOOST_REQUIRE( first.m_Uuid != second.m_Uuid );
    second.GetDesignSettings().m_DrcExclusions.insert( saved.get<DRC_EXCLUSION>() );
    auto unresolved = exclusion.ToProto();
    unresolved.mutable_marker()->mutable_items( 0 )->set_value( KIID().AsStdString() );
    second.GetDesignSettings().m_DrcExclusions.insert( DRC_EXCLUSION::FromProto( unresolved ) );
    auto restored = second.ResolveDRCExclusions( true );
    BOOST_REQUIRE_EQUAL( restored.size(), 1 );
    second.Add( restored[0] );
    BOOST_CHECK( restored[0]->GetRCItem()->GetMainItemID() == second.m_Uuid );
    BOOST_CHECK_EQUAL( restored[0]->GetComment(), "reviewed board outline" );
    BOOST_CHECK_EQUAL( DRC_EXCLUSION::FromMarker( *restored[0] ).GetSortKey(), exclusion.GetSortKey() );
    BOOST_CHECK_EQUAL( second.GetDesignSettings().m_DrcExclusions.size(), 2 );
    BOOST_CHECK( second.ResolveDRCExclusions( true ).empty() );

    // New actual findings use the current UUID and still match the same saved
    // declaration; position or name similarity is not involved.
    restored[0]->SetExcluded( false );
    second.ResolveDRCExclusions( false );
    BOOST_CHECK( restored[0]->IsExcluded() );
    BOOST_CHECK_EQUAL( restored[0]->GetComment(), "reviewed board outline" );
}

BOOST_AUTO_TEST_CASE( RemovingOneNetColorPreservesUnrepresentedDeclarations )
{
    NET_SETTINGS settings( nullptr, "net_settings" );
    settings.SetNetColorAssignment( "visible", KIGFX::COLOR4D( 1, 0, 0, 1 ) );
    settings.SetNetColorAssignment( "unrepresented", KIGFX::COLOR4D( 0, 1, 0, 1 ) );
    const auto before = settings.GetNetColorAssignments().at( "unrepresented" );
    settings.RemoveNetColorAssignment( "visible" );
    BOOST_CHECK( !settings.GetNetColorAssignments().contains( "visible" ) );
    BOOST_CHECK( settings.GetNetColorAssignments().at( "unrepresented" ) == before );
    settings.RemoveNetColorAssignment( "already absent" );
    BOOST_CHECK_EQUAL( settings.GetNetColorAssignments().size(), 1 );
}

BOOST_AUTO_TEST_SUITE_END()
