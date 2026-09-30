/* Multi-editor command routing. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <api/api_handler.h>
#include <api/api_request_target.h>
#include <api/common/commands/editor_commands.pb.h>
#include <api/common/commands/project_commands.pb.h>
#include <google/protobuf/empty.pb.h>
#include <array>

namespace
{
using namespace kiapi::common;
using namespace kiapi::common::commands;
using namespace kiapi::common::types;

class ROUTED_HANDLER : public API_HANDLER
{
public:
    ROUTED_HANDLER( DocumentType aType, bool aBusy ) : m_type( aType ), m_busy( aBusy )
    {
        registerHandler<SaveDocument, google::protobuf::Empty>( &ROUTED_HANDLER::save );
    }

    int calls = 0;

protected:
    std::optional<ApiResponseStatus> checkRequestTarget( const google::protobuf::Message& aRequest ) const override
    {
        if( !RequestTargetsOtherDocument( aRequest, m_type ) )
            return std::nullopt;
        ApiResponseStatus result;
        result.set_status( AS_UNHANDLED );
        return result;
    }

private:
    HANDLER_RESULT<google::protobuf::Empty> save( const HANDLER_CONTEXT<SaveDocument>& )
    {
        ++calls;
        if( m_busy )
        {
            ApiResponseStatus result;
            result.set_status( AS_BUSY );
            return tl::unexpected( result );
        }
        return google::protobuf::Empty();
    }

    DocumentType m_type;
    bool m_busy;
};
}

BOOST_AUTO_TEST_SUITE( ApiRequestTarget )

BOOST_AUTO_TEST_CASE( SharedCommandsReachTheirOwnerBeforeUnrelatedBusyChecksInEitherOrder )
{
    for( bool boardFirst : { false, true } )
    {
        ROUTED_HANDLER schematic( DOCTYPE_SCHEMATIC, true ), board( DOCTYPE_PCB, false );
        std::array<API_HANDLER*, 2> order = boardFirst
                ? std::array<API_HANDLER*, 2>{ &board, &schematic }
                : std::array<API_HANDLER*, 2>{ &schematic, &board };
        SaveDocument save;
        save.mutable_document()->set_type( DOCTYPE_PCB );
        ApiRequest request;
        request.mutable_message()->PackFrom( save );
        API_RESULT result = tl::unexpected( ApiResponseStatus{} );
        for( auto* handler : order )
        {
            result = handler->Handle( request );
            if( result || result.error().status() != AS_UNHANDLED )
                break;
        }
        BOOST_REQUIRE( result.has_value() );
        BOOST_CHECK_EQUAL( result->status().status(), AS_OK );
        BOOST_CHECK_EQUAL( schematic.calls, 0 );
        BOOST_CHECK_EQUAL( board.calls, 1 );

        save.mutable_document()->set_type( DOCTYPE_SCHEMATIC );
        request.mutable_message()->PackFrom( save );
        auto busy = schematic.Handle( request );
        BOOST_REQUIRE( !busy );
        BOOST_CHECK_EQUAL( busy.error().status(), AS_BUSY );
        BOOST_CHECK_EQUAL( schematic.calls, 1 );
    }
}

BOOST_AUTO_TEST_CASE( OnlyExplicitRequestTargetsRouteNotEmbeddedPayloadObjects )
{
    CreateItems request;
    request.mutable_header()->mutable_document()->set_type( DOCTYPE_SCHEMATIC );
    DocumentSpecifier payload;
    payload.set_type( DOCTYPE_PCB );
    request.add_items()->PackFrom( payload );
    const auto before = request.SerializeAsString();
    BOOST_CHECK( !RequestTargetsOtherDocument( request, DOCTYPE_SCHEMATIC ) );
    BOOST_CHECK( RequestTargetsOtherDocument( request, DOCTYPE_PCB ) );
    BOOST_CHECK_EQUAL( request.SerializeAsString(), before );
    request.mutable_header()->clear_document();
    BOOST_CHECK( !RequestTargetsOtherDocument( request, DOCTYPE_SCHEMATIC ) );
    BOOST_CHECK( !RequestTargetsOtherDocument( request, DOCTYPE_PCB ) );

    GetOpenDocuments discovery;
    discovery.set_type( DOCTYPE_PCB );
    BOOST_CHECK( !RequestTargetsOtherDocument( discovery, DOCTYPE_SCHEMATIC ) );
}

BOOST_AUTO_TEST_CASE( AbsentTargetsAndProjectValidationStayWithTheOwningCommand )
{
    SaveDocument save;
    BOOST_CHECK( !RequestTargetsOtherDocument( save, DOCTYPE_PCB ) );
    save.mutable_document();
    BOOST_CHECK( !RequestTargetsOtherDocument( save, DOCTYPE_PCB ) );
    save.mutable_document()->set_type( DOCTYPE_PCB );
    save.mutable_document()->mutable_project()->set_name( "wrong-project" );
    BOOST_CHECK( !RequestTargetsOtherDocument( save, DOCTYPE_PCB ) );
    BOOST_CHECK( RequestTargetsOtherDocument( save, DOCTYPE_SCHEMATIC ) );
}

BOOST_AUTO_TEST_SUITE_END()
