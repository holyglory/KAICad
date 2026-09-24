/*
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#pragma once

#include <api/native_state_digest.h>

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <vector>

class SCHEMATIC;
class SCH_COMMIT;
class SCH_ITEM;
class SCH_SCREEN;
class SCH_SHEET;
class wxString;

/**
 * Digests of the persisted state of an open schematic, grouped the way it is saved: one
 * group for every loaded screen exactly as SCH_IO_KICAD_SEXPR writes it, and one group for
 * the project settings.
 *
 * This is the only definition of the native schematic state.  The lifecycle state digest
 * that automation clients compare (ReadDocumentLifecycleState.state_sha256) is computed
 * from these groups, so a tracked owner decides "changed" from exactly the state a client
 * observes.  Only digests are kept, never design or settings text.
 */
class SCH_STATE_GROUPS
{
public:
    /// Capture every screen and the project settings.  Throws when a group cannot be written.
    static SCH_STATE_GROUPS Capture( SCHEMATIC& aSchematic );

    /**
     * Capture only @a aScreens.  For an owner that provably changes nothing else; every
     * such use is reviewed by the change-tracking oracle.  Throws when a group cannot be
     * written.
     */
    static SCH_STATE_GROUPS CaptureScreens( SCHEMATIC& aSchematic,
                                            const std::vector<const SCH_SCREEN*>& aScreens );

    bool operator==( const SCH_STATE_GROUPS& aOther ) const { return m_groups == aOther.m_groups; }
    bool operator!=( const SCH_STATE_GROUPS& aOther ) const { return !( *this == aOther ); }

    /// Names of the groups that were added, removed or changed in @a aAfter.
    std::vector<std::string> ChangedGroups( const SCH_STATE_GROUPS& aAfter ) const;

    /// The whole-document digest ("kicad-native-state-v1") over every captured group.
    std::string DocumentSha256() const { return m_document.Hex(); }

    /// The sheet each captured screen was written through, ordered by screen identity.
    const std::vector<SCH_SHEET*>& WrittenSheets() const { return m_sheets; }

    /// Bytes serialised to produce the digests; the cost of one capture.
    uint64_t Bytes() const { return m_bytes; }

    /**
     * The saved form of one placed item, exactly as SCH_IO_KICAD_SEXPR writes it into its
     * screen, followed for a symbol by its own library definition, which the screen's cached
     * definition follows.  Comparing an item with the copy a commit staged for it needs no
     * capture of the rest of the design.  Empty when the writer has no form for the item's
     * type; callers treat that as changed.
     */
    static std::string PersistedItem( SCHEMATIC& aSchematic, SCH_ITEM* aItem );

private:
    void add( const std::string& aName, const NATIVE_STATE_DIGEST& aState );

    std::map<std::string, std::string> m_groups;     ///< Group name to byte count and digest.
    NATIVE_DOCUMENT_DIGEST             m_document;
    std::vector<SCH_SHEET*>            m_sheets;
    uint64_t                           m_bytes = 0;
};


/**
 * One native owner that can change persisted schematic state.
 *
 * Construct it before the owner changes anything and call Complete() (or PushOrRevert())
 * after the owner's own refresh.  A committed journal change is recorded only when the
 * persisted state really changed and no commit inside the owner already recorded it, so
 * cancelled, rejected and unchanged operations never become revisions.  The destructor
 * completes an owner that returned early.
 *
 * Three forms, each reviewed by the change-tracking oracle by its argument count:
 *  - two arguments: compares the whole persisted state (every screen and the project
 *    settings).  Only for edits made outside a commit, because each capture writes the
 *    whole design;
 *  - three arguments: an owner whose every persisted edit is staged in one SCH_COMMIT.
 *    Only the staged items are compared with the copies the commit saved, so the cost
 *    follows the edit, not the size of the design.  Declare it after the commit;
 *  - four arguments: named screens only, for an owner that provably changes nothing else.
 */
class SCH_TRACKED_CHANGE
{
public:
    /// A position in the schematic's change journal.
    struct MARK
    {
        std::string epoch;
        uint64_t    sequence = 0;
    };

    static MARK Mark( const SCHEMATIC& aSchematic );

    /// Track the whole persisted state from now on.
    SCH_TRACKED_CHANGE( SCHEMATIC& aSchematic, std::string aDescription );

    /**
     * Track an owner whose every persisted edit is staged in @a aCommit.  Nothing is
     * captured: completion compares only the staged items with the commit's saved copies
     * (see SCH_COMMIT::PersistsChange).  @a aCommit must outlive this tracker.
     */
    SCH_TRACKED_CHANGE( SCHEMATIC& aSchematic, std::string aDescription, SCH_COMMIT& aCommit );

    /**
     * Track only @a aScreens, as part of a user action that began at @a aSince.  When a
     * commit was recorded since that mark the action is already a revision, so nothing is
     * captured or recorded again.  Only for owners that provably change nothing but these
     * screens; the change-tracking oracle reviews every such use.
     */
    SCH_TRACKED_CHANGE( SCHEMATIC& aSchematic, std::string aDescription,
                        std::vector<const SCH_SCREEN*> aScreens, const MARK& aSince );

    ~SCH_TRACKED_CHANGE();

    SCH_TRACKED_CHANGE( const SCH_TRACKED_CHANGE& ) = delete;
    SCH_TRACKED_CHANGE& operator=( const SCH_TRACKED_CHANGE& ) = delete;

    /**
     * Finish tracking.  Returns true when the persisted state changed, whether this call
     * or a commit inside the owner recorded it; callers mark the document modified only
     * then.  A replaced document (new journal epoch) was not edited by this owner: nothing
     * is recorded and false is returned, so it is not marked modified either.
     */
    bool Complete();

    /**
     * Finish an owner that staged its edit in @a aCommit.  A real change is pushed (and
     * recorded once even if it lies outside the commit); an unchanged confirmation reverts
     * the commit, so it leaves no undo entry, modified flag or revision.
     *
     * When the document was replaced meanwhile (new journal epoch) nothing is pushed and
     * false is returned.  Replacing the document freed the items the commit staged, so the
     * commit is abandoned: its saved copies are dropped without applying them to, or
     * restoring, the freed items.
     */
    bool PushOrRevert( SCH_COMMIT& aCommit, const wxString& aMessage, int aCommitFlags = 0 );

private:
    std::optional<SCH_STATE_GROUPS> capture() const;
    bool                            replaced() const;
    bool                            recordedSinceStart() const;
    bool                            changedSinceStart();
    void                            recordIfUntracked( bool aChanged );

    SCHEMATIC&                      m_schematic;
    std::string                     m_description;
    std::vector<const SCH_SCREEN*>  m_screens;       ///< Empty: the whole persisted state.
    SCH_COMMIT*                     m_commit = nullptr; ///< Staged form: the compared commit.
    MARK                            m_start;
    std::optional<SCH_STATE_GROUPS> m_before;
    bool                            m_complete = false;
};
