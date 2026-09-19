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

private:
    void updateSelection();

    const std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> m_entries;
    const std::function<void( const std::string& )> m_openSource;
    std::optional<std::string> m_restoreRevision;
    wxListBox* m_history;
    wxStaticText* m_selectedHeading;
    wxTextCtrl* m_selectedText;
    wxButton* m_source;
    wxButton* m_restore;
};

#endif
