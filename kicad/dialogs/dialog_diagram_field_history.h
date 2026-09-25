/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#ifndef KICAD_DIALOG_DIAGRAM_FIELD_HISTORY_H
#define KICAD_DIALOG_DIAGRAM_FIELD_HISTORY_H

#include <api/common/types/diagram_revision_types.pb.h>
#include <dialog_shim.h>
#include <functional>
#include <optional>
#include <string>
#include <vector>

class wxButton;
class wxListBox;
class wxStaticText;
class wxTextCtrl;

struct DIAGRAM_FIELD_HISTORY_ENTRY
{
    std::string revisionId;
    wxString revisionLabel;
    wxString actor;
    wxString text;
    wxString sourceDescription;
    bool saved = false;
    /// The name of the earlier implementation the row was saved in, empty for a row of the implementation shown; the
    /// revision label then reads "name · vN".
    wxString implementation;
};

/** The rows of one field-history page, newest first. An implementation made from another one continues that
 * implementation's field history, and a row's version counts the diagram revisions of the implementation it was saved
 * in. A row saved in an earlier implementation therefore names that implementation ("Initial approach · v3") so it is
 * not mistaken for a version of this one; a row of this implementation shows its version alone ("v3").
 * @param aConnection true for a connection's or member's history, false for a block's. */
std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> DiagramFieldHistoryRows(
        const kiapi::automation::diagrams::v1::RecursiveBlockGraphData& aGraph,
        const kiapi::automation::diagrams::v1::FieldHistoryPageData& aPage, bool aConnection );

/** Read-only history comparison. Accepting returns an exact source revision for
 * the caller's draft; this dialog never writes a file or starts an AI agent. */
class DIALOG_DIAGRAM_FIELD_HISTORY : public DIALOG_SHIM
{
public:
    DIALOG_DIAGRAM_FIELD_HISTORY( wxWindow* aParent, const wxString& aFieldLabel,
            const wxString& aOwnerPath, const wxString& aSavedRevisionLabel,
            const wxString& aSavedText, std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> aEntries,
            std::function<void( const std::string& )> aOpenSource = {} );

    const std::optional<std::string>& RestoreRevision() const { return m_restoreRevision; }
    const DIAGRAM_FIELD_HISTORY_ENTRY* RestoredEntry() const;

    // Pages append to the exact comparison opened by the caller. A failed or
    // cancelled read must never clear inspected rows or select another revision.
    void ConfigurePaging( size_t aTotal, std::function<void( size_t )> aLoadOlder );
    bool AppendPage( size_t aOffset, size_t aTotal,
                     std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> aEntries );
    void PageFailed( const wxString& aMessage );
    size_t LoadedCount() const { return m_entries.size(); }
    size_t TotalCount() const { return m_total; }
    bool IsLoading() const { return m_loading; }
    std::string InspectedRevision() const;
    wxString PageError() const;
    /// Every loaded row as the list shows it: its revision label, the saved marker and the author.
    std::vector<wxString> RowLabels() const;

    /// Lays the dialog out and then fits the selected row's heading to the width its column got.
    bool Layout() override;

private:
    void updateSelection();
    void fitHeading();
    void updatePaging();
    void appendRows( const std::vector<DIAGRAM_FIELD_HISTORY_ENTRY>& aEntries );

    std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> m_entries;
    const std::function<void( const std::string& )> m_openSource;
    std::function<void( size_t )> m_loadOlder;
    size_t m_total = 0;
    bool m_loading = false;
    bool m_showPageCount = false;
    std::optional<std::string> m_restoreRevision;
    wxListBox* m_history;
    wxStaticText* m_pageStatus;
    wxStaticText* m_pageError;
    wxButton* m_older;
    wxStaticText* m_selectedHeading = nullptr;
    // The selected row's heading in two parts: the earlier implementation's name (possibly empty) and "vN · Author —
    // Selected text". Only the name is shortened when the heading is too narrow, so the version and author stay readable.
    wxString m_headingImplementation;
    wxString m_headingRest;
    wxTextCtrl* m_selectedText;
    wxButton* m_source;
    wxButton* m_restore;
};

#endif
