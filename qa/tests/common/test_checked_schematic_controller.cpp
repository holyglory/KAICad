/* Exact-state batch admission, replay and failure containment. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <api/checked_schematic_controller.h>
#include <google/protobuf/util/message_differencer.h>
#include <kiid.h>
#include <algorithm>
#include <cctype>
#include <filesystem>
#include <optional>
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
    unsigned electricalSchema = 0;
    bool failBefore = false, failAfter = false, reject = false, partialRejection = false;
    bool throwMutation = false, wrongResult = false, noOp = false, wrongAfter = false;
    bool changeDuringCapture = false, wrongElectricalRevision = false;
    size_t payloadBytes = 0;
    bool ignoreReplyLimit = false;
    std::string rejectMessage = "Fixture rejection";
    bool verified = false;

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
        else if( request.message().Is<ReadSchematicElectricalState>() )
        {
            ReadSchematicElectricalState query;
            if( !request.message().UnpackTo( &query ) ) return error();
            electricalSchema = query.schema_version();
            SchematicElectricalState electrical;
            electrical.mutable_hierarchy()->mutable_data()->mutable_document()->CopyFrom( state.document() );
            electrical.mutable_hierarchy()->mutable_revision()->CopyFrom( state.revision() );
            if( wrongElectricalRevision ) electrical.mutable_hierarchy()->mutable_revision()->set_sequence( 0 );
            if( changeDuringCapture ) state.set_state_sha256( std::string( 64, 'c' ) );
            response.mutable_message()->PackFrom( electrical );
        }
        else if( request.message().Is<ApplySchematicItemBatch>() )
        {
            ++mutations;
            ApplySchematicItemBatch batch;
            if( !request.message().UnpackTo( &batch ) ) return error();
            if( payloadBytes && !ignoreReplyLimit && payloadBytes + 256 > batch.maximum_result_bytes() )
                return error();
            if( throwMutation ) throw std::runtime_error( "Fixture native exception" );
            if( reject )
            {
                if( partialRejection ) state.set_state_sha256( std::string( 64, 'c' ) );
                ApiResponseStatus status; status.set_status( ApiStatusCode::AS_BAD_REQUEST );
                status.set_error_message( rejectMessage ); return tl::unexpected( status );
            }
            if( !noOp )
            {
                state.mutable_revision()->set_sequence( state.revision().sequence() + 1 );
                state.set_state_sha256( std::string( 64, 'd' ) );
            }
            SchematicItemBatchResult result; result.mutable_revision()->CopyFrom( state.revision() );
            result.set_connectivity_assertion_verified( verified );
            if( payloadBytes ) result.add_items()->set_value( std::string( payloadBytes, 'x' ) );
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

    // A realization-shaped batch: one staged creation followed by the post-condition.
    CheckedSchematicBatch AssertedRequest()
    {
        auto request = Request();
        auto* batch = request.mutable_batch();
        batch->clear_operations();
        batch->add_operations()->mutable_create()->set_type_url( "type.googleapis.com/kiapi.schematic.types.LocalLabel" );
        auto* group = batch->add_operations()->mutable_assert_connectivity();
        group->set_version( 1 );
        auto* pin = group->add_expected_groups()->add_pins();
        pin->mutable_path()->CopyFrom( state.document().sheet_path() );
        pin->mutable_pin()->set_value( KIID().AsStdString() );
        return request;
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

BOOST_AUTO_TEST_CASE( CombinedCaptureBindsElectricalDataToOneUnchangedNativeState )
{
    CHECKED_FIXTURE f;
    ReadCheckedSchematicState query;
    query.mutable_document()->CopyFrom( f.state.document() ); query.set_process_epoch( f.process );
    auto response = f.Handle( query ); BOOST_REQUIRE( response );
    CheckedSchematicState state; BOOST_REQUIRE( response->message().UnpackTo( &state ) );
    BOOST_CHECK( MessageDifferencer::Equals( state.state(), f.state ) );
    BOOST_CHECK( MessageDifferencer::Equals( state.electrical().hierarchy().revision(), f.state.revision() ) );
    BOOST_CHECK_EQUAL( f.electricalSchema, 9 );
    BOOST_CHECK_EQUAL( f.mutations, 0 );
    f.changeDuringCapture = true;
    BOOST_CHECK( !f.Handle( query ) ); BOOST_CHECK_EQUAL( f.mutations, 0 );
    f.changeDuringCapture = false; f.wrongElectricalRevision = true;
    BOOST_CHECK( !f.Handle( query ) );
    f.wrongElectricalRevision = false;
    query.mutable_document()->mutable_sheet_path()->add_path()->set_value( KIID().AsStdString() );
    BOOST_CHECK( !f.Handle( query ) );
    query.mutable_document()->CopyFrom( f.state.document() ); query.set_process_epoch( KIID().AsStdString() );
    BOOST_CHECK( !f.Handle( query ) );
}

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

BOOST_AUTO_TEST_CASE( ShortRequestsCanReturnLargeObjectsAndReleaseUnusedReservation )
{
    CHECKED_FIXTURE f; f.payloadBytes = 64 * 1024;
    auto firstRequest = f.Request(); const auto first = f.Apply( firstRequest );
    BOOST_REQUIRE_EQUAL( first.status(), CSBS_COMPLETED );
    BOOST_CHECK_GT( first.result().ByteSizeLong(), firstRequest.ByteSizeLong() + 4096 );
    BOOST_CHECK_EQUAL( first.result().items( 0 ).value().size(), f.payloadBytes );
    const auto calls = f.mutations;
    BOOST_CHECK( MessageDifferencer::Equals( first, f.Apply( firstRequest ) ) );
    BOOST_CHECK_EQUAL( f.mutations, calls );
    BOOST_CHECK_EQUAL( f.Apply( f.Request() ).status(), CSBS_COMPLETED );
}

BOOST_AUTO_TEST_CASE( ReplyAllowanceRejectsBeforeCommitAndDoesNotPreventLaterRecovery )
{
    CHECKED_FIXTURE f; f.payloadBytes = 64 * 1024;
    auto request = f.Request(); request.mutable_batch()->set_maximum_result_bytes( 1024 );
    const auto before = f.state; auto result = f.Apply( request );
    BOOST_CHECK_EQUAL( result.status(), CSBS_REJECTED );
    BOOST_CHECK( MessageDifferencer::Equals( before, f.state ) );
    BOOST_CHECK_EQUAL( f.Apply( f.Request() ).status(), CSBS_COMPLETED );
    // A broken peer which ignores the negotiated limit is still indeterminate,
    // not a successful edit and never an invitation to repeat the operation.
    f.ignoreReplyLimit = true;
    request = f.Request(); request.mutable_batch()->set_maximum_result_bytes( 1024 );
    result = f.Apply( request );
    BOOST_CHECK_EQUAL( result.status(), CSBS_INDETERMINATE );
    const auto calls = f.mutations;
    BOOST_CHECK( MessageDifferencer::Equals( result, f.Apply( request ) ) );
    BOOST_CHECK_EQUAL( f.mutations, calls );
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

BOOST_AUTO_TEST_CASE( RequestTypesAreSortedFullNamesThatDriveDispatch )
{
    const auto& types = CHECKED_SCHEMATIC_CONTROLLER::RequestTypes();
    const std::vector<std::string> expected{ "kiapi.automation.v1.CheckedSchematicBatch",
                                             "kiapi.automation.v1.ReadCheckedSchematicBatchReceipt",
                                             "kiapi.automation.v1.ReadCheckedSchematicState" };
    BOOST_CHECK_EQUAL_COLLECTIONS( types.begin(), types.end(), expected.begin(), expected.end() );
    BOOST_CHECK( std::is_sorted( types.begin(), types.end() ) );
    auto handles = []( const google::protobuf::Message& message )
    {
        ApiRequest envelope; envelope.mutable_message()->PackFrom( message );
        return CHECKED_SCHEMATIC_CONTROLLER::Handles( envelope );
    };
    BOOST_CHECK( handles( CheckedSchematicBatch() ) );
    BOOST_CHECK( handles( ReadCheckedSchematicBatchReceipt() ) );
    BOOST_CHECK( handles( ReadCheckedSchematicState() ) );
    // False positives: the unchecked native batch and state reads stay with their handlers.
    BOOST_CHECK( !handles( ApplySchematicItemBatch() ) );
    BOOST_CHECK( !handles( ReadDocumentLifecycleState() ) );
    ApiRequest malformed; malformed.mutable_message()->set_type_url( "kiapi.automation.v1.CheckedSchematicBatch" );
    BOOST_CHECK( !CHECKED_SCHEMATIC_CONTROLLER::Handles( malformed ) );
    malformed.mutable_message()->set_type_url( "type.googleapis.com/kiapi.automation.v1.CheckedSchematicBatchX" );
    BOOST_CHECK( !CHECKED_SCHEMATIC_CONTROLLER::Handles( malformed ) );
}

namespace
{
struct ASSERTION_FIXTURE
{
    std::string root = KIID().AsStdString(), child = KIID().AsStdString(), unloaded = KIID().AsStdString();
    std::vector<std::string> pins{ KIID().AsStdString(), KIID().AsStdString(), KIID().AsStdString() };

    ApplySchematicItemBatch Batch()
    {
        ApplySchematicItemBatch batch;
        batch.set_operation_id( KIID().AsStdString() );
        batch.mutable_expected_revision()->set_epoch( KIID().AsStdString() );
        batch.add_operations()->mutable_create()->set_type_url( "type.googleapis.com/kiapi.schematic.types.LocalLabel" );
        batch.add_operations()->mutable_update()->set_type_url( "type.googleapis.com/kiapi.schematic.types.SheetSymbol" );
        batch.add_operations()->mutable_replace_library_cache();
        auto* assertion = batch.add_operations()->mutable_assert_connectivity();
        assertion->set_version( 1 );
        auto* joined = assertion->add_expected_groups();
        Pin( *joined, { root }, pins[0] ); Pin( *joined, { root, child }, pins[1] );
        Pin( *assertion->add_expected_groups(), { root }, pins[2] );
        return batch;
    }

    static void Pin( SchematicPinGroup& group, const std::vector<std::string>& path, const std::string& pin )
    {
        auto* anchor = group.add_pins();
        for( const std::string& id : path ) anchor->mutable_path()->add_path()->set_value( id );
        anchor->mutable_pin()->set_value( pin );
    }

    std::optional<std::string> Prepare( const ApplySchematicItemBatch& batch,
                                        CHECKED_SCHEMATIC_CONTROLLER::CONNECTIVITY_ASSERTION& result )
    {
        return CHECKED_SCHEMATIC_CONTROLLER::PrepareConnectivityAssertion( batch,
                [&]( const kiapi::common::types::SheetPath& path )
                {
                    return std::none_of( path.path().begin(), path.path().end(),
                                         [&]( const auto& id ) { return id.value() == unloaded; } );
                }, result );
    }

    std::string Rejection( const ApplySchematicItemBatch& batch )
    {
        CHECKED_SCHEMATIC_CONTROLLER::CONNECTIVITY_ASSERTION result;
        auto rejected = Prepare( batch, result );
        BOOST_REQUIRE( rejected );
        BOOST_CHECK_EQUAL( result.index, -1 );
        return *rejected;
    }
};

bool StartsWith( const std::string& value, const std::string& prefix ) { return value.rfind( prefix, 0 ) == 0; }
}

BOOST_AUTO_TEST_CASE( ConnectivityAssertionAdmissionFailsClosedBeforeAnyOperation )
{
    ASSERTION_FIXTURE f;
    CHECKED_SCHEMATIC_CONTROLLER::CONNECTIVITY_ASSERTION admitted;
    BOOST_REQUIRE( !f.Prepare( f.Batch(), admitted ) );
    BOOST_CHECK_EQUAL( admitted.index, 3 );
    BOOST_REQUIRE_EQUAL( admitted.expected.size(), 2 );
    BOOST_CHECK( admitted.expected[0] == ( CHECKED_SCHEMATIC_CONTROLLER::PIN_GROUP{
            "/" + f.root + "#" + f.pins[0], "/" + f.root + "/" + f.child + "#" + f.pins[1] } ) );
    BOOST_CHECK( admitted.expected[1] == ( CHECKED_SCHEMATIC_CONTROLLER::PIN_GROUP{ "/" + f.root + "#" + f.pins[2] } ) );

    // A batch without an assertion is untouched, whatever its operations.
    ApplySchematicItemBatch plain; plain.add_operations()->mutable_set_title_block()->set_title( "Plain" );
    BOOST_CHECK( !f.Prepare( plain, admitted ) ); BOOST_CHECK_EQUAL( admitted.index, -1 );
    // An empty expectation is well defined: the batch must not change any pin connection.
    auto unchanged = f.Batch(); unchanged.mutable_operations( 3 )->mutable_assert_connectivity()->clear_expected_groups();
    BOOST_CHECK( !f.Prepare( unchanged, admitted ) ); BOOST_CHECK_EQUAL( admitted.index, 3 );
    BOOST_CHECK( admitted.expected.empty() );

    auto notLast = f.Batch(); notLast.mutable_operations()->SwapElements( 2, 3 );
    BOOST_CHECK( StartsWith( f.Rejection( notLast ), "Atomic operation 2 rejected: A connectivity assertion must be the last" ) );
    auto twice = f.Batch(); twice.add_operations()->CopyFrom( twice.operations( 3 ) );
    BOOST_CHECK( StartsWith( f.Rejection( twice ), "Atomic operation 4 rejected: A batch can contain at most one" ) );
    auto targeted = f.Batch();
    targeted.mutable_operations( 3 )->mutable_target_document()->set_type( kiapi::common::types::DOCTYPE_SCHEMATIC );
    BOOST_CHECK( StartsWith( f.Rejection( targeted ), "Atomic operation 3 rejected: A connectivity assertion covers the whole batch" ) );
    for( uint32_t version : { 0u, 2u } )
    {
        auto versioned = f.Batch(); versioned.mutable_operations( 3 )->mutable_assert_connectivity()->set_version( version );
        BOOST_CHECK( StartsWith( f.Rejection( versioned ), "Atomic operation 3 rejected: Only connectivity assertion version 1" ) );
    }
    auto empty = f.Batch(); empty.mutable_operations( 3 )->mutable_assert_connectivity()->add_expected_groups();
    BOOST_CHECK( StartsWith( f.Rejection( empty ), "Atomic operation 3 rejected: An expected connectivity group cannot be empty" ) );
    auto duplicate = f.Batch();
    ASSERTION_FIXTURE::Pin( *duplicate.mutable_operations( 3 )->mutable_assert_connectivity()->mutable_expected_groups( 1 ),
                            { f.root }, f.pins[0] );
    BOOST_CHECK( StartsWith( f.Rejection( duplicate ), "Atomic operation 3 rejected: An expected pin can appear only once" ) );
    std::string upper = f.pins[0]; std::transform( upper.begin(), upper.end(), upper.begin(), ::toupper );
    for( auto [path, pin] : std::vector<std::pair<std::vector<std::string>, std::string>>{
                 { { f.root }, upper }, { { f.root }, "" }, { {}, f.pins[0] },
                 { { f.root, "not-a-uuid" }, f.pins[0] }, { { f.root }, "00000000-0000-0000-0000-000000000000" } } )
    {
        auto malformed = f.Batch(); auto* group = malformed.mutable_operations( 3 )->mutable_assert_connectivity()->add_expected_groups();
        ASSERTION_FIXTURE::Pin( *group, path, pin );
        BOOST_CHECK( StartsWith( f.Rejection( malformed ), "Atomic operation 3 rejected: Expected pins require canonical" ) );
    }
    auto unloaded = f.Batch();
    ASSERTION_FIXTURE::Pin( *unloaded.mutable_operations( 3 )->mutable_assert_connectivity()->add_expected_groups(),
                            { f.root, f.unloaded }, KIID().AsStdString() );
    BOOST_CHECK( StartsWith( f.Rejection( unloaded ), "Atomic operation 3 rejected: An expected pin path is not a loaded" ) );
    auto anonymous = f.Batch(); anonymous.clear_operation_id();
    BOOST_CHECK( StartsWith( f.Rejection( anonymous ), "Atomic operation 3 rejected: A connectivity assertion requires revision" ) );
    auto unrevisioned = f.Batch(); unrevisioned.clear_expected_revision();
    BOOST_CHECK( StartsWith( f.Rejection( unrevisioned ), "Atomic operation 3 rejected: A connectivity assertion requires revision" ) );
    auto mixed = f.Batch(); mixed.mutable_operations( 1 )->mutable_set_title_block()->set_title( "Not a realization" );
    BOOST_CHECK( StartsWith( f.Rejection( mixed ), "Atomic operation 1 rejected: Only create, update and library cache" ) );
    auto missing = f.Batch(); missing.mutable_operations( 0 )->Clear();
    BOOST_CHECK( StartsWith( f.Rejection( missing ), "Atomic operation 0 rejected: Only create, update and library cache" ) );
    // Fields reserved for a later contract revision fail closed until they are implemented.
    auto future = f.Batch();
    future.mutable_operations( 3 )->mutable_assert_connectivity()->GetReflection()->MutableUnknownFields(
            future.mutable_operations( 3 )->mutable_assert_connectivity() )->AddVarint( 3, 1 );
    BOOST_CHECK( StartsWith( f.Rejection( future ), "Atomic operation 3 rejected: The connectivity assertion contains unsupported fields" ) );
}

BOOST_AUTO_TEST_CASE( ConnectivityPostconditionCatchesEveryMismatchAndAcceptsEquivalentGroupings )
{
    using PARTITION = CHECKED_SCHEMATIC_CONTROLLER::PIN_PARTITION;
    using GROUPS = std::vector<CHECKED_SCHEMATIC_CONTROLLER::PIN_GROUP>;
    const PARTITION before{ { "a" }, { "b" }, { "c" }, { "d" }, { "x", "y" } };
    auto check = [&]( const PARTITION& after, const GROUPS& expected ) {
        return CHECKED_SCHEMATIC_CONTROLLER::CheckConnectivity( before, after, expected );
    };
    auto first = []( const std::optional<std::string>& failure, const std::string& detail )
    {
        BOOST_REQUIRE( failure );
        BOOST_TEST_INFO( *failure );
        BOOST_CHECK( StartsWith( *failure, "connectivity_postcondition_failed: expected=" ) );
        BOOST_CHECK( failure->find( " first=" + detail ) != std::string::npos );
    };

    // False positives: the exact result passes regardless of group or member order, and a new
    // pin the batch created passes when it is asserted as its own group.
    BOOST_CHECK( !check( { { "a", "b" }, { "c" }, { "d" }, { "x", "y" } }, { { "b", "a" } } ) );
    BOOST_CHECK( !check( { { "a", "b" }, { "c", "d" }, { "x", "y" } }, { { "d", "c" }, { "b", "a" } } ) );
    BOOST_CHECK( !check( { { "a", "b", "n" }, { "c" }, { "d" }, { "x", "y" }, { "m" } }, { { "m" }, { "n", "a", "b" } } ) );
    BOOST_CHECK( !check( before, {} ) );
    // An asserted group may name an existing net whole; its members need not be singletons.
    BOOST_CHECK( !check( { { "a" }, { "b" }, { "c" }, { "d", "x", "y" } }, { { "x", "y", "d" } } ) );

    // Must-catch: a member the batch failed to join (missing member).
    auto missingMember = check( { { "a", "b" }, { "c" }, { "d" }, { "x", "y" } }, { { "a", "b", "c" } } );
    first( missingMember, "unexpected_split:c" );
    BOOST_CHECK( missingMember->find( "expected=1 mismatches=3 " ) != std::string::npos );
    // Must-catch: an asserted pin that does not exist after the batch.
    first( check( { { "a", "b" }, { "c" }, { "d" }, { "x", "y" } }, { { "a", "b", "n" } } ), "missing_pin:n" );
    // Must-catch: an extra member that joined the asserted group.
    first( check( { { "a", "b", "c" }, { "d" }, { "x", "y" } }, { { "a", "b" } } ), "unexpected_join:c" );
    // Must-catch: two asserted groups merged into one net.
    auto merged = check( { { "a", "b", "c", "d" }, { "x", "y" } }, { { "a", "b" }, { "c", "d" } } );
    first( merged, "unexpected_join:c,d" );
    BOOST_CHECK( merged->find( "expected=2 mismatches=3 " ) != std::string::npos );
    // Must-catch: an asserted group split into two nets.
    first( check( { { "a", "b" }, { "c", "d" }, { "x", "y" } }, { { "a", "b", "c", "d" } } ), "unexpected_split:c,d" );
    // Must-catch: a net the assertion does not mention changed anyway.
    first( check( { { "a", "b" }, { "c" }, { "d" }, { "x" }, { "y" } }, { { "a", "b" } } ), "unaffected_group_changed:x,y" );
    first( check( { { "a", "b" }, { "c" }, { "d" }, { "x", "y", "n" } }, { { "a", "b" } } ), "unaffected_group_changed:x,y" );
    // Must-catch: a pin the batch created but did not assert.
    first( check( { { "a", "b" }, { "c" }, { "d" }, { "x", "y" }, { "n" } }, { { "a", "b" } } ), "unaffected_group_changed:n" );

    // The detail stays bounded however many and however deep the reported pins are.
    PARTITION wide; GROUPS asserted{ {} };
    for( int index = 0; index < 400; ++index )
    {
        std::string key = "/" + KIID().AsStdString() + "/" + KIID().AsStdString() + "#" + KIID().AsStdString();
        wide.insert( { key } ); asserted[0].insert( key );
    }
    auto bounded = CHECKED_SCHEMATIC_CONTROLLER::CheckConnectivity( wide, wide, asserted );
    BOOST_REQUIRE( bounded );
    BOOST_CHECK_LE( bounded->size(), 2048 );
    BOOST_CHECK( bounded->find( " more" ) != std::string::npos );
    BOOST_CHECK( bounded->find( "first=unexpected_split:" ) != std::string::npos );
}

BOOST_AUTO_TEST_CASE( AssertedBatchesMapPostconditionFailuresAndRequireNativeProof )
{
    const std::string detail = "connectivity_postcondition_failed: expected=1 mismatches=2 first=unexpected_split:/a#b";
    {
        // Rejected with no native change: the post-condition code and bounded detail reach the caller.
        CHECKED_FIXTURE f; f.reject = true; f.rejectMessage = detail;
        auto request = f.AssertedRequest(); const auto before = f.state;
        auto result = f.Apply( request );
        BOOST_CHECK_EQUAL( result.status(), CSBS_REJECTED );
        BOOST_CHECK_EQUAL( result.error_code(), "connectivity_postcondition_failed" );
        BOOST_CHECK_EQUAL( result.error_message(), detail );
        BOOST_CHECK( !result.has_result() );
        BOOST_CHECK( MessageDifferencer::Equals( result.observed_before(), result.observed_after() ) );
        BOOST_CHECK( MessageDifferencer::Equals( before, f.state ) );
        BOOST_CHECK( MessageDifferencer::Equals( result, f.Apply( request ) ) );
        BOOST_CHECK_EQUAL( f.mutations, 1 );
    }
    {
        // A changed document proves nothing was rolled back, whatever the message says.
        CHECKED_FIXTURE f; f.reject = true; f.partialRejection = true; f.rejectMessage = detail;
        auto result = f.Apply( f.AssertedRequest() );
        BOOST_CHECK_EQUAL( result.status(), CSBS_INDETERMINATE );
        BOOST_CHECK_EQUAL( result.error_code(), "native_batch_failed_unverified" );
    }
    {
        // False positive: a batch that asserted nothing cannot fail a post-condition.
        CHECKED_FIXTURE f; f.reject = true; f.rejectMessage = detail;
        auto result = f.Apply( f.Request() );
        BOOST_CHECK_EQUAL( result.status(), CSBS_REJECTED );
        BOOST_CHECK_EQUAL( result.error_code(), "native_batch_rejected" );
    }
    {
        // Committed without the proof: the edit happened but is not a verified realization.
        CHECKED_FIXTURE f;
        auto request = f.AssertedRequest(); auto result = f.Apply( request );
        BOOST_CHECK_EQUAL( result.status(), CSBS_INDETERMINATE );
        BOOST_CHECK_EQUAL( result.error_code(), "connectivity_assertion_unverified" );
        BOOST_CHECK( result.has_result() && result.has_observed_after() );
        BOOST_CHECK( MessageDifferencer::Equals( result, f.Apply( request ) ) );
        BOOST_CHECK_EQUAL( f.mutations, 1 );
    }
    {
        CHECKED_FIXTURE f; f.verified = true;
        auto result = f.Apply( f.AssertedRequest() );
        BOOST_CHECK_EQUAL( result.status(), CSBS_COMPLETED );
        BOOST_CHECK( result.result().connectivity_assertion_verified() );
        BOOST_CHECK( result.error_code().empty() );
    }
    {
        // A proof nobody asked for is an inconsistent native reply, not success.
        CHECKED_FIXTURE f; f.verified = true;
        auto result = f.Apply( f.Request() );
        BOOST_CHECK_EQUAL( result.status(), CSBS_INDETERMINATE );
        BOOST_CHECK_EQUAL( result.error_code(), "invalid_native_result" );
    }
}

BOOST_AUTO_TEST_SUITE_END()
