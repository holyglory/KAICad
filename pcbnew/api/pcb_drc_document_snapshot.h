/* Detached document inputs for background DRC. GPL-3.0-or-later. */
#ifndef KICAD_PCB_DRC_DOCUMENT_SNAPSHOT_H
#define KICAD_PCB_DRC_DOCUMENT_SNAPSHOT_H

#include <kiid.h>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
class BOARD;
class PROJECT;
class ZONE;

/**
 * Own a board and its current project settings without borrowing live editor data.
 * This is the document portion only: rule files, libraries, drawing-sheet data
 * and schematic parity inputs are captured separately (PCB_DRC_RUN_INPUTS).
 *
 * The copy keeps the identity of every object of the source board, so a finding
 * names the object a person or agent can select in the open board. KiCad writes
 * teardrops without an identity, so the copy takes each teardrop's identity from
 * the source teardrop with the same layer, net, kind and outline. The identity
 * gaps that remain, if any, are counted instead of being hidden.
 */
class PCB_DRC_DOCUMENT_SNAPSHOT
{
public:
    // Objects of the source board without an object of the same identity in the
    // copy (missing), objects of the copy without one in the source (unexpected),
    // and identities that more than one source object holds (shared).
    struct IDENTITY_GAP
    {
        size_t missing = 0;
        size_t unexpected = 0;
        size_t shared = 0;
        bool Empty() const { return !missing && !unexpected && !shared; }
    };

    ~PCB_DRC_DOCUMENT_SNAPSHOT();
    static std::unique_ptr<PCB_DRC_DOCUMENT_SNAPSHOT> Capture( BOARD& aBoard );
    BOARD& GetBoard() const;
    int SourceSequence() const { return m_sourceSequence; }

    // Every identity the source board held at capture: the board itself and every
    // object and child object except DRC markers, which are not part of the design.
    const std::set<KIID>& SourceIdentities() const { return m_identities; }
    const IDENTITY_GAP& IdentityGap() const { return m_gap; }
    // The source teardrop with the same layer, net, kind and outline as this
    // teardrop of the copy, as the source held it at capture.
    std::optional<KIID> SourceTeardrop( const ZONE& aTeardrop ) const;

private:
    PCB_DRC_DOCUMENT_SNAPSHOT() = default;
    int m_sourceSequence = 0;
    std::set<KIID> m_identities;
    IDENTITY_GAP m_gap;
    std::multimap<std::string, KIID> m_teardrops;
    // Destroy the board first: its nested settings still refer to the project.
    std::unique_ptr<PROJECT> m_project;
    std::unique_ptr<BOARD> m_board;
};
#endif
