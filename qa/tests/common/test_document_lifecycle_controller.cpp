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
#include <file_write_observer.h>
#include <iterator>
#include <wx/filename.h>

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
            BOOST_CHECK( Contains( message, "making files writable does not help" ) );
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

BOOST_AUTO_TEST_SUITE_END()
