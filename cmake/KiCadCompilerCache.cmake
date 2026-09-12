# Optional owner-local reusable compiler cache. Existing USE_CCACHE behavior
# remains unchanged unless a shared cache directory is explicitly selected.
set( KICAD_CCACHE_DIR "" CACHE PATH "Shared local KiCad compiler-cache directory" )
set( KICAD_CCACHE_MAX_SIZE "100G" CACHE STRING "Maximum reusable compiler cache size" )
set( KICAD_CCACHE_ARGUMENTS "" )

if( KICAD_CCACHE_DIR )
    if( NOT IS_ABSOLUTE "${KICAD_CCACHE_DIR}" )
        message( FATAL_ERROR "KICAD_CCACHE_DIR must be absolute." )
    endif()
    if( NOT CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang" )
        message( FATAL_ERROR "The cross-worktree cache profile currently supports GCC and Clang." )
    endif()
    file( REAL_PATH "${KICAD_CCACHE_DIR}" _cache_root )
    file( MAKE_DIRECTORY "${_cache_root}/tmp" )
    set( KICAD_CCACHE_ARGUMENTS
        "cache_dir=${_cache_root}"
        "temporary_dir=${_cache_root}/tmp"
        "base_dir=${CMAKE_SOURCE_DIR}"
        "max_size=${KICAD_CCACHE_MAX_SIZE}"
        "compiler_check=content"
        "namespace=kicad"
        "hard_link=false"
        "remote_storage="
        "remote_only=false"
        "hash_dir=true"
        "compression=true"
        "umask=0077" )
    # Stable source/debug paths let equivalent worktrees share results without
    # returning another checkout's absolute paths in diagnostics or __FILE__.
    add_compile_options( "-ffile-prefix-map=${CMAKE_SOURCE_DIR}=." )
    if( KICAD_USE_PCH )
        list( APPEND KICAD_CCACHE_ARGUMENTS "sloppiness=pch_defines,time_macros" )
        if( CMAKE_CXX_COMPILER_ID MATCHES "Clang" )
            add_compile_options( "$<$<COMPILE_LANGUAGE:CXX>:-Xclang>" "$<$<COMPILE_LANGUAGE:CXX>:-fno-pch-timestamp>" )
        endif()
        # Ccache cannot see time macros inside a PCH. Native source roots only:
        # generated/build trees must not be scanned or made source dependencies.
        foreach( _source_dir 3d-viewer bitmap2component common cvpcb eeschema gerbview
                 include kicad libs pagelayout_editor pcb_calculator pcbnew plugins thirdparty )
            file( GLOB_RECURSE _source_files LIST_DIRECTORIES false
                  "${CMAKE_SOURCE_DIR}/${_source_dir}/*.cpp"
                  "${CMAKE_SOURCE_DIR}/${_source_dir}/*.cc"
                  "${CMAKE_SOURCE_DIR}/${_source_dir}/*.c"
                  "${CMAKE_SOURCE_DIR}/${_source_dir}/*.h"
                  "${CMAKE_SOURCE_DIR}/${_source_dir}/*.hpp" )
            foreach( _source IN LISTS _source_files )
                file( STRINGS "${_source}" _time_macros LIMIT_COUNT 1 REGEX "__DATE__|__TIME__|__TIMESTAMP__" )
                if( _time_macros )
                    file( READ "${_source}" _prefix LIMIT 4096 )
                    if( NOT _prefix MATCHES "ccache:disable" )
                        message( FATAL_ERROR "Timestamp-bearing source must bypass reusable caching: ${_source}" )
                    endif()
                endif()
            endforeach()
        endforeach()
    else()
        list( APPEND KICAD_CCACHE_ARGUMENTS "sloppiness=" )
    endif()
    message( STATUS "Reusable KiCad cache: ${_cache_root} (limit ${KICAD_CCACHE_MAX_SIZE})" )
endif()
