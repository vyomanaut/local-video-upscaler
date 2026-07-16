cmake_minimum_required(VERSION 3.20)
cmake_policy(SET CMP0091 NEW)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

project(LocalVSRRifeProcessor LANGUAGES C CXX)

if(NOT DEFINED RIFE_SOURCE_DIR OR NOT EXISTS "${RIFE_SOURCE_DIR}/rife.cpp")
    message(FATAL_ERROR "RIFE_SOURCE_DIR must point to the pinned rife-ncnn-vulkan src directory.")
endif()
file(TO_CMAKE_PATH "${RIFE_SOURCE_DIR}" RIFE_SOURCE_DIR_CMAKE)

# The pinned upstream project owns the shader generation and its ncnn/webp
# configuration. Reuse those exact targets, but replace its image-directory
# frontend with LocalVSR's persistent raw-frame frontend.
add_subdirectory("${RIFE_SOURCE_DIR_CMAKE}" "${CMAKE_BINARY_DIR}/rife-upstream")

add_executable(RifeProcessor
    RifeProcessor.cpp
    "${RIFE_SOURCE_DIR_CMAKE}/rife.cpp"
    "${RIFE_SOURCE_DIR_CMAKE}/warp.cpp"
)

add_dependencies(RifeProcessor generate-spirv)
target_compile_features(RifeProcessor PRIVATE cxx_std_20)
target_compile_definitions(RifeProcessor PRIVATE NOMINMAX UNICODE _UNICODE)
target_include_directories(RifeProcessor PRIVATE
    "${RIFE_SOURCE_DIR_CMAKE}"
    "${CMAKE_BINARY_DIR}/rife-upstream"
)
target_link_libraries(RifeProcessor PRIVATE ncnn webp "${Vulkan_LIBRARY}")

if(MSVC)
    target_compile_options(RifeProcessor PRIVATE /W4 /O2)
endif()
