/* Native settings persistence outcomes. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <settings/json_settings.h>
#include <settings/nested_settings.h>
#include <settings/parameters.h>
#include <project.h>
#include <project/project_file.h>
#include <wx/filename.h>
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

class SAVE_PROJECT : public PROJECT
{
public:
    explicit SAVE_PROJECT( const wxString& aDirectory ) : m_directory( aDirectory ) {}
    const wxString GetProjectName() const override { return "migration"; }
    const wxString GetProjectPath() const override { return m_directory + wxFileName::GetPathSeparator(); }
    const wxString GetProjectFullName() const override { return GetProjectPath() + "migration.kicad_pro"; }
private:
    wxString m_directory;
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

BOOST_AUTO_TEST_CASE( FailedProjectWriteKeepsMigrationPendingUntilSuccessfulRetry )
{
    SAVE_DIRECTORY directory;
    SAVE_PROJECT project( directory.Directory() );
    PROJECT_FILE file( "migration" );
    file.SetProject( &project );
    const auto path = directory.path / "migration.kicad_pro";
    { std::ofstream source( path ); source << R"({"meta":{"version":3},"schematic":{}})"; }
    BOOST_REQUIRE( file.LoadFromFile( directory.Directory() ) );
    BOOST_CHECK( !file.ShouldAutoSave() ); // The missing root inventory was migrated in memory.
    fs::rename( path, directory.path / "before.kicad_pro" );
    BOOST_REQUIRE( fs::create_directory( path ) );
    SETTINGS_SAVE_RESULT result = SETTINGS_SAVE_RESULT::SKIPPED;
    BOOST_CHECK( !file.SaveToFile( directory.Directory(), false, &result ) );
    BOOST_CHECK( result == SETTINGS_SAVE_RESULT::FAILED );
    BOOST_CHECK( !file.ShouldAutoSave() );
    BOOST_REQUIRE( fs::remove( path ) );
    BOOST_CHECK( file.SaveToFile( directory.Directory(), false, &result ) );
    BOOST_CHECK( result == SETTINGS_SAVE_RESULT::WRITTEN );
    BOOST_CHECK( file.ShouldAutoSave() );
    BOOST_CHECK( Read( path ).find( "top_level_sheets" ) != std::string::npos );
}

BOOST_AUTO_TEST_CASE( LoadedAndWrittenBaselinesDoNotAdoptExternalEditsOrFailedWrites )
{
    SAVE_DIRECTORY directory;
    SAVE_ROOT root;
    const auto path = directory.path / "save-result.json";
    const wxString nativePath = wxString::FromUTF8( path.string() );
    BOOST_REQUIRE( root.SaveToFile( directory.Directory() ) );
    BOOST_CHECK( root.FileBaseline().Check( nativePath ) == FILE_BASELINE_CHECK::UNCHANGED );
    SAVE_ROOT loaded;
    BOOST_REQUIRE( loaded.LoadFromFile( directory.Directory() ) );
    BOOST_CHECK_EQUAL( loaded.FileBaseline().Sha256(), root.FileBaseline().Sha256() );
    { std::ofstream external( path, std::ios::binary ); external << "external edit"; }
    BOOST_CHECK( loaded.FileBaseline().Check( nativePath ) == FILE_BASELINE_CHECK::CHANGED );
    SETTINGS_SAVE_RESULT result;
    BOOST_CHECK( !loaded.SaveToFile( directory.Directory(), false, &result ) );
    BOOST_CHECK( result == SETTINGS_SAVE_RESULT::UNCHANGED ); // Legacy no-op, NOT a checked save.
    BOOST_CHECK( loaded.FileBaseline().Check( nativePath ) == FILE_BASELINE_CHECK::CHANGED );
    const auto baseline = loaded.FileBaseline().Sha256();
    fs::rename( path, directory.path / "external.json" );
    BOOST_REQUIRE( fs::create_directory( path ) );
    loaded.value = 9;
    BOOST_CHECK( !loaded.SaveToFile( directory.Directory(), false, &result ) );
    BOOST_CHECK( result == SETTINGS_SAVE_RESULT::FAILED );
    BOOST_CHECK_EQUAL( loaded.FileBaseline().Sha256(), baseline );
    BOOST_REQUIRE( fs::remove( path ) );
    BOOST_REQUIRE( loaded.SaveToFile( directory.Directory(), false, &result ) );
    BOOST_CHECK( loaded.FileBaseline().Check( nativePath ) == FILE_BASELINE_CHECK::UNCHANGED );
    BOOST_CHECK( loaded.FileBaseline().Sha256() != baseline );
    SAVE_ROOT missing;
    BOOST_CHECK( !missing.LoadFromFile( directory.Directory() + "/absent" ) );
    BOOST_CHECK( missing.FileBaseline().Known() && !missing.FileBaseline().Exists() );
}

BOOST_AUTO_TEST_SUITE_END()
