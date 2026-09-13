/* Resource-preserving native state serialization. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <schematic.h>
#include <sch_sheet.h>
#include <sch_screen.h>
#include <sch_io/kicad_sexpr/sch_io_kicad_sexpr.h>
#include <embedded_files.h>
#include <richio.h>
#include <api/native_state_digest.h>
#include <project.h>
#include <wx/filename.h>
#include <filesystem>
#include <fstream>

BOOST_AUTO_TEST_SUITE( SchematicStateFormat )

BOOST_AUTO_TEST_CASE( NativeSaveLoadAndExportKeepTheCorrectFileBaseline )
{
    namespace fs = std::filesystem;
    const auto directory = fs::temp_directory_path() / ( "kicad-sch-baseline-" + KIID().AsStdString() );
    BOOST_REQUIRE( fs::create_directory( directory ) );
    struct CLEANUP { fs::path path; ~CLEANUP() { std::error_code error; fs::remove_all( path, error ); } } cleanup{ directory };
    class TEST_PROJECT : public PROJECT
    {
    public:
        wxString directory;
        const wxString GetProjectName() const override { return "baseline"; }
        const wxString GetProjectPath() const override { return directory + wxFileName::GetPathSeparator(); }
        const wxString GetProjectFullName() const override { return GetProjectPath() + "baseline.kicad_pro"; }
    } project;
    project.directory = wxString::FromUTF8( directory.string() );
    const wxString file = project.GetProjectPath() + "baseline.kicad_sch";
    SCHEMATIC schematic( &project );
    schematic.CreateDefaultScreens();
    schematic.RootScreen()->SetFileName( file );
    SCH_IO_KICAD_SEXPR io;
    io.SaveSchematicFile( file, schematic.GetTopLevelSheet(), &schematic );
    const auto baseline = schematic.RootScreen()->FileBaseline();
    BOOST_CHECK( baseline.Check( file ) == FILE_BASELINE_CHECK::UNCHANGED );
    SCHEMATIC reloaded( &project );
    std::unique_ptr<SCH_SHEET> sheet( io.LoadSchematicFile( file, &reloaded ) );
    BOOST_REQUIRE( sheet && sheet->GetScreen() );
    BOOST_CHECK_EQUAL( sheet->GetScreen()->FileBaseline().Sha256(), baseline.Sha256() );
    io.SaveSchematicFile( project.GetProjectPath() + "copy.kicad_sch", schematic.GetTopLevelSheet(), &schematic );
    BOOST_CHECK_EQUAL( schematic.RootScreen()->FileBaseline().Path().ToStdString( wxConvUTF8 ),
                       baseline.Path().ToStdString( wxConvUTF8 ) );
    { std::ofstream external( directory / "baseline.kicad_sch", std::ios::app ); external << "\n;external edit\n"; }
    BOOST_CHECK( sheet->GetScreen()->FileBaseline().Check( file ) == FILE_BASELINE_CHECK::CHANGED );
    BOOST_CHECK( schematic.RootScreen()->FileBaseline().Check( file ) == FILE_BASELINE_CHECK::CHANGED );
}

BOOST_AUTO_TEST_CASE( RepeatedStateSerializationPreservesCurrentResourcesAndDirtyState )
{
    SCHEMATIC schematic( nullptr );
    schematic.CreateDefaultScreens();
    schematic.SetAreFontsEmbedded( false );
    auto asset = std::make_shared<EMBEDDED_FILES::EMBEDDED_FILE>();
    asset->name = "retained.ttf";
    asset->type = EMBEDDED_FILES::EMBEDDED_FILE::FILE_TYPE::FONT;
    // Collection sentinel only; the formatter must not try to load it as a font.
    asset->decompressedData = { 'f', 'i', 'x', 't', 'u', 'r', 'e' };
    BOOST_REQUIRE( EMBEDDED_FILES::CompressAndEncode( *asset ) == EMBEDDED_FILES::RETURN_CODE::OK );
    schematic.GetEmbeddedFiles()->AddFile( asset );
    const auto encoded = asset->compressedEncodedData;
    const auto hash = asset->data_hash;
    const bool dirty = schematic.RootScreen()->IsContentModified();
    SCH_IO_KICAD_SEXPR writer;
    STRING_FORMATTER first, second;
    writer.FormatSchematicToFormatter( &first, schematic.GetTopLevelSheet(), &schematic, nullptr, false );
    writer.FormatSchematicToFormatter( &second, schematic.GetTopLevelSheet(), &schematic, nullptr, false );
    BOOST_CHECK_EQUAL( first.GetString(), second.GetString() );
    NATIVE_STATE_DIGEST streamed;
    writer.FormatSchematicToFormatter( &streamed, schematic.GetTopLevelSheet(), &schematic, nullptr, false );
    BOOST_CHECK_EQUAL( streamed.Hex(), picosha2::hash256_hex_string( first.GetString() ) );
    BOOST_CHECK_EQUAL( streamed.Bytes(), first.GetString().size() );
    BOOST_CHECK( first.GetString().find( "retained.ttf" ) != std::string::npos );
    BOOST_REQUIRE( schematic.GetEmbeddedFiles()->GetEmbeddedFile( "retained.ttf" ) == asset.get() );
    BOOST_CHECK_EQUAL( asset->compressedEncodedData, encoded );
    BOOST_CHECK_EQUAL( asset->data_hash, hash );
    BOOST_CHECK_EQUAL( schematic.RootScreen()->IsContentModified(), dirty );
    STRING_FORMATTER saved;
    writer.FormatSchematicToFormatter( &saved, schematic.GetTopLevelSheet(), &schematic );
    BOOST_CHECK( schematic.GetEmbeddedFiles()->GetEmbeddedFile( "retained.ttf" ) == nullptr );
    BOOST_CHECK( saved.GetString().find( "retained.ttf" ) == std::string::npos );
}

BOOST_AUTO_TEST_SUITE_END()
