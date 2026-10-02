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
#include <pcb_io/pcb_io_mgr.h>
#include <pcb_io/kicad_sexpr/pcb_io_kicad_sexpr.h>
#include <api/native_file_observation.h>
#include <api/native_state_digest.h>
#include <file_content_baseline.h>
#include <string_utils.h>
#include <wildcards_and_files_ext.h>
#include <wx/filefn.h>
#include <wx/filename.h>

#include <api/common/types/base_types.pb.h>

#include <memory>
#include <wx/file.h>

using namespace kiapi::common::commands;
using types::CommandStatus;
using types::DocumentType;
using types::ItemRequestStatus;

namespace
{
constexpr int FOOTPRINT_SOURCE_PERSISTED_LIBRARY = 1;
constexpr int FOOTPRINT_SOURCE_EDITOR = 2;

struct TEMP_FOOTPRINT_LIBRARY
{
    wxString directory;

    ~TEMP_FOOTPRINT_LIBRARY()
    {
        if( !directory.IsEmpty() )
            wxFileName::Rmdir( directory, wxPATH_RMDIR_RECURSIVE );
    }
};

struct FOOTPRINT_DEFINITION_CAPTURE
{
    kiapi::board::types::FootprintInstance footprint;
    std::string                             canonicalSha256;
    bool                                    canonicalComplete = false;
    std::vector<std::string>                limitations;
};

std::string DigestText( std::string_view aText )
{
    NATIVE_STATE_DIGEST digest;
    digest.Append( aText );
    return digest.Hex();
}

std::string ProviderDigest( FOOTPRINT_LIBRARY_ADAPTER* aAdapter, const LIB_ID& aTarget )
{
    std::optional<LIBRARY_TABLE_ROW*> row = aAdapter->GetRow( aTarget.GetLibNickname() );

    if( !row )
        return {};

    NATIVE_STATE_DIGEST digest;
    digest.Append( "kicad-footprint-provider-v1" );
    const auto part = [&]( const std::string& value )
    {
        digest.Append( std::to_string( value.size() ) );
        digest.Append( ":" );
        digest.Append( value );
    };
    part( ( *row )->Nickname().utf8_string() );
    part( ( *row )->Type().utf8_string() );
    part( ( *row )->URI().utf8_string() );
    part( LIBRARY_MANAGER::GetFullURI( *row, true ).utf8_string() );
    part( ( *row )->Options().utf8_string() );
    part( std::to_string( static_cast<int>( ( *row )->Scope() ) ) );
    part( ( *row )->Disabled() ? "disabled" : "enabled" );
    return digest.Hex();
}

std::optional<wxString> LibraryUri( FOOTPRINT_LIBRARY_ADAPTER* aAdapter,
                                    const wxString& aNickname )
{
    if( !aAdapter )
        return std::nullopt;

    return aAdapter->Manager().GetFullURI( LIBRARY_TABLE_TYPE::FOOTPRINT, aNickname, true );
}

PCB_IO_MGR::PCB_FILE_T LibraryPluginType( FOOTPRINT_LIBRARY_ADAPTER* aAdapter,
                                           const wxString& aNickname )
{
    std::optional<LIBRARY_TABLE_ROW*> row = aAdapter->GetRow( aNickname );

    if( !row || ( *row )->Disabled() )
        return PCB_IO_MGR::PCB_FILE_UNKNOWN;

    return PCB_IO_MGR::EnumFromStr( ( *row )->Type() );
}

std::optional<wxString> FootprintFilePath( FOOTPRINT_LIBRARY_ADAPTER* aAdapter,
                                           const LIB_ID& aTarget )
{
    std::optional<wxString> uri = LibraryUri( aAdapter, aTarget.GetLibNickname() );

    if( !uri || LibraryPluginType( aAdapter, aTarget.GetLibNickname() ) != PCB_IO_MGR::KICAD_SEXP )
        return std::nullopt;

    wxString fileName = aTarget.GetLibItemName();
    ReplaceIllegalFileNameChars( fileName, '_' );
    wxFileName result( *uri, fileName, FILEEXT::KiCadFootprintFileExtension );
    return result.GetFullPath();
}

void AddLimit( FOOTPRINT_DEFINITION_CAPTURE& aCapture, const std::string& aLimit )
{
    if( std::find( aCapture.limitations.begin(), aCapture.limitations.end(), aLimit )
            == aCapture.limitations.end() )
    {
        aCapture.limitations.push_back( aLimit );
    }
}

void CopyDefinitionFields( FOOTPRINT& aFootprint,
                           kiapi::board::types::Footprint& aDefinition )
{
    if( const PCB_FIELD* field = aFootprint.GetField( FIELD_T::REFERENCE ) )
        field->Serialize( *aDefinition.mutable_reference_field() );

    if( const PCB_FIELD* field = aFootprint.GetField( FIELD_T::VALUE ) )
        field->Serialize( *aDefinition.mutable_value_field() );

    if( const PCB_FIELD* field = aFootprint.GetField( FIELD_T::DATASHEET ) )
        field->Serialize( *aDefinition.mutable_datasheet_field() );

    if( const PCB_FIELD* field = aFootprint.GetField( FIELD_T::DESCRIPTION ) )
        field->Serialize( *aDefinition.mutable_description_field() );
}

FOOTPRINT_DEFINITION_CAPTURE CaptureCanonicalDefinition( const FOOTPRINT& aSource,
                                                         const LIB_ID& aTarget )
{
    FOOTPRINT_DEFINITION_CAPTURE result;
    std::unique_ptr<FOOTPRINT> detached(
            static_cast<FOOTPRINT*>( aSource.Clone() ) );

    detached->SetParent( nullptr );
    detached->SetParentGroup( nullptr );
    detached->ClearAllNets();
    detached->SetFPID( aTarget );

    const wxString tempDirectory = wxFileName::GetTempDir() + wxFileName::GetPathSeparator()
            + wxS( "kicad-footprint-definition-" ) + wxString::FromUTF8( KIID().AsStdString() );

    if( !wxFileName::Mkdir( tempDirectory, 0700, wxPATH_MKDIR_FULL ) )
    {
        AddLimit( result, "could not allocate a private native temporary footprint library" );
        return result;
    }

    TEMP_FOOTPRINT_LIBRARY temp{ tempDirectory };
    wxString tempFileName = aTarget.GetUniStringLibItemName();
    ReplaceIllegalFileNameChars( tempFileName, '_' );
    const wxString tempPath = wxFileName( temp.directory, tempFileName,
                                         FILEEXT::KiCadFootprintFileExtension ).GetFullPath();

    try
    {
        PCB_IO_KICAD_SEXPR writer;
        writer.FootprintSave( temp.directory, detached.get(), nullptr );

        std::string nativeBytes;
        FILE_CONTENT_BASELINE nativeFile = FILE_CONTENT_BASELINE::Read( tempPath, &nativeBytes );

        if( !nativeFile.Known() || !nativeFile.Exists() || nativeBytes.empty() )
        {
            AddLimit( result, "native footprint writer did not produce readable canonical bytes" );
            return result;
        }

        result.canonicalSha256 = DigestText( nativeBytes );

        wxString importedName;
        std::unique_ptr<FOOTPRINT> normalized(
                writer.ImportFootprint( tempPath, importedName, nullptr ) );

        if( !normalized )
        {
            AddLimit( result, "native canonical bytes could not be parsed back into a footprint" );
            return result;
        }

        normalized->SetParent( nullptr );
        normalized->SetParentGroup( nullptr );
        normalized->SetFPID( aTarget );

        google::protobuf::Any packed;
        normalized->Serialize( packed );

        if( !packed.UnpackTo( &result.footprint ) )
        {
            AddLimit( result, "native footprint definition could not be serialized" );
            return result;
        }

        kiapi::board::types::Footprint* definition = result.footprint.mutable_definition();
        CopyDefinitionFields( *normalized, *definition );
        const std::string description = definition->attributes().description();
        const std::string keywords = definition->attributes().keywords();
        *definition->mutable_attributes() = result.footprint.attributes();
        definition->mutable_attributes()->set_description( description );
        definition->mutable_attributes()->set_keywords( keywords );
        *definition->mutable_overrides() = result.footprint.overrides();
        definition->mutable_anchor()->set_x_nm( 0 );
        definition->mutable_anchor()->set_y_nm( 0 );

        result.canonicalComplete = definition->has_id() && !definition->id().entry_name().empty()
                && definition->has_reference_field() && definition->has_value_field()
                && definition->has_datasheet_field() && definition->has_description_field();

        if( !result.canonicalComplete )
            AddLimit( result, "canonical definition is missing one or more mandatory fields" );

        const auto unrepresented = [&]( bool present, const char* limitation )
        {
            if( present )
            {
                result.canonicalComplete = false;
                AddLimit( result, limitation );
            }
        };
        unrepresented( normalized->HasExtrudedBody(), "extruded 3D body has no footprint transport representation" );
        unrepresented( !normalized->GetVariants().empty(), "footprint variants have no footprint transport representation" );
        unrepresented( !normalized->GetUnitInfo().empty(), "unit swapping metadata has no footprint transport representation" );
        unrepresented( normalized->GetStackupMode() != FOOTPRINT_STACKUP::EXPAND_INNER_LAYERS,
                       "custom footprint stackup has no footprint transport representation" );
        unrepresented( normalized->GetAreFontsEmbedded(), "embedded-font policy has no footprint transport representation" );
        unrepresented( normalized->GetInitialComments() && !normalized->GetInitialComments()->empty(),
                       "initial library comments have no footprint transport representation" );
        unrepresented( !normalized->Groups().empty(),
                       "footprint groups have no detached footprint transport representation" );
        unrepresented( !normalized->Constraints().empty(),
                       "footprint constraints have no detached footprint transport representation" );

        // Compare against KiCad's native writer after deserialize as well: a full
        // writer digest does not by itself prove the transport envelope holds every
        // property.  Native-only metadata remains an explicit incomplete result.
        google::protobuf::Any projected;
        projected.PackFrom( result.footprint );
        FOOTPRINT roundTrip( nullptr );

        if( !roundTrip.Deserialize( projected ) )
        {
            result.canonicalComplete = false;
            AddLimit( result, "canonical transport definition could not be deserialized" );
        }
        else
        {
            writer.FootprintSave( temp.directory, &roundTrip, nullptr );
            const FILE_CONTENT_BASELINE roundTripFile = FILE_CONTENT_BASELINE::Read( tempPath );

            if( !roundTripFile.Known() || !roundTripFile.Exists()
                || roundTripFile.Sha256() != result.canonicalSha256 )
            {
                result.canonicalComplete = false;
                AddLimit( result, "canonical transport projection does not preserve the complete native writer representation" );
            }
        }
    }
    catch( const IO_ERROR& error )
    {
        AddLimit( result, error.What().ToStdString() );
    }
    catch( const std::exception& error )
    {
        AddLimit( result, error.what() );
    }

    return result;
}

FOOTPRINT_DEFINITION_CAPTURE CaptureCanonicalDefinitionFromBytes( std::string_view aBytes,
                                                                  const LIB_ID& aTarget )
{
    FOOTPRINT_DEFINITION_CAPTURE result;
    const wxString tempDirectory = wxFileName::GetTempDir() + wxFileName::GetPathSeparator()
            + wxS( "kicad-footprint-source-" ) + wxString::FromUTF8( KIID().AsStdString() );

    if( !wxFileName::Mkdir( tempDirectory, 0700, wxPATH_MKDIR_FULL ) )
    {
        AddLimit( result, "could not allocate a private native source footprint path" );
        return result;
    }

    TEMP_FOOTPRINT_LIBRARY temp{ tempDirectory };
    const wxString sourcePath = wxFileName( temp.directory, wxS( "captured.kicad_mod" ) ).GetFullPath();

    try
    {
        wxFile sourceFile;

        if( !sourceFile.Open( sourcePath, wxFile::write )
            || sourceFile.Write( aBytes.data(), aBytes.size() ) != aBytes.size() )
        {
            AddLimit( result, "could not persist the captured native footprint bytes for parsing" );
            return result;
        }

        sourceFile.Close();
        PCB_IO_KICAD_SEXPR parser;
        wxString importedName;
        std::unique_ptr<FOOTPRINT> parsed(
                parser.ImportFootprint( sourcePath, importedName, nullptr ) );

        if( !parsed )
        {
            AddLimit( result, "captured native footprint bytes could not be parsed" );
            return result;
        }

        return CaptureCanonicalDefinition( *parsed, aTarget );
    }
    catch( const IO_ERROR& error )
    {
        AddLimit( result, error.What().ToStdString() );
    }
    catch( const std::exception& error )
    {
        AddLimit( result, error.what() );
    }

    return result;
}
}


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
    registerHandler<kiapi::automation::v1::NativeCapabilityFootprintReadDefinition,
                    kiapi::automation::v1::NativeCapabilityFootprintReadDefinitionResponse>(
            &API_HANDLER_FOOTPRINT::handleReadDefinition );

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

    FOOTPRINT_LIBRARY_ADAPTER* adapter = PROJECT_PCB::FootprintLibAdapter( &project() );

    if( !aCtx.Request.options().overwrite() && adapter->FootprintExists( libraryName, newName ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "footprint copy target '{}:{}' already exists and overwrite is false",
                                          libraryName.ToStdString(), newName.ToStdString() ) );
        return tl::unexpected( e );
    }

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


HANDLER_RESULT<kiapi::automation::v1::NativeCapabilityFootprintReadDefinitionResponse>
API_HANDLER_FOOTPRINT::handleReadDefinition(
        const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityFootprintReadDefinition>& aCtx )
{
    using namespace kiapi::automation::v1;

    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    const DocumentSpecifier& document = aCtx.Request.document();

    if( document.type() != DocumentType::DOCTYPE_FOOTPRINT || !document.has_lib_id()
        || document.lib_id().library_nickname().empty() || document.lib_id().entry_name().empty() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "a footprint document with a library nickname and entry name is required" );
        return tl::unexpected( e );
    }

    const int source = static_cast<int>( aCtx.Request.source() );

    if( source != FOOTPRINT_SOURCE_PERSISTED_LIBRARY && source != FOOTPRINT_SOURCE_EDITOR )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "footprint definition source must be persisted_library or editor" );
        return tl::unexpected( e );
    }

    if( source == FOOTPRINT_SOURCE_PERSISTED_LIBRARY && aCtx.Request.has_canonical_target() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "canonical_target is only valid for an editor definition projection" );
        return tl::unexpected( e );
    }

    FOOTPRINT_LIBRARY_ADAPTER* adapter = PROJECT_PCB::FootprintLibAdapter( &project() );
    const wxString nickname = wxString::FromUTF8( document.lib_id().library_nickname() );
    const wxString name = wxString::FromUTF8( document.lib_id().entry_name() );
    LIB_ID target( nickname, name );
    LIB_ID sourceTarget( nickname, name );
    std::unique_ptr<FOOTPRINT> persisted;
    FOOTPRINT* sourceFootprint = nullptr;
    std::optional<wxString> baselinePath;
    FILE_CONTENT_BASELINE capturedBaseline;
    std::string capturedBytes;
    bool baselineCaptured = false;
    std::string targetProviderBefore;
    std::string sourceProviderBefore;
    FOOTPRINT_DEFINITION_CAPTURE capture;

    try
    {
        if( source == FOOTPRINT_SOURCE_PERSISTED_LIBRARY )
        {
            if( !adapter->LoadOne( nickname ) )
            {
                ApiResponseStatus e;
                e.set_status( ApiStatusCode::AS_BAD_REQUEST );
                e.set_error_message( "the requested footprint library could not be loaded" );
                return tl::unexpected( e );
            }

            // Snapshot capture is explicitly forced so equal size/mtime edits cannot be served
            // from the adapter/plugin cache. Normal browsing keeps the timestamp fast path.
            adapter->RefreshLibraryIfChanged( nickname, true );
            targetProviderBefore = ProviderDigest( adapter, target );
            sourceProviderBefore = targetProviderBefore;
            baselinePath = FootprintFilePath( adapter, target );

            if( baselinePath )
            {
                capturedBaseline = FILE_CONTENT_BASELINE::Read( *baselinePath, &capturedBytes );
                baselineCaptured = capturedBaseline.Known() && capturedBaseline.Exists()
                        && !capturedBytes.empty();
            }

            persisted.reset( adapter->LoadFootprint( nickname, name, true ) );

            if( !persisted )
            {
                ApiResponseStatus e;
                e.set_status( ApiStatusCode::AS_BAD_REQUEST );
                e.set_error_message( "the requested persisted footprint could not be read" );
                return tl::unexpected( e );
            }
            sourceFootprint = persisted.get();

            if( baselineCaptured )
                capture = CaptureCanonicalDefinitionFromBytes( capturedBytes, target );
            else
                capture = CaptureCanonicalDefinition( *sourceFootprint, target );
        }
        else
        {
            HANDLER_RESULT<FOOTPRINT*> editor = validateAndGetFootprint( document );

            if( !editor )
                return tl::unexpected( editor.error() );

            sourceFootprint = *editor;
            sourceProviderBefore = ProviderDigest( adapter, sourceTarget );

            if( aCtx.Request.has_canonical_target() )
            {
                const auto& canonicalTarget = aCtx.Request.canonical_target();

                if( canonicalTarget.library_nickname().empty() || canonicalTarget.entry_name().empty() )
                {
                    ApiResponseStatus e;
                    e.set_status( ApiStatusCode::AS_BAD_REQUEST );
                    e.set_error_message( "canonical_target requires a library nickname and entry name" );
                    return tl::unexpected( e );
                }

                target = LIB_ID( wxString::FromUTF8( canonicalTarget.library_nickname() ),
                                 wxString::FromUTF8( canonicalTarget.entry_name() ) );
            }

            targetProviderBefore = ProviderDigest( adapter, target );
            baselinePath = FootprintFilePath( adapter, sourceTarget );

            if( baselinePath )
            {
                capturedBaseline = FILE_CONTENT_BASELINE::Read( *baselinePath, &capturedBytes );
                baselineCaptured = capturedBaseline.Known() && capturedBaseline.Exists();
            }

            capture = CaptureCanonicalDefinition( *sourceFootprint, target );
        }

        const PCB_IO_MGR::PCB_FILE_T pluginType =
                LibraryPluginType( adapter, target.GetLibNickname() );
        std::optional<wxString> uri = LibraryUri( adapter, target.GetLibNickname() );
        const bool providerKnown = uri.has_value() && pluginType == PCB_IO_MGR::KICAD_SEXP;
        const std::string targetProviderAfter = ProviderDigest( adapter, target );
        const std::string sourceProviderAfter = ProviderDigest( adapter, sourceTarget );

        if( targetProviderBefore.empty() || targetProviderAfter != targetProviderBefore )
        {
            capture.canonicalComplete = false;
            AddLimit( capture, "the configured target footprint provider changed during capture" );
        }

        if( sourceProviderBefore.empty() || sourceProviderAfter != sourceProviderBefore )
        {
            capture.canonicalComplete = false;
            AddLimit( capture, "the configured source footprint provider changed during capture" );
        }

        if( !providerKnown )
            AddLimit( capture, "the configured footprint provider has no complete KiCad canonical projection" );

        NativeCapabilityFootprintReadDefinitionResponse response;
        response.mutable_document()->CopyFrom( document );
        response.set_process_epoch( apiServer().Token() );
        response.mutable_footprint()->CopyFrom( capture.footprint );
        response.set_canonical_sha256( capture.canonicalSha256 );
        response.set_provider_sha256( uri ? targetProviderBefore : "" );

        bool fileBaselineCoherent = true;

        if( baselinePath && baselineCaptured )
        {
            *response.mutable_file_baseline() = ObserveNativeFile( *baselinePath, capturedBaseline );
            fileBaselineCoherent = response.file_baseline().status()
                    == NativeFileBaselineStatus::NFBS_UNCHANGED;

            if( !fileBaselineCoherent )
            {
                capture.canonicalComplete = false;
                AddLimit( capture, "the native footprint file changed or became unreadable during capture" );
            }
        }
        else
        {
            fileBaselineCoherent = source == FOOTPRINT_SOURCE_EDITOR;

            if( !fileBaselineCoherent )
                AddLimit( capture, "native persisted footprint file baseline is unavailable" );
            else
                AddLimit( capture, "native footprint file baseline is unavailable for this editor projection" );
        }

        std::vector<std::string> limitations = capture.limitations;
        for( const std::string& limitation : limitations )
            response.add_limitations( limitation );

        response.set_definition_complete( capture.canonicalComplete && providerKnown
                && ( source == FOOTPRINT_SOURCE_EDITOR || fileBaselineCoherent ) );

        if( source == FOOTPRINT_SOURCE_EDITOR )
            response.mutable_canonical_target()->CopyFrom( aCtx.Request.has_canonical_target()
                    ? aCtx.Request.canonical_target() : document.lib_id() );

        return response;
    }
    catch( const IO_ERROR& error )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( error.What().ToStdString() );
        return tl::unexpected( e );
    }
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
