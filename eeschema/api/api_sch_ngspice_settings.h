/* Typed ngspice project settings. GPL-3.0-or-later. */
#ifndef API_SCH_NGSPICE_SETTINGS_H
#define API_SCH_NGSPICE_SETTINGS_H

#include <sim/spice_settings.h>
#include <schematic/schematic_types.pb.h>

#include <string>

namespace SCH_NGSPICE_SETTINGS
{
using MESSAGE = kiapi::schematic::types::SchematicNgspiceSettings;

inline MESSAGE Capture( const NGSPICE_SETTINGS& aSettings )
{
    MESSAGE value;
    value.set_workbook_filename( aSettings.GetWorkbookFilename().ToStdString( wxConvUTF8 ) );
    value.set_fix_include_paths( aSettings.GetFixIncludePaths() );
    value.set_model_mode( static_cast<int>( aSettings.GetCompatibilityMode() ) );
    return value;
}

inline bool Validate( const MESSAGE& aValue, std::string& aFailure )
{
    auto known = aValue;
    known.DiscardUnknownFields();
    if( known.ByteSizeLong() != aValue.ByteSizeLong() )
    {
        aFailure = "ngspice settings contain unsupported fields";
        return false;
    }
    if( aValue.workbook_filename().find( '\0' ) != std::string::npos )
    {
        aFailure = "ngspice workbook filename cannot contain NUL";
        return false;
    }
    if( aValue.model_mode() < static_cast<int>( NGSPICE_COMPATIBILITY_MODE::USER_CONFIG )
            || aValue.model_mode() > static_cast<int>( NGSPICE_COMPATIBILITY_MODE::HSPICE ) )
    {
        aFailure = "ngspice model mode is unsupported";
        return false;
    }
    return true;
}
}

#endif
