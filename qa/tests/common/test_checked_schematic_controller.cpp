/* Exact-state batch admission, replay and failure containment. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <api/checked_schematic_controller.h>
#include <google/protobuf/util/message_differencer.h>
#include <kiid.h>
#include <filesystem>
#include <stdexcept>

namespace
{
using namespace kiapi::automation::v1;
using google::protobuf::util::MessageDifferencer;

struct CHECKED_FIXTURE
{
    CHECKED_SCHEMATIC_CONTROLLER controller;
    DocumentLifecycleState state;
    std::string process = KIID().AsStdString();
    unsigned reads = 0, mutations = 0;
    bool failBefore = false, failAfter = false, reject = false, partialRejection = false;
    bool throwMutation = false, wrongResult = false, noOp = false, wrongAfter = false;

    CHECKED_FIXTURE()
    {
        auto* document = state.mutable_document();
        document->set_type( kiapi::common::types::DOCTYPE_SCHEMATIC );
        document->mutable_sheet_path()->add_path()->set_value( KIID().AsStdString() );
        document->mutable_project()->set_name( "checked" );
        document->mutable_project()->set_path( std::filesystem::temp_directory_path().string() );
        state.set_process_epoch( process ); state.set_native_identity( KIID().AsStdString() );
        state.mutable_revision()->set_epoch( KIID().AsStdString() ); state.mutable_revision()->set_sequence( 7 );
        state.set_scope( DLS_SCHEMATIC_HIERARCHY ); state.set_project_settings_included( true );
        state.set_state_sha256( std::string( 64, 'a' ) ); state.set_native_content_dirty( true );
        // The fake owning reader supplies complete, consistent baseline observations.
        // Native integration separately exercises real filesystem observations.
        for( const char* name : { "checked.kicad_sch", "checked.kicad_pro" } )
        {
            const auto path = ( std::filesystem::temp_directory_path() / name ).string();
            state.add_native_files( path );
            auto* file = state.add_file_baselines();
            file->set_path( path ); file->set_baseline_path( path );
            file->set_baseline_known( true ); file->set_current_known( true );
            file->set_baseline_exists( true ); file->set_current_exists( true );
            file->set_baseline_sha256( std::string( 64, 'b' ) ); file->set_current_sha256( std::string( 64, 'b' ) );
            file->set_baseline_bytes( 3 ); file->set_current_bytes( 3 ); file->set_status( NFBS_UNCHANGED );
        }
    }

    CheckedSchematicBatch Request()
    {
        CheckedSchematicBatch request;
        request.mutable_expected_state()->CopyFrom( state );
        auto* batch = request.mutable_batch();
        batch->mutable_document()->CopyFrom( state.document() );
        batch->set_operation_id( KIID().AsStdString() ); batch->set_document_epoch( state.revision().epoch() );
        batch->mutable_expected_revision()->CopyFrom( state.revision() );
        batch->add_operations()->mutable_set_title_block()->set_title( "Checked edit" );
        return request;
    }

    API_RESULT Dispatch( ApiRequest& request )
    {
        auto error = []() -> API_RESULT
        {
            ApiResponseStatus status; status.set_status( ApiStatusCode::AS_BAD_REQUEST );
            status.set_error_message( "Fixture rejection" ); return tl::unexpected( status );
        };
        ApiResponse response; response.mutable_status()->set_status( ApiStatusCode::AS_OK );
        if( request.message().Is<ReadDocumentLifecycleState>() )
        {
            ++reads;
            if( ( !mutations && failBefore ) || ( mutations && failAfter ) ) return error();
            auto observed = state;
            if( mutations && wrongAfter ) observed.set_process_epoch( KIID().AsStdString() );
            response.mutable_message()->PackFrom( observed );
        }
        else if( request.message().Is<ApplySchematicItemBatch>() )
        {
            ++mutations;
            if( throwMutation ) throw std::runtime_error( "Fixture native exception" );
            if( reject )
            {
                if( partialRejection ) state.set_state_sha256( std::string( 64, 'c' ) );
                return error();
            }
            if( !noOp )
            {
                state.mutable_revision()->set_sequence( state.revision().sequence() + 1 );
                state.set_state_sha256( std::string( 64, 'd' ) );
            }
            SchematicItemBatchResult result; result.mutable_revision()->CopyFrom( state.revision() );
            if( wrongResult ) result.mutable_revision()->set_epoch( KIID().AsStdString() );
            response.mutable_message()->PackFrom( result );
        }
        else return error();
        return response;
    }

    API_RESULT Handle( const google::protobuf::Message& request )
    {
        ApiRequest envelope; envelope.mutable_message()->PackFrom( request );
        return controller.Handle( envelope, process, [this]( ApiRequest& value ) { return Dispatch( value ); } );
    }

    CheckedSchematicBatchReceipt Apply( const CheckedSchematicBatch& request )
    {
        auto response = Handle( request );
        if( !response ) throw std::runtime_error( response.error().error_message() );
        CheckedSchematicBatchReceipt result;
        if( !response->message().UnpackTo( &result ) ) throw std::runtime_error( "Wrong checked reply" );
        return result;
    }
};
}

BOOST_AUTO_TEST_SUITE( CheckedSchematicBatchController )

BOOST_AUTO_TEST_CASE( ContentAndDiskChangesRejectWithoutDependingOnTheCursor )
{
    for( unsigned change = 0; change < 5; ++change )
    {
        CHECKED_FIXTURE f;
        auto request = f.Request();
        if( change == 0 ) f.state.set_state_sha256( std::string( 64, 'c' ) );
        if( change == 1 ) f.state.mutable_file_baselines( 0 )->set_current_sha256( std::string( 64, 'c' ) );
        if( change == 2 ) f.state.set_native_identity( KIID().AsStdString() );
        if( change == 3 ) f.state.mutable_revision()->set_sequence( 8 );
        if( change == 4 ) f.state.set_native_content_dirty( false );
        auto result = f.Apply( request );
        BOOST_CHECK_EQUAL( result.status(), CSBS_REJECTED );
        BOOST_CHECK_EQUAL( result.error_code(), "stale_document_state" );
        BOOST_CHECK_EQUAL( f.mutations, 0 );
        f.state.CopyFrom( request.expected_state() );
        BOOST_CHECK( MessageDifferencer::Equals( result, f.Apply( request ) ) );
        BOOST_CHECK_EQUAL( f.reads, 1 );
    }
}

BOOST_AUTO_TEST_CASE( SuccessAndNoOpReplayTheExactRequestWithoutRepeatingTheMutation )
{
    for( bool noOp : { false, true } )
    {
        CHECKED_FIXTURE f; f.noOp = noOp;
        auto request = f.Request(); const auto first = f.Apply( request );
        BOOST_CHECK_EQUAL( first.status(), CSBS_COMPLETED );
        BOOST_CHECK( first.expected_request_verified() );
        BOOST_CHECK( first.has_observed_before() && first.has_observed_after() && first.has_result() );
        BOOST_CHECK_EQUAL( f.mutations, 1 ); BOOST_CHECK_EQUAL( f.reads, 2 );
        f.state.set_state_sha256( std::string( 64, 'c' ) );
        BOOST_CHECK( MessageDifferencer::Equals( first, f.Apply( request ) ) );
        BOOST_CHECK_EQUAL( f.mutations, 1 ); BOOST_CHECK_EQUAL( f.reads, 2 );
        auto changed = request; changed.mutable_expected_state()->set_state_sha256( std::string( 64, 'e' ) );
        BOOST_CHECK( !f.Handle( changed ) );
        changed = request; changed.mutable_batch()->set_description( "Different request" );
        BOOST_CHECK( !f.Handle( changed ) );
    }
}

BOOST_AUTO_TEST_CASE( ReceiptInspectionIsBoundToTheExactDocumentProcessAndRequest )
{
    CHECKED_FIXTURE f; auto request = f.Request();
    ReadCheckedSchematicBatchReceipt read;
    read.mutable_document()->CopyFrom( request.batch().document() ); read.set_process_epoch( f.process );
    read.set_operation_id( request.batch().operation_id() ); read.mutable_expected_request()->CopyFrom( request );
    auto missing = f.Handle( read ); BOOST_REQUIRE( missing );
    CheckedSchematicBatchReceipt result; BOOST_REQUIRE( missing->message().UnpackTo( &result ) );
    BOOST_CHECK_EQUAL( result.status(), CSBS_NOT_FOUND ); BOOST_CHECK( !result.expected_request_verified() );
    const auto applied = f.Apply( request );
    auto response = f.Handle( read ); BOOST_REQUIRE( response ); BOOST_REQUIRE( response->message().UnpackTo( &result ) );
    BOOST_CHECK( MessageDifferencer::Equals( applied, result ) );
    read.set_process_epoch( KIID().AsStdString() ); BOOST_CHECK( !f.Handle( read ) );
    read.set_process_epoch( f.process ); read.mutable_expected_request()->mutable_batch()->set_description( "Other" );
    BOOST_CHECK( !f.Handle( read ) );
    BOOST_CHECK_EQUAL( f.mutations, 1 );
}

BOOST_AUTO_TEST_CASE( IncompleteBaselinesAndMalformedStateNeverReachNativeMutation )
{
    for( unsigned invalid = 0; invalid < 6; ++invalid )
    {
        CHECKED_FIXTURE f;
        if( invalid == 0 ) f.state.clear_file_baselines();
        if( invalid == 1 ) f.state.mutable_file_baselines( 0 )->set_baseline_known( false );
        if( invalid == 2 ) f.state.mutable_file_baselines( 0 )->set_current_sha256( std::string( 64, 'c' ) );
        auto request = f.Request();
        if( invalid == 3 ) request.mutable_expected_state()->set_project_settings_included( false );
        if( invalid == 4 ) request.mutable_expected_state()->set_process_epoch( KIID().AsStdString() );
        if( invalid == 5 ) request.mutable_batch()->clear_expected_revision();
        auto reply = f.Handle( request );
        if( invalid < 3 )
        {
            BOOST_REQUIRE( reply ); CheckedSchematicBatchReceipt result;
            BOOST_REQUIRE( reply->message().UnpackTo( &result ) );
            BOOST_CHECK_EQUAL( result.status(), CSBS_REJECTED );
            BOOST_CHECK_EQUAL( result.error_code(), "file_baseline_conflict" );
        }
        else BOOST_CHECK( !reply );
        BOOST_CHECK_EQUAL( f.mutations, 0 );
    }
}

BOOST_AUTO_TEST_CASE( NativeFailureCannotClaimRejectionWhenPostStateIsChangedOrUnavailable )
{
    for( unsigned failure = 0; failure < 7; ++failure )
    {
        CHECKED_FIXTURE f;
        f.failBefore = failure == 0;
        f.reject = failure == 1 || failure == 2;
        f.partialRejection = failure == 2;
        f.failAfter = failure == 3;
        f.throwMutation = failure == 4;
        f.wrongResult = failure == 5;
        f.wrongAfter = failure == 6;
        auto request = f.Request(); auto result = f.Apply( request );
        BOOST_CHECK_EQUAL( result.status(), failure < 2 ? CSBS_REJECTED : CSBS_INDETERMINATE );
        BOOST_CHECK_EQUAL( f.mutations, failure == 0 ? 0 : 1 );
        const auto calls = f.mutations;
        BOOST_CHECK( MessageDifferencer::Equals( result, f.Apply( request ) ) );
        BOOST_CHECK_EQUAL( calls, f.mutations );
        if( failure == 3 || failure == 6 ) BOOST_CHECK( result.has_result() );
    }
}

BOOST_AUTO_TEST_CASE( OversizedRequestIsRefusedBeforeObservationOrMutation )
{
    CHECKED_FIXTURE f; auto request = f.Request();
    request.mutable_batch()->set_description( std::string( 3 * 1024 * 1024, 'x' ) );
    BOOST_CHECK( !f.Handle( request ) );
    BOOST_CHECK_EQUAL( f.mutations, 0 ); BOOST_CHECK_EQUAL( f.reads, 0 );
}

BOOST_AUTO_TEST_CASE( ReceiptBudgetStopsAdmissionWithoutEvictingTheFirstSuccessfulIdentity )
{
    CHECKED_FIXTURE f;
    const auto firstRequest = f.Request(); const auto first = f.Apply( firstRequest );
    bool exhausted = false;
    for( unsigned index = 0; index < 4096; ++index )
    {
        const auto mutations = f.mutations, reads = f.reads;
        auto response = f.Handle( f.Request() );
        if( !response )
        {
            BOOST_CHECK_EQUAL( f.mutations, mutations ); BOOST_CHECK_EQUAL( f.reads, reads );
            exhausted = true;
            break;
        }
    }
    BOOST_CHECK( exhausted );
    const auto calls = f.mutations;
    BOOST_CHECK( MessageDifferencer::Equals( first, f.Apply( firstRequest ) ) );
    BOOST_CHECK_EQUAL( f.mutations, calls );
}

BOOST_AUTO_TEST_SUITE_END()
