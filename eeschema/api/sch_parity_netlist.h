/* Non-interactive native schematic comparison input. GPL-3.0-or-later. */
#ifndef KICAD_SCH_PARITY_NETLIST_H
#define KICAD_SCH_PARITY_NETLIST_H

#include <stdexcept>
#include <string>
#include <vector>

class SCHEMATIC;
class KIWAY;
class OUTPUTFORMATTER;

enum class SCH_PARITY_INPUT_STATUS
{
    NOT_INITIALIZED,
    EDIT_IN_PROGRESS,
    MISSING_SYMBOL_DEFINITION,
    ANNOTATION_REQUIRED,
    DUPLICATE_SHEET_NAMES,
    CONNECTIVITY_PENDING
};

class SCH_PARITY_INPUT_ERROR : public std::runtime_error
{
public:
    SCH_PARITY_INPUT_ERROR( SCH_PARITY_INPUT_STATUS aStatus, const std::string& aMessage ) :
            std::runtime_error( aMessage ), m_status( aStatus ) {}
    SCH_PARITY_INPUT_STATUS Status() const { return m_status; }
private:
    SCH_PARITY_INPUT_STATUS m_status;
};

// Requires an uninterrupted native checkpoint, with no staged API transaction.
// Never annotates, recalculates connectivity, saves files or opens dialogs.
// Returns retained warnings; failure produces no usable comparison snapshot.
std::vector<std::string> FormatSchematicParityNetlist(
        SCHEMATIC& aSchematic, OUTPUTFORMATTER& aOutput,
        bool aAllowDuplicateSheetNames = false, KIWAY* aKiway = nullptr );

#endif
