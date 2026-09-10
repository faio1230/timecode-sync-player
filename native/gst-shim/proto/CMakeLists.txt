cmake_minimum_required(VERSION 3.15)
project(tcs_gst_proto CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT DEFINED GST_SDK_DIR)
  set(GST_SDK_DIR "$ENV{GSTREAMER_1_0_ROOT_MSVC_X86_64}")
endif()
if(NOT EXISTS "${GST_SDK_DIR}/include/gstreamer-1.0/gst/gst.h")
  message(FATAL_ERROR "GStreamer SDK not found at ${GST_SDK_DIR}")
endif()

get_filename_component(REPO_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../.." ABSOLUTE)
set(SPOUT_DIR "${REPO_ROOT}/vendor/Spout2/SPOUTSDK")

add_executable(tcs-gst-proto
  main.cpp
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
  ${SPOUT_DIR}/SpoutDirectX/SpoutDX/SpoutDX.cpp
)

target_compile_definitions(tcs-gst-proto PRIVATE
  SPOUT_BUILD_STATIC
  GST_USE_UNSTABLE_API
  UNICODE _UNICODE)

target_include_directories(tcs-gst-proto PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}/include"
  "${GST_SDK_DIR}/include/glib-2.0"
  "${GST_SDK_DIR}/lib/glib-2.0/include"
  "${GST_SDK_DIR}/include/gstreamer-1.0"
  "${SPOUT_DIR}/SpoutGL"
  "${SPOUT_DIR}/SpoutDirectX/SpoutDX")

target_link_directories(tcs-gst-proto PRIVATE "${GST_SDK_DIR}/lib")

target_link_libraries(tcs-gst-proto PRIVATE
  debug  glib-2.0.lib
  optimized glib-2.0.lib
  gobject-2.0.lib
  gthread-2.0.lib
  gstreamer-1.0.lib
  gstbase-1.0.lib
  gstapp-1.0.lib
  gstvideo-1.0.lib
  gstd3d11-1.0.lib
  opengl32 d3d9 d3d11 dxgi user32 gdi32 shell32 ole32 uuid winmm version advapi32)
