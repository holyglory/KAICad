/* Revision/file checked save dispatch and retry semantics. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <api/document_lifecycle_controller.h>
#include <api/common/commands/project_commands.pb.h>
#include <google/protobuf/empty.pb.h>
#include <google/protobuf/util/message_differencer.h>
#include <kiid.h>
#include <filesystem>
#include <fstream>
#include <file_content_baseline.h>
#include <richio.h>

namespace
{
using namespace kiapi::automation::v1;
struct LIFECYCLE_FIXTURE
{
    std::string epoch = KIID().AsStdString();
    DocumentLifecycleState state;
    DOCUMENT_LIFECYCLE_CONTROLLER controller;
    unsigned reads = 0, saves = 0, closes = 0;
    bool failSave = false, throwSave = false, failAfter = false, changeIdentity = false;
    bool closed = false, failClose = false;
    std::function<void()> persist;
    LIFECYCLE_FIXTURE()
    {
        auto* document = state.mutable_document();
        document->set_type( kiapi::common::types::DOCTYPE_PCB );
        document->set_board_filename( "fixture.kicad_pcb" );
        auto directory = std::filesystem::temp_directory_path();
        document->mutable_project()->set_name( "fixture" );
        document->mutable_project()->set_path( directory.string() );
        state.set_process_epoch( epoch );
        state.set_native_identity( KIID().AsStdString() );
        state.mutable_revision()->set_epoch( KIID().AsStdString() );
        state.mutable_revision()->set_sequence( 3 );
        state.set_state_sha256( std::string( 64, 'a' ) );
        state.set_scope( DLS_PCB ); state.set_project_settings_included( true );
        state.set_native_content_dirty( true );
        for( const char* name : { "fixture.kicad_pcb", "fixture.kicad_pro" } )
        {
            const auto path = ( directory / name ).string();
            state.add_native_files( path );
            auto* file = state.add_file_baselines();
            file->set_path( path ); file->set_baseline_path( path );
            file->set_baseline_known( true ); file->set_current_known( true );
            file->set_baseline_exists( true ); file->set_current_exists( true );
            file->set_baseline_sha256( std::string( 64, 'b' ) ); file->set_current_sha256( std::string( 64, 'b' ) );
            file->set_baseline_bytes( 7 ); file->set_current_bytes( 7 );
            file->set_status( NFBS_UNCHANGED );
        }
    }
    CheckedSaveDocument Request()
    {
        controller.AnnotateCleanState( state );
        CheckedSaveDocument request;
        request.mutable_document()->CopyFrom( state.document() );
        request.set_operation_id( KIID().AsStdString() );
        request.mutable_expected_state()->CopyFrom( state );
        return request;
    }
    CheckedCloseDocument CloseRequest()
    {
        auto save = Request();
        CheckedCloseDocument close;
        close.mutable_document()->CopyFrom( save.document() ); close.set_operation_id( save.operation_id() );
        close.mutable_expected_state()->CopyFrom( save.expected_state() ); return close;
    }
    API_RESULT Dispatch( ApiRequest& request )
    {
        ApiResponse reply; reply.mutable_status()->set_status( ApiStatusCode::AS_OK );
        if( request.message().Is<ReadDocumentLifecycleState>() )
        {
            ++reads;
            if( closed )
            {
                ApiResponseStatus error; error.set_status( ApiStatusCode::AS_UNHANDLED );
                error.set_error_message( "Fixture editor is closed" ); return tl::unexpected( error );
            }
            if( saves && failAfter )
            {
                ApiResponseStatus error; error.set_status( ApiStatusCode::AS_BUSY );
                error.set_error_message( "Fixture observation unavailable" ); return tl::unexpected( error );
            }
            reply.mutable_message()->PackFrom( state );
        }
        else if( request.message().Is<kiapi::common::commands::SaveDocument>() )
        {
            ++saves;
            if( throwSave ) throw std::runtime_error( "Fixture unexpected save failure" );
            if( failSave )
            {
                ApiResponseStatus error; error.set_status( ApiStatusCode::AS_BAD_REQUEST );
                error.set_error_message( "Fixture write failure" ); return tl::unexpected( error );
            }
            if( persist ) persist();
            state.set_native_content_dirty( false );
            if( changeIdentity ) state.set_native_identity( KIID().AsStdString() );
            reply.mutable_message()->PackFrom( google::protobuf::Empty{} );
        }
        else if( request.message().Is<kiapi::common::commands::CloseDocument>() )
        {
            ++closes;
            if( failClose )
            {
                ApiResponseStatus error; error.set_status( ApiStatusCode::AS_BUSY );
                error.set_error_message( "Fixture close veto" ); return tl::unexpected( error );
            }
            closed = true;
            reply.mutable_message()->PackFrom( google::protobuf::Empty{} );
        }
        else throw std::runtime_error( "Unexpected lifecycle dispatch" );
        return reply;
    }
    template<typename T> API_RESULT Call( const T& request )
    {
        ApiRequest envelope; envelope.mutable_message()->PackFrom( request );
        return controller.Handle( envelope, epoch, [this]( ApiRequest& value ) { return Dispatch( value ); } );
    }
    template<typename T> LifecycleOperationResult Result( const T& request )
    {
        auto response = Call( request ); BOOST_REQUIRE( response );
        LifecycleOperationResult result; BOOST_REQUIRE( response->message().UnpackTo( &result ) ); return result;
    }
};
}

BOOST_AUTO_TEST_SUITE( DocumentLifecycleController )

BOOST_AUTO_TEST_CASE( SaveAndReceiptReplayAreProcessOwnedAndNeverRepeatDispatch )
{
    LIFECYCLE_FIXTURE fixture;
    auto request = fixture.Request();
    auto result = fixture.Result( request );
    BOOST_CHECK( result.status() == LOS_SAVED );
    BOOST_CHECK_EQUAL( fixture.saves, 1 );
    auto replay = fixture.Result( request );
    BOOST_CHECK( google::protobuf::util::MessageDifferencer::Equals( result, replay ) );
    BOOST_CHECK_EQUAL( fixture.reads, 2 ); BOOST_CHECK_EQUAL( fixture.saves, 1 );
    ReadLifecycleOperation query;
    query.mutable_document()->CopyFrom( request.document() );
    query.set_operation_id( request.operation_id() ); query.set_process_epoch( fixture.epoch );
    auto receipt = fixture.Call( query ); BOOST_REQUIRE( receipt );
    BOOST_REQUIRE( receipt->message().UnpackTo( &replay ) );
    BOOST_CHECK( google::protobuf::util::MessageDifferencer::Equals( result, replay ) );
    auto different = request; different.mutable_expected_state()->set_native_content_dirty( false );
    BOOST_CHECK( !fixture.Call( different ) );
    query.mutable_document()->set_board_filename( "other.kicad_pcb" ); BOOST_CHECK( !fixture.Call( query ) );
    query.mutable_document()->CopyFrom( request.document() );
    query.set_process_epoch( KIID().AsStdString() ); BOOST_CHECK( !fixture.Call( query ) );
    BOOST_CHECK_EQUAL( fixture.saves, 1 );
}

BOOST_AUTO_TEST_CASE( StaleIdentityRevisionContentAndDiskNeverSave )
{
    for( int variant = 0; variant < 5; ++variant )
    {
        LIFECYCLE_FIXTURE fixture;
        auto request = fixture.Request();
        switch( variant )
        {
        case 0: fixture.state.set_native_identity( KIID().AsStdString() ); break;
        case 1: fixture.state.mutable_revision()->set_sequence( 4 ); break;
        case 2: fixture.state.set_state_sha256( std::string( 64, 'c' ) ); break;
        case 3: fixture.state.mutable_file_baselines( 0 )->set_current_sha256( std::string( 64, 'c' ) ); break;
        case 4: request.mutable_expected_state()->set_process_epoch( KIID().AsStdString() ); break;
        }
        if( variant == 4 ) BOOST_CHECK( !fixture.Call( request ) );
        else BOOST_CHECK( fixture.Result( request ).status() == LOS_REJECTED );
        BOOST_CHECK_EQUAL( fixture.saves, 0 );
    }
}

BOOST_AUTO_TEST_CASE( FreshButIncompleteOrConflictingFileObservationsNeverSave )
{
    for( int variant = 0; variant < 5; ++variant )
    {
        LIFECYCLE_FIXTURE fixture;
        switch( variant )
        {
        case 0: fixture.state.mutable_file_baselines( 0 )->set_status( NFBS_CHANGED ); break;
        case 1: fixture.state.mutable_file_baselines( 0 )->set_baseline_known( false ); break;
        case 2: fixture.state.mutable_file_baselines( 0 )->set_current_known( false ); break;
        case 3: fixture.state.clear_file_baselines(); break;
        case 4: fixture.state.add_native_files( fixture.state.native_files( 0 ) ); break;
        }
        BOOST_CHECK( fixture.Result( fixture.Request() ).status() == LOS_REJECTED );
        BOOST_CHECK_EQUAL( fixture.saves, 0 );
    }
}

BOOST_AUTO_TEST_CASE( NativeFailureAndUncertainResultsAreRetainedUntilExplicitNewRequest )
{
    for( int variant = 0; variant < 4; ++variant )
    {
        LIFECYCLE_FIXTURE fixture;
        fixture.failSave = variant == 0; fixture.throwSave = variant == 1;
        fixture.failAfter = variant == 2; fixture.changeIdentity = variant == 3;
        auto request = fixture.Request();
        auto result = fixture.Result( request );
        BOOST_CHECK( result.status() == ( variant == 0 ? LOS_FAILED : LOS_INDETERMINATE ) );
        BOOST_CHECK_EQUAL( fixture.saves, 1 );
        fixture.failSave = fixture.throwSave = fixture.failAfter = fixture.changeIdentity = false;
        BOOST_CHECK( google::protobuf::util::MessageDifferencer::Equals( result, fixture.Result( request ) ) );
        BOOST_CHECK_EQUAL( fixture.saves, 1 );
        BOOST_CHECK( fixture.Result( fixture.Request() ).status() == LOS_SAVED );
        BOOST_CHECK_EQUAL( fixture.saves, 2 );
    }
}

BOOST_AUTO_TEST_CASE( ExternalChangeAfterObservationIsRejectedAtTheActualWriter )
{
    namespace fs = std::filesystem;
    const auto directory = fs::temp_directory_path() / ( "lifecycle-writer-" + KIID().AsStdString() );
    BOOST_REQUIRE( fs::create_directory( directory ) );
    struct CLEANUP { fs::path path; ~CLEANUP() { std::error_code error; fs::remove_all( path, error ); } } cleanup{ directory };
    LIFECYCLE_FIXTURE fixture;
    fixture.state.clear_native_files(); fixture.state.clear_file_baselines();
    const auto path = directory / "fixture.kicad_pcb";
    { std::ofstream output( path ); output << "original"; }
    const wxString nativePath = wxString::FromUTF8( path.string() );
    const auto baseline = FILE_CONTENT_BASELINE::Read( nativePath );
    fixture.state.add_native_files( path.string() );
    auto* file = fixture.state.add_file_baselines();
    file->set_path( path.string() ); file->set_baseline_path( path.string() );
    file->set_baseline_known( true ); file->set_current_known( true );
    file->set_baseline_exists( true ); file->set_current_exists( true );
    file->set_baseline_sha256( baseline.Sha256() ); file->set_current_sha256( baseline.Sha256() );
    file->set_baseline_bytes( baseline.Bytes() ); file->set_current_bytes( baseline.Bytes() );
    file->set_status( NFBS_UNCHANGED );
    fixture.persist = [&]()
    {
        { std::ofstream external( path ); external << "external"; }
        PRETTIFIED_FILE_OUTPUTFORMATTER writer( nativePath );
        writer.Print( "(would-overwrite)" );
        writer.Finish();
    };
    const auto request = fixture.Request();
    BOOST_CHECK( fixture.Result( request ).status() == LOS_INDETERMINATE );
    std::string retained;
    const auto current = FILE_CONTENT_BASELINE::Read( nativePath, &retained );
    BOOST_REQUIRE( current.Known() ); BOOST_CHECK_EQUAL( retained, "external" );
    BOOST_CHECK( fixture.Result( request ).status() == LOS_INDETERMINATE );
    BOOST_CHECK_EQUAL( fixture.saves, 1 );
}

BOOST_AUTO_TEST_CASE( LoadedCleanDocumentClosesWithoutSavingAndReceiptSurvivesClosedEditor )
{
    LIFECYCLE_FIXTURE fixture;
    fixture.state.set_native_content_dirty( false );
    fixture.controller.RememberCleanState( fixture.state );
    auto request = fixture.CloseRequest();
    auto result = fixture.Result( request );
    BOOST_CHECK( result.status() == LOS_CLOSED );
    BOOST_CHECK_EQUAL( fixture.closes, 1 ); BOOST_CHECK_EQUAL( fixture.saves, 0 );
    BOOST_CHECK( fixture.closed );
    BOOST_CHECK( google::protobuf::util::MessageDifferencer::Equals( result, fixture.Result( request ) ) );
    BOOST_CHECK_EQUAL( fixture.reads, 1 ); BOOST_CHECK_EQUAL( fixture.closes, 1 );
    ReadLifecycleOperation query;
    query.mutable_document()->CopyFrom( request.document() ); query.set_operation_id( request.operation_id() );
    query.set_process_epoch( fixture.epoch );
    auto receipt = fixture.Call( query ); BOOST_REQUIRE( receipt );
    LifecycleOperationResult replay; BOOST_REQUIRE( receipt->message().UnpackTo( &replay ) );
    BOOST_CHECK( google::protobuf::util::MessageDifferencer::Equals( result, replay ) );
    auto otherKind = fixture.Request(); otherKind.set_operation_id( request.operation_id() );
    BOOST_CHECK( !fixture.Call( otherKind ) );
}

BOOST_AUTO_TEST_CASE( CloseRefusesDirtyUncheckpointedChangedAndReplacedDocuments )
{
    for( int variant = 0; variant < 4; ++variant )
    {
        LIFECYCLE_FIXTURE fixture;
        fixture.state.set_native_content_dirty( false );
        if( variant != 1 ) fixture.controller.RememberCleanState( fixture.state );
        if( variant == 0 ) fixture.state.set_native_content_dirty( true );
        if( variant == 2 ) fixture.state.set_state_sha256( std::string( 64, 'd' ) );
        if( variant == 3 ) fixture.state.mutable_revision()->set_epoch( KIID().AsStdString() );
        BOOST_CHECK( fixture.Result( fixture.CloseRequest() ).status() == LOS_REJECTED );
        BOOST_CHECK_EQUAL( fixture.closes, 0 ); BOOST_CHECK_EQUAL( fixture.saves, 0 );
    }
}

BOOST_AUTO_TEST_CASE( SavedCheckpointAllowsCloseAndVetoDoesNotConsumeTheEditor )
{
    LIFECYCLE_FIXTURE fixture;
    BOOST_CHECK( fixture.Result( fixture.Request() ).status() == LOS_SAVED );
    fixture.failClose = true;
    const auto request = fixture.CloseRequest();
    BOOST_CHECK( fixture.Result( request ).status() == LOS_FAILED );
    BOOST_CHECK( !fixture.closed );
    fixture.failClose = false;
    BOOST_CHECK( fixture.Result( request ).status() == LOS_FAILED );
    BOOST_CHECK_EQUAL( fixture.closes, 1 );
    BOOST_CHECK( fixture.Result( fixture.CloseRequest() ).status() == LOS_CLOSED );
    BOOST_CHECK_EQUAL( fixture.saves, 1 ); BOOST_CHECK_EQUAL( fixture.closes, 2 );
}

BOOST_AUTO_TEST_SUITE_END()
