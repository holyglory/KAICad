/* State-bound schematic mutation dispatch. GPL-3.0-or-later. */
#ifndef KICAD_CHECKED_SCHEMATIC_CONTROLLER_H
#define KICAD_CHECKED_SCHEMATIC_CONTROLLER_H

#include <api/api_handler.h>
#include <api/common/commands/automation_commands.pb.h>
#include <functional>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <vector>

class KICOMMON_API CHECKED_SCHEMATIC_CONTROLLER
{
public:
    using DISPATCH = std::function<API_RESULT( ApiRequest& )>;

    // Sorted full protobuf names of every request this controller owns. Handles() is built
    // from this list, so a capability probe can never disagree with the dispatcher.
    static const std::vector<std::string>& RequestTypes();
    static bool Handles( const ApiRequest& aRequest );
    API_RESULT Handle( ApiRequest& aRequest, const std::string& aProcessEpoch,
                       const DISPATCH& aDispatch );

    // CN-1 §8: exact pin-partition post-condition of an atomic schematic batch. A pin key is
    // the loaded sheet-instance path ("/<uuid>/<uuid>") plus "#" and the placed pin UUID.
    using PIN_GROUP = std::set<std::string>;
    using PIN_PARTITION = std::set<PIN_GROUP>;
    static constexpr const char* CONNECTIVITY_FAILURE = "connectivity_postcondition_failed";

    struct CONNECTIVITY_ASSERTION
    {
        int                    index = -1;    // operation index, or -1 when the batch has none
        std::vector<PIN_GROUP> expected;      // request order, each group nonempty and disjoint
    };

    // Validates every §8.1 admission rule before any operation is applied. aLoadedPath says
    // whether a canonical path names a loaded (not staged) sheet instance. On failure returns
    // the complete rejection, prefixed "Atomic operation N rejected: ".
    static std::optional<std::string> PrepareConnectivityAssertion(
            const kiapi::automation::v1::ApplySchematicItemBatch& aBatch,
            const std::function<bool( const kiapi::common::types::SheetPath& )>& aLoadedPath,
            CONNECTIVITY_ASSERTION& aAssertion );

    // Requires aAfter == { g in aBefore : g does not meet the expected pins } + aExpected.
    // Returns the bounded "connectivity_postcondition_failed: ..." rejection when it does not.
    static std::optional<std::string> CheckConnectivity( const PIN_PARTITION& aBefore,
                                                         const PIN_PARTITION& aAfter,
                                                         const std::vector<PIN_GROUP>& aExpected );

private:
    API_RESULT ReadState( ApiRequest& aRequest, const std::string& aProcessEpoch,
                          const DISPATCH& aDispatch );
    struct RECEIPT
    {
        kiapi::automation::v1::CheckedSchematicBatch request;
        kiapi::automation::v1::CheckedSchematicBatchReceipt result;
    };
    // Process-owned receipts survive editor reattachment. Never evict an ID:
    // a timed-out caller must not accidentally execute the same operation twice.
    std::map<std::string, RECEIPT> m_receipts;
    size_t m_reservedBytes = 0;
};

#endif
