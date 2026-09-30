/* Persisted reference-numbering policy codec. GPL-3.0-or-later. */
#ifndef API_SCH_ANNOTATION_H
#define API_SCH_ANNOTATION_H

#include <refdes_tracker.h>
#include <schematic_settings.h>
#include <schematic/schematic_types.pb.h>
#include <string>

namespace SCH_ANNOTATION
{
using MESSAGE = kiapi::schematic::types::SchematicAnnotationSettings;

inline MESSAGE Capture( const SCHEMATIC_SETTINGS& aSettings )
{
    MESSAGE result;
    result.set_start_after( aSettings.m_AnnotateStartNum );
    result.set_order( static_cast<kiapi::schematic::types::SchematicAnnotationOrder>(
            aSettings.m_AnnotateSortOrder + 1 ) );
    result.set_method( static_cast<kiapi::schematic::types::SchematicAnnotationMethod>(
            aSettings.m_AnnotateMethod + 1 ) );
    result.set_reuse_designators( aSettings.m_refDesTracker && aSettings.m_refDesTracker->GetReuseRefDes() );
    return result;
}

inline bool Validate( const MESSAGE& aValue, std::string& aFailure )
{
    auto known = aValue;
    known.DiscardUnknownFields();
    if( known.ByteSizeLong() != aValue.ByteSizeLong() || aValue.order() < 1 || aValue.order() > 2
            || aValue.method() < 1 || aValue.method() > 3 )
    {
        aFailure = "Annotation requires supported ordering and numbering methods without unknown fields";
        return false;
    }
    return true;
}

inline void Restore( SCHEMATIC_SETTINGS& aSettings, const MESSAGE& aValue )
{
    // Preserve the tracker and its used-designator inventory. Changing policy
    // must never reconstruct that history from currently placed symbols.
    if( !aSettings.m_refDesTracker )
        aSettings.m_refDesTracker = std::make_shared<REFDES_TRACKER>();
    aSettings.m_AnnotateStartNum = aValue.start_after();
    aSettings.m_AnnotateSortOrder = aValue.order() - 1;
    aSettings.m_AnnotateMethod = aValue.method() - 1;
    aSettings.m_refDesTracker->SetReuseRefDes( aValue.reuse_designators() );
}
}
#endif
