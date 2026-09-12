/* BOM settings persistence and native snapshot coverage. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <api/api_sch_bom_settings.h>
#include <project/project_file.h>
#include <schematic_settings.h>
#include <settings/json_settings_internals.h>

BOOST_AUTO_TEST_SUITE( SchematicBomSettings )

BOOST_AUTO_TEST_CASE( EmptyColumnsReloadTheirOwnCurrentAndLegacyEncoding )
{
    BOM_PRESET value = BOM_PRESET::DefaultEditing();
    value.fieldsOrdered.clear();
    nlohmann::json encoded = value;
    BOOST_CHECK( encoded.at( "fields_ordered" ).is_array() );
    BOOST_CHECK( encoded.at( "fields_ordered" ).empty() );
    BOM_PRESET current = encoded.get<BOM_PRESET>();
    BOOST_CHECK( current.fieldsOrdered.empty() );
    encoded.erase( "fields_ordered" );
    BOM_PRESET legacy = BOM_PRESET::DefaultEditing();
    from_json( encoded, legacy );
    BOOST_CHECK( legacy.fieldsOrdered.empty() );
    BOOST_CHECK_EQUAL( legacy.name, value.name );
    auto malformed = encoded; malformed["fields_ordered"] = 7;
    BOOST_CHECK_THROW( malformed.get<BOM_PRESET>(), nlohmann::json::exception );
}

BOOST_AUTO_TEST_CASE( TypedSnapshotMatchesEveryPersistedViewAndFormatField )
{
    PROJECT_FILE project( "bom-snapshot.kicad_pro" );
    SCHEMATIC_SETTINGS settings( &project, "schematic" );
    project.m_SchematicSettings = &settings;
    project.Load(); settings.Load();
    settings.m_BomExportFileName = wxString::FromUTF8( "${PROJECTNAME}-組立.csv" );
    auto& view = settings.m_BomSettings;
    view.name = wxString::FromUTF8( "電源 & Grouped" );
    view.fieldsOrdered = { { "Value", wxString::FromUTF8( "値 & rating" ), true, true },
                          { "Reference", "Designators", false, false } };
    view.sortField = "Value"; view.sortAsc = false; view.filterString = "R*|C*";
    view.filterScope = BOM_FILTER_SCOPE::VISIBLE;
    view.groupSymbols = true; view.excludeDNP = true; view.includeExcludedFromBOM = true;
    settings.m_BomPresets = { view, BOM_PRESET{} };
    settings.m_BomPresets.back().filterScope = BOM_FILTER_SCOPE::ALL;
    auto& format = settings.m_BomFmtSettings;
    format.name = "Assembly"; format.fieldDelimiter = "\t;"; format.stringDelimiter = "\"";
    format.refDelimiter = ", "; format.refRangeDelimiter = wxString::FromUTF8( "–" );
    format.keepTabs = true; format.keepLineBreaks = true; format.includeByteOrderMark = true;
    settings.m_BomFmtPresets = { format, BOM_FMT_PRESET{} };
    const auto persisted = settings.CaptureCurrentState();
    const auto snapshot = SCH_BOM_SETTINGS::Capture( settings );
    BOOST_CHECK_EQUAL( SCH_BOM_SETTINGS::MESSAGE::descriptor()->field_count(), 5 );
    BOOST_CHECK_EQUAL( SCH_BOM_SETTINGS::VIEW::descriptor()->field_count(), 9 );
    BOOST_CHECK_EQUAL( SCH_BOM_SETTINGS::FORMAT::descriptor()->field_count(), 8 );
    BOOST_CHECK_EQUAL( nlohmann::json( view ).size(), 9 );
    BOOST_CHECK_EQUAL( nlohmann::json( view.fieldsOrdered.front() ).size(), 4 );
    BOOST_CHECK_EQUAL( nlohmann::json( format ).size(), 8 );
    std::string failure;
    BOOST_REQUIRE_MESSAGE( SCH_BOM_SETTINGS::Validate( snapshot, failure ), failure );
    view.readOnly = !view.readOnly; format.readOnly = !format.readOnly;
    for( auto& preset : settings.m_BomPresets ) preset.readOnly = !preset.readOnly;
    for( auto& preset : settings.m_BomFmtPresets ) preset.readOnly = !preset.readOnly;
    BOOST_CHECK_EQUAL( snapshot.SerializeAsString(), SCH_BOM_SETTINGS::Capture( settings ).SerializeAsString() );
    auto cleared = snapshot;
    cleared.mutable_current_view()->clear_fields();
    cleared.clear_saved_views(); cleared.clear_saved_formats(); cleared.clear_export_filename();
    SCH_BOM_SETTINGS::Restore( settings, cleared );
    BOOST_CHECK( settings.m_BomSettings.fieldsOrdered.empty() );
    BOOST_CHECK( settings.m_BomPresets.empty() && settings.m_BomFmtPresets.empty() );
    SCH_BOM_SETTINGS::Restore( settings, snapshot );
    BOOST_CHECK_EQUAL( snapshot.SerializeAsString(), SCH_BOM_SETTINGS::Capture( settings ).SerializeAsString() );
    BOOST_CHECK( settings.CaptureCurrentState() == persisted );
    size_t count = 0;
    for( const auto& [key, value] : persisted.items() )
        if( key.starts_with( "bom_" ) ) ++count;
    BOOST_CHECK_EQUAL( count, 5 );
    for( int scope = 1; scope <= 3; ++scope )
    {
        auto item = snapshot;
        item.mutable_current_view()->set_filter_scope( static_cast<kiapi::schematic::types::SchematicBomFilterScope>( scope ) );
        BOOST_REQUIRE( SCH_BOM_SETTINGS::Validate( item, failure ) );
        SCH_BOM_SETTINGS::Restore( settings, item );
        BOOST_CHECK_EQUAL( SCH_BOM_SETTINGS::Capture( settings ).SerializeAsString(), item.SerializeAsString() );
    }
    for( int kind = 0; kind < 4; ++kind )
    {
        auto invalid = snapshot;
        if( kind == 0 ) invalid.clear_current_view();
        if( kind == 1 ) invalid.clear_current_format();
        if( kind == 2 ) invalid.mutable_saved_views( 0 )->set_filter_scope( kiapi::schematic::types::SBFS_UNKNOWN );
        if( kind == 3 )
        {
            auto* nested = invalid.mutable_saved_formats( 0 );
            nested->GetReflection()->MutableUnknownFields( nested )->AddVarint( 100, 1 );
        }
        const auto before = settings.CaptureCurrentState();
        BOOST_CHECK( !SCH_BOM_SETTINGS::Validate( invalid, failure ) );
        BOOST_CHECK( settings.CaptureCurrentState() == before );
    }
}

BOOST_AUTO_TEST_SUITE_END()
