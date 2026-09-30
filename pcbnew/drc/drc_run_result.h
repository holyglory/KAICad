/* Native DRC invocation result, independent of violation count. GPL-3.0-or-later. */
#ifndef KICAD_DRC_RUN_RESULT_H
#define KICAD_DRC_RUN_RESULT_H

enum class DRC_RUN_RESULT
{
    COMPLETED,  // Every provider completed; violations may still exist.
    CANCELLED,
    BUSY,       // No invocation was admitted.
    INCOMPLETE  // Preparation/provider stopped or checked board state changed.
};

#endif
