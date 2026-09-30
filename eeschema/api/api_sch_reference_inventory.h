/* Persisted reference allocation inventory codec. GPL-3.0-or-later. */
#ifndef API_SCH_REFERENCE_INVENTORY_H
#define API_SCH_REFERENCE_INVENTORY_H

#include <refdes_tracker.h>
#include <schematic_settings.h>
#include <schematic/schematic_types.pb.h>

namespace SCH_REFERENCE_INVENTORY
{
using MESSAGE = kiapi::schematic::types::SchematicReferenceInventory;

inline MESSAGE Capture( const SCHEMATIC_SETTINGS& aSettings )
{
    MESSAGE result;
    if( aSettings.m_refDesTracker )
        for( const std::string& reference : aSettings.m_refDesTracker->GetAllocatedReferences() )
            result.add_allocated( reference );
    return result;
}

inline bool Prepare( const MESSAGE& aValue, REFDES_TRACKER& aPrepared, std::string& aFailure )
{
    auto known = aValue;
    known.DiscardUnknownFields();
    if( known.ByteSizeLong() != aValue.ByteSizeLong() )
    {
        aFailure = "Reference inventory contains unsupported fields";
        return false;
    }
    std::vector<std::string> entries;
    for( const auto& reference : aValue.allocated() )
    {
        if( reference.empty() || reference.find( '\0' ) != std::string::npos
                || wxString::FromUTF8( reference ).ToStdString( wxConvUTF8 ) != reference )
        {
            aFailure = "Allocated references must be nonempty UTF-8 entries without NUL";
            return false;
        }
        entries.push_back( reference );
    }
    if( !aPrepared.ReplaceAllocatedReferences( entries ) )
    {
        aFailure = "Reference inventory must contain unique canonical entries preserved by native save/load";
        return false;
    }
    return true;
}
}
#endif
