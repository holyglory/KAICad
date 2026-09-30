/* Project-owned symbol defaults and comparison policy. GPL-3.0-or-later. */
#ifndef API_SCH_SYMBOL_PROJECT_SETTINGS_H
#define API_SCH_SYMBOL_PROJECT_SETTINGS_H

#include <schematic_settings.h>
#include <schematic/schematic_types.pb.h>
#include <template_fieldnames.h>
#include <string>
#include <utility>

namespace SCH_SYMBOL_COMPARISON
{
using MESSAGE = kiapi::schematic::types::SchematicSymbolComparisonSettings;

inline MESSAGE Capture( const SYMBOL_PARITY_SETTINGS& aSettings )
{
    MESSAGE value;
    value.set_missing_fields( aSettings.m_MissingFields );
    value.set_extra_fields( aSettings.m_ExtraFields );
    value.set_field_texts( aSettings.m_FieldTexts );
    value.set_field_visibilities( aSettings.m_FieldVisibilities );
    value.set_field_styles( aSettings.m_FieldStyles );
    value.set_field_positions( aSettings.m_FieldPositions );
    value.set_pin_visibilities( aSettings.m_PinVisibilities );
    value.set_pin_alt_functions( aSettings.m_PinAltFunctions );
    value.set_exclude_from_board_flags( aSettings.m_ExcludeFromBoardFlags );
    value.set_dnp_flags( aSettings.m_DNPFlags );
    value.set_exclude_from_bom_flags( aSettings.m_ExcludeFromBOMFlags );
    value.set_exclude_from_position_file_flags( aSettings.m_ExcludeFromPosFileFlags );
    return value;
}

inline bool Validate( const MESSAGE& aValue, std::string& aFailure )
{
    auto known = aValue;
    known.DiscardUnknownFields();
    if( known.ByteSizeLong() != aValue.ByteSizeLong() )
    {
        aFailure = "Symbol comparison settings contain unsupported fields";
        return false;
    }
    return true;
}

inline void Restore( SYMBOL_PARITY_SETTINGS& aSettings, const MESSAGE& aValue )
{
    aSettings.m_MissingFields = aValue.missing_fields();
    aSettings.m_ExtraFields = aValue.extra_fields();
    aSettings.m_FieldTexts = aValue.field_texts();
    aSettings.m_FieldVisibilities = aValue.field_visibilities();
    aSettings.m_FieldStyles = aValue.field_styles();
    aSettings.m_FieldPositions = aValue.field_positions();
    aSettings.m_PinVisibilities = aValue.pin_visibilities();
    aSettings.m_PinAltFunctions = aValue.pin_alt_functions();
    aSettings.m_ExcludeFromBoardFlags = aValue.exclude_from_board_flags();
    aSettings.m_DNPFlags = aValue.dnp_flags();
    aSettings.m_ExcludeFromBOMFlags = aValue.exclude_from_bom_flags();
    aSettings.m_ExcludeFromPosFileFlags = aValue.exclude_from_position_file_flags();
}
}

namespace SCH_FIELD_TEMPLATES
{
using MESSAGE = kiapi::schematic::types::SchematicFieldTemplates;

inline MESSAGE Capture( TEMPLATES& aTemplates )
{
    MESSAGE value;
    for( const auto& field : aTemplates.GetTemplateFieldNames( TEMPLATES::SCOPE::PROJECT ) )
    {
        auto* entry = value.add_fields();
        entry->set_name( field.m_Name.ToUTF8() );
        entry->set_visible( field.m_Visible );
        entry->set_url( field.m_URL );
    }
    return value;
}

inline void Restore( TEMPLATES& aTemplates, const MESSAGE& aValue )
{
    // Build the replacement off-model. Copy global defaults but replace only
    // PROJECT scope; allocation failure cannot leave half the live list.
    TEMPLATES prepared = aTemplates;
    prepared.DeleteFieldNameTemplates( TEMPLATES::SCOPE::PROJECT );
    for( const auto& entry : aValue.fields() )
    {
        TEMPLATE_FIELDNAME field( wxString::FromUTF8( entry.name() ) );
        field.m_Visible = entry.visible(); field.m_URL = entry.url();
        prepared.AddTemplateFieldName( field, TEMPLATES::SCOPE::PROJECT );
    }
    std::swap( aTemplates, prepared );
}

inline bool Validate( const MESSAGE& aValue, std::string& aFailure )
{
    auto known = aValue;
    known.DiscardUnknownFields();
    if( known.ByteSizeLong() != aValue.ByteSizeLong() )
    {
        aFailure = "Field templates contain unsupported fields";
        return false;
    }
    for( const auto& entry : aValue.fields() )
    {
        if( entry.name().empty() || entry.name().find( '\0' ) != std::string::npos
                || wxString::FromUTF8( entry.name() ).ToStdString( wxConvUTF8 ) != entry.name() )
        {
            aFailure = "Field-template names must be nonempty UTF-8 without NUL";
            return false;
        }
    }
    TEMPLATES prepared;
    Restore( prepared, aValue );
    if( Capture( prepared ).SerializeAsString() != aValue.SerializeAsString() )
    {
        aFailure = "Field templates must preserve unique names and order without reserved symbol fields";
        return false;
    }
    return true;
}
}
#endif

