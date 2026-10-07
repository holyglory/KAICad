/* Native library write admission. GPL-3.0-or-later. */
#include <api/library_write_admission.h>

#include <ki_exception.h>
#include <kiplatform/io.h>
#include <lockfile.h>

#include <wx/filename.h>

#include <algorithm>
#include <filesystem>
#include <map>
#include <set>
#include <utility>


namespace
{
namespace fs = std::filesystem;


fs::path ToFilesystemPath( const wxString& aPath )
{
#ifdef __WXMSW__
    return fs::path( std::wstring( aPath.wc_str() ) );
#else
    return fs::path( aPath.utf8_string() );
#endif
}


wxString FromFilesystemPath( const fs::path& aPath )
{
#ifdef __WXMSW__
    return wxString( aPath.wstring() );
#else
    return wxString::FromUTF8( aPath.string() );
#endif
}


wxString NormalizeRequestedPath( const wxString& aPath )
{
    if( aPath.empty() || aPath.Find( '\0' ) != wxNOT_FOUND )
        THROW_IO_ERROR( "A library admission path must be a nonempty absolute file path" );

    wxFileName path( aPath );

    if( !path.IsAbsolute() || !path.Normalize( wxPATH_NORM_DOTS | wxPATH_NORM_ABSOLUTE ) )
        THROW_IO_ERROR( "A library admission path must be a nonempty absolute file path" );

    return path.GetFullPath();
}


std::string PathKey( const wxString& aPath )
{
    return aPath.ToStdString( wxConvUTF8 );
}


bool SameBaseline( const FILE_CONTENT_BASELINE& aExpected, const FILE_CONTENT_BASELINE& aCurrent )
{
    return aExpected.Known() && aCurrent.Known()
           && FILE_CONTENT_BASELINE::SamePath( aExpected.Path(), aCurrent.Path() )
           && aExpected.Exists() == aCurrent.Exists() && aExpected.Bytes() == aCurrent.Bytes()
           && aExpected.Sha256() == aCurrent.Sha256();
}


} // namespace


std::mutex LIBRARY_WRITE_ADMISSION::s_registryMutex;
std::set<std::string> LIBRARY_WRITE_ADMISSION::s_reservedPaths;


void LIBRARY_WRITE_ADMISSION::validateRequestedBaseline( const FILE_CONTENT_BASELINE& aBaseline )
{
    if( !aBaseline.Known() )
        THROW_IO_ERROR( "A library admission baseline must have known content state" );

    (void) NormalizeRequestedPath( aBaseline.Path() );
}


wxString LIBRARY_WRITE_ADMISSION::resolvePath( const wxString& aRequested )
{
    const wxString normalized = NormalizeRequestedPath( aRequested );
    std::error_code error;
    const fs::path canonical = fs::weakly_canonical( ToFilesystemPath( normalized ), error );

    if( error || canonical.empty() )
        THROW_IO_ERRORF( "Could not resolve library admission path '%s'", normalized );

    const fs::path parent = canonical.parent_path();
    const fs::file_status parentStatus = fs::status( parent, error );

    if( error || !fs::is_directory( parentStatus ) )
        THROW_IO_ERRORF( "Library admission path '%s' has no accessible parent directory", normalized );

    const fs::path requestedPath = ToFilesystemPath( normalized );
    const fs::file_status requestedLinkStatus = fs::symlink_status( requestedPath, error );

    if( error && error != std::errc::no_such_file_or_directory )
        THROW_IO_ERRORF( "Could not inspect library admission path '%s'", normalized );

    error.clear();

    const fs::file_status targetStatus = fs::status( canonical, error );

    if( error && error != std::errc::no_such_file_or_directory )
        THROW_IO_ERRORF( "Could not inspect resolved library admission path '%s'", FromFilesystemPath( canonical ) );

    error.clear();

    if( fs::is_symlink( requestedLinkStatus ) && !fs::exists( targetStatus ) )
        THROW_IO_ERRORF( "Library admission path '%s' is a broken symbolic link", normalized );

    if( fs::exists( targetStatus ) && !fs::is_regular_file( targetStatus ) )
        THROW_IO_ERRORF( "Library admission path '%s' is not a regular file", normalized );

    return FromFilesystemPath( canonical );
}


LIBRARY_WRITE_ADMISSION::LIBRARY_WRITE_ADMISSION( std::vector<ENTRY> aEntries ) :
        m_entries( std::move( aEntries ) )
{
    m_expected.reserve( m_entries.size() );

    for( const ENTRY& entry : m_entries )
        m_expected.push_back( entry.expected );
}


std::unique_ptr<LIBRARY_WRITE_ADMISSION> LIBRARY_WRITE_ADMISSION::Acquire(
        const std::vector<FILE_CONTENT_BASELINE>& aExpected )
{
    if( aExpected.empty() )
        THROW_IO_ERROR( "A library write admission requires at least one file baseline" );

    std::vector<ENTRY> entries;
    entries.reserve( aExpected.size() );
    std::set<std::string> resolvedKeys;

    for( const FILE_CONTENT_BASELINE& expected : aExpected )
    {
        validateRequestedBaseline( expected );
        const wxString resolved = resolvePath( expected.Path() );
        const std::string key = PathKey( resolved );

        if( !resolvedKeys.insert( key ).second )
            THROW_IO_ERRORF( "Library admission contains duplicate resolved path '%s'", resolved );

        entries.push_back( ENTRY { expected, resolved } );
    }

    std::vector<size_t> lockOrder( entries.size() );

    for( size_t index = 0; index < lockOrder.size(); ++index )
        lockOrder[index] = index;

    std::sort( lockOrder.begin(), lockOrder.end(),
               [&]( size_t aLeft, size_t aRight )
               {
                   return PathKey( entries[aLeft].resolved ) < PathKey( entries[aRight].resolved );
               } );

    std::unique_ptr<LIBRARY_WRITE_ADMISSION> admission(
            new LIBRARY_WRITE_ADMISSION( std::move( entries ) ) );

    {
        std::lock_guard registryGuard( s_registryMutex );

        for( size_t index : lockOrder )
        {
            const std::string key = PathKey( admission->m_entries[index].resolved );

            if( s_reservedPaths.contains( key ) )
                THROW_IO_ERRORF( "Library admission path '%s' is already reserved", admission->m_entries[index].resolved );
        }

        for( size_t index : lockOrder )
        {
            const wxString& resolved = admission->m_entries[index].resolved;
            s_reservedPaths.insert( PathKey( resolved ) );
            admission->m_reserved.push_back( resolved );
        }
    }

    for( size_t index : lockOrder )
    {
        auto lock = std::make_unique<LOCKFILE>( admission->m_entries[index].resolved );

        if( !lock->Locked() )
            THROW_IO_ERRORF( "Library admission path '%s' is busy or cannot be locked",
                             admission->m_entries[index].resolved );

        admission->m_locks.push_back( std::move( lock ) );
    }

    admission->verifyBindings();
    admission->CheckUnchanged();
    return admission;
}


void LIBRARY_WRITE_ADMISSION::verifyBindings() const
{
    for( const ENTRY& entry : m_entries )
    {
        const wxString currentResolved = resolvePath( entry.expected.Path() );

        if( currentResolved != entry.resolved )
            THROW_IO_ERRORF( "Library admission path '%s' resolved to a different target",
                             entry.expected.Path() );
    }
}


std::vector<FILE_CONTENT_BASELINE> LIBRARY_WRITE_ADMISSION::observeCurrent() const
{
    verifyBindings();
    std::vector<FILE_CONTENT_BASELINE> observed;
    observed.reserve( m_entries.size() );

    for( const ENTRY& entry : m_entries )
    {
        FILE_CONTENT_BASELINE current = FILE_CONTENT_BASELINE::Read( entry.expected.Path() );

        if( !current.Known() || !FILE_CONTENT_BASELINE::SamePath( current.Path(), entry.expected.Path() ) )
            THROW_IO_ERRORF( "Could not capture a complete library baseline for '%s'", entry.expected.Path() );

        observed.push_back( std::move( current ) );
    }

    return observed;
}


void LIBRARY_WRITE_ADMISSION::CheckUnchanged() const
{
    const std::vector<FILE_CONTENT_BASELINE> current = observeCurrent();

    for( size_t index = 0; index < m_expected.size(); ++index )
    {
        if( !SameBaseline( m_expected[index], current[index] ) )
            THROW_IO_ERRORF( "Library admission baseline changed for '%s'", m_expected[index].Path() );
    }
}


std::vector<FILE_CONTENT_BASELINE> LIBRARY_WRITE_ADMISSION::Observe() const
{
    return observeCurrent();
}


void LIBRARY_WRITE_ADMISSION::releaseReservations() noexcept
{
    std::lock_guard registryGuard( s_registryMutex );

    for( const wxString& path : m_reserved )
        s_reservedPaths.erase( PathKey( path ) );

    m_reserved.clear();
}


LIBRARY_WRITE_ADMISSION::~LIBRARY_WRITE_ADMISSION()
{
    m_locks.clear();
    releaseReservations();
}
