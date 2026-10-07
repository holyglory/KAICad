/* Native library write-admission contract tests. GPL-3.0-or-later. */

#include <boost/test/unit_test.hpp>

#include <api/library_write_admission.h>
#include <file_content_baseline.h>
#include <ki_exception.h>
#include <kiid.h>
#include <kiplatform/io.h>
#include <lockfile.h>

#include <filesystem>
#include <fstream>
#include <functional>
#include <string_view>


namespace
{
namespace fs = std::filesystem;


struct ADMISSION_DIRECTORY
{
    fs::path path = fs::temp_directory_path() / ( "kicad-library-write-admission-" + KIID().AsStdString() );

    ADMISSION_DIRECTORY()
    {
        std::error_code error;
        BOOST_REQUIRE_MESSAGE( fs::create_directories( path, error ) && !error,
                               error.message() );
    }

    ~ADMISSION_DIRECTORY()
    {
        std::error_code error;
        fs::remove_all( path, error );
    }

    wxString File( std::string_view aName ) const
    {
        return wxString::FromUTF8( ( path / std::string( aName ) ).string() );
    }

    void Write( std::string_view aName, std::string_view aBytes ) const
    {
        std::ofstream output( path / std::string( aName ), std::ios::binary );
        BOOST_REQUIRE( output );
        output.write( aBytes.data(), static_cast<std::streamsize>( aBytes.size() ) );
        output.close();
        BOOST_REQUIRE( output );
    }
};


FILE_CONTENT_BASELINE Baseline( const wxString& aPath )
{
    FILE_CONTENT_BASELINE result = FILE_CONTENT_BASELINE::Read( aPath );
    BOOST_REQUIRE( result.Known() );
    return result;
}


void ExpectIoError( const std::function<void()>& aAction )
{
    BOOST_CHECK_THROW( aAction(), IO_ERROR );
}
} // namespace


BOOST_AUTO_TEST_SUITE( LibraryWriteAdmission )


BOOST_AUTO_TEST_CASE( UnknownAndAbsentBaselinesAreRefused )
{
    ADMISSION_DIRECTORY directory;
    const wxString absent = directory.File( "future.kicad_sym" );

    FILE_CONTENT_BASELINE unknown;
    ExpectIoError( [&] { (void) LIBRARY_WRITE_ADMISSION::Acquire( { unknown } ); } );

    const FILE_CONTENT_BASELINE expectedAbsent = Baseline( absent );
    BOOST_REQUIRE( expectedAbsent.Known() );
    BOOST_CHECK( !expectedAbsent.Exists() );

    ExpectIoError( [&] { (void) LIBRARY_WRITE_ADMISSION::Acquire( { expectedAbsent } ); } );
}


BOOST_AUTO_TEST_CASE( TwoRegularFilesPreserveRequestedOrderAndRelease )
{
    ADMISSION_DIRECTORY directory;
    directory.Write( "table", "table-v1" );
    directory.Write( "library", "library-v1" );
    const wxString tablePath = directory.File( "table" );
    const wxString libraryPath = directory.File( "library" );
    const FILE_CONTENT_BASELINE table = Baseline( tablePath );
    const FILE_CONTENT_BASELINE library = Baseline( libraryPath );

    {
        auto admission = LIBRARY_WRITE_ADMISSION::Acquire( { table, library } );
        BOOST_REQUIRE( admission );
        BOOST_REQUIRE_EQUAL( admission->Before().size(), 2 );
        BOOST_CHECK( FILE_CONTENT_BASELINE::SamePath( admission->Before()[0].Path(), tablePath ) );
        BOOST_CHECK( FILE_CONTENT_BASELINE::SamePath( admission->Before()[1].Path(), libraryPath ) );
        admission->CheckUnchanged();
    }

    // Destructor release must permit the same complete file set to be admitted again.
    auto reacquired = LIBRARY_WRITE_ADMISSION::Acquire( { table, library } );
    BOOST_REQUIRE( reacquired );
    reacquired->CheckUnchanged();
}


BOOST_AUTO_TEST_CASE( StaleBaselineIsRefusedBeforeAdmission )
{
    ADMISSION_DIRECTORY directory;
    directory.Write( "library", "before" );
    const wxString path = directory.File( "library" );
    FILE_CONTENT_BASELINE stale = Baseline( path );
    directory.Write( "library", "after!" );

    ExpectIoError( [&] { (void) LIBRARY_WRITE_ADMISSION::Acquire( { stale } ); } );
}


BOOST_AUTO_TEST_CASE( DuplicateCanonicalAndSymlinkAliasAreRefused )
{
    ADMISSION_DIRECTORY directory;
    directory.Write( "library", "bytes" );
    const wxString path = directory.File( "library" );
    const FILE_CONTENT_BASELINE baseline = Baseline( path );

    ExpectIoError( [&] { (void) LIBRARY_WRITE_ADMISSION::Acquire( { baseline, baseline } ); } );

#if !defined( _WIN32 )
    std::error_code error;
    fs::create_symlink( directory.path / "library", directory.path / "library-alias", error );
    BOOST_REQUIRE_MESSAGE( !error, error.message() );
    const FILE_CONTENT_BASELINE alias = Baseline( directory.File( "library-alias" ) );
    ExpectIoError( [&] { (void) LIBRARY_WRITE_ADMISSION::Acquire( { baseline, alias } ); } );
#endif
}


BOOST_AUTO_TEST_CASE( BusyLockContentionIsRefusedAndReleaseRestoresAdmission )
{
    ADMISSION_DIRECTORY directory;
    directory.Write( "library", "bytes" );
    const wxString path = directory.File( "library" );
    const FILE_CONTENT_BASELINE baseline = Baseline( path );
    const wxString lockPath = LOCKFILE::LockPathFor( path );

    KIPLATFORM::IO::FILE_LOCK holder;
    bool created = false;
    BOOST_REQUIRE( holder.Acquire( lockPath, created ) == KIPLATFORM::IO::FILE_LOCK::STATE::HELD );
    ExpectIoError( [&] { (void) LIBRARY_WRITE_ADMISSION::Acquire( { baseline } ); } );
    holder.Release();

    auto admission = LIBRARY_WRITE_ADMISSION::Acquire( { baseline } );
    BOOST_REQUIRE( admission );
    admission->CheckUnchanged();
}


BOOST_AUTO_TEST_CASE( CheckUnchangedDetectsExternalBytesAndObserveReportsThem )
{
    ADMISSION_DIRECTORY directory;
    directory.Write( "library", "original" );
    const wxString path = directory.File( "library" );
    const FILE_CONTENT_BASELINE baseline = Baseline( path );
    auto admission = LIBRARY_WRITE_ADMISSION::Acquire( { baseline } );
    BOOST_REQUIRE( admission );

    // Keep the file size different so a content digest is the only honest source of the change.
    directory.Write( "library", "changed-by-another-writer" );
    ExpectIoError( [&] { admission->CheckUnchanged(); } );

    const auto observed = admission->Observe();
    BOOST_REQUIRE_EQUAL( observed.size(), 1 );
    BOOST_CHECK( observed[0].Known() );
    BOOST_CHECK( observed[0].Exists() );
    BOOST_CHECK( observed[0].Sha256() != baseline.Sha256() );
    BOOST_CHECK( observed[0].Bytes() != baseline.Bytes() );
    BOOST_CHECK( observed[0].Check( path ) == FILE_BASELINE_CHECK::UNCHANGED );
}


BOOST_AUTO_TEST_SUITE_END()
