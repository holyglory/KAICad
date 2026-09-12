/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright (C) 2016 CERN
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * @author Tomasz Wlostowski <tomasz.wlostowski@cern.ch>
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU General Public License
 * as published by the Free Software Foundation; either version 2
 * of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

#pragma once

#include <commit.h>
#include <memory>
#include <map>
#include <optional>
#include <array>
#include <set>
#include <nlohmann/json_fwd.hpp>
#include <connection_graph.h>

class EMBEDDED_FILES;
namespace kiapi::schematic::types { class SchematicFormattingSettings; }
namespace kiapi::schematic::types { class SchematicAnnotationSettings; }
class BUS_ALIAS;
class SCH_EMBEDDED_FILES_UNDO_ITEM;
class SCH_PAGE_SETTINGS_UNDO_ITEM;
class SCH_MARKER;
namespace SCH_ERC_SETTINGS { struct PREPARED; }
class SCH_LIBRARY_CACHE_UNDO_ITEM;
class SCH_SYMBOL_CACHE_EDIT_SCOPE;
class SCH_SYMBOL_CACHE_STATE;
class SCH_SHEET;
class SCH_SCREEN;
class SCH_SYMBOL;
class TITLE_BLOCK;
class PAGE_INFO;

class PICKED_ITEMS_LIST;
class TOOL_MANAGER;
class SCH_EDIT_FRAME;
class SCH_BASE_FRAME;
class EDA_DRAW_FRAME;
class TOOL_BASE;

template<class T>
class SCH_TOOL_BASE;

#define SKIP_UNDO          0x0001
#define APPEND_UNDO        0x0002
#define SKIP_SET_DIRTY     0x0004

class SCH_COMMIT : public COMMIT
{
public:
    SCH_COMMIT( TOOL_MANAGER* aToolMgr );
    SCH_COMMIT( EDA_DRAW_FRAME* aFrame );
    SCH_COMMIT( SCH_TOOL_BASE<SCH_BASE_FRAME>* aFrame );

    virtual ~SCH_COMMIT();

    virtual void Push( const wxString& aMessage = wxT( "A commit" ), int aCommitFlags = 0 ) override;

    virtual void Revert() override;
    bool Empty() const override;
    void SetAutomationOrigin( const std::string& aOriginId, const std::string& aOperationId )
    {
        m_originId = aOriginId;
        m_operationId = aOperationId;
    }
    // Candidate has already been decoded and validated. Snapshot once, then
    // include asset replacement in the same undo/cancellation as item edits.
    void ReplaceEmbeddedFiles( EMBEDDED_FILES& aCandidate );
    void CaptureLibraryCache( SCH_SCREEN& aScreen );
    void ReplaceLibraryCache( SCH_SCREEN& aScreen, SCH_SYMBOL_CACHE_STATE& aCandidate );
    bool ValidateLibraryCaches( wxString& aFailure );
    void SetRootInstance( SCH_SHEET* aSheet, const std::optional<wxString>& aPageNumber );
    void SetTitleBlock( SCH_SCREEN* aScreen, const TITLE_BLOCK& aTitle );
    void SetBusAliases( const std::vector<std::shared_ptr<BUS_ALIAS>>& aAliases );
    void SetTextVariables( const std::map<wxString, wxString>& aVariables );
    void SetSetupSettings( const nlohmann::json& aBefore, const nlohmann::json& aAfter );
    void SetVariantDescription( const wxString& aName, const wxString& aDescription );
    void StageVariantRegistry();
    void SetDrawingRatios( const std::array<double, 5>& aRatios );
    void SetFormatting( const kiapi::schematic::types::SchematicFormattingSettings& aFormatting );
    void SetAnnotation( const kiapi::schematic::types::SchematicAnnotationSettings& aAnnotation );
    bool SetErcSettings( SCH_ERC_SETTINGS::PREPARED& aPrepared, std::string& aFailure );
    void SetVariantRegistry( const std::map<wxString, wxString>& aDescriptions );
    // Stage graph declarations and the exact affected symbols before a native
    // net-chain action. Shared screens are captured once; foreign owners fail.
    bool StageNetChainEdit( const std::set<SCH_SYMBOL*>& aSymbols );
    void SetNetChainDefinitions( const std::map<wxString, CONNECTION_GRAPH::NET_CHAIN_DEFINITION>& aDefinitions );
    bool SetNetChainClasses( const std::set<wxString>& aDefinitions,
                            const std::map<wxString, wxString>& aAssignments );
    void SetPageSettings( SCH_SCREEN* aScreen, const PAGE_INFO& aPage,
                          const wxString& aDrawingSheet, const wxString& aPreparedLayout );
    COMMIT& Stage( EDA_ITEM *aItem, CHANGE_TYPE aChangeType, BASE_SCREEN *aScreen = nullptr,
                   RECURSE_MODE aRecurse = RECURSE_MODE::NO_RECURSE ) override;
    COMMIT& Stage( std::vector<EDA_ITEM*> &container, CHANGE_TYPE aChangeType,
                   BASE_SCREEN *aScreen = nullptr ) override;

private:
    std::string m_originId;
    std::string m_operationId;
    std::vector<std::unique_ptr<SCH_MARKER>> m_ercAddedMarkers;
    EDA_ITEM* undoLevelItem( EDA_ITEM* aItem ) const override;

    EDA_ITEM* makeImage( EDA_ITEM* aItem ) const override;

    void pushLibEdit(  const wxString& aMessage, int aCommitFlags );
    void pushSchEdit(  const wxString& aMessage, int aCommitFlags );

    void revertLibEdit();

private:
    TOOL_MANAGER*  m_toolMgr;
    bool           m_isLibEditor;
    std::unique_ptr<SCH_EMBEDDED_FILES_UNDO_ITEM> m_embeddedFilesUndo;
    std::unique_ptr<SCH_PAGE_SETTINGS_UNDO_ITEM> m_pageSettingsUndo;
    std::map<SCH_SCREEN*, std::unique_ptr<SCH_LIBRARY_CACHE_UNDO_ITEM>> m_libraryCacheUndo;
    // Destroy scopes before the undo items that retain the screen lifetime.
    std::map<SCH_SCREEN*, std::unique_ptr<SCH_SYMBOL_CACHE_EDIT_SCOPE>> m_libraryCacheScopes;
    bool m_libraryCacheChanged = false;
    bool m_connectivitySettingsChanged = false;
};
