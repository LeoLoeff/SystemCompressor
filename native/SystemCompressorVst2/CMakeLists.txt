cmake_minimum_required(VERSION 3.20)
# Statically linked C++ runtime: the DLL is loaded by the Windows audio service and must not depend on
# a Visual C++ Redistributable being installed on the target PC.
cmake_policy(SET CMP0091 NEW)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
project(SystemCompressorVst2 LANGUAGES CXX)

# Equalizer APO loads only VST 2.x plug-ins, so this is a self-contained DLL
# implementing the VST2 C ABI. No SDK is required.
add_library(SystemCompressor SHARED SystemCompressor.cpp)
target_compile_features(SystemCompressor PRIVATE cxx_std_17)
target_compile_definitions(SystemCompressor PRIVATE NOMINMAX UNICODE _UNICODE)
target_link_libraries(SystemCompressor PRIVATE shell32 ole32)
set_target_properties(SystemCompressor PROPERTIES PREFIX "")
