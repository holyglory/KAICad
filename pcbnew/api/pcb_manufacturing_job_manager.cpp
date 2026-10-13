/* Native PCB manufacturing admission and journal inspection. GPL-3.0-or-later. */
#include "pcb_manufacturing_job_manager.h"

#include <api/native_state_digest.h>
#include <google/protobuf/util/message_differencer.h>
#include <kiplatform/io.h>
#include <wx/filename.h>
#include <wx/string.h>
#include <algorithm>
#include <array>
#include <cerrno>
#include <cstdlib>
#include <filesystem>
#include <iterator>
#include <limits>
#include <memory>
#include <set>
#include <stdexcept>
#include <string_view>

#ifdef _WIN32
#include <windows.h>
#else
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>
#endif

using namespace kiapi::automation::v1;
using kiapi::common::types::DocumentType;
namespace fs = std::filesystem;

namespace
{
constexpr uint64_t MAX_SOURCE_BYTES = 32ULL * 1024 * 1024;
constexpr uint64_t MAX_JOURNAL_BYTES = 64ULL * 1024 * 1024;
constexpr uint32_t JOURNAL_VERSION = 2;

std::string Hash( std::string_view aBytes )
{
    NATIVE_STATE_DIGEST digest;
    digest.Append( aBytes );
    return digest.Hex();
}

wxString WxPath( const fs::path& aPath )
{
    const auto utf8 = aPath.u8string();
    return wxString::FromUTF8( reinterpret_cast<const char*>( utf8.data() ), utf8.size() );
}

bool Missing( const std::error_code& aError )
{
    return aError == std::errc::no_such_file_or_directory;
}

bool ReparsePath( const fs::path& aPath )
{
#ifdef _WIN32
    DWORD attributes = GetFileAttributesW( aPath.c_str() );
    return attributes != INVALID_FILE_ATTRIBUTES && ( attributes & FILE_ATTRIBUTE_REPARSE_POINT );
#else
    (void) aPath;
    return false;
#endif
}

// Check all existing ancestors as well as the target: accepting a link in the
// parent would defeat a no-follow open of the leaf. No path is canonicalized
// through a link and no derived receipt path becomes an authority to delete.
tl::expected<void, std::string> NoLinks( const fs::path& aPath )
{
    fs::path current = aPath.root_path();
    for( const fs::path& part : aPath.relative_path() )
    {
        current /= part;
        std::error_code error;
        const auto status = fs::symlink_status( current, error );
        if( error && !Missing( error ) ) return tl::unexpected( "manufacturing_journal_unavailable" );
        if( !error && ( fs::is_symlink( status ) || ReparsePath( current ) ) )
            return tl::unexpected( "manufacturing_journal_link_rejected" );
    }
    return {};
}

tl::expected<bool, std::string> RegularOrMissing( const fs::path& aPath )
{
    if( auto valid = NoLinks( aPath ); !valid ) return tl::unexpected( valid.error() );
    std::error_code error;
    const auto status = fs::symlink_status( aPath, error );
    if( Missing( error ) || ( !error && status.type() == fs::file_type::not_found ) ) return false;
    if( error ) return tl::unexpected( "manufacturing_journal_unavailable" );
    if( !fs::is_regular_file( status ) ) return tl::unexpected( "manufacturing_journal_not_regular" );
    return true;
}

// A bounded descriptor read cannot block on a substituted FIFO or follow a
// substituted leaf symlink. The full journal hash below rejects torn/growing
// records and a valid receipt is exposed only after all semantic checks.
tl::expected<std::string, std::string> ReadBytes( const fs::path& aPath )
{
    if( auto regular = RegularOrMissing( aPath ); !regular ) return tl::unexpected( regular.error() );
    else if( !*regular ) return tl::unexpected( "journal_not_found" );
    std::string bytes;
#ifdef _WIN32
    HANDLE handle = CreateFileW( aPath.c_str(), GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, nullptr );
    if( handle == INVALID_HANDLE_VALUE ) return tl::unexpected( "manufacturing_journal_unavailable" );
    struct HANDLE_CLOSE { HANDLE value; ~HANDLE_CLOSE() { CloseHandle( value ); } } close{ handle };
    BY_HANDLE_FILE_INFORMATION information;
    if( !GetFileInformationByHandle( handle, &information )
        || ( information.dwFileAttributes & ( FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY ) ) )
        return tl::unexpected( "manufacturing_journal_not_regular" );
    const uint64_t length = ( uint64_t( information.nFileSizeHigh ) << 32 ) | information.nFileSizeLow;
    if( length == 0 || length > MAX_JOURNAL_BYTES ) return tl::unexpected( "manufacturing_journal_oversized" );
    std::array<char, 4096> buffer;
    DWORD count;
    while( true )
    {
        if( !ReadFile( handle, buffer.data(), DWORD( buffer.size() ), &count, nullptr ) )
            return tl::unexpected( "manufacturing_journal_unavailable" );
        if( count == 0 ) break;
        if( bytes.size() + count > MAX_JOURNAL_BYTES ) return tl::unexpected( "manufacturing_journal_oversized" );
        bytes.append( buffer.data(), count );
    }
#else
    int descriptor = ::open( aPath.c_str(), O_RDONLY | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK );
    if( descriptor < 0 ) return tl::unexpected( "manufacturing_journal_unavailable" );
    struct CLOSE { int descriptor; ~CLOSE() { ::close( descriptor ); } } close{ descriptor };
    struct stat information;
    if( ::fstat( descriptor, &information ) != 0 || !S_ISREG( information.st_mode ) )
        return tl::unexpected( "manufacturing_journal_not_regular" );
    if( information.st_size <= 0 || uint64_t( information.st_size ) > MAX_JOURNAL_BYTES )
        return tl::unexpected( "manufacturing_journal_oversized" );
    std::array<char, 4096> buffer;
    while( true )
    {
        ssize_t count = ::read( descriptor, buffer.data(), buffer.size() );
        if( count < 0 && errno == EINTR ) continue;
        if( count < 0 ) return tl::unexpected( "manufacturing_journal_unavailable" );
        if( count == 0 ) break;
        if( bytes.size() + size_t( count ) > MAX_JOURNAL_BYTES )
            return tl::unexpected( "manufacturing_journal_oversized" );
        bytes.append( buffer.data(), size_t( count ) );
    }
#endif
    return bytes;
}

// Cooperating native instances cannot replace one another's admission record.
// OS locks release after a crash. Reads need no lock and create no files.
tl::expected<std::unique_ptr<KIPLATFORM::IO::FILE_LOCK>, std::string> AcquireOperationLock(
        const fs::path& aDirectory, const std::string& aOperation )
{
    const fs::path path = aDirectory / ( aOperation + ".mfg-lock" );
    if( auto regular = RegularOrMissing( path ); !regular ) return tl::unexpected( regular.error() );
    auto lock = std::make_unique<KIPLATFORM::IO::FILE_LOCK>();
    bool created = false;
    if( lock->Acquire( WxPath( path ), created ) != KIPLATFORM::IO::FILE_LOCK::STATE::HELD )
        return tl::unexpected( "manufacturing_journal_busy" );
    if( auto regular = RegularOrMissing( path ); !regular || !*regular )
        return tl::unexpected( "manufacturing_journal_unavailable" );
    return lock;
}

std::string JournalHash( PcbManufacturingJournalRecord aRecord )
{
    aRecord.mutable_receipt()->clear_durable_journal_sha256();
    return Hash( aRecord.SerializeAsString() );
}
}

PCB_MANUFACTURING_JOB_MANAGER::PCB_MANUFACTURING_JOB_MANAGER( const wxString& aDirectory ) :
        m_journalDirectory( aDirectory )
{
    // Construction is read-only. First admission creates the dedicated root
    // only after target identity and live lifecycle validation have succeeded.
}

wxString PCB_MANUFACTURING_JOB_MANAGER::JournalDirectory( const wxString& aSettingsDirectory )
{
    wxFileName root;
    root.AssignDir( aSettingsDirectory );
    root.AppendDir( wxS( "pcb-manufacturing-jobs" ) );
    return root.GetPath();
}

std::string PCB_MANUFACTURING_JOB_MANAGER::pathFor( const std::string& aId ) const
{
    if( !IsUuid( aId ) ) throw std::invalid_argument( "invalid_manufacturing_operation_id" );
    return ( fs::u8path( m_journalDirectory.ToStdString() ) / ( aId + ".mfg-journal" ) ).generic_string();
}

tl::expected<bool, std::string> PCB_MANUFACTURING_JOB_MANAGER::requireDirectory( bool aCreate ) const
{
    const fs::path directory = fs::u8path( m_journalDirectory.ToStdString() );
    if( !directory.is_absolute() || directory != directory.lexically_normal() )
        return tl::unexpected( "invalid_manufacturing_journal_directory" );
    if( auto valid = NoLinks( directory ); !valid ) return tl::unexpected( valid.error() );
    std::error_code error;
    auto status = fs::symlink_status( directory, error );
    if( Missing( error ) || ( !error && status.type() == fs::file_type::not_found ) )
    {
        if( !aCreate ) return false;
        fs::create_directories( directory, error );
        if( error ) return tl::unexpected( "manufacturing_journal_unavailable" );
        if( auto valid = NoLinks( directory ); !valid ) return tl::unexpected( valid.error() );
        status = fs::symlink_status( directory, error );
    }
    if( error || !fs::is_directory( status ) ) return tl::unexpected( "manufacturing_journal_unavailable" );
    return true;
}

bool PCB_MANUFACTURING_JOB_MANAGER::IsUuid( const std::string& aValue )
{
    if( aValue.size() != 36 || aValue == "00000000-0000-0000-0000-000000000000" ) return false;
    for( size_t index = 0; index < aValue.size(); ++index )
    {
        const char c = aValue[index];
        if( index == 8 || index == 13 || index == 18 || index == 23 )
        { if( c != '-' ) return false; }
        else if( !( ( c >= '0' && c <= '9' ) || ( c >= 'a' && c <= 'f' ) ) ) return false;
    }
    return true;
}

bool PCB_MANUFACTURING_JOB_MANAGER::IsDigest( const std::string& aValue )
{
    return aValue.size() == 64 && std::all_of( aValue.begin(), aValue.end(), []( char c )
             { return ( c >= '0' && c <= '9' ) || ( c >= 'a' && c <= 'f' ); } );
}

bool PCB_MANUFACTURING_JOB_MANAGER::SameDocument(
        const kiapi::common::types::DocumentSpecifier& aLeft,
        const kiapi::common::types::DocumentSpecifier& aRight )
{
    return google::protobuf::util::MessageDifferencer::Equals( aLeft, aRight );
}

bool PCB_MANUFACTURING_JOB_MANAGER::ValidDocument( const kiapi::common::types::DocumentSpecifier& aDocument )
{
    const fs::path project = fs::u8path( aDocument.project().path() );
    const fs::path board = fs::u8path( aDocument.board_filename() );
    return aDocument.type() == DocumentType::DOCTYPE_PCB && !aDocument.project().name().empty()
        && project.is_absolute() && !board.empty() && board == board.filename()
        && board.extension() == ".kicad_pcb";
}

bool PCB_MANUFACTURING_JOB_MANAGER::ValidState( const DocumentLifecycleState& aState )
{
    if( !ValidDocument( aState.document() ) || aState.scope() != DLS_PCB
        || !IsUuid( aState.process_epoch() ) || !IsUuid( aState.native_identity() )
        || !aState.has_revision() || !IsUuid( aState.revision().epoch() )
        || !IsDigest( aState.state_sha256() ) || !aState.project_settings_included()
        || aState.native_files_size() == 0 || aState.native_files_size() > 4096
        || aState.file_baselines_size() != aState.native_files_size() ) return false;
    std::set<std::string> files;
    for( int index = 0; index < aState.native_files_size(); ++index )
    {
        const std::string& file = aState.native_files( index );
        if( !fs::u8path( file ).is_absolute() || !files.insert( file ).second
            || aState.file_baselines( index ).path() != file ) return false;
    }
    return true;
}

std::string PCB_MANUFACTURING_JOB_MANAGER::JobKind( const Request& aRequest )
{
    switch( aRequest.job_case() )
    {
    case Request::kExport3D: return "3d";
    case Request::kExportRender: return "render";
    case Request::kExportSvg: return "svg";
    case Request::kExportDxf: return "dxf";
    case Request::kExportPdf: return "pdf";
    case Request::kExportPs: return "ps";
    case Request::kExportGerbers: return "gerbers";
    case Request::kExportDrill: return "drill";
    case Request::kExportPosition: return "position";
    case Request::kExportGencad: return "gencad";
    case Request::kExportIpc2581: return "ipc2581";
    case Request::kExportIpcD356: return "ipc-d356";
    case Request::kExportOdb: return "odb";
    case Request::kExportStats: return "stats";
    default: return {};
    }
}

tl::expected<void, std::string> PCB_MANUFACTURING_JOB_MANAGER::ValidateRequest( const Request& aRequest )
{
    if( !IsUuid( aRequest.operation_id() ) || !IsUuid( aRequest.process_epoch() )
        || !ValidDocument( aRequest.document() ) || !ValidState( aRequest.expected_state() )
        || !SameDocument( aRequest.document(), aRequest.expected_state().document() )
        || aRequest.process_epoch() != aRequest.expected_state().process_epoch()
        || !fs::u8path( aRequest.output_directory() ).is_absolute() || JobKind( aRequest ).empty()
        || aRequest.ByteSizeLong() > MAX_SOURCE_BYTES )
        return tl::unexpected( "invalid_manufacturing_manager_request" );
    return {};
}

void PCB_MANUFACTURING_JOB_MANAGER::SetUnsupported( Receipt& aReceipt )
{
    aReceipt.set_status( PCB_MFG_EXPORT_UNSUBMITTED );
    aReceipt.set_dispatch_stage( PCB_MFG_DISPATCH_ADMITTED );
    aReceipt.set_terminal( true );
    aReceipt.set_worker_finished( true );
    aReceipt.set_recovery_state( PCB_MFG_RECOVERY_UNSUBMITTED );
    aReceipt.set_error_code( "export_adapter_unavailable" );
    aReceipt.set_error_message( "The detached exporter is unavailable; the admitted request was not dispatched." );
    aReceipt.set_publication_state( PCB_MFG_PUBLICATION_UNSPECIFIED );
    aReceipt.clear_staging_directory();
    aReceipt.clear_artifacts();
    aReceipt.clear_output_manifest_sha256();
    aReceipt.set_output_manifest_complete( false );
    aReceipt.set_publication_committed( false );
}

tl::expected<void, std::string> PCB_MANUFACTURING_JOB_MANAGER::ValidateRecord( const JournalRecord& aRecord )
{
    if( aRecord.format_version() != JOURNAL_VERSION || aRecord.journal_revision() == 0
        || !aRecord.has_request() || !aRecord.has_receipt() )
        return tl::unexpected( "invalid_manufacturing_journal" );
    if( auto valid = ValidateRequest( aRecord.request() ); !valid )
        return tl::unexpected( "invalid_manufacturing_journal" );
    const Request& request = aRecord.request();
    const Receipt& receipt = aRecord.receipt();
    const bool sameState = google::protobuf::util::MessageDifferencer::Equals(
            request.expected_state(), receipt.observed_before() )
        && google::protobuf::util::MessageDifferencer::Equals( request.expected_state(), receipt.expected_state() );
    if( receipt.operation_id() != request.operation_id() || receipt.process_epoch() != request.process_epoch()
        || !SameDocument( receipt.document(), request.document() ) || !sameState
        || receipt.request_fingerprint_sha256() != Hash( request.SerializeAsString() )
        || receipt.job_kind() != JobKind( request ) || receipt.output_directory() != request.output_directory()
        || aRecord.source_board().empty() || aRecord.source_board().size() > MAX_SOURCE_BYTES
        || aRecord.source_board().rfind( "(kicad_pcb", 0 ) != 0
        || receipt.source_snapshot_sha256() != Hash( aRecord.source_board() )
        || receipt.snapshot_complete() || receipt.export_adapter_available()
        || receipt.native_dispatch() || receipt.publication_committed()
        || receipt.publication_state() != PCB_MFG_PUBLICATION_UNSPECIFIED
        || !receipt.staging_directory().empty() || receipt.artifacts_size() != 0
        || receipt.output_manifest_complete() || !receipt.output_manifest_sha256().empty()
        || receipt.has_observed_after() || receipt.dispatch_stage() != PCB_MFG_DISPATCH_ADMITTED )
        return tl::unexpected( "invalid_manufacturing_journal" );
    if( receipt.status() == PCB_MFG_EXPORT_ADMITTED )
    {
        if( receipt.terminal() || receipt.worker_finished() || receipt.cancellation_requested()
            || receipt.recovery_state() != PCB_MFG_RECOVERY_ACTIVE || !receipt.error_code().empty()
            || !receipt.error_message().empty() ) return tl::unexpected( "invalid_manufacturing_journal" );
    }
    else if( receipt.status() == PCB_MFG_EXPORT_UNSUBMITTED )
    {
        if( !receipt.terminal() || !receipt.worker_finished() || receipt.error_message().empty() )
            return tl::unexpected( "invalid_manufacturing_journal" );
        if( receipt.recovery_state() == PCB_MFG_RECOVERY_UNSUBMITTED )
        {
            if( receipt.cancellation_requested() || receipt.error_code() != "export_adapter_unavailable" )
                return tl::unexpected( "invalid_manufacturing_journal" );
        }
        else if( receipt.recovery_state() == PCB_MFG_RECOVERY_CANCELLED )
        {
            if( !receipt.cancellation_requested() || receipt.error_code() != "export_cancelled_before_dispatch" )
                return tl::unexpected( "invalid_manufacturing_journal" );
        }
        else return tl::unexpected( "invalid_manufacturing_journal" );
    }
    else return tl::unexpected( "manufacturing_journal_phase_unsupported" );
    return {};
}

tl::expected<PCB_MANUFACTURING_JOB_MANAGER::JournalRecord, std::string>
PCB_MANUFACTURING_JOB_MANAGER::read( const std::string& aId ) const
{
    if( !IsUuid( aId ) ) return tl::unexpected( "invalid_manufacturing_operation_id" );
    if( auto root = requireDirectory( false ); !root ) return tl::unexpected( root.error() );
    else if( !*root ) return tl::unexpected( "journal_not_found" );
    auto bytes = ReadBytes( fs::u8path( pathFor( aId ) ) );
    if( !bytes ) return tl::unexpected( bytes.error() );
    JournalRecord result;
    if( !result.ParseFromString( *bytes ) || result.request().operation_id() != aId
        || !IsDigest( result.receipt().durable_journal_sha256() )
        || JournalHash( result ) != result.receipt().durable_journal_sha256() )
        return tl::unexpected( "invalid_manufacturing_journal" );
    if( auto valid = ValidateRecord( result ); !valid ) return tl::unexpected( valid.error() );
    return result;
}

tl::expected<void, std::string> PCB_MANUFACTURING_JOB_MANAGER::write( JournalRecord& aRecord ) const
{
    if( auto valid = ValidateRecord( aRecord ); !valid ) return tl::unexpected( valid.error() );
    if( auto root = requireDirectory( true ); !root ) return tl::unexpected( root.error() );
    const fs::path path = fs::u8path( pathFor( aRecord.request().operation_id() ) );
    if( auto regular = RegularOrMissing( path ); !regular ) return tl::unexpected( regular.error() );
    aRecord.mutable_receipt()->set_durable_journal_sha256( JournalHash( aRecord ) );
    const std::string bytes = aRecord.SerializeAsString();
    if( bytes.size() > MAX_JOURNAL_BYTES ) return tl::unexpected( "manufacturing_journal_oversized" );
    wxString error;
    if( !KIPLATFORM::IO::AtomicWriteFile( WxPath( path ), bytes.data(), bytes.size(), &error ) )
        return tl::unexpected( "manufacturing_journal_write_unconfirmed: " + error.ToStdString() );
    auto persisted = read( aRecord.request().operation_id() );
    if( !persisted || persisted->SerializeAsString() != bytes )
        return tl::unexpected( "manufacturing_journal_write_unconfirmed" );
    aRecord.Swap( &*persisted );
    return {};
}

bool PCB_MANUFACTURING_JOB_MANAGER::TestAdmissionBarrier()
{
    const char* value = std::getenv( "KICAD_MANUFACTURING_TEST_BARRIER" );
    return value && std::string( value ) == "after-admission";
}

tl::expected<PCB_MANUFACTURING_JOB_MANAGER::Receipt, std::string>
PCB_MANUFACTURING_JOB_MANAGER::Start( const Request& aRequest, const std::string& aEpoch,
                                    const CAPTURE_SOURCE& aCapture )
{
    std::lock_guard lock( m_mutex );
    if( auto valid = ValidateRequest( aRequest ); !valid ) return tl::unexpected( valid.error() );
    if( !IsUuid( aEpoch ) || aRequest.process_epoch() != aEpoch )
        return tl::unexpected( "stale_process_epoch" );
    if( auto prior = read( aRequest.operation_id() ); prior )
    {
        if( prior->request().SerializeAsString() != aRequest.SerializeAsString() )
            return tl::unexpected( "manufacturing_operation_reused" );
        return prior->receipt();
    }
    else if( prior.error() != "journal_not_found" ) return tl::unexpected( prior.error() );
    if( !aCapture ) return tl::unexpected( "manufacturing_capture_unavailable" );
    auto captured = aCapture( aRequest.document() );
    if( !captured ) return tl::unexpected( captured.error() );
    if( !ValidState( captured->before ) || !ValidState( captured->after )
        || !google::protobuf::util::MessageDifferencer::Equals( captured->before, aRequest.expected_state() )
        || !google::protobuf::util::MessageDifferencer::Equals( captured->before, captured->after ) )
        return tl::unexpected( "pcb_export_state_changed" );
    if( captured->sourceBoard.empty() || captured->sourceBoard.size() > MAX_SOURCE_BYTES
        || captured->sourceBoard.rfind( "(kicad_pcb", 0 ) != 0 )
        return tl::unexpected( "manufacturing_capture_incomplete" );
    if( auto root = requireDirectory( true ); !root ) return tl::unexpected( root.error() );
    auto lease = AcquireOperationLock( fs::u8path( m_journalDirectory.ToStdString() ), aRequest.operation_id() );
    if( !lease ) return tl::unexpected( lease.error() );
    // A different cooperating process may have admitted while capture ran.
    if( auto prior = read( aRequest.operation_id() ); prior )
    {
        if( prior->request().SerializeAsString() != aRequest.SerializeAsString() )
            return tl::unexpected( "manufacturing_operation_reused" );
        return prior->receipt();
    }
    else if( prior.error() != "journal_not_found" ) return tl::unexpected( prior.error() );

    JournalRecord journal;
    journal.set_format_version( JOURNAL_VERSION );
    journal.mutable_request()->CopyFrom( aRequest );
    journal.set_source_board( captured->sourceBoard );
    journal.set_journal_revision( 1 );
    Receipt& receipt = *journal.mutable_receipt();
    receipt.set_operation_id( aRequest.operation_id() );
    receipt.set_process_epoch( aEpoch );
    receipt.mutable_document()->CopyFrom( aRequest.document() );
    receipt.set_request_fingerprint_sha256( Hash( aRequest.SerializeAsString() ) );
    receipt.set_source_snapshot_sha256( Hash( captured->sourceBoard ) );
    receipt.set_job_kind( JobKind( aRequest ) );
    receipt.set_status( PCB_MFG_EXPORT_ADMITTED );
    receipt.set_dispatch_stage( PCB_MFG_DISPATCH_ADMITTED );
    receipt.mutable_expected_state()->CopyFrom( aRequest.expected_state() );
    receipt.mutable_observed_before()->CopyFrom( captured->before );
    receipt.set_output_directory( aRequest.output_directory() );
    receipt.set_recovery_state( PCB_MFG_RECOVERY_ACTIVE );
    // No staging directory, source dependency set or exporter exists yet.
    receipt.set_publication_state( PCB_MFG_PUBLICATION_UNSPECIFIED );
    if( auto persisted = write( journal ); !persisted ) return tl::unexpected( persisted.error() );
    if( !TestAdmissionBarrier() )
    {
        SetUnsupported( *journal.mutable_receipt() );
        journal.set_journal_revision( 2 );
        if( auto persisted = write( journal ); !persisted ) return tl::unexpected( persisted.error() );
    }
    return journal.receipt();
}

tl::expected<PCB_MANUFACTURING_JOB_MANAGER::Receipt, std::string>
PCB_MANUFACTURING_JOB_MANAGER::Cancel( const CancelPcbManufacturingExport& aRequest,
                                     const std::string& aEpoch )
{
    std::lock_guard lock( m_mutex );
    if( !IsUuid( aRequest.operation_id() ) || !ValidDocument( aRequest.document() ) )
        return tl::unexpected( "invalid_manufacturing_cancel_request" );
    if( !IsUuid( aEpoch ) || aRequest.process_epoch() != aEpoch ) return tl::unexpected( "stale_process_epoch" );
    auto prior = read( aRequest.operation_id() );
    if( !prior ) return tl::unexpected( prior.error() );
    if( prior->receipt().process_epoch() != aEpoch
        || !SameDocument( prior->receipt().document(), aRequest.document() ) )
        return tl::unexpected( "manufacturing_cancel_identity_mismatch" );
    if( prior->receipt().terminal() ) return prior->receipt();
    auto lease = AcquireOperationLock( fs::u8path( m_journalDirectory.ToStdString() ), aRequest.operation_id() );
    if( !lease ) return tl::unexpected( lease.error() );
    auto journal = read( aRequest.operation_id() );
    if( !journal ) return tl::unexpected( journal.error() );
    if( journal->receipt().terminal() ) return journal->receipt();
    Receipt& receipt = *journal->mutable_receipt();
    receipt.set_cancellation_requested( true );
    receipt.set_recovery_state( PCB_MFG_RECOVERY_CANCELLED );
    receipt.set_status( PCB_MFG_EXPORT_UNSUBMITTED );
    receipt.set_terminal( true );
    receipt.set_worker_finished( true );
    receipt.set_error_code( "export_cancelled_before_dispatch" );
    receipt.set_error_message( "The admitted request was cancelled before native dispatch; no exporter ran." );
    if( journal->journal_revision() == std::numeric_limits<uint64_t>::max() )
        return tl::unexpected( "invalid_manufacturing_journal" );
    journal->set_journal_revision( journal->journal_revision() + 1 );
    if( auto persisted = write( *journal ); !persisted ) return tl::unexpected( persisted.error() );
    return journal->receipt();
}

tl::expected<PcbManufacturingExportReceiptResponse, std::string>
PCB_MANUFACTURING_JOB_MANAGER::Read( const ReadPcbManufacturingExportJob& aRequest,
                                   const std::string& aEpoch )
{
    std::lock_guard lock( m_mutex );
    if( !IsUuid( aRequest.operation_id() ) || !ValidDocument( aRequest.document() )
        || !IsDigest( aRequest.request_fingerprint_sha256() ) )
        return tl::unexpected( "invalid_manufacturing_read_request" );
    if( !IsUuid( aEpoch ) || aRequest.process_epoch() != aEpoch ) return tl::unexpected( "stale_process_epoch" );
    PcbManufacturingExportReceiptResponse response;
    response.set_reconciliation_supported( true );
    response.set_observing_process_epoch( aEpoch );
    auto journal = read( aRequest.operation_id() );
    if( !journal )
    {
        if( journal.error() == "journal_not_found" ) return response;
        return tl::unexpected( journal.error() );
    }
    const Receipt& receipt = journal->receipt();
    if( receipt.process_epoch() != aEpoch || !SameDocument( receipt.document(), aRequest.document() )
        || receipt.request_fingerprint_sha256() != aRequest.request_fingerprint_sha256() )
        return tl::unexpected( "manufacturing_read_identity_mismatch" );
    response.set_found( true );
    response.set_request_fingerprint_verified( true );
    response.set_journal_verified( true );
    response.mutable_receipt()->CopyFrom( receipt );
    return response;
}

tl::expected<PcbManufacturingExportReceiptResponse, std::string>
PCB_MANUFACTURING_JOB_MANAGER::Reconcile( const ReconcilePcbManufacturingExport& aRequest,
                                        const std::string& aEpoch )
{
    std::lock_guard lock( m_mutex );
    if( !IsUuid( aRequest.operation_id() ) || !ValidDocument( aRequest.document() )
        || !IsDigest( aRequest.request_fingerprint_sha256() )
        || aRequest.allow_prior_process_epoch() != !aRequest.prior_process_epoch().empty()
        || ( aRequest.allow_prior_process_epoch() && !IsUuid( aRequest.prior_process_epoch() ) ) )
        return tl::unexpected( "invalid_manufacturing_reconcile_request" );
    if( !IsUuid( aEpoch ) || aRequest.process_epoch() != aEpoch ) return tl::unexpected( "stale_process_epoch" );
    PcbManufacturingExportReceiptResponse response;
    response.set_reconciliation_supported( true );
    response.set_observing_process_epoch( aEpoch );
    auto journal = read( aRequest.operation_id() );
    if( !journal )
    {
        if( journal.error() == "journal_not_found" ) return response;
        return tl::unexpected( journal.error() );
    }
    const Receipt& receipt = journal->receipt();
    const std::string expectedEpoch = aRequest.allow_prior_process_epoch()
        ? aRequest.prior_process_epoch() : aEpoch;
    if( receipt.process_epoch() != expectedEpoch || !SameDocument( receipt.document(), aRequest.document() )
        || receipt.request_fingerprint_sha256() != aRequest.request_fingerprint_sha256() )
        return tl::unexpected( "manufacturing_reconcile_identity_mismatch" );
    // This inspects historical admission only. Never rewrite a cancelled,
    // unavailable or abandoned ACTIVE receipt as "recovered", and never
    // resume, publish, delete or rerun it in a different native process.
    response.set_found( true );
    response.set_request_fingerprint_verified( true );
    response.set_journal_verified( true );
    response.set_prior_process_epoch_verified( aRequest.allow_prior_process_epoch() );
    response.mutable_receipt()->CopyFrom( receipt );
    return response;
}

PcbManufacturingManagerCapabilities PCB_MANUFACTURING_JOB_MANAGER::Capabilities(
        const kiapi::common::types::DocumentSpecifier& aDocument, const std::string& aEpoch ) const
{
    PcbManufacturingManagerCapabilities response;
    response.mutable_document()->CopyFrom( aDocument );
    response.set_process_epoch( aEpoch );
    response.set_admission_supported( true );
    response.set_request_journal_supported( true );
    response.set_journal_read_supported( true );
    response.set_cancellation_before_dispatch_supported( true );
    response.set_prior_epoch_journal_inspection_supported( true );
    response.set_unavailable_reason( "detached export, worker cancellation, publication and complete dependency snapshots remain unavailable" );
    return response;
}
