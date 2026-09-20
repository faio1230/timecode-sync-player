cmake_minimum_required(VERSION 3.15)
project(tcs_gstreamer_shim CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT DEFINED GST_SDK_DIR)
  set(GST_SDK_DIR "$ENV{GSTREAMER_1_0_ROOT_MSVC_X86_64}")
endif()
if(NOT EXISTS "${GST_SDK_DIR}/include/gstreamer-1.0/gst/gst.h")
  message(FATAL_ERROR "GStreamer SDK not found at ${GST_SDK_DIR}")
endif()

get_filename_component(REPO_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../.." ABSOLUTE)
set(SPOUT_DIR "${REPO_ROOT}/vendor/Spout2/SPOUTSDK")
set(PROTO_INCLUDE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/proto/include")

set(GST_LIBS
  glib-2.0 gobject-2.0 gthread-2.0
  gstreamer-1.0 gstbase-1.0 gstapp-1.0 gstvideo-1.0 gstd3d11-1.0)

set(SPOUT_SOURCES
  ${SPOUT_DIR}/SpoutGL/Spout.cpp
  ${SPOUT_DIR}/SpoutGL/SpoutCopy.cpp
  ${SPOUT_DIR}/SpoutGL/SpoutDirectX.cpp
  ${SPOUT_DIR}/SpoutGL/SpoutFrameCount.cpp
  ${SPOUT_DIR}/SpoutGL/SpoutGL.cpp
  ${SPOUT_DIR}/SpoutGL/SpoutGLextensions.cpp
  ${SPOUT_DIR}/SpoutGL/SpoutReceiver.cpp
  ${SPOUT_DIR}/SpoutGL/SpoutSender.cpp
  ${SPOUT_DIR}/SpoutGL/SpoutSenderNames.cpp
  ${SPOUT_DIR}/SpoutGL/SpoutSharedMemory.cpp
  ${SPOUT_DIR}/SpoutGL/SpoutUtils.cpp
  ${SPOUT_DIR}/SpoutDirectX/SpoutDX/SpoutDX.cpp)

set(TCS_COMMON_INCLUDES
  "${CMAKE_CURRENT_SOURCE_DIR}/include"
  "${PROTO_INCLUDE_DIR}"
  "${GST_SDK_DIR}/include/glib-2.0"
  "${GST_SDK_DIR}/lib/glib-2.0/include"
  "${GST_SDK_DIR}/include/gstreamer-1.0"
  "${SPOUT_DIR}/SpoutGL"
  "${SPOUT_DIR}/SpoutDirectX/SpoutDX")

#--------------------------------------
# the shim DLL (C ABI)
#--------------------------------------
add_library(tcs_gstreamer SHARED src/tcs_gstreamer.cpp src/tcs_hap_gpu.cpp ${SPOUT_SOURCES})
target_compile_definitions(tcs_gstreamer PRIVATE
  TCS_GST_BUILDING_DLL SPOUT_BUILD_STATIC GST_USE_UNSTABLE_API)
target_include_directories(tcs_gstreamer PRIVATE ${TCS_COMMON_INCLUDES})
target_link_directories(tcs_gstreamer PRIVATE "${GST_SDK_DIR}/lib")
target_link_libraries(tcs_gstreamer PRIVATE
  ${GST_LIBS}
  opengl32 d3d9 d3d11 dxgi user32 gdi32 shell32 ole32 uuid winmm version advapi32)

#--------------------------------------
# native smoke test driving only the C ABI
#--------------------------------------
add_executable(tcs-shim-test test/shim_test.cpp)
target_link_directories(tcs-shim-test PRIVATE "${GST_SDK_DIR}/lib")
target_link_libraries(tcs-shim-test PRIVATE tcs_gstreamer ${GST_LIBS} d3d11 dxgi)
target_include_directories(tcs-shim-test PRIVATE ${TCS_COMMON_INCLUDES})
