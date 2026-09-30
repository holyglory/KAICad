/*
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#pragma once

#include <api/native_state_digest.h>
#include <connection_graph.h>
#include <kiid.h>

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
     * Capture only @a aScreens, and the project settings when @a aWithProjectSettings is set.
     * For an owner that provably changes nothing else; every such use is reviewed by the
     * change-tracking oracle.  Throws when a group cannot be written.
     */
    static SCH_STATE_GROUPS CaptureScreens( SCHEMATIC& aSchematic,
                                            const std::vector<const SCH_SCREEN*>& aScreens,
                                            bool aWithProjectSettings = false );

    bool operator==( const SCH_STATE_GROUPS& aOther ) const { return m_groups == aOther.m_groups; }
    bool operator!=( const SCH_STATE_GROUPS& aOther ) const { return !( *this == aOther ); }

    /// Names of the groups that were added, removed or changed in @a aAfter.
    std::vector<std::string> ChangedGroups( const SCH_STATE_GROUPS& aAfter ) const;

    /// The whole-document digest ("kicad-native-state-v1") over every captured group.
    std::string DocumentSha256() const { return m_document.Hex(); }

    /**
     * The whole-document digest with the project settings taken without the entries every
     * save writes from the schematic itself: the sheet list, the top-level sheet list, the
     * root sheet's revision kept for IPC-2581 and the project file name.  Saving writes
     * nothing else into the project settings, so a save leaves this digest unchanged even
     * when it rewrites a stale sheet list (ReadDocumentLifecycleState.save_stable_state_sha256).
     * Empty when the project settings were not captured.
     */
    std::string SaveStableSha256() const { return m_projectSettings ? m_saveStable.Hex() : std::string(); }

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
    void add( const std::string& aName, const NATIVE_STATE_DIGEST& aState,
              const NATIVE_STATE_DIGEST& aSaveStable );

    std::map<std::string, std::string> m_groups;     ///< Group name to byte count and digest.
    NATIVE_DOCUMENT_DIGEST             m_document;
    NATIVE_DOCUMENT_DIGEST             m_saveStable; ///< The same groups, save-derived entries left out.
    bool                               m_projectSettings = false;
    std::vector<SCH_SHEET*>            m_sheets;
    uint64_t                           m_bytes = 0;
};


/**
 * Named parts of the persisted state, for an owner that can reach nothing else outside a commit.
 * Each part is taken exactly as the schematic writer saves it, so comparing the parts costs what
 * the parts cost, not what the design costs, and a part whose saved form is unchanged is never
 * reported as changed.  The project settings are always compared (the same group, computed the
 * same way, as the lifecycle digest).
 */
struct SCH_PERSISTED_PARTS
{
    /// Every loaded screen's paper and title block, as each screen saves them ("(paper" and
    /// "(title_block").
    bool pages = false;

    /// What the first top-level sheet saves once for the whole schematic: the embedded fonts
    /// flag, the embedded files and the net chains.
    bool schematicWide = false;

    /// The identity of every saved item on every loaded screen.  Renumbering an item changes its
    /// identity in place, and an item left behind adds one; the items themselves are not written.
    bool identities = false;

    /// Screens whose cached library definitions ("(lib_symbols") are compared.
    std::vector<const SCH_SCREEN*> libraryCaches;

    /**
     * Page Settings.  The dialog sets the current screen's paper and title block and, when asked
     * to export them, those of every other screen; it sets the drawing sheet file name (a project
     * setting) and adds and removes the embedded drawing sheet (schematic embedded files).  It
     * writes no item, library cache or other screen state, so these parts are all it can change.
     */
    static SCH_PERSISTED_PARTS PageSettings();

    /**
     * Import Sheet and design block placement into @a aScreen.  Every placed item is staged in
     * the placement commit, which a cancel reverts; what loading the file changes outside that
     * commit is limited to: the identity of an existing item on any screen that a loaded item
     * duplicates (renumbered), @a aScreen's cached library definitions (merged, and refreshed in
     * place when equal), the schematic-wide embedded files, embedded fonts flag and net chains
     * the loaded file carries, and the project's bus aliases and reference inventory (annotating
     * the placed symbols).  Loaded child sheets get screens of their own that leave with their
     * placed sheet.
     */
    static SCH_PERSISTED_PARTS SheetImport( const SCH_SCREEN* aScreen );
};


/**
 * A snapshot of the named parts of the persisted state (SCH_PERSISTED_PARTS).  Unlike the
 * whole-state groups it keeps each part's saved form (or item identities and net chain
 * definitions) rather than a digest, because the parts are small and hashing would cost more
 * than comparing them.
 */
class SCH_PERSISTED_PARTS_STATE
{
public:
    /// Capture @a aParts of @a aSchematic.  Throws when a part cannot be written or a named
    /// screen is not part of the schematic.
    static SCH_PERSISTED_PARTS_STATE Capture( SCHEMATIC& aSchematic, const SCH_PERSISTED_PARTS& aParts );

    bool operator==( const SCH_PERSISTED_PARTS_STATE& aOther ) const;
    bool operator!=( const SCH_PERSISTED_PARTS_STATE& aOther ) const { return !( *this == aOther ); }

    /// Names of the parts that were added, removed or changed in @a aAfter.
    std::vector<std::string> ChangedParts( const SCH_PERSISTED_PARTS_STATE& aAfter ) const;

    /// Bytes kept by the snapshot; the size of what one capture writes.
    uint64_t Bytes() const { return m_bytes; }

private:
    std::map<std::string, std::string>       m_texts;       ///< Part name to its saved form.
    std::map<std::string, std::vector<KIID>> m_identities;  ///< Screen identity to sorted item identities.
    std::optional<std::map<wxString, CONNECTION_GRAPH::NET_CHAIN_DEFINITION>> m_netChains;
    uint64_t                                 m_bytes = 0;
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
 * Four forms, each reviewed by the change-tracking oracle by its argument list:
 *  - two arguments: compares the whole persisted state (every screen and the project
 *    settings).  Only for edits whose reach outside a commit no narrower form covers, because
 *    each capture writes the whole design;
 *  - three arguments, a commit: an owner whose every persisted edit is staged in one
 *    SCH_COMMIT.  Only the staged items are compared with the copies the commit saved, so the
 *    cost follows the edit, not the size of the design.  Declare it after the commit.  An edit
 *    the owner makes outside the commit is counted only when it reports it
 *    (ChangedOutsideCommit());
 *  - three arguments, SCH_PERSISTED_PARTS: named parts of the persisted state and the project
 *    settings, for an owner whose reach outside its commits is exactly those parts (Page
 *    Settings, sheet import);
 *  - four arguments: named screens and the project settings, for an owner that provably
 *    changes nothing else.
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
     * Track only @a aParts and the project settings from now on.  Only for an owner whose every
     * persisted edit outside a commit lies in those parts, and whose commits either record
     * themselves when pushed or are reverted exactly; the change-tracking oracle reviews every
     * such use.  A part that cannot be captured counts as changed.
     */
    SCH_TRACKED_CHANGE( SCHEMATIC& aSchematic, std::string aDescription, SCH_PERSISTED_PARTS aParts );

    /**
     * Track only @a aScreens and the project settings, as part of a user action that began
     * at @a aSince.  When a commit was recorded since that mark the action is already a
     * revision, so nothing is captured or recorded again.  Only for owners that provably
     * change nothing but these screens and the project settings; the change-tracking oracle
     * reviews every such use.
     */
    SCH_TRACKED_CHANGE( SCHEMATIC& aSchematic, std::string aDescription,
                        std::vector<const SCH_SCREEN*> aScreens, const MARK& aSince );

    ~SCH_TRACKED_CHANGE();

    SCH_TRACKED_CHANGE( const SCH_TRACKED_CHANGE& ) = delete;
    SCH_TRACKED_CHANGE& operator=( const SCH_TRACKED_CHANGE& ) = delete;

    /**
     * Report a persisted change the owner made outside its staged commit and detected itself,
     * such as a sheet's screen being renamed or replaced.  Completion then counts the action
     * as changed, and records it once, even when every staged item compares unchanged.  Only
     * for the staged form; the other forms compare everything they may change.
     */
    void ChangedOutsideCommit();

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
    std::optional<SCH_STATE_GROUPS>          capture() const;
    std::optional<SCH_PERSISTED_PARTS_STATE> captureParts() const;
    bool                                     replaced() const;
    bool                                     recordedSinceStart() const;
    bool                                     changedSinceStart();
    void                                     recordIfUntracked( bool aChanged );

    SCHEMATIC&                               m_schematic;
    std::string                              m_description;
    std::vector<const SCH_SCREEN*>           m_screens;       ///< Empty: the whole persisted state.
    SCH_COMMIT*                              m_commit = nullptr; ///< Staged form: the compared commit.
    std::optional<SCH_PERSISTED_PARTS>       m_parts;         ///< Parts form: the compared parts.
    MARK                                     m_start;
    std::optional<SCH_STATE_GROUPS>          m_before;
    std::optional<SCH_PERSISTED_PARTS_STATE> m_partsBefore;
    bool                                     m_changedOutsideCommit = false;
    bool                                     m_complete = false;
};
