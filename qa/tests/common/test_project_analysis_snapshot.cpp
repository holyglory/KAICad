/* Analysis must own unsaved project settings without acquiring or writing live files. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <project.h>
#include <project/project_file.h>
#include <project/project_local_settings.h>
#include <project/net_settings.h>
#include <settings/settings_manager.h>
#include <settings/json_settings_internals.h>
#include <json_common.h>
#include <filesystem>
#include <fstream>

namespace
{
struct PRIVATE_PROJECT
{
    std::filesystem::path directory = std::filesystem::temp_directory_path()
                                     / ( "kicad-analysis-" + KIID().AsStdString() );
    PRIVATE_PROJECT()
    {
        if( !std::filesystem::create_directory( directory ) )
            throw std::runtime_error( "Could not create isolated project fixture" );
    }
    ~PRIVATE_PROJECT()
    {
        std::error_code error;
        std::filesystem::remove_all( directory, error );
    }
};

std::string Read( const std::filesystem::path& path )
{
    std::ifstream stream( path, std::ios::binary );
    if( !stream ) throw std::runtime_error( "Cannot read fixture project" );
    return { std::istreambuf_iterator<char>( stream ), {} };
}
}

BOOST_AUTO_TEST_SUITE( ProjectAnalysisSnapshot )

BOOST_AUTO_TEST_CASE( UninitializedProjectCannotBeSilentlyReplacedWithDefaults )
{
    PROJECT project;
    BOOST_CHECK_THROW( project.CloneForAnalysis(), std::logic_error );
}

BOOST_AUTO_TEST_CASE( UnsavedSettingsAreIndependentAndOutliveTheirSource )
{
    PRIVATE_PROJECT scratch;
    const auto path = scratch.directory / "fixture.kicad_pro";
    {
        std::ofstream stream( path );
        stream << R"({"meta":{"filename":"fixture.kicad_pro","version":3}})";
    }
    const wxString filename = wxString::FromUTF8( path.string() );
    SETTINGS_MANAGER manager;
    BOOST_REQUIRE( manager.LoadProject( filename, false ) );
    PROJECT* source = manager.GetProject( filename );
    BOOST_REQUIRE( source );
    auto& settings = source->GetProjectFile();
    source->GetTextVars()["SUPPLY"] = "1.8 V";
    settings.NetSettings()->GetDefaultNetclass()->SetTrackWidth( 330000 );
    settings.Set<int>( "analysis_test.unknown_extension", 17 );
    source->GetLocalSettings().m_ActiveLayerPreset = "unsaved analysis view";
    const auto before = settings.CaptureCurrentState();
    const auto localBefore = source->GetLocalSettings().CaptureCurrentState();
    const auto storeBefore = static_cast<const nlohmann::json&>( *settings.Internals() );
    const auto fileBefore = Read( path );
    const auto modifiedBefore = std::filesystem::last_write_time( path );

    auto copy = source->CloneForAnalysis();
    BOOST_REQUIRE( copy );
    BOOST_CHECK( copy->GetProjectFullName() == source->GetProjectFullName() );
    BOOST_CHECK( copy->IsReadOnly() );
    BOOST_CHECK( copy->GetProjectLock() == nullptr );
    BOOST_CHECK( copy->GetProjectFile().GetOwningProject() == copy.get() );
    BOOST_CHECK( copy->GetLocalSettings().GetOwningProject() == copy.get() );
    BOOST_CHECK( copy->GetProjectFile().CaptureCurrentState() == before );
    BOOST_CHECK( copy->GetLocalSettings().CaptureCurrentState() == localBefore );
    BOOST_CHECK( settings.CaptureCurrentState() == before );
    BOOST_CHECK( static_cast<const nlohmann::json&>( *settings.Internals() ) == storeBefore );
    BOOST_CHECK( copy->GetProjectFile().NetSettings().get() != settings.NetSettings().get() );
    BOOST_CHECK( copy->GetProjectFile().NetSettings()->GetDefaultNetclass().get()
                 != settings.NetSettings()->GetDefaultNetclass().get() );

    wxString supply = "SUPPLY";
    BOOST_CHECK( copy->TextVarResolver( &supply ) );
    BOOST_CHECK( supply == "1.8 V" );
    copy->GetProjectFile().NetSettings()->GetDefaultNetclass()->SetTrackWidth( 220000 );
    BOOST_CHECK( settings.CaptureCurrentState() == before );
    source->GetTextVars()["SUPPLY"] = "3.3 V";
    BOOST_CHECK( copy->GetTextVars().at( "SUPPLY" ) == "1.8 V" );
    BOOST_CHECK( manager.UnloadProject( source, false ) );
    BOOST_CHECK_EQUAL( copy->GetProjectFile().NetSettings()->GetDefaultNetclass()->GetTrackWidth(), 220000 );
    BOOST_CHECK( copy->GetLocalSettings().m_ActiveLayerPreset == "unsaved analysis view" );
    supply = "SUPPLY";
    BOOST_CHECK( copy->TextVarResolver( &supply ) );
    BOOST_CHECK( supply == "1.8 V" );

    // Even a forced persistence request must not write through the retained source path.
    BOOST_CHECK( !copy->GetProjectFile().SaveToFile( "", true ) );
    BOOST_CHECK( !copy->GetLocalSettings().SaveToFile( "", true ) );
    BOOST_CHECK_EQUAL( Read( path ), fileBefore );
    BOOST_CHECK( std::filesystem::last_write_time( path ) == modifiedBefore );
}

BOOST_AUTO_TEST_SUITE_END()
