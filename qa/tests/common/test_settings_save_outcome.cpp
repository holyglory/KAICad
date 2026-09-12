/* Native settings persistence outcomes. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <settings/json_settings.h>
#include <settings/nested_settings.h>
#include <settings/parameters.h>
#include <kiid.h>
#include <filesystem>
#include <fstream>

namespace
{
namespace fs = std::filesystem;

struct SAVE_DIRECTORY
{
    SAVE_DIRECTORY() : path( fs::temp_directory_path() / ( "kicad-save-result-" + KIID().AsStdString() ) )
    {
        if( !fs::create_directory( path ) ) throw std::runtime_error( "Could not create unique save fixture" );
    }
    ~SAVE_DIRECTORY() { std::error_code ignored; fs::remove_all( path, ignored ); }
    wxString Directory() const { return wxString::FromUTF8( path.string() ); }
    fs::path path;
};

class SAVE_ROOT : public JSON_SETTINGS
{
public:
    SAVE_ROOT() : JSON_SETTINGS( "save-result", SETTINGS_LOC::NONE, 0, true, true, true )
    { m_params.emplace_back( new PARAM<int>( "value", &value, 1 ) ); }
    int value = 1;
};

class SAVE_CHILD : public NESTED_SETTINGS
{
public:
    explicit SAVE_CHILD( JSON_SETTINGS* aParent ) : NESTED_SETTINGS( "child", 0, aParent, "child", false ) {}
    bool SaveToFile( const wxString& aDirectory = "", bool aForce = false,
                     SETTINGS_SAVE_RESULT* aResult = nullptr ) override
    {
        if( fail )
        {
            if( aResult ) *aResult = SETTINGS_SAVE_RESULT::FAILED;
            return false;
        }
        return NESTED_SETTINGS::SaveToFile( aDirectory, aForce, aResult );
    }
    bool fail = true;
};

std::string Read( const fs::path& aPath )
{
    std::ifstream input( aPath, std::ios::binary );
    if( !input ) throw std::runtime_error( "Missing expected fixture file" );
    return { std::istreambuf_iterator<char>( input ), std::istreambuf_iterator<char>() };
}
}

BOOST_AUTO_TEST_SUITE( SettingsSaveOutcome )

BOOST_AUTO_TEST_CASE( WrittenUnchangedAndSkippedKeepLegacyBooleanMeaning )
{
    SAVE_DIRECTORY directory;
    SAVE_ROOT root;
    SETTINGS_SAVE_RESULT result = SETTINGS_SAVE_RESULT::FAILED;
    BOOST_CHECK( root.SaveToFile( directory.Directory(), false, &result ) );
    BOOST_CHECK( result == SETTINGS_SAVE_RESULT::WRITTEN );
    const auto file = directory.path / "save-result.json";
    const auto bytes = Read( file );
    const auto modified = fs::last_write_time( file );
    BOOST_CHECK( !root.SaveToFile( directory.Directory(), false, &result ) );
    BOOST_CHECK( result == SETTINGS_SAVE_RESULT::UNCHANGED );
    BOOST_CHECK( Read( file ) == bytes );
    BOOST_CHECK( fs::last_write_time( file ) == modified );
    root.SetReadOnly( true );
    BOOST_CHECK( !root.SaveToFile( directory.Directory(), true, &result ) );
    BOOST_CHECK( result == SETTINGS_SAVE_RESULT::SKIPPED );
    BOOST_CHECK( Read( file ) == bytes );
}

BOOST_AUTO_TEST_CASE( FailedAtomicWritePreservesObstructionAndCanRetry )
{
    SAVE_DIRECTORY directory;
    SAVE_ROOT root; root.value = 7;
    const auto target = directory.path / "save-result.json";
    BOOST_REQUIRE( fs::create_directory( target ) );
    { std::ofstream keep( target / "keep" ); keep << "retained fixture"; }
    SETTINGS_SAVE_RESULT result = SETTINGS_SAVE_RESULT::SKIPPED;
    BOOST_CHECK( !root.SaveToFile( directory.Directory(), false, &result ) );
    BOOST_CHECK( result == SETTINGS_SAVE_RESULT::FAILED );
    BOOST_CHECK_EQUAL( root.value, 7 );
    BOOST_CHECK_EQUAL( Read( target / "keep" ), "retained fixture" );
    BOOST_REQUIRE( fs::remove( target / "keep" ) );
    BOOST_REQUIRE( fs::remove( target ) );
    BOOST_CHECK( root.SaveToFile( directory.Directory(), false, &result ) );
    BOOST_CHECK( result == SETTINGS_SAVE_RESULT::WRITTEN );
    BOOST_CHECK( Read( target ).find( "7" ) != std::string::npos );
}

BOOST_AUTO_TEST_CASE( FailedNestedStoreCannotBecomeASuccessfulParentFile )
{
    SAVE_DIRECTORY directory;
    SAVE_ROOT root;
    SAVE_CHILD child( &root );
    SETTINGS_SAVE_RESULT result = SETTINGS_SAVE_RESULT::SKIPPED;
    BOOST_CHECK( !root.SaveToFile( directory.Directory(), false, &result ) );
    BOOST_CHECK( result == SETTINGS_SAVE_RESULT::FAILED );
    BOOST_CHECK( !fs::exists( directory.path / "save-result.json" ) );
    child.fail = false;
    BOOST_CHECK( root.SaveToFile( directory.Directory(), false, &result ) );
    BOOST_CHECK( result == SETTINGS_SAVE_RESULT::WRITTEN );
}

BOOST_AUTO_TEST_SUITE_END()
