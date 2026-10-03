# Generate protobuf C++ files in a private staging tree and publish only files
# whose contents changed.  The caller owns the witness file; this script must
# finish successfully before the caller touches that witness.

if( NOT DEFINED PROTOC OR NOT DEFINED PROTO_ROOT OR NOT DEFINED PROTO_FILES
    OR NOT DEFINED STAGING_ROOT OR NOT DEFINED DEST_ROOT )
    message( FATAL_ERROR
             "GenerateProtobuf.cmake requires PROTOC, PROTO_ROOT, PROTO_FILES, "
             "STAGING_ROOT and DEST_ROOT" )
endif()

file( REMOVE_RECURSE "${STAGING_ROOT}" )
file( MAKE_DIRECTORY "${STAGING_ROOT}" )

set( _protobuf_command "${PROTOC}" "--cpp_out=dllexport_decl=KIAPI_IMPORTEXPORT:${STAGING_ROOT}"
     "--proto_path=${PROTO_ROOT}" "--experimental_allow_proto3_optional" )
list( APPEND _protobuf_command ${PROTO_FILES} )

execute_process(
    COMMAND ${_protobuf_command}
    WORKING_DIRECTORY "${PROTO_ROOT}"
    RESULT_VARIABLE _protobuf_result
    OUTPUT_VARIABLE _protobuf_stdout
    ERROR_VARIABLE _protobuf_stderr
)

if( NOT _protobuf_result EQUAL 0 )
    message( SEND_ERROR "protoc failed with exit code ${_protobuf_result}" )
    if( _protobuf_stdout )
        message( SEND_ERROR "protoc stdout:\n${_protobuf_stdout}" )
    endif()
    if( _protobuf_stderr )
        message( SEND_ERROR "protoc stderr:\n${_protobuf_stderr}" )
    endif()
    return()
endif()

set( _protobuf_outputs )
foreach( _proto IN LISTS PROTO_FILES )
    string( REGEX REPLACE "\\.proto$" ".pb.cc" _cpp "${_proto}" )
    string( REGEX REPLACE "\\.proto$" ".pb.h" _header "${_proto}" )
    list( APPEND _protobuf_outputs "${_cpp}" "${_header}" )
endforeach()

# Validate the complete staged result before touching any canonical output.
foreach( _output IN LISTS _protobuf_outputs )
    if( NOT EXISTS "${STAGING_ROOT}/${_output}" )
        message( FATAL_ERROR "protoc did not produce expected output ${_output}" )
    endif()
endforeach()

foreach( _output IN LISTS _protobuf_outputs )
    set( _source "${STAGING_ROOT}/${_output}" )
    set( _destination "${DEST_ROOT}/${_output}" )
    get_filename_component( _destination_directory "${_destination}" DIRECTORY )
    file( MAKE_DIRECTORY "${_destination_directory}" )
    file( COPY_FILE "${_source}" "${_destination}" ONLY_IF_DIFFERENT )
endforeach()
