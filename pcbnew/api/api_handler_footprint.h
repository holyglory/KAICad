/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright (C) 2026 Benjamin Chung (ckfinite@gmail.com)
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 *
 * This program is free software: you can redistribute it and/or modify it
 * under the terms of the GNU General Public License as published by the
 * Free Software Foundation, either version 3 of the License, or (at your
 * option) any later version.
 *
 * This program is distributed in the hope that it will be useful, but
 * WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

#ifndef KICAD_API_HANDLER_FOOTPRINT_H
#define KICAD_API_HANDLER_FOOTPRINT_H

#include <api/api_handler_board.h>
#include <api/footprint_context.h>
#include <api/common/commands/project_commands.pb.h>
#include <api/common/commands/automation_commands.pb.h>
#include <api/common/commands/footprint_definition_commands.pb.h>
#include <map>


class FOOTPRINT;
class FOOTPRINT_EDIT_FRAME;


class API_HANDLER_FOOTPRINT : public API_HANDLER_BOARD
{
public:
    API_HANDLER_FOOTPRINT( FOOTPRINT_EDIT_FRAME* aFrame );
    API_HANDLER_FOOTPRINT( std::shared_ptr<FOOTPRINT_CONTEXT> aContext,
                           FOOTPRINT_EDIT_FRAME* aFrame = nullptr );

private:
    HANDLER_RESULT<Empty> handleOpenLibraryItem( const HANDLER_CONTEXT<commands::OpenLibraryItem>& aCtx );

    HANDLER_RESULT<commands::GetOpenDocumentsResponse> handleGetOpenDocuments(
            const HANDLER_CONTEXT<commands::GetOpenDocuments>& aCtx );

    HANDLER_RESULT<Empty> handleSaveDocument( const HANDLER_CONTEXT<commands::SaveDocument>& aCtx );

    HANDLER_RESULT<Empty> handleSaveCopyOfDocument(
            const HANDLER_CONTEXT<commands::SaveCopyOfDocument>& aCtx );

    HANDLER_RESULT<Empty> handleRevertDocument(
            const HANDLER_CONTEXT<commands::RevertDocument>& aCtx );

    HANDLER_RESULT<kiapi::automation::v1::NativeCapabilityFootprintReadOperationResponse>
            handleReadOperation(
                    const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityFootprintReadOperation>& aCtx );

    HANDLER_RESULT<kiapi::automation::v1::NativeCapabilityFootprintLibraryReadNamesResponse>
            handleReadLibraryNames(
                    const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityFootprintLibraryReadNames>& aCtx );

    HANDLER_RESULT<kiapi::automation::v1::NativeCapabilityFootprintLibraryDeleteResponse>
            handleDeleteLibraryFootprint(
                    const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityFootprintLibraryDelete>& aCtx );

    HANDLER_RESULT<kiapi::automation::v1::NativeCapabilityFootprintLibraryReadOperationResponse>
            handleReadLibraryOperation(
                    const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityFootprintLibraryReadOperation>& aCtx );

    HANDLER_RESULT<kiapi::automation::v1::NativeCapabilityFootprintReadDefinitionResponse>
            handleReadDefinition(
                    const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityFootprintReadDefinition>& aCtx );

    HANDLER_RESULT<commands::GetItemsResponse> handleGetItems(
            const HANDLER_CONTEXT<commands::GetItems>& aCtx );

protected:
    kiapi::common::types::DocumentType thisDocumentType() const override
    {
        return kiapi::common::types::DOCTYPE_FOOTPRINT;
    }

    tl::expected<bool, ApiResponseStatus> validateDocumentInternal( const DocumentSpecifier& aDocument ) const override;

    BOARD_ITEM_CONTAINER* getDefaultContainer() override;

private:
    FOOTPRINT_CONTEXT* footprintContext() const
    {
        return static_cast<FOOTPRINT_CONTEXT*>( context() );
    }

    FOOTPRINT_EDIT_FRAME* frame() const;

    HANDLER_RESULT<FOOTPRINT*> validateAndGetFootprint( const DocumentSpecifier& aDocument );

    struct FOOTPRINT_RECEIPT
    {
        std::string operation;
        DocumentSpecifier document;
        bool completed = false;
        std::string fingerprint;
    };

    std::map<std::string, FOOTPRINT_RECEIPT> m_operationReceipts;

    // Process-owned receipts for configured footprint-library entry mutations.
    // The library lifecycle has no document revision controller yet, so bind
    // each operation id to the exact protobuf request and retain its response
    // while this native editor process remains alive.
    std::map<std::string, std::string> m_libraryOperationFingerprints;
    std::map<std::string, kiapi::automation::v1::NativeCapabilityFootprintLibraryDeleteResponse>
            m_libraryDeleteReceipts;
};


#endif //KICAD_API_HANDLER_FOOTPRINT_H
