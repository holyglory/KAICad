/* Owned footprint-library inputs for an isolated DRC invocation. GPL-3.0-or-later. */
#ifndef KICAD_DRC_LIBRARY_INPUTS_H
#define KICAD_DRC_LIBRARY_INPUTS_H

#include <lib_id.h>
#include <map>
#include <memory>
#include <string>
class BOARD;
class FOOTPRINT;
class FOOTPRINT_LIBRARY_ADAPTER;
class PROGRESS_REPORTER;

class DRC_LIBRARY_INPUTS
{
public:
    enum class STATUS { LOADED, MISSING_LIBRARY, DISABLED_LIBRARY, UNAVAILABLE_LIBRARY, UNAVAILABLE_FOOTPRINT };
    struct ENTRY
    {
        STATUS status = STATUS::MISSING_LIBRARY;
        wxString uri;
        std::shared_ptr<const FOOTPRINT> footprint;
    };

    // The caller owns the native capture checkpoint and the matching project adapter.
    // A null result means cancellation; no partial catalogue is returned. This owns
    // content, not an assertion that external files remain unchanged after capture.
    static std::shared_ptr<const DRC_LIBRARY_INPUTS> Capture(
            const BOARD& aBoard, FOOTPRINT_LIBRARY_ADAPTER& aAdapter, PROGRESS_REPORTER* aReporter = nullptr );
    const ENTRY* Find( const LIB_ID& aId ) const;
    size_t Size() const { return m_entries.size(); }
    // Stable across fresh native loads: library serialization omits generated
    // object UUIDs, but preserves every persisted library-definition property.
    std::string ContentFingerprint() const;

private:
    std::map<LIB_ID, ENTRY> m_entries;
};
#endif
