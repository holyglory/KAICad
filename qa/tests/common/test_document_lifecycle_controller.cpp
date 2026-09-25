/* Revision/file checked save dispatch and retry semantics. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <api/document_lifecycle_controller.h>
#include <api/common/commands/capability_commands.pb.h>
#include <api/common/commands/project_commands.pb.h>
#include <google/protobuf/empty.pb.h>
#include <google/protobuf/util/message_differencer.h>
#include <kiid.h>
#include <filesystem>
#include <fstream>
#include <file_content_baseline.h>
#include <richio.h>
#include <file_write_observer.h>
#include <iterator>
#include <json_common.h>
#include <kiplatform/io.h>
#include <lockfile.h>
#include <project.h>
#include <settings/settings_manager.h>
#include <wx/filename.h>
#include <wx/utils.h>

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
    // Runs as the native saver inside the checked save; returning false fails the save.
    std::function<bool()> native;
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
            if( native && !native() )
            {
                ApiResponseStatus error; error.set_status( ApiStatusCode::AS_BAD_REQUEST );
                error.set_error_message( "Fixture native saver failed" ); return tl::unexpected( error );
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

// A fake editor behind the checked view (NativeCapabilityReadCheckedView): it answers the three
// reads the controller dispatches for one request, records them in order, and can refuse any of
// them or answer with a state, image or object list of another revision or sheet.
struct CHECKED_VIEW_FIXTURE
{
    std::string epoch = KIID().AsStdString();
    kiapi::common::types::DocumentSpecifier root, sheet;
    CheckedSchematicState checkedState;
    SchematicObservation observation;
    DocumentLifecycleState after;
    std::vector<std::string> calls;
    std::map<std::string, ApiResponseStatus> refusals;
    DOCUMENT_LIFECYCLE_CONTROLLER controller;

    CHECKED_VIEW_FIXTURE()
    {
        root.set_type( kiapi::common::types::DOCTYPE_SCHEMATIC );
        root.mutable_project()->set_name( "fixture" );
        root.mutable_project()->set_path( std::filesystem::temp_directory_path().string() );
        root.mutable_sheet_path()->add_path()->set_value( KIID().AsStdString() );
        sheet = root;
        sheet.mutable_sheet_path()->add_path()->set_value( KIID().AsStdString() );
        auto* state = checkedState.mutable_state();
        state->mutable_document()->CopyFrom( root );
        state->set_process_epoch( epoch );
        state->set_native_identity( KIID().AsStdString() );
        state->mutable_revision()->set_epoch( KIID().AsStdString() );
        state->mutable_revision()->set_sequence( 12 );
        state->set_state_sha256( std::string( 64, 'c' ) );
        state->set_scope( DLS_SCHEMATIC_HIERARCHY );
        checkedState.mutable_electrical()->mutable_hierarchy()->mutable_revision()->CopyFrom( state->revision() );
        checkedState.mutable_electrical()->mutable_hierarchy()->mutable_data()->mutable_document()->CopyFrom( root );
        after = *state;
        observation.mutable_snapshot()->mutable_revision()->CopyFrom( state->revision() );
        observation.mutable_snapshot()->mutable_data()->mutable_metadata()->mutable_document()->CopyFrom( sheet );
        observation.mutable_preview()->mutable_revision()->CopyFrom( state->revision() );
        observation.mutable_preview()->mutable_document()->CopyFrom( sheet );
        observation.mutable_preview()->set_width_pixels( 2 );
        observation.mutable_preview()->set_height_pixels( 1 );
        observation.mutable_preview()->set_png( std::string( "\x89PNG\r\n\x1a\n", 8 ) );
    }

    NativeCapabilityReadCheckedView Request() const
    {
        NativeCapabilityReadCheckedView request;
        request.mutable_document()->CopyFrom( root );
        request.set_process_epoch( epoch );
        request.mutable_view()->CopyFrom( sheet );
        return request;
    }

    API_RESULT Dispatch( ApiRequest& aRequest )
    {
        std::string type;
        BOOST_REQUIRE( google::protobuf::Any::ParseAnyTypeUrl( aRequest.message().type_url(), &type ) );
        calls.push_back( type );
        // Every read carries the caller's header, so KiCad checks the same instance token.
        BOOST_CHECK_EQUAL( aRequest.header().kicad_token(), "fixture-token" );
        if( auto refusal = refusals.find( type ); refusal != refusals.end() )
            return tl::unexpected( refusal->second );
        ApiResponse reply;
        reply.mutable_status()->set_status( ApiStatusCode::AS_OK );
        if( aRequest.message().Is<ReadCheckedSchematicState>() )
        {
            ReadCheckedSchematicState query;
            BOOST_REQUIRE( aRequest.message().UnpackTo( &query ) );
            BOOST_CHECK( google::protobuf::util::MessageDifferencer::Equals( query.document(), root ) );
            BOOST_CHECK_EQUAL( query.process_epoch(), epoch );
            reply.mutable_message()->PackFrom( checkedState );
        }
        else if( aRequest.message().Is<CaptureSchematicObservation>() )
        {
            CaptureSchematicObservation capture;
            BOOST_REQUIRE( aRequest.message().UnpackTo( &capture ) );
            BOOST_CHECK( google::protobuf::util::MessageDifferencer::Equals( capture.document(), sheet ) );
            BOOST_CHECK_EQUAL( capture.schema_version(), 9u );
            reply.mutable_message()->PackFrom( observation );
        }
        else if( aRequest.message().Is<ReadDocumentLifecycleState>() )
        {
            ReadDocumentLifecycleState read;
            BOOST_REQUIRE( aRequest.message().UnpackTo( &read ) );
            BOOST_CHECK( google::protobuf::util::MessageDifferencer::Equals( read.document(), root ) );
            reply.mutable_message()->PackFrom( after );
        }
        else throw std::runtime_error( "Unexpected checked view dispatch " + type );
        return reply;
    }

    API_RESULT Call( const google::protobuf::Message& aRequest )
    {
        ApiRequest envelope;
        envelope.mutable_message()->PackFrom( aRequest );
        return Call( envelope );
    }

    API_RESULT Call( ApiRequest& aEnvelope )
    {
        aEnvelope.mutable_header()->set_kicad_token( "fixture-token" );
        return controller.Handle( aEnvelope, epoch, [this]( ApiRequest& value ) { return Dispatch( value ); } );
    }
};

// The reads one checked view dispatches, in order.
std::vector<std::string> CheckedViewReads()
{
    return { std::string( ReadCheckedSchematicState::descriptor()->full_name() ),
             std::string( CaptureSchematicObservation::descriptor()->full_name() ),
             std::string( ReadDocumentLifecycleState::descriptor()->full_name() ) };
}

using SAVE_PROBLEM = DOCUMENT_LIFECYCLE_CONTROLLER::SAVE_PROBLEM;

// Real files for the fixture's observed board and project, so writers can replace them while the
// checked save watches every write.
struct DISK_DOCUMENT
{
    std::filesystem::path directory =
            std::filesystem::temp_directory_path() / ( "lifecycle-save-" + KIID().AsStdString() );
    std::string pcb, project;
    explicit DISK_DOCUMENT( LIFECYCLE_FIXTURE& aFixture )
    {
        std::filesystem::create_directory( directory );
        aFixture.state.clear_native_files(); aFixture.state.clear_file_baselines();
        pcb = Add( aFixture, "fixture.kicad_pcb" );
        project = Add( aFixture, "fixture.kicad_pro" );
    }
    ~DISK_DOCUMENT()
    {
        std::error_code error;
        std::filesystem::permissions( directory, std::filesystem::perms::owner_all,
                                      std::filesystem::perm_options::add, error );
        std::filesystem::remove_all( directory, error );
    }
    std::string Add( LIFECYCLE_FIXTURE& aFixture, const char* aName )
    {
        const std::string path = ( directory / aName ).string();
        { std::ofstream output( path ); output << "original " << aName; }
        const auto baseline = FILE_CONTENT_BASELINE::Read( wxString::FromUTF8( path ) );
        aFixture.state.add_native_files( path );
        auto* file = aFixture.state.add_file_baselines();
        file->set_path( path ); file->set_baseline_path( path );
        file->set_baseline_known( true ); file->set_current_known( true );
        file->set_baseline_exists( true ); file->set_current_exists( true );
        file->set_baseline_sha256( baseline.Sha256() ); file->set_current_sha256( baseline.Sha256() );
        file->set_baseline_bytes( baseline.Bytes() ); file->set_current_bytes( baseline.Bytes() );
        file->set_status( NFBS_UNCHANGED );
        return path;
    }
    static std::string Contents( const std::string& aPath )
    {
        std::ifstream input( aPath );
        return std::string( std::istreambuf_iterator<char>( input ), std::istreambuf_iterator<char>() );
    }
};

// Replaces a file the way KiCad's savers do, and reports a writer error the way
// SCH_API_SAVE::SaveSheetToFile does.
bool SaveLikeKiCad( const std::string& aPath )
{
    try
    {
        PRETTIFIED_FILE_OUTPUTFORMATTER writer( wxString::FromUTF8( aPath ) );
        writer.Print( "(saved)" );
        writer.Finish();
        return true;
    }
    catch( const IO_ERROR& error )
    {
        DOCUMENT_LIFECYCLE_CONTROLLER::ReportSaveProblem( SAVE_PROBLEM::WRITE_BLOCKED, wxString::FromUTF8( aPath ),
                                                          error.Problem() );
        return false;
    }
}

bool CanCreateIn( const std::filesystem::path& aDirectory )
{
    const auto probe = aDirectory / ( "probe-" + KIID().AsStdString() );
    {
        std::ofstream output( probe );
        if( !output ) return false;
    }
    std::error_code error;
    std::filesystem::remove( probe, error );
    return true;
}

bool Contains( const std::string& aText, const std::string& aPart )
{
    return aText.find( aPart ) != std::string::npos;
}

std::vector<std::string> List( const google::protobuf::RepeatedPtrField<std::string>& aField )
{
    return std::vector<std::string>( aField.begin(), aField.end() );
}
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

BOOST_AUTO_TEST_CASE( OnlyWriteBlockedProblemsMarkFilesNotWritable )
{
    for( int variant = 0; variant < 5; ++variant )
    {
        LIFECYCLE_FIXTURE fixture;
        const std::string pcb = fixture.state.native_files( 0 );
        const std::string project = fixture.state.native_files( 1 );
        fixture.native = [&]()
        {
            if( variant == 4 )
                DOCUMENT_LIFECYCLE_CONTROLLER::ReportSaveProblem( SAVE_PROBLEM::WRITE_FAILED, wxString::FromUTF8( project ),
                        wxS( "writing the project settings failed, and the settings writer gave no system reason" ) );
            if( variant == 0 || variant == 3 )
                DOCUMENT_LIFECYCLE_CONTROLLER::ReportSaveProblem( SAVE_PROBLEM::WRITE_BLOCKED, wxString::FromUTF8( pcb ),
                                                                  wxS( "the file is read-only" ) );
            if( variant == 1 || variant == 3 )
                DOCUMENT_LIFECYCLE_CONTROLLER::ReportSaveProblem( SAVE_PROBLEM::SAVE_REFUSED, wxString::FromUTF8( pcb ),
                        wxS( "one shared sheet file has conflicting root page numbers" ) );
            if( variant == 2 )
                DOCUMENT_LIFECYCLE_CONTROLLER::ReportSaveProblem( SAVE_PROBLEM::SAVE_REFUSED, wxEmptyString,
                                                                  wxS( "the root schematic has no file name" ) );
            return false;
        };
        const auto request = fixture.Request();
        const auto result = fixture.Result( request );
        const std::string& message = result.error_message();
        BOOST_CHECK( result.status() == LOS_FAILED );
        BOOST_CHECK( result.written_files().empty() );
        BOOST_CHECK( Contains( message, "KiCad replaced none of the document's files" ) );
        if( variant == 0 || variant == 3 )
        {
            BOOST_CHECK_EQUAL( result.error_code(), "file_not_writable" );
            BOOST_CHECK( List( result.blocked_files() ) == std::vector<std::string>{ pcb } );
            BOOST_CHECK( Contains( message, "the file is read-only" ) );
        }
        else if( variant == 4 )
        {
            // A failure nobody could explain is neither a blocked file nor a refusal.
            BOOST_CHECK_EQUAL( result.error_code(), "native_save_failed" );
            BOOST_CHECK( result.blocked_files().empty() );
            BOOST_CHECK( Contains( message, "KiCad could not write 'fixture.kicad_pro'" ) );
            BOOST_CHECK( Contains( message, "gave no system reason" ) );
            BOOST_CHECK( Contains( message, "KiCad found nothing that blocks the named files now" ) );
            BOOST_CHECK( !Contains( message, "make the file or folder writable" ) );
            BOOST_CHECK( !Contains( message, "KiCad refused to save" ) );
        }
        else
        {
            // A refusal is not a write problem: no blocked file, and no advice to make files writable.
            BOOST_CHECK_EQUAL( result.error_code(), "native_save_refused" );
            BOOST_CHECK( result.blocked_files().empty() );
            BOOST_CHECK( Contains( message, "making the document's files writable does not help" ) );
            BOOST_CHECK( !Contains( message, "make the file or folder writable" ) );
        }
        if( variant == 1 || variant == 3 )
            BOOST_CHECK( Contains( message, "conflicting root page numbers" ) );
        if( variant == 2 )
            BOOST_CHECK( Contains( message, "the root schematic has no file name" ) );
        // Reports belong to the one save that made them; the retained result never changes.
        BOOST_CHECK( google::protobuf::util::MessageDifferencer::Equals( result, fixture.Result( request ) ) );
        BOOST_CHECK_EQUAL( fixture.saves, 1 );
    }
    // Outside a checked save a report goes nowhere, so it cannot fail a later save.
    DOCUMENT_LIFECYCLE_CONTROLLER::ReportSaveProblem( SAVE_PROBLEM::WRITE_BLOCKED, wxS( "/nowhere" ), wxS( "ignored" ) );
    LIFECYCLE_FIXTURE clean;
    BOOST_CHECK( clean.Result( clean.Request() ).status() == LOS_SAVED );
}

BOOST_AUTO_TEST_CASE( UnexplainedNativeFailureChecksTheFilesOrStaysGeneric )
{
    namespace fs = std::filesystem;
    LIFECYCLE_FIXTURE fixture;
    DISK_DOCUMENT disk( fixture );
    fixture.failSave = true;
    auto result = fixture.Result( fixture.Request() );
    BOOST_CHECK( result.status() == LOS_FAILED );
    BOOST_CHECK_EQUAL( result.error_code(), "native_save_failed" );
    BOOST_CHECK( result.blocked_files().empty() );
    BOOST_CHECK( Contains( result.error_message(), "KiCad reported: Fixture write failure" ) );
    fs::permissions( disk.pcb, fs::perms::owner_write | fs::perms::group_write | fs::perms::others_write,
                     fs::perm_options::remove );
    // Like the native journeys, these checks need an account that file modes really restrict.
    BOOST_REQUIRE_MESSAGE( !wxFileName::IsFileWritable( wxString::FromUTF8( disk.pcb ) ),
                           "This account can write read-only files, so the check cannot be staged" );
    result = fixture.Result( fixture.Request() );
    BOOST_CHECK_EQUAL( result.error_code(), "file_not_writable" );
    BOOST_CHECK( List( result.blocked_files() ) == std::vector<std::string>{ disk.pcb } );
    BOOST_CHECK( Contains( result.error_message(), "the file is read-only" ) );
    BOOST_CHECK( Contains( result.error_message(), "KiCad reported: Fixture write failure" ) );
}

BOOST_AUTO_TEST_CASE( ControllerRefusalsKeepTheirOwnCodeAndNeverBlockFiles )
{
    namespace fs = std::filesystem;
    const char* codes[] = { "file_changed_during_save", "save_outside_document", "file_unreadable_during_save" };

    for( int variant = 0; variant < 3; ++variant )
    {
        LIFECYCLE_FIXTURE fixture;
        DISK_DOCUMENT disk( fixture );
        const std::string outside = ( disk.directory / "other.kicad_sch" ).string();
        bool staged = true;
        fixture.native = [&]()
        {
            if( variant == 1 )
                return SaveLikeKiCad( outside );

            if( variant == 2 )
            {
                // Another program leaves the board writable but unreadable once the save began.
                fs::permissions( disk.pcb, fs::perms::owner_write, fs::perm_options::replace );
                staged = !std::ifstream( disk.pcb ).is_open();
                return SaveLikeKiCad( disk.pcb );
            }

            { std::ofstream external( disk.pcb ); external << "external"; }
            return SaveLikeKiCad( disk.pcb );
        };
        const auto result = fixture.Result( fixture.Request() );
        BOOST_REQUIRE_MESSAGE( staged, "This account can read unreadable files, so the case cannot be staged" );
        const std::string& message = result.error_message();
        BOOST_CHECK( result.status() == LOS_FAILED );
        BOOST_CHECK_EQUAL( result.error_code(), codes[variant] );
        // The writer reported the refused write as its own failure; that never marks a file blocked.
        BOOST_CHECK( result.blocked_files().empty() );
        BOOST_CHECK( result.written_files().empty() );
        BOOST_CHECK( !Contains( message, "make the file or folder writable" ) );
        if( variant == 0 )
        {
            BOOST_CHECK( Contains( message, "another program changed it" ) );
            BOOST_CHECK_EQUAL( DISK_DOCUMENT::Contents( disk.pcb ), "external" );
        }
        else if( variant == 1 )
        {
            BOOST_CHECK( Contains( message, "not one of the observed document's files" ) );
            BOOST_CHECK( !std::filesystem::exists( outside ) );
        }
        else
        {
            BOOST_CHECK( Contains( message, "could not read it to confirm" ) );
            BOOST_CHECK( Contains( message, "Make the file readable again" ) );
            fs::permissions( disk.pcb, fs::perms::owner_read | fs::perms::owner_write, fs::perm_options::replace );
            BOOST_CHECK_EQUAL( DISK_DOCUMENT::Contents( disk.pcb ), "original fixture.kicad_pcb" );
        }
    }
}

BOOST_AUTO_TEST_CASE( PartialSaveNamesWhatReachedTheDisk )
{
    namespace fs = std::filesystem;
    for( int variant = 0; variant < 4; ++variant )
    {
        LIFECYCLE_FIXTURE fixture;
        DISK_DOCUMENT disk( fixture );
        bool staged = true;
        fixture.native = [&]()
        {
            if( variant == 3 )
            {
                // The writer replaced the board and then failed, for example flushing its folder,
                // so it never confirmed the write.
                FILE_WRITE_OBSERVER::BeforeWrite( wxString::FromUTF8( disk.pcb ) );
                { std::ofstream replaced( disk.pcb ); replaced << "(saved)"; }
                DOCUMENT_LIFECYCLE_CONTROLLER::ReportSaveProblem( SAVE_PROBLEM::WRITE_BLOCKED,
                        wxString::FromUTF8( disk.pcb ), wxS( "Cannot flush directory to disk" ) );
                return false;
            }

            if( !SaveLikeKiCad( disk.pcb ) )
                return false;

            if( variant == 2 )
            {
                // The settings writer failed without a reason, and the file is still writable.
                DOCUMENT_LIFECYCLE_CONTROLLER::ReportSaveProblem( SAVE_PROBLEM::WRITE_FAILED,
                        wxString::FromUTF8( disk.project ),
                        wxS( "writing the project settings failed after the sheets were written, and the settings "
                             "writer gave no system reason" ) );
                return false;
            }

            if( variant == 0 )
            {
                // The folder stops accepting new files once the board is written, so the project
                // writer cannot create its temporary file: a real system error.
                fs::permissions( disk.directory, fs::perms::owner_write | fs::perms::group_write
                                 | fs::perms::others_write, fs::perm_options::remove );
                staged = !CanCreateIn( disk.directory );
            }
            else
            {
                std::ofstream external( disk.project );
                external << "external";
            }

            return SaveLikeKiCad( disk.project );
        };
        const auto request = fixture.Request();
        const auto result = fixture.Result( request );
        BOOST_REQUIRE_MESSAGE( staged, "This account can create files in a read-only folder, so the case cannot be staged" );
        const std::string& message = result.error_message();
        BOOST_CHECK( result.status() == LOS_FAILED );
        BOOST_CHECK_EQUAL( result.error_code(), "partial_save" );
        BOOST_CHECK( List( result.written_files() ) == std::vector<std::string>{ disk.pcb } );
        // Only a file the file system blocks is listed; a failure without a found reason, and a
        // file that was replaced before the failure, never are.
        BOOST_CHECK( List( result.blocked_files() )
                     == ( variant == 0 ? std::vector<std::string>{ disk.project } : std::vector<std::string>{} ) );
        BOOST_CHECK( Contains( message, "Already written: fixture.kicad_pcb" ) );
        BOOST_CHECK( Contains( message, "Not written: fixture.kicad_pro" ) );
        const char* causes[] = { "KiCad cannot write 'fixture.kicad_pro'", "another program changed it",
                                 "KiCad could not write 'fixture.kicad_pro'",
                                 "After replacing it, KiCad reported a failure for 'fixture.kicad_pcb'" };
        BOOST_CHECK( Contains( message, causes[variant] ) );
        BOOST_CHECK( DISK_DOCUMENT::Contents( disk.pcb ) != "original fixture.kicad_pcb" );
        BOOST_CHECK_EQUAL( DISK_DOCUMENT::Contents( disk.project ),
                           variant == 1 ? "external" : "original fixture.kicad_pro" );
        BOOST_CHECK( google::protobuf::util::MessageDifferencer::Equals( result, fixture.Result( request ) ) );
        BOOST_CHECK_EQUAL( fixture.saves, 1 );
    }
}

BOOST_AUTO_TEST_CASE( UnknownOperationRefusalStartsWithTheFixedMarker )
{
    LIFECYCLE_FIXTURE fixture;
    ReadLifecycleOperation query;
    query.mutable_document()->CopyFrom( fixture.state.document() );
    query.set_operation_id( KIID().AsStdString() ); query.set_process_epoch( fixture.epoch );
    const auto reply = fixture.Call( query );
    BOOST_REQUIRE( !reply );
    BOOST_CHECK( reply.error().status() == ApiStatusCode::AS_BAD_REQUEST );
    BOOST_CHECK_EQUAL( reply.error().error_message().rfind(
                               std::string( DOCUMENT_LIFECYCLE_CONTROLLER::UNKNOWN_OPERATION_MARKER ) + ":", 0 ), 0u );
    BOOST_CHECK_EQUAL( fixture.reads + fixture.saves + fixture.closes, 0u );
}

// KiCad opens a project read-only when it cannot take the project lock (SETTINGS_MANAGER::
// LoadProject), and the manager then keeps a lock object of its own for it
// (KICAD_MANAGER_FRAME::ProjectChanged). The real-editor journeys prove the reasons a checked save
// gives for another user's lock and for a read-only lock file; this proves every lock state with
// real locks, including the ones a journey cannot stage reliably: another program holding the
// lock, that program letting go, and KiCad's own lock object holding the lock file itself. Each
// reason names what stands in the way now, never guesses, and says to reopen the project.
BOOST_AUTO_TEST_CASE( LockedProjectReasonNamesWhatHoldsTheLockNow )
{
    namespace fs = std::filesystem;
    struct FOLDER
    {
        fs::path path = fs::temp_directory_path() / ( "lifecycle-lock-" + KIID().AsStdString() );
        FOLDER() { fs::create_directory( path ); }
        ~FOLDER() { std::error_code error; fs::remove_all( path, error ); }
    } folder;

    // A loadable project whose lock file records aOwner.
    auto seed = [&]( const std::string& aName, const nlohmann::json& aOwner )
    {
        const fs::path pro = folder.path / ( aName + ".kicad_pro" );
        {
            std::ofstream out( pro.string() );
            out << R"({"meta": {"filename": ")" << aName << R"(.kicad_pro", "version": 3}})";
        }
        std::ofstream lock( LOCKFILE::LockPathFor( wxString( pro.string() ) ).ToStdString() );
        lock << aOwner.dump();
        return wxString( pro.string() );
    };
    auto owner = []( const wxString& aUser, const wxString& aHost )
    {
        nlohmann::json record;
        record["username"] = std::string( aUser.mb_str() );
        record["hostname"] = std::string( aHost.mb_str() );
        record["token"] = "0123456789abcdef0123456789abcdef";
        return record;
    };
    auto text = []( const wxString& aText ) { return aText.ToStdString( wxConvUTF8 ); };
    // Loads the project the way KiCad does; it opens read-only and without a lock of its own.
    auto load = []( SETTINGS_MANAGER& aManager, const wxString& aPath ) -> PROJECT&
    {
        BOOST_REQUIRE( aManager.LoadProject( aPath ) );
        PROJECT* project = aManager.GetProject( aPath );
        BOOST_REQUIRE( project );
        BOOST_REQUIRE( project->IsReadOnly() );
        BOOST_REQUIRE( project->GetProjectLock() == nullptr );
        return *project;
    };
    auto reason = [&]( const PROJECT& aProject )
    {
        return text( DOCUMENT_LIFECYCLE_CONTROLLER::ReadOnlyProjectReason( aProject ) );
    };
    const std::string me = "names user '" + text( wxGetUserId() ) + "' on computer '" + text( wxGetHostName() ) + "'";
    const auto        self = owner( wxGetUserId(), wxGetHostName() );

    // 1. Another open lock holds it (a second open file description, as another process has).
    {
        const wxString    path = seed( "held", self );
        const std::string lockPath = text( LOCKFILE::LockPathFor( path ) );
        {
            KIPLATFORM::IO::FILE_LOCK holder;
            bool created = false;
            BOOST_REQUIRE( holder.Acquire( LOCKFILE::LockPathFor( path ), created )
                           == KIPLATFORM::IO::FILE_LOCK::STATE::HELD );
            SETTINGS_MANAGER manager;
            PROJECT&         project = load( manager, path );
            const std::string held = reason( project );
            BOOST_CHECK( Contains( held, "another program holds its project lock '" + lockPath + "'" ) );
            BOOST_CHECK( Contains( held, me ) );
            BOOST_CHECK( Contains( held, "close the project there, then reopen it in KiCad" ) );
            BOOST_CHECK( !Contains( held, "nothing holds the lock now" ) );

            // 2. The manager keeps a lock object of its own, as KICAD_MANAGER_FRAME::ProjectChanged
            // does, while the other program still holds the lock: that object holds nothing, but
            // KiCad never calls the holder another program once it keeps an object. It names the
            // record and says to close the project in the other KiCad.
            project.SetProjectLock( new LOCKFILE( path ) );
            BOOST_REQUIRE( !project.GetProjectLock()->Valid() );
            const std::string kept = reason( project );
            BOOST_CHECK( Contains( kept, "could not take the project lock '" + lockPath + "'" ) );
            BOOST_CHECK( Contains( kept, me ) );
            BOOST_CHECK( Contains( kept, "close the project in any other KiCad that has it open, then reopen it in KiCad" ) );
            BOOST_CHECK( !Contains( kept, "another program holds" ) );
            project.SetProjectLock( nullptr );

            // 3. The holder lets go: the same read-only project now says nothing holds the lock,
            // without guessing why KiCad could not take it when it opened the project.
            holder.Release();
            const std::string released = reason( project );
            BOOST_CHECK( Contains( released, "could not take the project lock '" + lockPath + "'" ) );
            BOOST_CHECK( Contains( released, "nothing holds the lock now, so reopen the project in KiCad" ) );
            BOOST_CHECK( !Contains( released, "another program holds" ) );
            BOOST_CHECK( !Contains( released, "for example" ) );
        }

        // Reopening the project, as the reason advises, really takes the lock.
        SETTINGS_MANAGER reopened;
        BOOST_REQUIRE( reopened.LoadProject( path ) );
        BOOST_CHECK( !reopened.GetProject( path )->IsReadOnly() );
        BOOST_CHECK( DOCUMENT_LIFECYCLE_CONTROLLER::ReadOnlyProjectReason( *reopened.GetProject( path ) ).empty() );
    }

    // 4. Another user's record with nobody holding it: KiCad never takes it over.
    {
        const wxString    path = seed( "foreign", owner( wxS( "someone-else" ), wxS( "another-host" ) ) );
        const std::string lockPath = text( LOCKFILE::LockPathFor( path ) );
        SETTINGS_MANAGER  manager;
        PROJECT&          project = load( manager, path );

        for( bool kept : { false, true } )
        {
            // 5. The manager keeps its own lock object for that record (ProjectChanged). The
            // object holds the lock file's system lock itself, so an inspection of the lock sees it
            // held; the reason still names the other user, never another program.
            if( kept )
            {
                project.SetProjectLock( new LOCKFILE( path ) );
                BOOST_REQUIRE( !project.GetProjectLock()->Valid() );
                BOOST_REQUIRE_MESSAGE( !LOCKFILE::Inspect( path ).Valid(),
                                       "KiCad's own lock object must hold the lock file for this case" );
            }

            const std::string foreign = reason( project );
            BOOST_TEST_CONTEXT( ( kept ? "with" : "without" ) << " KiCad's own lock object" )
            {
                BOOST_CHECK( Contains( foreign, "project lock '" + lockPath + "' belongs to user 'someone-else' on "
                                                "computer 'another-host'" ) );
                BOOST_CHECK( Contains( foreign, "never takes over another user's lock" ) );
                BOOST_CHECK( Contains( foreign, "delete the lock file if nobody has the project open, then reopen "
                                                "it in KiCad" ) );
                BOOST_CHECK( !Contains( foreign, "another program holds" ) );
                BOOST_CHECK( !Contains( foreign, "nothing holds the lock now" ) );
            }
        }
    }

    // 6. The lock file is read-only, so KiCad cannot take even its own abandoned lock, with and
    // without the manager's own lock object.
    {
        const wxString path = seed( "unwritable", self );
        const fs::path lock( text( LOCKFILE::LockPathFor( path ) ) );
        fs::permissions( lock, fs::perms::owner_write | fs::perms::group_write | fs::perms::others_write,
                         fs::perm_options::remove );
        // Like the native journeys, this needs an account that file modes really restrict.
        BOOST_REQUIRE_MESSAGE( !wxFileName::IsFileWritable( LOCKFILE::LockPathFor( path ) ),
                               "This account can write read-only files, so the case cannot be staged" );
        SETTINGS_MANAGER manager;
        PROJECT&         project = load( manager, path );

        for( bool kept : { false, true } )
        {
            if( kept )
            {
                project.SetProjectLock( new LOCKFILE( path ) );
                BOOST_REQUIRE( !project.GetProjectLock()->Valid() );
            }

            const std::string unwritable = reason( project );
            BOOST_TEST_CONTEXT( ( kept ? "with" : "without" ) << " KiCad's own lock object" )
            {
                BOOST_CHECK( Contains( unwritable, "cannot write its project lock file '" + lock.string()
                                                           + "' (the file is read-only)" ) );
                BOOST_CHECK( Contains( unwritable, "make the lock file writable or delete it, then reopen the project "
                                                   "in KiCad" ) );
                BOOST_CHECK( !Contains( unwritable, "another program holds" ) );
                BOOST_CHECK( !Contains( unwritable, "nothing holds the lock now" ) );
            }
        }
    }
}

// The checked view is one request: the checked state, the displayed sheet's capture and a closing
// state read, in that order, with the caller's header, and the reply pairs exactly what KiCad
// returned.  The request type is advertised and claimed like the controller's other requests.
BOOST_AUTO_TEST_CASE( CheckedViewPairsTheImageWithTheStateItWasCapturedAt )
{
    CHECKED_VIEW_FIXTURE fixture;
    const auto& types = DOCUMENT_LIFECYCLE_CONTROLLER::RequestTypes();
    BOOST_CHECK( std::binary_search( types.begin(), types.end(),
                                     std::string( NativeCapabilityReadCheckedView::descriptor()->full_name() ) ) );
    ApiRequest envelope;
    envelope.mutable_message()->PackFrom( fixture.Request() );
    BOOST_CHECK( DOCUMENT_LIFECYCLE_CONTROLLER::Handles( envelope ) );

    for( bool viewRoot : { false, true } )
    {
        CHECKED_VIEW_FIXTURE current;
        auto request = current.Request();
        if( viewRoot )
        {
            current.sheet = current.root;
            current.observation.mutable_preview()->mutable_document()->CopyFrom( current.root );
            current.observation.mutable_snapshot()->mutable_data()->mutable_metadata()->mutable_document()->CopyFrom( current.root );
            request.mutable_view()->CopyFrom( current.root );
        }
        auto reply = current.Call( request );
        BOOST_REQUIRE( reply );
        BOOST_CHECK( reply->status().status() == ApiStatusCode::AS_OK );
        NativeCapabilityCheckedView view;
        BOOST_REQUIRE( reply->message().UnpackTo( &view ) );
        BOOST_CHECK( google::protobuf::util::MessageDifferencer::Equals( view.checked(), current.checkedState ) );
        BOOST_CHECK( google::protobuf::util::MessageDifferencer::Equals( view.view(), current.observation ) );
        BOOST_CHECK( current.calls == CheckedViewReads() );
    }
}

// Anything that differs after the capture refuses the pair with AS_NOT_READY, so the caller
// observes again instead of receiving an image of one revision with the state of another.
BOOST_AUTO_TEST_CASE( CheckedViewRefusesAChangeDuringTheCapture )
{
    const std::vector<std::pair<std::string, std::function<void( CHECKED_VIEW_FIXTURE& )>>> changes = {
        { "a later revision after rendering", []( CHECKED_VIEW_FIXTURE& f ) { f.after.mutable_revision()->set_sequence( 13 ); } },
        { "other content after rendering", []( CHECKED_VIEW_FIXTURE& f ) { f.after.set_state_sha256( std::string( 64, 'd' ) ); } },
        { "unsaved work after rendering", []( CHECKED_VIEW_FIXTURE& f ) { f.after.set_native_content_dirty( true ); } },
        { "an image of another revision", []( CHECKED_VIEW_FIXTURE& f )
          { f.observation.mutable_preview()->mutable_revision()->set_sequence( 11 ); } },
        { "an image of another journal epoch", []( CHECKED_VIEW_FIXTURE& f )
          { f.observation.mutable_preview()->mutable_revision()->set_epoch( KIID().AsStdString() ); } },
        { "objects of another revision", []( CHECKED_VIEW_FIXTURE& f )
          { f.observation.mutable_snapshot()->mutable_revision()->set_sequence( 13 ); } },
        { "an image of another sheet", []( CHECKED_VIEW_FIXTURE& f )
          { f.observation.mutable_preview()->mutable_document()->mutable_sheet_path()->mutable_path( 1 )->set_value( KIID().AsStdString() ); } },
        { "objects of another sheet", []( CHECKED_VIEW_FIXTURE& f )
          { f.observation.mutable_snapshot()->mutable_data()->mutable_metadata()->mutable_document()->CopyFrom( f.root ); } }
    };
    for( const auto& [name, change] : changes )
    {
        BOOST_TEST_CONTEXT( name )
        {
            CHECKED_VIEW_FIXTURE fixture;
            change( fixture );
            auto reply = fixture.Call( fixture.Request() );
            BOOST_REQUIRE( !reply );
            BOOST_CHECK( reply.error().status() == ApiStatusCode::AS_NOT_READY );
            BOOST_CHECK( Contains( reply.error().error_message(), "observe again" ) );
            BOOST_CHECK( fixture.calls == CheckedViewReads() );
        }
    }
}

// A read KiCad refuses (a busy editor, a pending edit, a sheet it does not display) ends the view
// with that exact refusal and dispatches nothing after it.
BOOST_AUTO_TEST_CASE( CheckedViewPassesARefusedReadThroughUnchanged )
{
    const std::vector<std::string> order = CheckedViewReads();
    for( size_t refused = 0; refused < order.size(); ++refused )
    {
        for( ApiStatusCode code : { ApiStatusCode::AS_BUSY, ApiStatusCode::AS_NOT_READY, ApiStatusCode::AS_BAD_REQUEST } )
        {
            BOOST_TEST_CONTEXT( order[refused] << " refused with " << static_cast<int>( code ) )
            {
                CHECKED_VIEW_FIXTURE fixture;
                ApiResponseStatus refusal;
                refusal.set_status( code );
                refusal.set_error_message( "Fixture refusal of " + order[refused] );
                fixture.refusals[order[refused]] = refusal;
                auto reply = fixture.Call( fixture.Request() );
                BOOST_REQUIRE( !reply );
                BOOST_CHECK( google::protobuf::util::MessageDifferencer::Equals( reply.error(), refusal ) );
                BOOST_CHECK( fixture.calls == std::vector<std::string>( order.begin(), order.begin() + refused + 1 ) );
            }
        }
    }
}

// A view that is not of a sheet of the named schematic root in the same project, of another process,
// or that cannot be decoded is refused as a bad request before KiCad reads anything.
BOOST_AUTO_TEST_CASE( CheckedViewRefusesAnotherRootProjectOrAnUndecodableRequest )
{
    const std::vector<std::pair<std::string, std::function<void( NativeCapabilityReadCheckedView& )>>> changes = {
        { "a sheet of another root", []( NativeCapabilityReadCheckedView& r )
          { r.mutable_view()->mutable_sheet_path()->mutable_path( 0 )->set_value( KIID().AsStdString() ); } },
        { "a sheet of another project", []( NativeCapabilityReadCheckedView& r )
          { r.mutable_view()->mutable_project()->set_name( "other" ); } },
        { "a sheet in another project directory", []( NativeCapabilityReadCheckedView& r )
          { r.mutable_view()->mutable_project()->set_path( "/other" ); } },
        { "no sheet to view", []( NativeCapabilityReadCheckedView& r ) { r.clear_view(); } },
        { "a board to view", []( NativeCapabilityReadCheckedView& r )
          { r.mutable_view()->set_type( kiapi::common::types::DOCTYPE_PCB ); } },
        { "a sheet below the root named as the root", []( NativeCapabilityReadCheckedView& r )
          { r.mutable_document()->CopyFrom( r.view() ); } },
        { "a root that is no UUID", []( NativeCapabilityReadCheckedView& r )
          {
              r.mutable_document()->mutable_sheet_path()->mutable_path( 0 )->set_value( "root" );
              r.mutable_view()->mutable_sheet_path()->mutable_path( 0 )->set_value( "root" );
          } },
        { "another process", []( NativeCapabilityReadCheckedView& r ) { r.set_process_epoch( KIID().AsStdString() ); } }
    };
    for( const auto& [name, change] : changes )
    {
        BOOST_TEST_CONTEXT( name )
        {
            CHECKED_VIEW_FIXTURE fixture;
            auto request = fixture.Request();
            change( request );
            auto reply = fixture.Call( request );
            BOOST_REQUIRE( !reply );
            BOOST_CHECK( reply.error().status() == ApiStatusCode::AS_BAD_REQUEST );
            BOOST_CHECK( fixture.calls.empty() );
        }
    }

    CHECKED_VIEW_FIXTURE fixture;
    ApiRequest undecodable;
    undecodable.mutable_message()->PackFrom( fixture.Request() );
    // Field 1 claims five bytes of which only two follow.
    undecodable.mutable_message()->set_value( std::string( "\x0a\x05" "ab", 4 ) );
    BOOST_REQUIRE( DOCUMENT_LIFECYCLE_CONTROLLER::Handles( undecodable ) );
    auto reply = fixture.Call( undecodable );
    BOOST_REQUIRE( !reply );
    BOOST_CHECK( reply.error().status() == ApiStatusCode::AS_BAD_REQUEST );
    BOOST_CHECK( fixture.calls.empty() );
}

BOOST_AUTO_TEST_SUITE_END()
