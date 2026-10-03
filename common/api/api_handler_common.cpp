/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright (C) 2023 Jon Evans <jon@craftyjon.com>
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

#include <ranges>
#include <tuple>

#include <api/api_handler_common.h>
#include <bitmap_trace.h>
#include <build_version.h>
#include <eda_shape.h>
#include <eda_text.h>
#include <gestfich.h>
#include <geometry/shape_compound.h>
#include <google/protobuf/empty.pb.h>
#include <paths.h>
#include <pgm_base.h>
#include <api/api_plugin.h>
#include <api/api_utils.h>
#include <project/net_settings.h>
#include <project/project_file.h>
#include <settings/settings_manager.h>
#include <wx/string.h>
#include <wx/image.h>
#include <wx/mstream.h>
#include <wx/filename.h>
#include <api/native_state_digest.h>
#include <array>
#include <algorithm>
#include <filesystem>
#include <fstream>

using namespace kiapi::common::commands;
using namespace kiapi::common::types;
using google::protobuf::Empty;

namespace
{
constexpr uint64_t MAX_SOURCE_BYTES = 32 * 1024 * 1024;
constexpr uint32_t MAX_PIXELS = 4'194'304;

bool IsSha256( const std::string& aValue )
{
    return aValue.size() == 64 && std::all_of( aValue.begin(), aValue.end(), []( char c )
            { return ( c >= '0' && c <= '9' ) || ( c >= 'a' && c <= 'f' ); } );
}

uint32_t BigEndian32( const unsigned char* aBytes )
{
    return ( static_cast<uint32_t>( aBytes[0] ) << 24 ) | ( static_cast<uint32_t>( aBytes[1] ) << 16 )
           | ( static_cast<uint32_t>( aBytes[2] ) << 8 ) | static_cast<uint32_t>( aBytes[3] );
}

void Fail( ApiResponseStatus& aError, const char* aCode, const std::string& aMessage )
{
    aError.set_status( ApiStatusCode::AS_BAD_REQUEST );
    aError.set_error_message( std::string( aCode ) + ": " + aMessage );
}

kiapi::automation::v1::RasterTraceBounds ToProtoBounds( const BITMAP_TRACE_BOUNDS& aBounds )
{
    kiapi::automation::v1::RasterTraceBounds result;
    result.set_min_x_nm( aBounds.minX ); result.set_min_y_nm( aBounds.minY );
    result.set_max_x_nm( aBounds.maxX ); result.set_max_y_nm( aBounds.maxY );
    return result;
}

void CopyTraceResult( const BITMAP_TRACE_RESULT& aTrace,
                      kiapi::automation::v1::RasterTraceResult& aResult )
{
    aResult.set_accepted( aTrace.accepted );
    aResult.set_rejection_code( aTrace.rejectionCode );
    for( const std::string& diagnostic : aTrace.diagnostics ) aResult.add_diagnostics( diagnostic );
    aResult.set_physical_height_nm( aTrace.physicalHeightNm );
    aResult.set_scale_numerator( aTrace.scaleNumerator );
    aResult.set_scale_denominator( aTrace.scaleDenominator );
    aResult.set_coordinate_orientation( "image_x_right_y_down" );
    aResult.set_rounding( "nearest_integer" );
    aResult.set_geometry_sha256( aTrace.geometrySha256 );
    aResult.set_contour_count( static_cast<uint32_t>( aTrace.contours.size() ) );
    aResult.set_vertex_count( aTrace.vertexCount );
    aResult.set_filled_piece_count( static_cast<uint32_t>( aTrace.filledPieces.size() ) );
    if( !aTrace.contours.empty() ) *aResult.mutable_bounds() = ToProtoBounds( aTrace.bounds );
    for( const auto& contour : aTrace.contours )
    {
        auto* output = aResult.add_contours();
        output->set_id( contour.id ); output->set_parent_id( contour.parentId );
        output->set_hole( contour.hole ); output->set_orientation( contour.orientation );
        *output->mutable_bounds() = ToProtoBounds( contour.bounds );
        for( const auto& point : contour.points )
        {
            auto* target = output->add_points(); target->set_x_nm( point.x ); target->set_y_nm( point.y );
        }
    }
    for( const auto& piece : aTrace.filledPieces )
    {
        auto* output = aResult.add_filled_pieces();
        output->set_id( piece.id ); *output->mutable_bounds() = ToProtoBounds( piece.bounds );
        for( const std::string& source : piece.sourceContourIds ) output->add_source_contour_ids( source );
        for( const auto& point : piece.points )
        {
            auto* target = output->add_points(); target->set_x_nm( point.x ); target->set_y_nm( point.y );
        }
    }
}
}


API_HANDLER_COMMON::API_HANDLER_COMMON() :
        API_HANDLER()
{
    registerHandler<commands::GetVersion, GetVersionResponse>( &API_HANDLER_COMMON::handleGetVersion );
    registerHandler<GetKiCadBinaryPath, PathResponse>(
            &API_HANDLER_COMMON::handleGetKiCadBinaryPath );
    registerHandler<GetNetClasses, NetClassesResponse>( &API_HANDLER_COMMON::handleGetNetClasses );
    registerHandler<SetNetClasses, Empty>( &API_HANDLER_COMMON::handleSetNetClasses );
    registerHandler<Ping, Empty>( &API_HANDLER_COMMON::handlePing );
    registerHandler<GetTextExtents, types::Box2>( &API_HANDLER_COMMON::handleGetTextExtents );
    registerHandler<GetTextAsShapes, GetTextAsShapesResponse>(
            &API_HANDLER_COMMON::handleGetTextAsShapes );
    registerHandler<ExpandTextVariables, ExpandTextVariablesResponse>(
            &API_HANDLER_COMMON::handleExpandTextVariables );
    registerHandler<GetPluginSettingsPath, StringResponse>(
            &API_HANDLER_COMMON::handleGetPluginSettingsPath );
    registerHandler<GetTextVariables, project::TextVariables>(
            &API_HANDLER_COMMON::handleGetTextVariables );
    registerHandler<SetTextVariables, Empty>(
            &API_HANDLER_COMMON::handleSetTextVariables );
    registerHandler<kiapi::automation::v1::RasterTraceRequest,
                    kiapi::automation::v1::RasterTraceResult>( &API_HANDLER_COMMON::handleRasterTrace );

    // OpenDocument, CloseDocument and CloseAllDocuments are registered by their setters, only
    // while the KiCad mode that serves them has set their callbacks.
}


HANDLER_RESULT<kiapi::automation::v1::RasterTraceResult> API_HANDLER_COMMON::handleRasterTrace(
        const HANDLER_CONTEXT<kiapi::automation::v1::RasterTraceRequest>& aCtx )
{
    using namespace kiapi::automation::v1;
    const auto& request = aCtx.Request;
    ApiResponseStatus error;
    wxFileName file( wxString::FromUTF8( request.source_path() ) );
    if( !file.IsAbsolute() || request.source_path().find( '\0' ) != std::string::npos )
    {
        Fail( error, "invalid_source_path", "source_path must be an absolute NUL-free path" );
        return tl::unexpected( error );
    }
    if( !IsSha256( request.expected_source_sha256() ) || request.physical_width_nm() <= 0
        || request.threshold() > 255 || request.turd_size() > 1'000'000 || request.curve_tolerance_nm() <= 0
        || request.max_contours() == 0 || request.max_contours() > 4096
        || request.max_vertices() < 3 || request.max_vertices() > 1'000'000 )
    {
        Fail( error, "invalid_trace_settings", "source hash, physical width, threshold, tolerance and limits are invalid" );
        return tl::unexpected( error );
    }

    std::ifstream input( std::filesystem::path( request.source_path() ), std::ios::binary );
    if( !input ) { Fail( error, "source_unreadable", "the source file could not be opened" ); return tl::unexpected( error ); }
    std::vector<unsigned char> bytes;
    std::array<char, 64 * 1024> buffer;
    while( input )
    {
        input.read( buffer.data(), buffer.size() );
        std::streamsize count = input.gcount();
        if( count > 0 )
        {
            if( bytes.size() > MAX_SOURCE_BYTES - static_cast<size_t>( count ) )
            { Fail( error, "source_too_large", "source bytes exceed the bounded 32 MiB limit" ); return tl::unexpected( error ); }
            bytes.insert( bytes.end(), buffer.data(), buffer.data() + count );
        }
    }
    if( input.bad() ) { Fail( error, "source_unreadable", "the source file could not be read" ); return tl::unexpected( error ); }
    NATIVE_STATE_DIGEST sourceHash; sourceHash.Append( std::string_view( reinterpret_cast<const char*>( bytes.data() ), bytes.size() ) );
    const std::string actualHash = sourceHash.Hex();
    if( actualHash != request.expected_source_sha256() )
    { Fail( error, "source_hash_mismatch", "the exact bytes read do not match expected_source_sha256" ); return tl::unexpected( error ); }

    // PNG-first boundary: inspect IHDR before wxImage allocates decoded pixels.
    if( bytes.size() < 33 || !std::equal( bytes.begin(), bytes.begin() + 8,
            std::array<unsigned char, 8>{ 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }.begin() )
        || BigEndian32( bytes.data() + 8 ) != 13 || std::string( reinterpret_cast<char*>( bytes.data() + 12 ), 4 ) != "IHDR" )
    { Fail( error, "unsupported_raster_format", "only PNG sources are supported by this bounded proposal" ); return tl::unexpected( error ); }
    const uint32_t width = BigEndian32( bytes.data() + 16 ), height = BigEndian32( bytes.data() + 20 );
    if( width == 0 || height == 0 || width > 4096 || height > 4096
        || static_cast<uint64_t>( width ) * height > MAX_PIXELS )
    { Fail( error, "raster_too_large", "PNG dimensions exceed the bounded pixel limit" ); return tl::unexpected( error ); }

    wxMemoryInputStream stream( bytes.data(), bytes.size() );
    wxImage image;
    if( !image.LoadFile( stream, wxBITMAP_TYPE_PNG ) || image.GetWidth() != static_cast<int>( width )
        || image.GetHeight() != static_cast<int>( height ) )
    { Fail( error, "decode_failed", "the PNG could not be decoded consistently" ); return tl::unexpected( error ); }
    const unsigned char* rgb = image.GetData(); const unsigned char* alpha = image.HasAlpha() ? image.GetAlpha() : nullptr;
    if( !rgb ) { Fail( error, "decode_failed", "decoded PNG has no pixel buffer" ); return tl::unexpected( error ); }
    BITMAP_TRACE_RASTER raster{ width, height, std::vector<uint8_t>( static_cast<size_t>( width ) * height ) };
    for( size_t i = 0; i < raster.foreground.size(); ++i )
    {
        const unsigned char luma = static_cast<unsigned char>( ( 299 * rgb[i * 3] + 587 * rgb[i * 3 + 1] + 114 * rgb[i * 3 + 2] ) / 1000 );
        bool foreground = alpha && alpha[i] == 0 ? false : luma <= request.threshold();
        raster.foreground[i] = request.invert() ? !foreground : foreground;
    }
    BITMAP_TRACE_SETTINGS settings{ request.physical_width_nm(), request.turd_size(), request.curve_tolerance_nm(), request.max_contours(), request.max_vertices() };
    BITMAP_TRACE_RESULT trace = TraceBitmap( raster, settings );
    RasterTraceResult result;
    result.set_source_sha256( actualHash ); result.set_source_bytes( bytes.size() );
    result.set_decoded_width( width ); result.set_decoded_height( height ); result.set_decoded_format( "png" );
    result.set_physical_width_nm( request.physical_width_nm() ); result.set_threshold( request.threshold() );
    result.set_invert( request.invert() ); result.set_turd_size( request.turd_size() );
    result.set_curve_tolerance_nm( request.curve_tolerance_nm() ); result.set_max_contours( request.max_contours() );
    result.set_max_vertices( request.max_vertices() ); result.set_proposal_only( true );
    result.set_native_document_mutated( false ); result.set_cad_projection_committed( false );
    result.set_electrical_validated( false ); result.set_manufacturing_validated( false );
    CopyTraceResult( trace, result );
    return result;
}


void API_HANDLER_COMMON::SetOpenDocumentHandler( OPEN_DOCUMENT_HANDLER aHandler )
{
    m_openDocumentHandler = std::move( aHandler );
    m_handlers.erase( std::string( OpenDocument().GetTypeName() ) );

    if( m_openDocumentHandler )
        registerHandler<OpenDocument, OpenDocumentResponse>( &API_HANDLER_COMMON::handleOpenDocument );
}


void API_HANDLER_COMMON::SetCloseDocumentHandler( CLOSE_DOCUMENT_HANDLER aHandler )
{
    m_closeDocumentHandler = std::move( aHandler );
    m_handlers.erase( std::string( CloseDocument().GetTypeName() ) );

    if( m_closeDocumentHandler )
        registerHandler<CloseDocument, Empty>( &API_HANDLER_COMMON::handleCloseDocument );
}


void API_HANDLER_COMMON::SetCloseAllDocumentsHandler( CLOSE_ALL_DOCUMENTS_HANDLER aHandler )
{
    m_closeAllDocumentsHandler = std::move( aHandler );
    m_handlers.erase( std::string( CloseAllDocuments().GetTypeName() ) );

    if( m_closeAllDocumentsHandler )
        registerHandler<CloseAllDocuments, Empty>( &API_HANDLER_COMMON::handleCloseAllDocuments );
}


HANDLER_RESULT<GetVersionResponse> API_HANDLER_COMMON::handleGetVersion(
        const HANDLER_CONTEXT<commands::GetVersion>& )
{
    GetVersionResponse reply;

    reply.mutable_version()->set_full_version( GetBuildVersion().ToStdString() );

    std::tuple<int, int, int> version = GetMajorMinorPatchTuple();
    reply.mutable_version()->set_major( std::get<0>( version ) );
    reply.mutable_version()->set_minor( std::get<1>( version ) );
    reply.mutable_version()->set_patch( std::get<2>( version ) );

    return reply;
}


HANDLER_RESULT<PathResponse> API_HANDLER_COMMON::handleGetKiCadBinaryPath(
        const HANDLER_CONTEXT<GetKiCadBinaryPath>& aCtx )
{
    wxFileName fn( wxEmptyString, wxString::FromUTF8( aCtx.Request.binary_name() ) );
#ifdef _WIN32
    fn.SetExt( wxT( "exe" ) );
#endif

    wxString path = FindKicadFile( fn.GetFullName() );
    PathResponse reply;
    reply.set_path( path.ToUTF8() );
    return reply;
}


HANDLER_RESULT<NetClassesResponse> API_HANDLER_COMMON::handleGetNetClasses(
        const HANDLER_CONTEXT<GetNetClasses>& aCtx )
{
    NetClassesResponse reply;

    std::shared_ptr<NET_SETTINGS>& netSettings =
            Pgm().GetSettingsManager().Prj().GetProjectFile().m_NetSettings;

    google::protobuf::Any any;

    netSettings->GetDefaultNetclass()->Serialize( any );
    any.UnpackTo( reply.add_net_classes() );

    for( const auto& netClass : netSettings->GetNetclasses() | std::views::values )
    {
        netClass->Serialize( any );
        any.UnpackTo( reply.add_net_classes() );
    }

    return reply;
}


HANDLER_RESULT<Empty> API_HANDLER_COMMON::handleSetNetClasses(
        const HANDLER_CONTEXT<SetNetClasses>& aCtx )
{
    std::shared_ptr<NET_SETTINGS>& netSettings =
            Pgm().GetSettingsManager().Prj().GetProjectFile().m_NetSettings;

    if( aCtx.Request.merge_mode() == MapMergeMode::MMM_REPLACE )
        netSettings->ClearNetclasses();

    auto netClasses = netSettings->GetNetclasses();
    google::protobuf::Any any;

    for( const auto& ncProto : aCtx.Request.net_classes() )
    {
        any.PackFrom( ncProto );
        wxString name = wxString::FromUTF8( ncProto.name() );

        if( name == wxT( "Default" ) )
        {
            netSettings->GetDefaultNetclass()->Deserialize( any );
        }
        else
        {
            if( !netClasses.contains( name ) )
                netClasses.insert( { name, std::make_shared<NETCLASS>( name, false ) } );

            netClasses[name]->Deserialize( any );
        }
    }

    netSettings->SetNetclasses( netClasses );

    return Empty();
}


HANDLER_RESULT<Empty> API_HANDLER_COMMON::handlePing( const HANDLER_CONTEXT<Ping>& aCtx )
{
    return Empty();
}


HANDLER_RESULT<types::Box2> API_HANDLER_COMMON::handleGetTextExtents(
        const HANDLER_CONTEXT<GetTextExtents>& aCtx )
{
    EDA_TEXT text( pcbIUScale );
    google::protobuf::Any any;
    any.PackFrom( aCtx.Request.text() );

    if( !text.Deserialize( any ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "Could not decode text in GetTextExtents message" );
        return tl::unexpected( e );
    }

    types::Box2 response;

    BOX2I bbox = text.GetTextBox( nullptr );
    EDA_ANGLE angle = text.GetTextAngle();

    if( !angle.IsZero() )
        bbox = bbox.GetBoundingBoxRotated( text.GetTextPos(), text.GetTextAngle() );

    response.mutable_position()->set_x_nm( bbox.GetPosition().x );
    response.mutable_position()->set_y_nm( bbox.GetPosition().y );
    response.mutable_size()->set_x_nm( bbox.GetSize().x );
    response.mutable_size()->set_y_nm( bbox.GetSize().y );

    return response;
}


HANDLER_RESULT<GetTextAsShapesResponse> API_HANDLER_COMMON::handleGetTextAsShapes(
        const HANDLER_CONTEXT<GetTextAsShapes>& aCtx )
{
    GetTextAsShapesResponse reply;

    for( const TextOrTextBox& textMsg : aCtx.Request.text() )
    {
        Text dummyText;
        const Text* textPtr = &textMsg.text();

        if( textMsg.has_textbox() )
        {
            dummyText.set_text( textMsg.textbox().text() );
            dummyText.mutable_attributes()->CopyFrom( textMsg.textbox().attributes() );
            textPtr = &dummyText;
        }

        EDA_TEXT text( pcbIUScale );
        google::protobuf::Any any;
        any.PackFrom( *textPtr );

        if( !text.Deserialize( any ) )
        {
            ApiResponseStatus e;
            e.set_status( ApiStatusCode::AS_BAD_REQUEST );
            e.set_error_message( "Could not decode text in GetTextAsShapes message" );
            return tl::unexpected( e );
        }

        std::shared_ptr<SHAPE_COMPOUND> shapes = text.GetEffectiveTextShape( false );

        TextWithShapes* entry = reply.add_text_with_shapes();
        entry->mutable_text()->CopyFrom( textMsg );

        for( SHAPE* subshape : shapes->Shapes() )
        {
            EDA_SHAPE proxy( *subshape );
            proxy.Serialize( any );
            GraphicShape* shapeMsg = entry->mutable_shapes()->add_shapes();
            any.UnpackTo( shapeMsg );
        }

        if( textMsg.has_textbox() )
        {
            GraphicShape* border = entry->mutable_shapes()->add_shapes();
            int width = textMsg.textbox().attributes().stroke_width().value_nm();
            border->mutable_attributes()->mutable_stroke()->mutable_width()->set_value_nm( width );
            VECTOR2I tl = UnpackVector2( textMsg.textbox().top_left() );
            VECTOR2I br = UnpackVector2( textMsg.textbox().bottom_right() );

            // top
            PackVector2( *border->mutable_segment()->mutable_start(), tl );
            PackVector2( *border->mutable_segment()->mutable_end(), VECTOR2I( br.x, tl.y ) );

            // right
            border = entry->mutable_shapes()->add_shapes();
            border->mutable_attributes()->mutable_stroke()->mutable_width()->set_value_nm( width );
            PackVector2( *border->mutable_segment()->mutable_start(), VECTOR2I( br.x, tl.y ) );
            PackVector2( *border->mutable_segment()->mutable_end(), br );

            // bottom
            border = entry->mutable_shapes()->add_shapes();
            border->mutable_attributes()->mutable_stroke()->mutable_width()->set_value_nm( width );
            PackVector2( *border->mutable_segment()->mutable_start(), br );
            PackVector2( *border->mutable_segment()->mutable_end(), VECTOR2I( tl.x, br.y ) );

            // left
            border = entry->mutable_shapes()->add_shapes();
            border->mutable_attributes()->mutable_stroke()->mutable_width()->set_value_nm( width );
            PackVector2( *border->mutable_segment()->mutable_start(), VECTOR2I( tl.x, br.y ) );
            PackVector2( *border->mutable_segment()->mutable_end(), tl );
        }
    }

    return reply;
}


HANDLER_RESULT<ExpandTextVariablesResponse> API_HANDLER_COMMON::handleExpandTextVariables(
        const HANDLER_CONTEXT<ExpandTextVariables>& aCtx )
{
    if( !aCtx.Request.has_document() || aCtx.Request.document().type() != DOCTYPE_PROJECT )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        // No error message, this is a flag that the server should try a different handler
        return tl::unexpected( e );
    }

    ExpandTextVariablesResponse reply;
    PROJECT& project = Pgm().GetSettingsManager().Prj();

    for( const std::string& textMsg : aCtx.Request.text() )
    {
        wxString result = ExpandTextVars( wxString::FromUTF8( textMsg ), &project );

        if( aCtx.Request.expand_env_vars() )
            result = ExpandEnvVarSubstitutions( result, &project );

        reply.add_text( result.ToUTF8() );
    }

    return reply;
}


HANDLER_RESULT<StringResponse> API_HANDLER_COMMON::handleGetPluginSettingsPath(
        const HANDLER_CONTEXT<GetPluginSettingsPath>& aCtx )
{
    wxString identifier = wxString::FromUTF8( aCtx.Request.identifier() );

    if( identifier.IsEmpty() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "plugin identifier is missing" );
        return tl::unexpected( e );
    }

    if( !API_PLUGIN::IsValidIdentifier( identifier ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "plugin identifier is invalid" );
        return tl::unexpected( e );
    }

    wxFileName path( PATHS::GetUserSettingsPath(), wxEmptyString );
    path.AppendDir( "plugins" );

    // Create the base plugins path if needed, but leave the specific plugin to create its own path
    PATHS::EnsurePathExists( path.GetPath() );

    path.AppendDir( identifier );

    StringResponse reply;
    reply.set_response( path.GetPath() );
    return reply;
}


HANDLER_RESULT<project::TextVariables> API_HANDLER_COMMON::handleGetTextVariables(
        const HANDLER_CONTEXT<GetTextVariables>& aCtx )
{
    if( !aCtx.Request.has_document() || aCtx.Request.document().type() != DOCTYPE_PROJECT )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        // No error message, this is a flag that the server should try a different handler
        return tl::unexpected( e );
    }

    const PROJECT& project = Pgm().GetSettingsManager().Prj();

    if( project.IsNullProject() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_NOT_READY );
        e.set_error_message( "no valid project is loaded, cannot get text variables" );
        return tl::unexpected( e );
    }

    const std::map<wxString, wxString>& vars = project.GetTextVars();

    project::TextVariables reply;
    auto map = reply.mutable_variables();

    for( const auto& [key, value] : vars )
        ( *map )[ std::string( key.ToUTF8() ) ] = value.ToUTF8();

    return reply;
}


HANDLER_RESULT<Empty> API_HANDLER_COMMON::handleSetTextVariables(
    const HANDLER_CONTEXT<SetTextVariables>& aCtx )
{
    if( !aCtx.Request.has_document() || aCtx.Request.document().type() != DOCTYPE_PROJECT )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        // No error message, this is a flag that the server should try a different handler
        return tl::unexpected( e );
    }

    PROJECT& project = Pgm().GetSettingsManager().Prj();

    if( project.IsNullProject() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_NOT_READY );
        e.set_error_message( "no valid project is loaded, cannot set text variables" );
        return tl::unexpected( e );
    }

    const project::TextVariables& newVars = aCtx.Request.variables();
    std::map<wxString, wxString>& vars = project.GetTextVars();

    if( aCtx.Request.merge_mode() == MapMergeMode::MMM_REPLACE )
        vars.clear();

    for( const auto& [key, value] : newVars.variables() )
        vars[wxString::FromUTF8( key )] = wxString::FromUTF8( value );

    Pgm().GetSettingsManager().SaveProject();

    return Empty();
}


HANDLER_RESULT<OpenDocumentResponse> API_HANDLER_COMMON::handleOpenDocument(
        const HANDLER_CONTEXT<OpenDocument>& aCtx )
{
    // Registered only while m_openDocumentHandler is set.
    return m_openDocumentHandler( aCtx.Request );
}


HANDLER_RESULT<Empty> API_HANDLER_COMMON::handleCloseDocument(
        const HANDLER_CONTEXT<CloseDocument>& aCtx )
{
    // Registered only while m_closeDocumentHandler is set.
    return m_closeDocumentHandler( aCtx.Request );
}


HANDLER_RESULT<Empty> API_HANDLER_COMMON::handleCloseAllDocuments( const HANDLER_CONTEXT<CloseAllDocuments>& aCtx )
{
    // Registered only while m_closeAllDocumentsHandler is set.
    return m_closeAllDocumentsHandler( aCtx.Request );
}
