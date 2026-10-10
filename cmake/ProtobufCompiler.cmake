# A target in COMMAND position is resolved by CMake automatically. Passing it
# through -D into a script requires an explicit path, selected for the active
# configuration (including IMPORTED_LOCATION_RELEASE and target mappings).
set( KIAPI_PROTOC_COMMAND "${Protobuf_PROTOC_EXECUTABLE}" )
if( TARGET "${Protobuf_PROTOC_EXECUTABLE}" )
    set( KIAPI_PROTOC_COMMAND "$<TARGET_FILE:${Protobuf_PROTOC_EXECUTABLE}>" )
endif()
