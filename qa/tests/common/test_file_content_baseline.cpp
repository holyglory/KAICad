/* Actual parser/writer file versions, not current-file adoption. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <file_content_baseline.h>
#include <digesting_file_line_reader.h>
#include <kiid.h>
#include <filesystem>
#include <fstream>
#include <atomic>
#include <barrier>
#include <thread>

namespace
{
namespace fs = std::filesystem;
struct BASELINE_DIRECTORY
{
    fs::path path = fs::temp_directory_path() / ( "kicad-file-baseline-" + KIID().AsStdString() );
    BASELINE_DIRECTORY() { if( !fs::create_directory( path ) ) throw std::runtime_error( "Fixture directory failed" ); }
    ~BASELINE_DIRECTORY() { std::error_code error; fs::remove_all( path, error ); }
    wxString File( const std::string& aName = "native.txt" ) const
    { return wxString::FromUTF8( ( path / aName ).string() ); }
    void Write( std::string_view aBytes, const std::string& aName = "native.txt" )
    {
        std::ofstream output( path / aName, std::ios::binary );
        output.write( aBytes.data(), aBytes.size() );
        output.close();
        if( !output ) throw std::runtime_error( "Fixture write failed" );
    }
};
}

BOOST_AUTO_TEST_SUITE( FileContentBaseline )

BOOST_AUTO_TEST_CASE( MissingUnknownWrongPathAndUnreadableAreDistinct )
{
    BASELINE_DIRECTORY directory;
    const auto file = directory.File();
    BOOST_CHECK( FILE_CONTENT_BASELINE{}.Check( file ) == FILE_BASELINE_CHECK::UNKNOWN );
    const auto missing = FILE_CONTENT_BASELINE::Read( file );
    BOOST_REQUIRE( missing.Known() );
    BOOST_CHECK( !missing.Exists() );
    BOOST_CHECK( missing.Check( file ) == FILE_BASELINE_CHECK::UNCHANGED );
    BOOST_CHECK( missing.Check( directory.File( "another.txt" ) ) == FILE_BASELINE_CHECK::WRONG_PATH );
    BOOST_REQUIRE( fs::create_directory( directory.path / "native.txt" ) );
    BOOST_CHECK( missing.Check( file ) == FILE_BASELINE_CHECK::UNREADABLE );
    BOOST_CHECK_THROW( DIGESTING_FILE_LINE_READER reader( file ), IO_ERROR );
    BOOST_REQUIRE( fs::remove( directory.path / "native.txt" ) );
    directory.Write( "" );
    BOOST_CHECK( missing.Check( file ) == FILE_BASELINE_CHECK::CHANGED );
    const auto empty = FILE_CONTENT_BASELINE::Read( file );
    BOOST_CHECK( empty.Known() && empty.Exists() );
    BOOST_CHECK_EQUAL( empty.Bytes(), 0 );
    BOOST_CHECK_EQUAL( empty.Sha256(), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" );
}

BOOST_AUTO_TEST_CASE( SameSizeAndTimestampChangesDeletionAndRecovery )
{
    BASELINE_DIRECTORY directory;
    directory.Write( "original" );
    const auto baseline = FILE_CONTENT_BASELINE::Read( directory.File() );
    const auto timestamp = fs::last_write_time( directory.path / "native.txt" );
    directory.Write( "modified" );
    fs::last_write_time( directory.path / "native.txt", timestamp );
    BOOST_CHECK( baseline.Check( directory.File() ) == FILE_BASELINE_CHECK::CHANGED );
    directory.Write( "original" );
    BOOST_CHECK( baseline.Check( directory.File() ) == FILE_BASELINE_CHECK::UNCHANGED );
    BOOST_REQUIRE( fs::remove( directory.path / "native.txt" ) );
    BOOST_CHECK( baseline.Check( directory.File() ) == FILE_BASELINE_CHECK::CHANGED );
    directory.Write( "original" );
    BOOST_CHECK( baseline.Check( directory.File() ) == FILE_BASELINE_CHECK::UNCHANGED );
}

BOOST_AUTO_TEST_CASE( ParserInputIncludesRawNewlinesNulsRewindAndUnreadTail )
{
    BASELINE_DIRECTORY directory;
    const std::string input = std::string( "one\r\ntwo\n\0three", 15 ) + std::string( 200000, 'x' );
    directory.Write( input );
    DIGESTING_FILE_LINE_READER reader( directory.File() );
    BOOST_REQUIRE( reader.ReadLine() );
    BOOST_CHECK_EQUAL( std::string( reader.Line(), reader.Length() ), "one\r\n" );
    reader.Rewind();
    BOOST_REQUIRE( reader.ReadLine() );
    auto baseline = reader.FinishBaseline();
    BOOST_CHECK_EQUAL( baseline.Sha256(), FILE_CONTENT_BASELINE::FromBytes( directory.File(), input ).Sha256() );
    BOOST_CHECK_EQUAL( baseline.Bytes(), input.size() );
    BOOST_CHECK_EQUAL( baseline.Sha256(), reader.FinishBaseline().Sha256() );
    BOOST_CHECK( baseline.Check( directory.File() ) == FILE_BASELINE_CHECK::UNCHANGED );
    std::string contents;
    BOOST_CHECK_EQUAL( FILE_CONTENT_BASELINE::Read( directory.File(), &contents ).Sha256(), baseline.Sha256() );
    BOOST_CHECK_EQUAL( contents, input );
}

#ifndef _WIN32
BOOST_AUTO_TEST_CASE( ReplacementAfterOpenCannotBecomeTheLoadedBaseline )
{
    BASELINE_DIRECTORY directory;
    directory.Write( "loaded\nold\n" );
    DIGESTING_FILE_LINE_READER reader( directory.File() );
    BOOST_REQUIRE( reader.ReadLine() );
    directory.Write( "edited\nnew\n", "replacement.txt" );
    fs::rename( directory.path / "replacement.txt", directory.path / "native.txt" );
    const auto baseline = reader.FinishBaseline();
    BOOST_CHECK_EQUAL( baseline.Sha256(), FILE_CONTENT_BASELINE::FromBytes( directory.File(), "loaded\nold\n" ).Sha256() );
    BOOST_CHECK( baseline.Check( directory.File() ) == FILE_BASELINE_CHECK::CHANGED );
}
#endif

BOOST_AUTO_TEST_CASE( OutputBaselineUsesCommittedBytesNotSubsequentDiskContents )
{
    BASELINE_DIRECTORY directory;
    PRETTIFIED_FILE_OUTPUTFORMATTER writer( directory.File() );
    writer.Print( "(node (value 1))" );
    BOOST_CHECK( !writer.CommittedBaseline().Known() );
    BOOST_REQUIRE( writer.Finish() );
    const auto baseline = writer.CommittedBaseline();
    BOOST_CHECK( baseline.Check( directory.File() ) == FILE_BASELINE_CHECK::UNCHANGED );
    directory.Write( "external change" );
    BOOST_CHECK_EQUAL( writer.CommittedBaseline().Sha256(), baseline.Sha256() );
    BOOST_CHECK( writer.CommittedBaseline().Check( directory.File() ) == FILE_BASELINE_CHECK::CHANGED );
}

BOOST_AUTO_TEST_CASE( CommitObserverRejectsBeforeReplacementAndRestoresItsScope )
{
    BASELINE_DIRECTORY directory;
    directory.Write( "original" );
    const auto before = FILE_CONTENT_BASELINE::Read( directory.File() );
    unsigned rejected = 0, committed = 0;
    {
        FILE_WRITE_OBSERVER observer( [&]( const wxString& ) { ++rejected; THROW_IO_ERROR( "Fixture rejection" ); },
                                      [&]( const FILE_CONTENT_BASELINE& ) { ++committed; } );
        PRETTIFIED_FILE_OUTPUTFORMATTER output( directory.File() );
        output.Print( "(changed)" );
        BOOST_CHECK_THROW( output.Finish(), IO_ERROR );
        BOOST_CHECK( before.Check( directory.File() ) == FILE_BASELINE_CHECK::UNCHANGED );
        BOOST_CHECK( !output.CommittedBaseline().Known() );
    }
    BOOST_CHECK_EQUAL( rejected, 1 ); BOOST_CHECK_EQUAL( committed, 0 );
    FILE_CONTENT_BASELINE observed;
    {
        FILE_WRITE_OBSERVER observer( {}, [&]( const FILE_CONTENT_BASELINE& value ) { ++committed; observed = value; } );
        PRETTIFIED_FILE_OUTPUTFORMATTER output( directory.File() );
        output.Print( "(changed)" );
        BOOST_REQUIRE( output.Finish() );
    }
    BOOST_CHECK_EQUAL( rejected, 1 ); BOOST_CHECK_EQUAL( committed, 1 );
    BOOST_CHECK( observed.Check( directory.File() ) == FILE_BASELINE_CHECK::UNCHANGED );
    PRETTIFIED_FILE_OUTPUTFORMATTER ordinary( directory.File() );
    ordinary.Print( "(normal-save)" ); BOOST_REQUIRE( ordinary.Finish() );
    BOOST_CHECK_EQUAL( committed, 1 );
}

BOOST_AUTO_TEST_CASE( NestedObserversRestoreAndOtherThreadsCannotBorrowTheScope )
{
    BASELINE_DIRECTORY directory;
    const wxString path = directory.File();
    const auto baseline = FILE_CONTENT_BASELINE::FromBytes( path, "native" );
    std::atomic<int> outer = 0, worker = 0;
    int nested = 0;
    {
        FILE_WRITE_OBSERVER scope( [&]( const wxString& ) { ++outer; },
                                   [&]( const FILE_CONTENT_BASELINE& ) { ++outer; } );
        FILE_WRITE_OBSERVER::BeforeWrite( path ); FILE_WRITE_OBSERVER::AfterWrite( baseline );
        {
            FILE_WRITE_OBSERVER inner( [&]( const wxString& ) { ++nested; },
                                       [&]( const FILE_CONTENT_BASELINE& ) { ++nested; } );
            FILE_WRITE_OBSERVER::BeforeWrite( path ); FILE_WRITE_OBSERVER::AfterWrite( baseline );
        }
        BOOST_CHECK_EQUAL( nested, 2 ); BOOST_CHECK_EQUAL( outer.load(), 2 );
        std::barrier gate( 2 );
        std::thread thread( [&]
        {
            FILE_WRITE_OBSERVER::BeforeWrite( path ); FILE_WRITE_OBSERVER::AfterWrite( baseline );
            FILE_WRITE_OBSERVER own( [&]( const wxString& ) { ++worker; },
                                     [&]( const FILE_CONTENT_BASELINE& ) { ++worker; } );
            gate.arrive_and_wait();
            FILE_WRITE_OBSERVER::BeforeWrite( path ); FILE_WRITE_OBSERVER::AfterWrite( baseline );
            gate.arrive_and_wait();
        } );
        gate.arrive_and_wait();
        FILE_WRITE_OBSERVER::BeforeWrite( path ); FILE_WRITE_OBSERVER::AfterWrite( baseline );
        gate.arrive_and_wait(); thread.join();
        BOOST_CHECK_EQUAL( outer.load(), 4 ); BOOST_CHECK_EQUAL( worker.load(), 2 );
    }
    FILE_WRITE_OBSERVER::BeforeWrite( path ); FILE_WRITE_OBSERVER::AfterWrite( baseline );
    BOOST_CHECK_EQUAL( outer.load(), 4 ); BOOST_CHECK_EQUAL( worker.load(), 2 );
}

BOOST_AUTO_TEST_SUITE_END()
