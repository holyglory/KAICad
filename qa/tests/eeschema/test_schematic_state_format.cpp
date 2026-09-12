/* Resource-preserving native state serialization. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <schematic.h>
#include <sch_sheet.h>
#include <sch_screen.h>
#include <sch_io/kicad_sexpr/sch_io_kicad_sexpr.h>
#include <embedded_files.h>
#include <richio.h>

BOOST_AUTO_TEST_SUITE( SchematicStateFormat )

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
