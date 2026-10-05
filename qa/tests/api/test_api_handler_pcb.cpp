/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 *
 * This program is free software: you can redistribute it and/or modify it
 * under the terms of the GNU General Public License as published by the
 * Free Software Foundation, either version 3 of the License, or (at your
 * option) any later version.
 *
 * This program is distributed in the hope that it will be useful, but
 * WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

#include <memory>
#include <fstream>
#include <vector>

#include <boost/test/unit_test.hpp>

#include <wx/filename.h>

#include <qa_utils/wx_utils/unit_test_utils.h>
#include <pcbnew_utils/board_test_utils.h>

#include <api/api_handler_pcb.h>
#include <api/headless_pcb_context.h>
#include <api/board/board_commands.pb.h>
#include <api/common/commands/editor_commands.pb.h>
#include <api/common/envelope.pb.h>
#include <api/common/types/base_types.pb.h>

#include <board.h>
#include <connectivity/connectivity_data.h>
#include <settings/settings_manager.h>
#include <zone.h>


namespace
{

/// issue5830 is a four-copper-zone board with stable, human-readable zone UUIDs.
const wxString F_CU_ZONE   = wxS( "00000000-0000-0000-0000-00005c07d704" );
const wxString B_CU_ZONE   = wxS( "00000000-0000-0000-0000-00005c07d701" );
const wxString IN1_CU_ZONE = wxS( "00000000-0000-0000-0000-00005c07d707" );
const wxString IN2_CU_ZONE = wxS( "00000000-0000-0000-0000-00005c07d70a" );


struct API_HANDLER_PCB_FIXTURE
{
    SETTINGS_MANAGER                      m_settingsManager;
    std::unique_ptr<BOARD>                m_board;
    std::shared_ptr<HEADLESS_PCB_CONTEXT> m_context;

    // The context takes ownership of the board; the returned raw pointer lets the test inspect
    // zone state after the handler runs.
    BOARD* loadBoard( const wxString& aRelPath )
    {
        KI_TEST::LoadBoard( m_settingsManager, aRelPath, m_board );

        BOARD* board = m_board.get();
        m_context = std::make_shared<HEADLESS_PCB_CONTEXT>( std::move( m_board ),
                                                            &m_settingsManager.Prj(), nullptr );
        return board;
    }

    kiapi::common::ApiRequest makeRefillRequest( BOARD* aBoard, const std::vector<wxString>& aZoneIds,
                                                 const std::string& aOperationId = {} ) const
    {
        kiapi::board::commands::RefillZones command;
        command.mutable_board()->set_type( kiapi::common::types::DocumentType::DOCTYPE_PCB );
        command.mutable_board()->set_board_filename(
                wxFileName( aBoard->GetFileName() ).GetFullName().ToStdString() );

        for( const wxString& id : aZoneIds )
            command.add_zones()->set_value( id.ToStdString() );

        command.set_operation_id( aOperationId );

        kiapi::common::ApiRequest request;
        request.mutable_header()->set_client_name( "kicad.qa" );
        BOOST_REQUIRE( request.mutable_message()->PackFrom( command ) );

        return request;
    }

    kiapi::common::ApiRequest makeImportRequest( BOARD* aBoard, const wxString& aNetlistPath,
                                                 const std::string& aOperationId ) const
    {
        kiapi::board::commands::ImportNetlist command;
        command.mutable_board()->set_type( kiapi::common::types::DocumentType::DOCTYPE_PCB );
        command.mutable_board()->set_board_filename(
                wxFileName( aBoard->GetFileName() ).GetFullName().ToStdString() );
        command.set_netlist_path( aNetlistPath.ToStdString() );
        command.set_match_mode( kiapi::board::commands::NMM_REFERENCE );
        command.set_operation_id( aOperationId );

        kiapi::common::ApiRequest request;
        request.mutable_header()->set_client_name( "kicad.qa" );
        BOOST_REQUIRE( request.mutable_message()->PackFrom( command ) );
        return request;
    }

    kiapi::common::ApiRequest makeOperationReadRequest( const std::string& aOperationId ) const
    {
        kiapi::automation::v1::NativeCapabilityPcbReadOperation command;
        command.set_operation_id( aOperationId );

        kiapi::common::ApiRequest request;
        request.mutable_header()->set_client_name( "kicad.qa" );
        BOOST_REQUIRE( request.mutable_message()->PackFrom( command ) );
        return request;
    }

    ZONE* zoneByUuid( BOARD* aBoard, const wxString& aUuid ) const
    {
        for( ZONE* zone : aBoard->Zones() )
        {
            if( zone->m_Uuid.AsString() == aUuid )
                return zone;
        }

        return nullptr;
    }

    void unfillAll( BOARD* aBoard ) const
    {
        // Start from a clean slate so a positive IsFilled() result can only come from this fill
        for( ZONE* zone : aBoard->Zones() )
        {
            zone->UnFill();
            BOOST_REQUIRE( !zone->IsFilled() );
        }
    }
};


kiapi::common::ApiRequest makeBeginCommitRequest()
{
    // No header, so the pre-11.0 path assumes the PCB editor
    kiapi::common::commands::BeginCommit command;

    kiapi::common::ApiRequest request;
    request.mutable_header()->set_client_name( "kicad.qa" );
    request.mutable_message()->PackFrom( command );

    return request;
}


kiapi::common::ApiRequest makeRevertRequest( BOARD* aBoard )
{
    kiapi::common::commands::RevertDocument command;
    command.mutable_document()->set_type( kiapi::common::types::DocumentType::DOCTYPE_PCB );
    command.mutable_document()->set_board_filename( wxFileName( aBoard->GetFileName() ).GetFullName().ToStdString() );

    kiapi::common::ApiRequest request;
    request.mutable_header()->set_client_name( "kicad.qa" );
    request.mutable_message()->PackFrom( command );

    return request;
}

} // namespace


BOOST_FIXTURE_TEST_SUITE( ApiHandlerPcb, API_HANDLER_PCB_FIXTURE )


BOOST_AUTO_TEST_CASE( RefillZonesSubset )
{
    BOARD* board = loadBoard( wxS( "issue5830" ) );

    unfillAll( board );

    API_HANDLER_PCB           handler( m_context );
    kiapi::common::ApiRequest request = makeRefillRequest( board, { F_CU_ZONE, IN1_CU_ZONE } );
    API_RESULT                result = handler.Handle( request );

    if( !result.has_value() )
    {
        BOOST_FAIL( "RefillZones returned status " << result.error().status() << ": "
                                                    << result.error().error_message() );
    }

    BOOST_CHECK_EQUAL( result->status().status(), kiapi::common::ApiStatusCode::AS_OK );

    ZONE* fCu   = zoneByUuid( board, F_CU_ZONE );
    ZONE* bCu   = zoneByUuid( board, B_CU_ZONE );
    ZONE* in1Cu = zoneByUuid( board, IN1_CU_ZONE );
    ZONE* in2Cu = zoneByUuid( board, IN2_CU_ZONE );

    BOOST_REQUIRE( fCu && bCu && in1Cu && in2Cu );

    // Exactly the requested zones must be filled; the others must be untouched.
    BOOST_CHECK( fCu->IsFilled() );
    BOOST_CHECK( in1Cu->IsFilled() );
    BOOST_CHECK( !bCu->IsFilled() );
    BOOST_CHECK( !in2Cu->IsFilled() );
}


BOOST_AUTO_TEST_CASE( RefillZonesSubsetRebuildsConnectivity )
{
    BOARD* board = loadBoard( wxS( "issue5830" ) );

    unfillAll( board );

    // Baseline ratsnest with every zone empty; the GND planes are unfilled so their pads still
    // ratsnest together.
    board->BuildConnectivity();
    const unsigned baseline = board->GetConnectivity()->GetUnconnectedCount( false );
    BOOST_REQUIRE_MESSAGE( baseline > 0, "expected an unconnected baseline with zones empty" );

    API_HANDLER_PCB           handler( m_context );
    kiapi::common::ApiRequest request = makeRefillRequest( board, { F_CU_ZONE, IN1_CU_ZONE } );
    API_RESULT                result = handler.Handle( request );

    if( !result.has_value() )
    {
        BOOST_FAIL( "RefillZones returned status " << result.error().status() << ": "
                                                    << result.error().error_message() );
    }

    const unsigned afterFill = board->GetConnectivity()->GetUnconnectedCount( false );

    // Filling the GND planes bridges GND pads that previously ratsnested, so the unconnected
    // count drops.  Push cleared the ratsnest, so if the handler skipped the connectivity
    // rebuild this would read zero instead of the reduced-but-nonzero count.
    BOOST_CHECK_MESSAGE( afterFill > 0, "connectivity was cleared, not rebuilt, after the fill" );
    BOOST_CHECK_MESSAGE( afterFill < baseline,
                         "filling the GND planes should reduce the unconnected count ("
                                 << afterFill << " vs baseline " << baseline << ")" );
}


BOOST_AUTO_TEST_CASE( RefillZonesAllHeadless )
{
    BOARD* board = loadBoard( wxS( "issue5830" ) );

    unfillAll( board );

    API_HANDLER_PCB           handler( m_context );
    kiapi::common::ApiRequest request = makeRefillRequest( board, {} );
    API_RESULT                result = handler.Handle( request );

    if( !result.has_value() )
    {
        BOOST_FAIL( "RefillZones returned status " << result.error().status() << ": "
                                                    << result.error().error_message() );
    }

    BOOST_CHECK_EQUAL( result->status().status(), kiapi::common::ApiStatusCode::AS_OK );

    // With no frame the empty-zones request must fill everything synchronously
    for( ZONE* zone : board->Zones() )
        BOOST_CHECK_MESSAGE( zone->IsFilled(), "zone " << zone->m_Uuid.AsStdString() << " not filled" );
}


BOOST_AUTO_TEST_CASE( RefillZonesUnknownIdRejected )
{
    BOARD* board = loadBoard( wxS( "issue5830" ) );

    API_HANDLER_PCB           handler( m_context );
    kiapi::common::ApiRequest request =
            makeRefillRequest( board, { wxS( "deadbeef-0000-0000-0000-000000000000" ) } );
    API_RESULT                result = handler.Handle( request );

    BOOST_REQUIRE( !result.has_value() );
    BOOST_CHECK_EQUAL( result.error().status(), kiapi::common::ApiStatusCode::AS_BAD_REQUEST );
}


BOOST_AUTO_TEST_CASE( ImportReceiptSurvivesHandlerReplacementAndLaterRefill )
{
    BOARD* board = loadBoard( wxS( "issue5830" ) );
    unfillAll( board );

    // An empty, valid KiCad netlist exercises the native import receipt without
    // requiring project footprint-library setup. The later selected refill is a
    // separate operation handled by the replacement handler instance.
    KI_TEST::TEMPORARY_DIRECTORY scratch( "pcb_receipt_lifecycle_", "" );
    const wxString netlistPath = wxString::FromUTF8(
            ( scratch.GetPath() / "empty.kicad.net" ).string() );
    {
        std::ofstream netlist( netlistPath.ToStdString() );
        netlist << "(export (version \"E\")\n"
                   "  (design (source \"fixture.kicad_sch\") (date \"2026-01-01\")"
                   " (tool \"kicad.qa\")\n"
                   "    (sheet (number \"1\") (name \"/\") (tstamps \"/\")))\n"
                   "  (components)\n  (nets))\n";
    }

    const std::string importOperation = KIID().AsStdString();
    kiapi::common::ApiRequest importRequest = makeImportRequest( board, netlistPath, importOperation );
    kiapi::common::ApiRequest readRequest = makeOperationReadRequest( importOperation );
    kiapi::automation::v1::NativeCapabilityPcbReadOperationResponse beforeReplacement;

    {
        API_HANDLER_PCB handler( m_context );
        API_RESULT result = handler.Handle( importRequest );
        BOOST_REQUIRE( result.has_value() );

        API_RESULT readResult = handler.Handle( readRequest );
        BOOST_REQUIRE( readResult.has_value() );
        BOOST_REQUIRE( readResult->message().UnpackTo( &beforeReplacement ) );
        BOOST_CHECK( beforeReplacement.found() );
        BOOST_CHECK_EQUAL( beforeReplacement.operation(), "netlist-import" );
        BOOST_CHECK_EQUAL( beforeReplacement.document().board_filename(),
                           wxFileName( board->GetFileName() ).GetFullName().ToStdString() );
    }

    API_HANDLER_PCB replacement( m_context );
    const std::string refillOperation = KIID().AsStdString();
    kiapi::common::ApiRequest refillRequest = makeRefillRequest( board, { F_CU_ZONE }, refillOperation );
    API_RESULT refillResult = replacement.Handle( refillRequest );
    BOOST_REQUIRE( refillResult.has_value() );

    API_RESULT afterRefillRead = replacement.Handle( readRequest );
    BOOST_REQUIRE( afterRefillRead.has_value() );
    kiapi::automation::v1::NativeCapabilityPcbReadOperationResponse afterRefill;
    BOOST_REQUIRE( afterRefillRead->message().UnpackTo( &afterRefill ) );
    BOOST_CHECK( afterRefill.found() );
    BOOST_CHECK_EQUAL( afterRefill.operation(), beforeReplacement.operation() );
    BOOST_CHECK_EQUAL( afterRefill.document().SerializeAsString(),
                       beforeReplacement.document().SerializeAsString() );
    BOOST_CHECK_EQUAL( afterRefill.process_epoch(), beforeReplacement.process_epoch() );

    kiapi::common::ApiRequest readRefillRequest = makeOperationReadRequest( refillOperation );
    API_RESULT refillRead = replacement.Handle( readRefillRequest );
    BOOST_REQUIRE( refillRead.has_value() );
    kiapi::automation::v1::NativeCapabilityPcbReadOperationResponse refillReceipt;
    BOOST_REQUIRE( refillRead->message().UnpackTo( &refillReceipt ) );
    BOOST_CHECK( refillReceipt.found() );
    BOOST_CHECK_EQUAL( refillReceipt.operation(), "zone-refill" );
    BOOST_CHECK_EQUAL( refillReceipt.process_epoch(), beforeReplacement.process_epoch() );
}


// RevertDocument reloads the board, freeing every item an in-flight client commit points at.
// The handler must refuse with AS_BUSY while a commit is open, or the pointers dangle
// (use-after-free). Latent UAF found via #24803.
BOOST_AUTO_TEST_CASE( RevertDocumentRejectedWithOpenCommit )
{
    BOARD* board = loadBoard( wxS( "issue5830" ) );

    API_HANDLER_PCB handler( m_context );

    // Open a client transaction, as a client staging edits would
    kiapi::common::ApiRequest beginRequest = makeBeginCommitRequest();
    BOOST_REQUIRE( handler.Handle( beginRequest ).has_value() );

    kiapi::common::ApiRequest request = makeRevertRequest( board );
    API_RESULT                result = handler.Handle( request );

    BOOST_REQUIRE( !result.has_value() );
    BOOST_CHECK_EQUAL( result.error().status(), kiapi::common::ApiStatusCode::AS_BUSY );
    BOOST_CHECK( result.error().error_message().find( "commit" ) != std::string::npos );
}


// With no open commit the guard passes; the reload then needs a running editor, so a headless
// handler reports AS_UNIMPLEMENTED. This confirms the guard does not reject the normal path.
BOOST_AUTO_TEST_CASE( RevertDocumentWithoutCommitPassesGuard )
{
    BOARD* board = loadBoard( wxS( "issue5830" ) );

    API_HANDLER_PCB           handler( m_context );
    kiapi::common::ApiRequest request = makeRevertRequest( board );
    API_RESULT                result = handler.Handle( request );

    BOOST_REQUIRE( !result.has_value() );
    BOOST_CHECK_EQUAL( result.error().status(), kiapi::common::ApiStatusCode::AS_UNIMPLEMENTED );
}


BOOST_AUTO_TEST_SUITE_END()
