/* Owned, identity-bound native schematic input for PCB checks. GPL-3.0-or-later. */
#ifndef KICAD_PCB_DRC_SCHEMATIC_INPUT_H
#define KICAD_PCB_DRC_SCHEMATIC_INPUT_H
#include <api/common/commands/automation_commands.pb.h>
#include <memory>
#include <string>

class NETLIST;

class PCB_DRC_SCHEMATIC_INPUT
{
public:
    ~PCB_DRC_SCHEMATIC_INPUT();
    static std::unique_ptr<PCB_DRC_SCHEMATIC_INPUT> Capture(
            const kiapi::automation::v1::SchematicParityNetlistSnapshot& aSnapshot,
            const kiapi::automation::v1::DocumentLifecycleState& aExpected,
            const kiapi::common::types::DocumentSpecifier& aBoard,
            const std::string& aProcessEpoch );
    NETLIST& Netlist() const;
    const kiapi::automation::v1::DocumentLifecycleState& Source() const { return m_source; }
    bool Matches( const kiapi::automation::v1::DocumentLifecycleState& aCurrent ) const;

private:
    PCB_DRC_SCHEMATIC_INPUT() = default;
    kiapi::automation::v1::DocumentLifecycleState m_source;
    std::unique_ptr<NETLIST> m_netlist;
};
#endif
