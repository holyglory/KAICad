/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#ifndef KICAD_DIALOG_DIAGRAM_FIELD_HISTORY_H
#define KICAD_DIALOG_DIAGRAM_FIELD_HISTORY_H

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
};

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

private:
    void updateSelection();
    void updatePaging();
    void appendRows( const std::vector<DIAGRAM_FIELD_HISTORY_ENTRY>& aEntries );

    std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> m_entries;
    const std::function<void( const std::string& )> m_openSource;
    std::function<void( size_t )> m_loadOlder;
    size_t m_total = 0;
    bool m_loading = false;
    std::optional<std::string> m_restoreRevision;
    wxListBox* m_history;
    wxStaticText* m_pageStatus;
    wxStaticText* m_pageError;
    wxButton* m_older;
    wxStaticText* m_selectedHeading;
    wxTextCtrl* m_selectedText;
    wxButton* m_source;
    wxButton* m_restore;
};

#endif
