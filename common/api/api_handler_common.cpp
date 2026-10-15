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
#include <cctype>

#include <api/api_handler_common.h>
#include <api/api_server.h>
#include <api/document_lifecycle_controller.h>
#include <bitmap_trace.h>
#include <build_version.h>
#include <eda_shape.h>
#include <eda_text.h>
#include <gestfich.h>
#include <geometry/shape_compound.h>
#include <google/protobuf/empty.pb.h>
#include <google/protobuf/util/message_differencer.h>
#include <paths.h>
#include <pgm_base.h>
#include <api/api_plugin.h>
#include <api/api_utils.h>
#include <netclass.h>
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
#include <map>
#include <optional>
#include <project.h>
#include <set>
#include <vector>

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

bool IsOperationId( const std::string& aValue )
{
    if( aValue.size() != 36 ) return false;
    for( size_t index = 0; index < aValue.size(); ++index )
    {
        if( index == 8 || index == 13 || index == 18 || index == 23 )
        {
            if( aValue[index] != '-' ) return false;
        }
        else if( !std::isxdigit( static_cast<unsigned char>( aValue[index] ) ) )
            return false;
    }
    return true;
}


bool IsCanonicalUuid( const std::string& aValue )
{
    if( aValue.size() != 36 ) return false;
    bool nonzero = false;

    for( size_t index = 0; index < aValue.size(); ++index )
    {
        const char c = aValue[index];

        if( index == 8 || index == 13 || index == 18 || index == 23 )
        {
            if( c != '-' ) return false;
        }
        else
        {
            if( !( ( c >= '0' && c <= '9' ) || ( c >= 'a' && c <= 'f' ) ) ) return false;
            nonzero |= c != '0';
        }
    }

    return nonzero;
}


size_t Utf16Length( const std::string& aValue )
{
    size_t result = 0;

    for( size_t index = 0; index < aValue.size(); )
    {
        const unsigned char first = static_cast<unsigned char>( aValue[index] );
        uint32_t codepoint = 0;
        size_t width = 0;

        if( first < 0x80 )
        {
            codepoint = first;
            width = 1;
        }
        else if( ( first & 0xe0 ) == 0xc0 )
        {
            codepoint = first & 0x1f;
            width = 2;
        }
        else if( ( first & 0xf0 ) == 0xe0 )
        {
            codepoint = first & 0x0f;
            width = 3;
        }
        else if( ( first & 0xf8 ) == 0xf0 )
        {
            codepoint = first & 0x07;
            width = 4;
        }
        else
        {
            return aValue.size();
        }

        if( index + width > aValue.size() ) return aValue.size();

        for( size_t offset = 1; offset < width; ++offset )
        {
            const unsigned char continuation = static_cast<unsigned char>( aValue[index + offset] );

            if( ( continuation & 0xc0 ) != 0x80 ) return aValue.size();
            codepoint = ( codepoint << 6 ) | ( continuation & 0x3f );
        }

        if( ( width == 2 && codepoint < 0x80 ) || ( width == 3 && codepoint < 0x800 )
            || ( width == 4 && codepoint < 0x10000 ) || codepoint > 0x10ffff
            || ( codepoint >= 0xd800 && codepoint <= 0xdfff ) )
            return aValue.size();

        result += codepoint > 0xffff ? 2 : 1;
        index += width;
    }

    return result;
}


std::string NetClassesDigest( const NetClassesResponse& aResponse )
{
    std::vector<const kiapi::common::project::NetClass*> classes;
    classes.reserve( aResponse.net_classes_size() );

    for( const auto& netClass : aResponse.net_classes() )
        classes.push_back( &netClass );

    std::sort( classes.begin(), classes.end(), []( const auto* left, const auto* right )
    {
        return left->name() < right->name();
    } );

    NATIVE_STATE_DIGEST digest;

    for( const auto* netClass : classes )
    {
        const std::string serialized = netClass->SerializeAsString();
        digest.Append( std::to_string( Utf16Length( netClass->name() ) ) );
        digest.Append( "\n" );
        digest.Append( netClass->name() );
        digest.Append( std::to_string( serialized.size() ) );
        digest.Append( "\n" );
        digest.Append( serialized );
    }

    return digest.Hex();
}


NetClassesResponse ReadProjectNetClasses( const PROJECT& aProject )
{
    NetClassesResponse result;
    const std::shared_ptr<NET_SETTINGS>& settings = aProject.GetProjectFile().m_NetSettings;

    if( settings && settings->GetDefaultNetclass() )
        settings->GetDefaultNetclass()->Serialize( *result.add_net_classes() );

    if( settings )
    {
        for( const auto& [unused, netClass] : settings->GetNetclasses() )
        {
            if( netClass ) netClass->Serialize( *result.add_net_classes() );
        }
    }

    return result;
}


bool ValidExplicitNetClasses( const NetClassesResponse& aResponse, std::string& aReason )
{
    if( aResponse.net_classes_size() > 4096 )
    {
        aReason = "a checked project netclass request is limited to 4096 classes";
        return false;
    }

    std::set<std::string> names;

    for( const auto& netClass : aResponse.net_classes() )
    {
        if( netClass.name().empty() || netClass.name().find( '\0' ) != std::string::npos
            || netClass.type() != kiapi::common::project::NCT_EXPLICIT || netClass.constituents_size() != 0
            || !names.insert( netClass.name() ).second )
        {
            aReason = "checked project netclasses must be unique named explicit classes";
            return false;
        }

        NETCLASS candidate( wxString::FromUTF8( netClass.name() ), false );
        if( !candidate.Deserialize( netClass ) )
        {
            aReason = "a checked project netclass contains unsupported values";
            return false;
        }
    }

    return true;
}


bool ValidCheckedProjectNetClassesRequest(
        const kiapi::automation::v1::CheckedProjectNetClassesTransaction& aRequest,
        const std::string& aProcessEpoch, bool aInspect, std::string& aReason )
{
    using namespace kiapi::automation::v1;

    if( !aRequest.has_project() || aRequest.project().type() != DOCTYPE_PROJECT
        || !aRequest.project().has_project() || aRequest.project().project().name().empty()
        || aRequest.project().project().path().empty() || !aRequest.has_document()
        || aRequest.document().type() != DOCTYPE_PCB || !aRequest.document().has_board_filename()
        || aRequest.document().board_filename().empty() || !aRequest.document().has_project()
        || !google::protobuf::util::MessageDifferencer::Equals( aRequest.project().project(),
                                                                 aRequest.document().project() ) )
    {
        aReason = "checked project netclasses require one named project and a PCB in that project";
        return false;
    }

    const wxFileName projectPath = wxFileName::DirName( wxString::FromUTF8( aRequest.project().project().path() ) );
    if( !projectPath.IsAbsolute() || aRequest.project().project().path().find( '\0' ) != std::string::npos
        || aRequest.document().board_filename().find( '\0' ) != std::string::npos )
    {
        aReason = "the checked project netclass project path must be absolute and NUL-free";
        return false;
    }

    if( !IsCanonicalUuid( aProcessEpoch ) || aRequest.process_epoch() != aProcessEpoch
        || !IsCanonicalUuid( aRequest.operation_id() ) )
    {
        aReason = "checked project netclasses require one canonical operation and process UUID";
        return false;
    }

    if( !aRequest.has_expected_state() || aRequest.expected_state().scope() != DLS_PCB
        || !aRequest.expected_state().has_document()
        || !google::protobuf::util::MessageDifferencer::Equals( aRequest.expected_state().document(),
                                                                 aRequest.document() )
        || aRequest.expected_state().process_epoch() != aProcessEpoch
        || !aRequest.expected_state().has_revision()
        || !IsCanonicalUuid( aRequest.expected_state().native_identity() )
        || aRequest.expected_state().revision().epoch() != aRequest.expected_state().native_identity()
        || !IsSha256( aRequest.expected_state().state_sha256() )
        || !aRequest.expected_state().project_settings_included()
        || !aRequest.expected_state().disk_baseline_checked() )
    {
        aReason = "checked project netclasses require an exact PCB lifecycle observation";
        return false;
    }

    if( !aInspect && !DOCUMENT_LIFECYCLE_CONTROLLER::HasUnchangedFileBaselines( aRequest.expected_state() ) )
    {
        aReason = "the checked PCB lifecycle observation lacks complete unchanged file coverage";
        return false;
    }

    if( !IsSha256( aRequest.expected_net_classes_sha256() ) )
    {
        aReason = "expected_net_classes_sha256 must be a lowercase SHA-256 digest";
        return false;
    }

    NetClassesResponse requested;
    for( const auto& netClass : aRequest.net_classes() )
        *requested.add_net_classes() = netClass;
    if( !ValidExplicitNetClasses( requested, aReason ) ) return false;

    if( aRequest.merge_mode() != MapMergeMode::MMM_MERGE
        && aRequest.merge_mode() != MapMergeMode::MMM_REPLACE )
    {
        aReason = "merge_mode must be MMM_MERGE or MMM_REPLACE";
        return false;
    }

    if( aRequest.maximum_result_bytes() != 0
        && ( aRequest.maximum_result_bytes() < 64 * 1024 || aRequest.maximum_result_bytes() > 4 * 1024 * 1024 ) )
    {
        aReason = "maximum_result_bytes must be zero or between 64 KiB and 4 MiB";
        return false;
    }

    if( aRequest.ByteSizeLong() > 4 * 1024 * 1024 )
    {
        aReason = "the checked project netclass request exceeds its 4 MiB limit";
        return false;
    }

    return true;
}

std::string TextVariablesDigest( const std::map<std::string, std::string>& aVariables )
{
    NATIVE_STATE_DIGEST digest;
    for( const auto& [key, value] : aVariables )
    {
        digest.Append( std::to_string( key.size() ) ); digest.Append( "\n" ); digest.Append( key );
        digest.Append( std::to_string( value.size() ) ); digest.Append( "\n" ); digest.Append( value );
    }
    return digest.Hex();
}

std::map<std::string, std::string> ReadTextVariables( const PROJECT& aProject )
{
    std::map<std::string, std::string> variables;
    for( const auto& [key, value] : aProject.GetTextVars() )
    {
        const wxScopedCharBuffer keyUtf8 = key.ToUTF8();
        const wxScopedCharBuffer valueUtf8 = value.ToUTF8();
        variables.emplace( keyUtf8.data() ? keyUtf8.data() : "", valueUtf8.data() ? valueUtf8.data() : "" );
    }
    return variables;
}

void PackTextVariables( const std::map<std::string, std::string>& aVariables,
                        project::TextVariables& aOutput )
{
    aOutput.mutable_variables()->clear();
    for( const auto& [key, value] : aVariables )
        ( *aOutput.mutable_variables() )[key] = value;
}

std::optional<ApiResponseStatus> ValidateProjectVariableTarget( const DocumentSpecifier& aDocument,
                                                                const PROJECT& aProject,
                                                                const KICAD_API_SERVER* aServer )
{
    const KICAD_API_SERVER* server = aServer;

    // SA-04: the automation boundary serves one explicit project and must reject a stale or
    // foreign descriptor before exposing or mutating project-owned values.
    // Existing SDK callers may omit the project submessage in the non-automation API server.
    // Automation instances always have one owned project and must bind requests to it.
    if( !server || !server->IsAutomation() )
    {
        if( !aDocument.has_project() )
            return std::nullopt;
    }
    else if( !aDocument.has_project() )
    {
        ApiResponseStatus error;
        Fail( error, "project_target_required", "automation project requests must identify the project name and absolute directory" );
        return error;
    }

    if( !aDocument.has_project() )
        return std::nullopt;

    const std::string& requestedPath = aDocument.project().path();
    if( requestedPath.empty() || requestedPath.find( '\0' ) != std::string::npos
        || aDocument.project().name().empty() || aDocument.project().name().find( '\0' ) != std::string::npos )
    {
        ApiResponseStatus error;
        Fail( error, "project_target_invalid", "project.path must be a non-empty NUL-free absolute directory" );
        return error;
    }

    wxFileName requested = wxFileName::DirName( wxString::FromUTF8( requestedPath ) );
    wxFileName current = wxFileName::DirName( aProject.GetProjectPath() );
    if( !requested.IsAbsolute() )
    {
        ApiResponseStatus error;
        Fail( error, "project_target_invalid", "project.path must be an absolute directory" );
        return error;
    }
    requested.Normalize( wxPATH_NORM_DOTS | wxPATH_NORM_ABSOLUTE );
    current.Normalize( wxPATH_NORM_DOTS | wxPATH_NORM_ABSOLUTE );

    bool matches = requested.IsAbsolute() && requested == current
                   && aDocument.project().name() == aProject.GetProjectName().ToStdString( wxConvUTF8 );

    if( matches && server && server->IsAutomation() )
    {
        wxFileName configured( wxString::FromUTF8( server->AutomationProjectPath() ) );
        wxFileName loaded( aProject.GetProjectFullName() );
        configured.Normalize( wxPATH_NORM_DOTS | wxPATH_NORM_ABSOLUTE );
        loaded.Normalize( wxPATH_NORM_DOTS | wxPATH_NORM_ABSOLUTE );
        matches = configured.IsAbsolute() && configured == loaded;
    }

    if( !matches )
    {
        ApiResponseStatus error;
        Fail( error, "project_target_mismatch", "the requested project does not match this API instance's loaded project" );
        return error;
    }

    return std::nullopt;
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
    aResult.set_potrace_opt_tolerance_px( aTrace.potraceOptTolerancePx );
    aResult.set_contributor_semantics( "filled pieces report whole-trace contour provenance; exact contributors are deferred to target projection" );
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
        output->set_source_contours_are_whole_trace( piece.sourceContoursAreWholeTrace );
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
    registerHandler<kiapi::automation::v1::CheckedProjectNetClassesTransaction,
                    kiapi::automation::v1::CheckedProjectNetClassesReceipt>(
            &API_HANDLER_COMMON::handleCheckedProjectNetClasses );
    registerHandler<kiapi::automation::v1::ReadCheckedProjectNetClassesReceipt,
                    kiapi::automation::v1::CheckedProjectNetClassesReceipt>(
            &API_HANDLER_COMMON::handleReadCheckedProjectNetClassesReceipt );
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
    registerHandler<SetTextVariablesChecked, SetTextVariablesCheckedResponse>(
            &API_HANDLER_COMMON::handleSetTextVariablesChecked );
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
    BITMAP_TRACE_SETTINGS settings{ request.physical_width_nm(), request.threshold(), request.invert(), request.turd_size(),
                                    request.curve_tolerance_nm(), request.max_contours(), request.max_vertices() };
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


HANDLER_RESULT<kiapi::automation::v1::CheckedProjectNetClassesReceipt>
API_HANDLER_COMMON::handleCheckedProjectNetClasses(
        const HANDLER_CONTEXT<kiapi::automation::v1::CheckedProjectNetClassesTransaction>& aCtx )
{
    using namespace kiapi::automation::v1;
    using google::protobuf::util::MessageDifferencer;

    const auto& request = aCtx.Request;
    const std::string processEpoch = apiServerOrNull() ? apiServer().Token() : std::string();

    auto reject = []( const char* code, const std::string& message )
            -> HANDLER_RESULT<CheckedProjectNetClassesReceipt>
    {
        ApiResponseStatus error;
        Fail( error, code, message );
        return tl::unexpected( error );
    };

    std::string reason;
    if( !apiServerOrNull() || !ValidCheckedProjectNetClassesRequest( request, processEpoch, false, reason ) )
        return reject( "project_net_classes_admission_invalid", reason.empty()
                ? "the checked project netclass owner is not attached to a native API server" : reason );

    PROJECT& project = Pgm().GetSettingsManager().Prj();
    if( project.IsNullProject() )
        return reject( "project_net_classes_project_unavailable", "no valid project is loaded" );

    if( auto target = ValidateProjectVariableTarget( request.project(), project, apiServerOrNull() ) )
        return tl::unexpected( *target );
    if( auto target = ValidateProjectVariableTarget( request.document(), project, apiServerOrNull() ) )
        return tl::unexpected( *target );

    auto existing = m_checkedProjectNetClassesReceipts.find( request.operation_id() );
    if( existing != m_checkedProjectNetClassesReceipts.end() )
    {
        if( !MessageDifferencer::Equals( existing->second.request, request ) )
            return reject( "operation_id_reused",
                           "the operation ID is already bound to another checked project netclass request" );

        CheckedProjectNetClassesReceipt replay = existing->second.response;
        replay.set_replayed( true );
        return replay;
    }

    auto observe = [&]() -> HANDLER_RESULT<DocumentLifecycleState>
    {
        ReadDocumentLifecycleState query;
        query.mutable_document()->CopyFrom( request.document() );
        ApiRequest envelope;
        envelope.mutable_message()->PackFrom( query );
        API_RESULT reply = apiServer().DispatchToHandlers( envelope );

        if( !reply ) return tl::unexpected( reply.error() );
        if( reply->status().status() != ApiStatusCode::AS_OK )
            return tl::unexpected( reply->status() );

        DocumentLifecycleState state;
        if( !reply->message().UnpackTo( &state ) )
        {
            ApiResponseStatus error;
            error.set_status( ApiStatusCode::AS_BAD_REQUEST );
            error.set_error_message( "the native PCB lifecycle observation was not parseable" );
            return tl::unexpected( error );
        }

        return state;
    };

    auto beforeState = observe();
    if( !beforeState )
        return reject( "project_net_classes_observation_failed", beforeState.error().error_message() );

    if( !MessageDifferencer::Equals( *beforeState, request.expected_state() ) )
        return reject( "project_net_classes_stale",
                       "the PCB lifecycle state changed; inspect a fresh state before writing" );

    const PROJECT& checkedProject = project;
    const std::string currentDigest = NetClassesDigest( ReadProjectNetClasses( checkedProject ) );
    if( currentDigest != request.expected_net_classes_sha256() )
        return reject( "project_net_classes_stale",
                       "the project netclass map changed; inspect a fresh map before writing" );

    // Project NET_SETTINGS has no native PCB editor undo/redo owner. Keep the checked
    // mutation unavailable until an editor-backed owner can provide a real undo entry
    // and post-undo/redo evidence; never mutate project settings behind a false receipt.
    if( m_checkedProjectNetClassesReceipts.size() >= 4096 )
        return reject( "project_net_classes_receipt_capacity_exhausted",
                       "checked project netclass receipt capacity is exhausted; no mutation was attempted" );

    CHECKED_PROJECT_NET_CLASSES_RECEIPT retained;
    retained.request = request;
    auto& response = retained.response;
    response.mutable_project()->CopyFrom( request.project() );
    response.mutable_document()->CopyFrom( request.document() );
    response.set_process_epoch( processEpoch );
    response.set_operation_id( request.operation_id() );
    response.set_state( CPCS_UNAVAILABLE );
    response.set_phase( CPCP_UNKNOWN );
    response.set_rollback_state( CPCRS_NOT_ATTEMPTED );
    response.set_post_commit_state( CPCPS_NOT_ATTEMPTED );
    response.set_error_code( "project_settings_checked_unavailable" );
    response.set_error_message(
            "The native PCB project-settings owner has no editor-backed undo/redo or save/reload path yet; no mutation was attempted" );
    response.set_undo_redo_supported( false );

    auto inserted = m_checkedProjectNetClassesReceipts.emplace( request.operation_id(), std::move( retained ) );
    if( !inserted.second )
        return reject( "operation_id_reused",
                       "the operation ID is already bound to another checked project netclass request" );

    return inserted.first->second.response;
}


HANDLER_RESULT<kiapi::automation::v1::CheckedProjectNetClassesReceipt>
API_HANDLER_COMMON::handleReadCheckedProjectNetClassesReceipt(
        const HANDLER_CONTEXT<kiapi::automation::v1::ReadCheckedProjectNetClassesReceipt>& aCtx )
{
    using namespace kiapi::automation::v1;
    using google::protobuf::util::MessageDifferencer;

    const auto& query = aCtx.Request;
    const std::string processEpoch = apiServerOrNull() ? apiServer().Token() : std::string();
    std::string reason;

    if( !apiServerOrNull() || !query.has_expected_request()
        || !ValidCheckedProjectNetClassesRequest( query.expected_request(), processEpoch, true, reason )
        || query.process_epoch() != processEpoch || query.operation_id() != query.expected_request().operation_id()
        || !MessageDifferencer::Equals( query.project(), query.expected_request().project() )
        || !MessageDifferencer::Equals( query.document(), query.expected_request().document() ) )
    {
        ApiResponseStatus error;
        Fail( error, "project_net_classes_receipt_query_invalid",
              reason.empty() ? "the checked project netclass receipt query must identify its exact request" : reason );
        return tl::unexpected( error );
    }

    PROJECT& project = Pgm().GetSettingsManager().Prj();
    if( project.IsNullProject() )
        return tl::unexpected( [&]() { ApiResponseStatus e; Fail( e, "project_net_classes_project_unavailable", "no valid project is loaded" ); return e; }() );

    if( auto target = ValidateProjectVariableTarget( query.project(), project, apiServerOrNull() ) )
        return tl::unexpected( *target );

    auto found = m_checkedProjectNetClassesReceipts.find( query.operation_id() );
    if( found == m_checkedProjectNetClassesReceipts.end() )
    {
        CheckedProjectNetClassesReceipt result;
        result.mutable_project()->CopyFrom( query.project() );
        result.mutable_document()->CopyFrom( query.document() );
        result.set_process_epoch( processEpoch );
        result.set_operation_id( query.operation_id() );
        result.set_state( CPCS_NOT_FOUND );
        result.set_phase( CPCP_UNKNOWN );
        result.set_rollback_state( CPCRS_NOT_ATTEMPTED );
        result.set_post_commit_state( CPCPS_NOT_ATTEMPTED );
        return result;
    }

    if( !MessageDifferencer::Equals( found->second.request, query.expected_request() ) )
    {
        ApiResponseStatus error;
        Fail( error, "operation_id_reused", "the operation ID belongs to a different checked project netclass request" );
        return tl::unexpected( error );
    }

    return found->second.response;
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

    if( auto target = ValidateProjectVariableTarget( aCtx.Request.document(), project, apiServerOrNull() ) )
        return tl::unexpected( *target );

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

    if( auto target = ValidateProjectVariableTarget( aCtx.Request.document(), project, apiServerOrNull() ) )
        return tl::unexpected( *target );

    const project::TextVariables& newVars = aCtx.Request.variables();
    std::map<wxString, wxString>& vars = project.GetTextVars();

    if( aCtx.Request.merge_mode() == MapMergeMode::MMM_REPLACE )
        vars.clear();

    for( const auto& [key, value] : newVars.variables() )
        vars[wxString::FromUTF8( key )] = wxString::FromUTF8( value );

    if( !Pgm().GetSettingsManager().SaveProject( wxEmptyString, &project ) )
    {
        ApiResponseStatus error;
        Fail( error, "project_variables_save_failed", "KiCad could not save the targeted project's text variables" );
        return tl::unexpected( error );
    }

    return Empty();
}


HANDLER_RESULT<SetTextVariablesCheckedResponse> API_HANDLER_COMMON::handleSetTextVariablesChecked(
        const HANDLER_CONTEXT<SetTextVariablesChecked>& aCtx )
{
    const auto& request = aCtx.Request;
    SetTextVariablesCheckedResponse response;

    if( !request.has_project() || request.project().type() != DOCTYPE_PROJECT
        || !request.has_document() || request.document().type() == DOCTYPE_PROJECT )
    {
        ApiResponseStatus error;
        Fail( error, "project_text_variables_target_invalid",
              "checked text-variable writes require a project descriptor and a schematic or PCB lifecycle document" );
        return tl::unexpected( error );
    }

    PROJECT& project = Pgm().GetSettingsManager().Prj();
    if( project.IsNullProject() )
    {
        ApiResponseStatus error;
        Fail( error, "project_text_variables_project_unavailable", "no valid project is loaded" );
        return tl::unexpected( error );
    }

    if( auto target = ValidateProjectVariableTarget( request.project(), project, apiServerOrNull() ) )
        return tl::unexpected( *target );
    if( auto target = ValidateProjectVariableTarget( request.document(), project, apiServerOrNull() ) )
        return tl::unexpected( *target );
    if( !IsOperationId( request.operation_id() ) || request.operation_id().find( '\0' ) != std::string::npos )
    {
        ApiResponseStatus error;
        Fail( error, "invalid_operation_id", "A canonical operation UUID is required" );
        return tl::unexpected( error );
    }
    if( request.process_epoch().empty() || request.process_epoch().find( '\0' ) != std::string::npos
        || ( apiServerOrNull() && request.process_epoch() != apiServerOrNull()->Token() ) )
    {
        ApiResponseStatus error;
        Fail( error, "stale_process_epoch", "The checked text-variable request belongs to another native process epoch" );
        return tl::unexpected( error );
    }
    if( !IsSha256( request.expected_variables_sha256() ) )
    {
        ApiResponseStatus error;
        Fail( error, "invalid_project_text_variables_revision", "expected_variables_sha256 must be a lowercase SHA-256 digest" );
        return tl::unexpected( error );
    }
    if( request.merge_mode() != MapMergeMode::MMM_MERGE && request.merge_mode() != MapMergeMode::MMM_REPLACE )
    {
        ApiResponseStatus error;
        Fail( error, "invalid_project_text_variables_merge_mode", "merge_mode must be MMM_MERGE or MMM_REPLACE" );
        return tl::unexpected( error );
    }
    if( request.variables().variables_size() > 4096 )
    {
        ApiResponseStatus error;
        Fail( error, "invalid_project_text_variables_request", "A project text-variable mutation is limited to 4096 entries" );
        return tl::unexpected( error );
    }
    for( const auto& [key, value] : request.variables().variables() )
    {
        if( key.empty() || key.find( '\0' ) != std::string::npos || value.find( '\0' ) != std::string::npos )
        {
            ApiResponseStatus error;
            Fail( error, "invalid_project_text_variables_request", "Project text-variable names and values must be nonempty and NUL-free" );
            return tl::unexpected( error );
        }
    }

    std::map<std::string, std::string> requested;
    for( const auto& [key, value] : request.variables().variables() ) requested.emplace( key, value );
    NATIVE_STATE_DIGEST fingerprint;
    fingerprint.Append( request.project().SerializeAsString() );
    fingerprint.Append( request.document().SerializeAsString() );
    fingerprint.Append( request.process_epoch() );
    fingerprint.Append( request.expected_variables_sha256() );
    fingerprint.Append( std::to_string( request.merge_mode() ) );
    fingerprint.Append( TextVariablesDigest( requested ) );
    const std::string fingerprintValue = fingerprint.Hex();

    auto prior = m_textVariablesReceipts.find( request.operation_id() );
    if( prior != m_textVariablesReceipts.end() )
    {
        if( prior->second.fingerprint != fingerprintValue )
        {
            ApiResponseStatus error;
            Fail( error, "operation_id_reused", "The operation ID is already bound to another project text-variable request" );
            return tl::unexpected( error );
        }
        response = prior->second.response;
        response.set_replayed( true );
        return response;
    }

    const std::map<std::string, std::string> before = ReadTextVariables( project );
    const std::string beforeDigest = TextVariablesDigest( before );
    if( beforeDigest != request.expected_variables_sha256() )
    {
        ApiResponseStatus error;
        Fail( error, "project_text_variables_stale", "The project text-variable map changed; inspect a fresh map before writing" );
        return tl::unexpected( error );
    }

    std::map<std::string, std::string> after = request.merge_mode() == MapMergeMode::MMM_REPLACE
            ? requested : before;
    if( request.merge_mode() == MapMergeMode::MMM_MERGE )
        for( const auto& [key, value] : requested ) after[key] = value;

    auto writeMap = [&]( const std::map<std::string, std::string>& values )
    {
        auto& native = project.GetTextVars();
        native.clear();
        for( const auto& [key, value] : values ) native[wxString::FromUTF8( key )] = wxString::FromUTF8( value );
    };
    writeMap( after );

    response.mutable_project()->CopyFrom( request.project() );
    response.mutable_document()->CopyFrom( request.document() );
    response.set_operation_id( request.operation_id() );
    response.set_process_epoch( request.process_epoch() );
    response.set_revision_tracked( false );
    response.set_complete_lifecycle( false );
    response.set_before_variables_sha256( beforeDigest );
    response.set_after_variables_sha256( TextVariablesDigest( after ) );
    PackTextVariables( before, *response.mutable_before_variables() );
    PackTextVariables( after, *response.mutable_after_variables() );

    if( !Pgm().GetSettingsManager().SaveProject( wxEmptyString, &project ) )
    {
        writeMap( before );
        bool rollback = Pgm().GetSettingsManager().SaveProject( wxEmptyString, &project );
        response.set_committed( false ); response.set_rollback_completed( rollback );
        response.set_error_code( "project_variables_save_failed" );
        response.set_error_message( "KiCad could not save the checked project text-variable mutation" );
        return response;
    }

    const std::map<std::string, std::string> readback = ReadTextVariables( project );
    if( readback != after )
    {
        writeMap( before );
        bool rollback = Pgm().GetSettingsManager().SaveProject( wxEmptyString, &project );
        response.set_committed( false ); response.set_rollback_completed( rollback );
        response.set_error_code( "project_variables_readback_mismatch" );
        response.set_error_message( "The checked project text-variable save did not read back the requested map" );
        return response;
    }

    response.set_committed( true );
    response.set_rollback_completed( false );
    m_textVariablesReceipts.emplace( request.operation_id(), TEXT_VARIABLES_RECEIPT{ fingerprintValue, response } );
    return response;
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
