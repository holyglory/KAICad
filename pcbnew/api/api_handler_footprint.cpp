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

#include <api/api_handler_footprint.h>
#include <api/api_pcb_utils.h>
#include <api/api_enums.h>
#include <api/api_utils.h>
#include <board.h>
#include <board_commit.h>
#include <footprint.h>
#include <footprint_edit_frame.h>
#include <ki_exception.h>
#include <pad.h>
#include <pcb_group.h>
#include <project.h>
#include <zone.h>
#include <footprint_library_adapter.h>
#include <project_pcb.h>

#include <api/common/types/base_types.pb.h>

using namespace kiapi::common::commands;
using types::CommandStatus;
using types::DocumentType;
using types::ItemRequestStatus;


API_HANDLER_FOOTPRINT::API_HANDLER_FOOTPRINT( FOOTPRINT_EDIT_FRAME* aFrame ) :
        API_HANDLER_FOOTPRINT( CreateFootprintFrameContext( aFrame ), aFrame )
{
}


API_HANDLER_FOOTPRINT::API_HANDLER_FOOTPRINT( std::shared_ptr<FOOTPRINT_CONTEXT> aContext,
                                              FOOTPRINT_EDIT_FRAME* aFrame ) :
        API_HANDLER_BOARD( std::move( aContext ), aFrame )
{
    registerHandler<OpenLibraryItem, Empty>(
            &API_HANDLER_FOOTPRINT::handleOpenLibraryItem );
    registerHandler<GetOpenDocuments, GetOpenDocumentsResponse>(
            &API_HANDLER_FOOTPRINT::handleGetOpenDocuments );
    registerHandler<SaveDocument, Empty>( &API_HANDLER_FOOTPRINT::handleSaveDocument );
    registerHandler<SaveCopyOfDocument, Empty>(
            &API_HANDLER_FOOTPRINT::handleSaveCopyOfDocument );
    registerHandler<RevertDocument, Empty>( &API_HANDLER_FOOTPRINT::handleRevertDocument );
    registerHandler<kiapi::automation::v1::NativeCapabilityFootprintReadOperation,
                    kiapi::automation::v1::NativeCapabilityFootprintReadOperationResponse>(
            &API_HANDLER_FOOTPRINT::handleReadOperation );
    registerHandler<kiapi::automation::v1::NativeCapabilityFootprintLibraryReadNames,
                    kiapi::automation::v1::NativeCapabilityFootprintLibraryReadNamesResponse>(
            &API_HANDLER_FOOTPRINT::handleReadLibraryNames );
    registerHandler<kiapi::automation::v1::NativeCapabilityFootprintLibraryDelete,
                    kiapi::automation::v1::NativeCapabilityFootprintLibraryDeleteResponse>(
            &API_HANDLER_FOOTPRINT::handleDeleteLibraryFootprint );
    registerHandler<kiapi::automation::v1::NativeCapabilityFootprintLibraryReadOperation,
                    kiapi::automation::v1::NativeCapabilityFootprintLibraryReadOperationResponse>(
            &API_HANDLER_FOOTPRINT::handleReadLibraryOperation );

    registerHandler<GetItems, GetItemsResponse>( &API_HANDLER_FOOTPRINT::handleGetItems );
}


FOOTPRINT_EDIT_FRAME* API_HANDLER_FOOTPRINT::frame() const
{
    return static_cast<FOOTPRINT_EDIT_FRAME*>( m_frame );
}


tl::expected<bool, ApiResponseStatus> API_HANDLER_FOOTPRINT::validateDocumentInternal( const DocumentSpecifier& aDocument ) const
{
    if( aDocument.type() != DocumentType::DOCTYPE_FOOTPRINT )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "the requested document is not a footprint" );
        return tl::unexpected( e );
    }

    LIB_ID target_fp = footprintContext()->GetLoadedFPID();

    if( !target_fp.IsValid() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "no footprint is currently open" );
        return tl::unexpected( e );
    }

    std::string actual_lib  = target_fp.GetUniStringLibNickname().ToStdString();                                                                            
    std::string actual_name = target_fp.GetUniStringLibItemName().ToStdString();

    if( 0 != aDocument.lib_id().library_nickname().compare( actual_lib ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "the requested library is {} but the actual library is {}",
            aDocument.lib_id().library_nickname(), actual_lib ) );
        return tl::unexpected( e );
    }

    if( 0 != aDocument.lib_id().entry_name().compare( actual_name ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "the requested footprint name is {} but the actual name is {}",
            aDocument.lib_id().entry_name(), actual_name ) );
        return tl::unexpected( e );
    }

    return true;
}

HANDLER_RESULT<FOOTPRINT*> API_HANDLER_FOOTPRINT::validateAndGetFootprint(
        const DocumentSpecifier& aDocument )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aDocument );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    FOOTPRINT* editorFootprint = board()->GetFirstFootprint();

    if( !editorFootprint )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "no footprint is currently loaded" );
        return tl::unexpected( e );
    }

    return editorFootprint;
}

HANDLER_RESULT<Empty> API_HANDLER_FOOTPRINT::handleOpenLibraryItem(
    const HANDLER_CONTEXT<OpenLibraryItem>& aCtx )
{
    if( aCtx.Request.type() != DocumentType::DOCTYPE_FOOTPRINT )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        return tl::unexpected( e );
    }

    FOOTPRINT_LIBRARY_ADAPTER* adapter = PROJECT_PCB::FootprintLibAdapter( &frame()->Prj() );

    wxString libraryName = aCtx.Request.identifier().library_nickname();
    wxString fpName = aCtx.Request.identifier().entry_name();

    LIB_ID fpid( libraryName, fpName );
    // preload the footprint to make sure it exists and so that we can make a nice error
    try
    {
        std::unique_ptr<FOOTPRINT> footprint(
                adapter->LoadFootprintWithOptionalNickname( fpid, true ) );

        if( !footprint )
        {
            ApiResponseStatus e;
            e.set_status( ApiStatusCode::AS_BAD_REQUEST );
            e.set_error_message( "could not open footprint" );
            return tl::unexpected( e );
        }
    }
    catch( const IO_ERROR& err )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "could not open footprint due to IO error {}",
                                          err.Problem().ToStdString() ) );
        return tl::unexpected( e );
    }

    frame()->LoadFootprintFromLibrary( fpid );
    return Empty();
}

HANDLER_RESULT<GetOpenDocumentsResponse> API_HANDLER_FOOTPRINT::handleGetOpenDocuments(
        const HANDLER_CONTEXT<GetOpenDocuments>& aCtx )
{
    if( aCtx.Request.type() != DocumentType::DOCTYPE_FOOTPRINT )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        return tl::unexpected( e );
    }

    GetOpenDocumentsResponse response;
    common::types::DocumentSpecifier doc;

    LIB_ID fpid = footprintContext()->GetLoadedFPID();

    doc.set_type( DocumentType::DOCTYPE_FOOTPRINT );
    doc.mutable_lib_id()->set_library_nickname( fpid.GetUniStringLibNickname() );
    doc.mutable_lib_id()->set_entry_name( fpid.GetUniStringLibItemName() );

    if( !project().IsNullProject() )
    {
        doc.mutable_project()->set_name( project().GetProjectName().ToStdString() );
        doc.mutable_project()->set_path( project().GetProjectDirectory().ToStdString() );
    }

    response.mutable_documents()->Add( std::move( doc ) );
    return response;
}


HANDLER_RESULT<Empty> API_HANDLER_FOOTPRINT::handleSaveDocument(
        const HANDLER_CONTEXT<SaveDocument>& aCtx )
{
    const std::string fingerprint = aCtx.Request.SerializeAsString();
    if( !aCtx.Request.operation_id().empty() )
    {
        auto found = m_operationReceipts.find( aCtx.Request.operation_id() );
        if( found != m_operationReceipts.end() )
        {
            if( found->second.operation != "save" || found->second.fingerprint != fingerprint )
            {
                ApiResponseStatus e; e.set_status( ApiStatusCode::AS_BAD_REQUEST );
                e.set_error_message( "footprint operation ID is already bound to different arguments" );
                return tl::unexpected( e );
            }
            return Empty();
        }
    }
    HANDLER_RESULT<FOOTPRINT*> footprint = validateAndGetFootprint( aCtx.Request.document() );

    if( !footprint )
        return tl::unexpected( footprint.error() );

    if( !footprintContext()->SaveFootprint( *footprint ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "failed to save footprint" );
        return tl::unexpected( e );
    }

    if( !aCtx.Request.operation_id().empty() )
        m_operationReceipts[aCtx.Request.operation_id()] = { "save", aCtx.Request.document(), true, fingerprint };

    return Empty();
}


HANDLER_RESULT<Empty> API_HANDLER_FOOTPRINT::handleSaveCopyOfDocument(
        const HANDLER_CONTEXT<SaveCopyOfDocument>& aCtx )
{
    const std::string fingerprint = aCtx.Request.SerializeAsString();
    if( !aCtx.Request.operation_id().empty() )
    {
        auto found = m_operationReceipts.find( aCtx.Request.operation_id() );
        if( found != m_operationReceipts.end() )
        {
            if( found->second.operation != "save-copy" || found->second.fingerprint != fingerprint )
            {
                ApiResponseStatus e; e.set_status( ApiStatusCode::AS_BAD_REQUEST );
                e.set_error_message( "footprint operation ID is already bound to different arguments" );
                return tl::unexpected( e );
            }
            return Empty();
        }
    }
    HANDLER_RESULT<FOOTPRINT*> footprint = validateAndGetFootprint( aCtx.Request.document() );

    if( !footprint )
        return tl::unexpected( footprint.error() );

    wxString pathStr = wxString::FromUTF8( aCtx.Request.path() );

    if( pathStr.IsEmpty() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "path must contain the new footprint name, "
                             "optionally prefixed with a library nickname (e.g. \"lib:name\")" );
        return tl::unexpected( e );
    }

    // path can be "NewName" (same library) or "LibNick:NewName" (different library)
    LIB_ID targetId;
    targetId.Parse( pathStr );

    wxString libraryName = targetId.GetLibNickname();

    if( libraryName.IsEmpty() )
        libraryName = footprintContext()->GetLoadedFPID().GetUniStringLibNickname();

    wxString newName = targetId.GetLibItemName();

    // Clone the footprint so we don't modify the one being edited
    std::unique_ptr<FOOTPRINT> copy( static_cast<FOOTPRINT*>( ( *footprint )->Clone() ) );
    copy->SetFPID( LIB_ID( libraryName, newName ) );

    if( !footprintContext()->SaveFootprintInLibrary( copy.get(), libraryName ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "failed to save footprint copy '{}' to library '{}'",
                                          newName.ToStdString(),
                                          libraryName.ToStdString() ) );
        return tl::unexpected( e );
    }

    if( !aCtx.Request.operation_id().empty() )
        m_operationReceipts[aCtx.Request.operation_id()] = { "save-copy", aCtx.Request.document(), true, fingerprint };

    return Empty();
}


HANDLER_RESULT<Empty> API_HANDLER_FOOTPRINT::handleRevertDocument(
        const HANDLER_CONTEXT<RevertDocument>& aCtx )
{
    const std::string fingerprint = aCtx.Request.SerializeAsString();
    if( !aCtx.Request.operation_id().empty() )
    {
        auto found = m_operationReceipts.find( aCtx.Request.operation_id() );
        if( found != m_operationReceipts.end() )
        {
            if( found->second.operation != "revert" || found->second.fingerprint != fingerprint )
            {
                ApiResponseStatus e; e.set_status( ApiStatusCode::AS_BAD_REQUEST );
                e.set_error_message( "footprint operation ID is already bound to different arguments" );
                return tl::unexpected( e );
            }
            return Empty();
        }
    }
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    frame()->GetScreen()->SetContentModified( false );
    frame()->RevertFootprint(); // dialog is suppressed by ^

    if( !aCtx.Request.operation_id().empty() )
        m_operationReceipts[aCtx.Request.operation_id()] = { "revert", aCtx.Request.document(), true, fingerprint };

    return Empty();
}


HANDLER_RESULT<kiapi::automation::v1::NativeCapabilityFootprintReadOperationResponse>
API_HANDLER_FOOTPRINT::handleReadOperation(
        const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityFootprintReadOperation>& aCtx )
{
    kiapi::automation::v1::NativeCapabilityFootprintReadOperationResponse response;
    response.set_operation_id( aCtx.Request.operation_id() );
    auto found = m_operationReceipts.find( aCtx.Request.operation_id() );
    if( found == m_operationReceipts.end() )
    {
        response.set_found( false );
        return response;
    }
    response.set_found( true );
    response.set_operation( found->second.operation );
    response.mutable_document()->CopyFrom( found->second.document );
    response.set_completed( found->second.completed );
    return response;
}


HANDLER_RESULT<kiapi::automation::v1::NativeCapabilityFootprintLibraryReadNamesResponse>
API_HANDLER_FOOTPRINT::handleReadLibraryNames(
        const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityFootprintLibraryReadNames>& aCtx )
{
    using namespace kiapi::automation::v1;
    const wxString nickname = wxString::FromUTF8( aCtx.Request.library_nickname() );
    if( nickname.IsEmpty() )
    {
        ApiResponseStatus e; e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "A footprint library nickname is required" );
        return tl::unexpected( e );
    }

    try
    {
        FOOTPRINT_LIBRARY_ADAPTER* adapter = PROJECT_PCB::FootprintLibAdapter( &project() );
        if( !adapter->LoadOne( nickname ) )
        {
            ApiResponseStatus e; e.set_status( ApiStatusCode::AS_BAD_REQUEST );
            e.set_error_message( "The requested footprint library could not be loaded" );
            return tl::unexpected( e );
        }
        NativeCapabilityFootprintLibraryReadNamesResponse response;
        response.set_library_nickname( aCtx.Request.library_nickname() );
        for( const wxString& name : adapter->GetFootprintNames( nickname, false ) )
            response.add_footprint_names( name.ToStdString( wxConvUTF8 ) );
        response.set_modify_hash( static_cast<uint64_t>( adapter->GenerateTimestamp( &nickname ) ) );
        return response;
    }
    catch( const IO_ERROR& error )
    {
        ApiResponseStatus e; e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( error.What().ToStdString() );
        return tl::unexpected( e );
    }
}


HANDLER_RESULT<kiapi::automation::v1::NativeCapabilityFootprintLibraryDeleteResponse>
API_HANDLER_FOOTPRINT::handleDeleteLibraryFootprint(
        const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityFootprintLibraryDelete>& aCtx )
{
    using namespace kiapi::automation::v1;
    const wxString nickname = wxString::FromUTF8( aCtx.Request.library_nickname() );
    const wxString name = wxString::FromUTF8( aCtx.Request.footprint_name() );
    const std::string operationId = aCtx.Request.operation_id();
    if( nickname.IsEmpty() || name.IsEmpty() || operationId.empty() )
    {
        ApiResponseStatus e; e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "A footprint library, footprint name and operation ID are required" );
        return tl::unexpected( e );
    }
    const std::string fingerprint = aCtx.Request.SerializeAsString();
    if( auto found = m_libraryDeleteReceipts.find( operationId ); found != m_libraryDeleteReceipts.end() )
    {
        if( m_libraryOperationFingerprints[operationId] != fingerprint )
        {
            ApiResponseStatus e; e.set_status( ApiStatusCode::AS_BAD_REQUEST );
            e.set_error_message( "The footprint operation ID is already bound to different arguments" );
            return tl::unexpected( e );
        }
        return found->second;
    }

    try
    {
        FOOTPRINT_LIBRARY_ADAPTER* adapter = PROJECT_PCB::FootprintLibAdapter( &project() );
        if( !adapter->LoadOne( nickname ) || !adapter->IsFootprintLibWritable( nickname ) )
        {
            ApiResponseStatus e; e.set_status( ApiStatusCode::AS_BAD_REQUEST );
            e.set_error_message( "The footprint library is unavailable or read-only" );
            return tl::unexpected( e );
        }
        const uint64_t currentHash = static_cast<uint64_t>( adapter->GenerateTimestamp( &nickname ) );
        if( aCtx.Request.has_expected_modify_hash() && currentHash != aCtx.Request.expected_modify_hash() )
        {
            ApiResponseStatus e; e.set_status( ApiStatusCode::AS_BAD_REQUEST );
            e.set_error_message( "The footprint library changed since the observed modify hash" );
            return tl::unexpected( e );
        }
        auto names = adapter->GetFootprintNames( nickname, false );
        if( std::find( names.begin(), names.end(), name ) == names.end() )
        {
            ApiResponseStatus e; e.set_status( ApiStatusCode::AS_BAD_REQUEST );
            e.set_error_message( "The requested footprint does not exist in the library" );
            return tl::unexpected( e );
        }
        const uint64_t beforeHash = currentHash;
        adapter->DeleteFootprint( nickname, name );
        auto remaining = adapter->GetFootprintNames( nickname, false );
        if( std::find( remaining.begin(), remaining.end(), name ) != remaining.end() )
        {
            ApiResponseStatus e; e.set_status( ApiStatusCode::AS_BAD_REQUEST );
            e.set_error_message( "The footprint delete was not verified by a native library re-read" );
            return tl::unexpected( e );
        }
        NativeCapabilityFootprintLibraryDeleteResponse response;
        response.set_library_nickname( aCtx.Request.library_nickname() );
        response.set_footprint_name( aCtx.Request.footprint_name() );
        response.set_operation_id( operationId ); response.set_completed( true );
        response.set_before_modify_hash( beforeHash );
        response.set_after_modify_hash( static_cast<uint64_t>( adapter->GenerateTimestamp( &nickname ) ) );
        m_libraryOperationFingerprints[operationId] = fingerprint;
        m_libraryDeleteReceipts[operationId] = response;
        return response;
    }
    catch( const IO_ERROR& error )
    {
        ApiResponseStatus e; e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( error.What().ToStdString() );
        return tl::unexpected( e );
    }
}


HANDLER_RESULT<kiapi::automation::v1::NativeCapabilityFootprintLibraryReadOperationResponse>
API_HANDLER_FOOTPRINT::handleReadLibraryOperation(
        const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityFootprintLibraryReadOperation>& aCtx )
{
    using namespace kiapi::automation::v1;
    NativeCapabilityFootprintLibraryReadOperationResponse response;
    response.set_operation_id( aCtx.Request.operation_id() );
    auto found = m_libraryDeleteReceipts.find( aCtx.Request.operation_id() );
    if( found == m_libraryDeleteReceipts.end() )
    {
        response.set_found( false );
        return response;
    }
    response.set_found( true );
    response.mutable_delete_response()->CopyFrom( found->second );
    return response;
}


HANDLER_RESULT<GetItemsResponse> API_HANDLER_FOOTPRINT::handleGetItems(
        const HANDLER_CONTEXT<GetItems>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    if( !validateItemHeaderDocument( aCtx.Request.header() ) )
    {
        ApiResponseStatus e;
        // No message needed for AS_UNHANDLED; this is an internal flag for the API server
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        return tl::unexpected( e );
    }

    GetItemsResponse response;

    FOOTPRINT* footprint = board()->GetFirstFootprint();
    std::vector<BOARD_ITEM*> items;
    std::set<KICAD_T> typesRequested, typesInserted;
    bool handledAnything = false;

    for( int typeRaw : aCtx.Request.types() )
    {
        auto typeMessage = static_cast<common::types::KiCadObjectType>( typeRaw );
        KICAD_T type = FromProtoEnum<KICAD_T>( typeMessage );

        if( type == TYPE_NOT_INIT )
            continue;

        typesRequested.emplace( type );

        if( typesInserted.count( type ) )
            continue;

        switch( type )
        {
        case PCB_PAD_T:
        {
            handledAnything = true;

            std::copy( footprint->Pads().begin(), footprint->Pads().end(),
                       std::back_inserter( items ) );

            typesInserted.insert( PCB_PAD_T );
            break;
        }

        case PCB_FIELD_T:
        {
            handledAnything = true;

            std::copy( footprint->GetFields().begin(), footprint->GetFields().end(),
                       std::back_inserter( items ) );

            typesInserted.insert( PCB_FIELD_T );
            break;
        }

        case PCB_SHAPE_T:
        case PCB_TEXT_T:
        case PCB_TEXTBOX_T:
        case PCB_BARCODE_T:
        case PCB_TABLE_T:
        {
            handledAnything = true;
            bool inserted = false;

            for( BOARD_ITEM* item : footprint->GraphicalItems() )
            {
                if( item->Type() == type )
                {
                    items.emplace_back( item );
                    inserted = true;
                }
            }

            if( inserted )
                typesInserted.insert( type );

            break;
        }

        case PCB_TABLECELL_T:
        {
            handledAnything = true;
            bool inserted = false;

            for( BOARD_ITEM* item : footprint->GraphicalItems() )
            {
                if( item->Type() != PCB_TABLE_T )
                    continue;

                item->RunOnChildren(
                        [&]( BOARD_ITEM* child )
                        {
                            items.emplace_back( child );
                            inserted = true;
                        },
                        RECURSE_MODE::NO_RECURSE );
            }

            if( inserted )
                typesInserted.insert( PCB_TABLECELL_T );

            break;
        }

        case PCB_DIMENSION_T:
        {
            handledAnything = true;
            bool inserted = false;

            for( BOARD_ITEM* item : footprint->GraphicalItems() )
            {
                switch( item->Type() )
                {
                case PCB_DIM_ALIGNED_T:
                case PCB_DIM_CENTER_T:
                case PCB_DIM_RADIAL_T:
                case PCB_DIM_ORTHOGONAL_T:
                case PCB_DIM_LEADER_T:
                    items.emplace_back( item );
                    inserted = true;
                    break;
                default:
                    break;
                }
            }

            // we have to add the dimension subtypes to the requested to get them out
            typesRequested.insert( { PCB_DIM_ALIGNED_T, PCB_DIM_CENTER_T, PCB_DIM_RADIAL_T,
                                     PCB_DIM_ORTHOGONAL_T, PCB_DIM_LEADER_T } );

            if( inserted )
            {
                typesInserted.insert( { PCB_DIM_ALIGNED_T, PCB_DIM_CENTER_T, PCB_DIM_RADIAL_T,
                                        PCB_DIM_ORTHOGONAL_T, PCB_DIM_LEADER_T } );
            }

            break;
        }

        case PCB_ZONE_T:
        {
            handledAnything = true;

            std::copy( footprint->Zones().begin(), footprint->Zones().end(),
                       std::back_inserter( items ) );

            typesInserted.insert( PCB_ZONE_T );
            break;
        }

        case PCB_GROUP_T:
        {
            handledAnything = true;

            std::copy( footprint->Groups().begin(), footprint->Groups().end(),
                       std::back_inserter( items ) );

            typesInserted.insert( PCB_GROUP_T );
            break;
        }

        default:
            break;
        }
    }

    if( !handledAnything )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "none of the requested types are valid for a Footprint object" );
        return tl::unexpected( e );
    }

    for( const BOARD_ITEM* item : items )
    {
        if( !typesRequested.count( item->Type() ) )
            continue;

        google::protobuf::Any itemBuf;
        item->Serialize( itemBuf );
        response.mutable_items()->Add( std::move( itemBuf ) );
    }

    response.set_status( ItemRequestStatus::IRS_OK );
    return response;
}


BOARD_ITEM_CONTAINER* API_HANDLER_FOOTPRINT::getDefaultContainer()
{
    return board()->GetFirstFootprint();
}
