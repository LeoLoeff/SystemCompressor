cmake_minimum_required(VERSION 3.20)
project(PluginHostTest CXX)
add_executable(host host.cpp)
target_compile_features(host PRIVATE cxx_std_17)
