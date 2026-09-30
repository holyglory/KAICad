/* Explicit editor request routing. GPL-3.0-or-later. */
#ifndef API_REQUEST_TARGET_H
#define API_REQUEST_TARGET_H

#include <api/common/types/base_types.pb.h>
#include <google/protobuf/message.h>

namespace kiapi::common
{
// Inspect only declared request targets, never Any payloads, repeated data,
// embedded objects or arbitrary nested messages that may describe other designs.
inline bool RequestTargetsOtherDocument( const google::protobuf::Message& aRequest,
                                         types::DocumentType aEditorType )
{
    const auto* descriptor = aRequest.GetDescriptor();
    const auto* reflection = aRequest.GetReflection();
    for( int index = 0; index < descriptor->field_count(); ++index )
    {
        const auto* field = descriptor->field( index );
        if( field->is_repeated()
                || field->cpp_type() != google::protobuf::FieldDescriptor::CPPTYPE_MESSAGE
                || !reflection->HasField( aRequest, field ) )
            continue;

        const google::protobuf::Message* document = nullptr;
        const auto& value = reflection->GetMessage( aRequest, field );
        if( field->message_type() == types::DocumentSpecifier::descriptor() )
            document = &value;
        else if( field->message_type() == types::ItemHeader::descriptor() )
        {
            const auto* target = types::ItemHeader::descriptor()->FindFieldByName( "document" );
            if( value.GetReflection()->HasField( value, target ) )
                document = &value.GetReflection()->GetMessage( value, target );
        }

        if( !document )
            continue;

        const auto* type = types::DocumentSpecifier::descriptor()->FindFieldByName( "type" );
        const int requested = document->GetReflection()->GetEnumValue( *document, type );
        // Missing/unknown targets retain the owning command's existing
        // validation and legacy behavior; this hook grants no authority.
        if( requested != types::DOCTYPE_UNKNOWN && requested != aEditorType )
            return true;
    }
    return false;
}
}
#endif

