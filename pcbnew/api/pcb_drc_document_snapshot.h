/* Detached document inputs for background DRC. GPL-3.0-or-later. */
#ifndef KICAD_PCB_DRC_DOCUMENT_SNAPSHOT_H
#define KICAD_PCB_DRC_DOCUMENT_SNAPSHOT_H

#include <memory>
class BOARD;
class PROJECT;

/**
 * Own a board and its current project settings without borrowing live editor data.
 * This is the document portion only: rule files, libraries, drawing-sheet data
 * and schematic parity inputs must be captured separately before claiming full DRC.
 */
class PCB_DRC_DOCUMENT_SNAPSHOT
{
public:
    ~PCB_DRC_DOCUMENT_SNAPSHOT();
    static std::unique_ptr<PCB_DRC_DOCUMENT_SNAPSHOT> Capture( BOARD& aBoard );
    BOARD& GetBoard() const;
    int SourceSequence() const { return m_sourceSequence; }

private:
    PCB_DRC_DOCUMENT_SNAPSHOT() = default;
    int m_sourceSequence = 0;
    // Destroy the board first: its nested settings still refer to the project.
    std::unique_ptr<PROJECT> m_project;
    std::unique_ptr<BOARD> m_board;
};
#endif
