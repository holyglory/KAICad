/* Preliminary read-only library lifecycle binding. GPL-3.0-or-later. */
#ifndef KICAD_LIBRARY_LIFECYCLE_SNAPSHOT_H
#define KICAD_LIBRARY_LIFECYCLE_SNAPSHOT_H

#include <file_content_baseline.h>
#include <libraries/library_table.h>

#include <api/common/types/base_types.pb.h>

#include <tl/expected.hpp>

#include <string>
#include <vector>


/**
 * The first library lifecycle slice is deliberately read-only.  It binds one detached symbol or
 * footprint definition to the project and library-table row from which it was observed, and
 * records the complete file baselines that were used for the observation.  This type is an
 * internal native contract; it is not an API/protobuf response and therefore cannot accidentally
 * advertise a checked mutation capability before all native writers are covered.
 */
struct KICOMMON_API LIBRARY_LIFECYCLE_SNAPSHOT
{
    kiapi::common::types::DocumentSpecifier document;

    wxString              tablePath;
    LIBRARY_TABLE_SCOPE   tableScope = LIBRARY_TABLE_SCOPE::UNINITIALIZED;
    wxString              nickname;
    wxString              rawUri;
    wxString              expandedUri;
    std::string           provider;
    // The exact options string from the retained library-table row.  Providers must receive the
    // same parsed option map that was observed here; an adapter must not silently substitute
    // defaults while building a detached definition.
    std::string           providerOptions;

    // This is a detached, canonical definition captured after a forced provider refresh.  The
    // snapshot does not own a mutable library object, so a later caller cannot write through it.
    std::string           detachedDefinition;
    // The direct KiCad symbol-file owner retains the exact bytes needed for a bounded rollback.
    // This is never exposed through the read snapshot transport; it remains native transaction
    // evidence and is intentionally empty for unsupported providers.
    std::string           rawLibraryContents;
    bool                  entryExists = true;
    std::vector<FILE_CONTENT_BASELINE> dependencyBaselines;

    bool forcedRefresh = false;
    bool providerObservable = false;
    bool contentSnapshotComplete = false;
    bool diskBaselineChecked = false;
    // Explicit scope for the MCP direct .kicad_sym writer.  This does not claim that every
    // application writer has joined the common lifecycle controller.
    bool mcpDirectSymbolFile = false;
    // Other native writers (symbol editor, import/rescue and remote jobs) are not yet joined to
    // this owner.  Keep this false until a shared mutation event and rollback proof exist.
    bool completeChangeTracking = false;
    // No native write is performed by this slice, so no post-write verification is claimed.
    bool postverify = false;
};


enum class LIBRARY_LIFECYCLE_ERROR_CODE
{
    INVALID_DOCUMENT,
    PROJECT_MISMATCH,
    INVALID_TABLE_ROW,
    UNKNOWN_PROVIDER,
    UNSUPPORTED_PROVIDER,
    UNSUPPORTED_DEPENDENCY,
    INCOMPLETE_SNAPSHOT,
    INCOMPLETE_BASELINE,
    MUTATION_NOT_AVAILABLE
};


struct KICOMMON_API LIBRARY_LIFECYCLE_ERROR
{
    LIBRARY_LIFECYCLE_ERROR_CODE code;
    std::string                  message;
};


using LIBRARY_LIFECYCLE_RESULT = tl::expected<void, LIBRARY_LIFECYCLE_ERROR>;


/**
 * Validate the read-only contract against the project that owns the native API instance.
 *
 * The expected project name and directory are supplied by the native owner rather than copied
 * from the request.  A partial digest, cursor or modify hash is never sufficient: every
 * dependency must carry a known FILE_CONTENT_BASELINE and the detached definition must be
 * present after a forced refresh.
 */
KICOMMON_API LIBRARY_LIFECYCLE_RESULT ValidateLibraryLifecycleSnapshot(
        const LIBRARY_LIFECYCLE_SNAPSHOT& aSnapshot, const wxString& aExpectedProjectName,
        const wxString& aExpectedProjectPath );


/**
 * Checked mutation is intentionally unavailable in this foundation slice.  Keep this explicit
 * so a future caller cannot mistake a valid read snapshot for permission to overwrite a library.
 */
KICOMMON_API LIBRARY_LIFECYCLE_RESULT AdmitLibraryLifecycleMutation(
        const LIBRARY_LIFECYCLE_SNAPSHOT& aSnapshot, const wxString& aExpectedProjectName,
        const wxString& aExpectedProjectPath );

#endif // KICAD_LIBRARY_LIFECYCLE_SNAPSHOT_H
