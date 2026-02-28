cmake_minimum_required(VERSION 3.20)
project(DartDetectLib LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# OpenCV pre-built location
set(OpenCV_DIR "C:/Users/clawd/opencv/build")
find_package(OpenCV REQUIRED COMPONENTS core imgproc imgcodecs)

# Library sources
add_library(DartDetectLib SHARED
    src/dart_detect.cpp
    src/skeleton.cpp
    src/pca_detection.cpp
    src/scoring.cpp
    src/triangulation.cpp
    src/mask.cpp
    src/iqdl.cpp
    src/dsa.cpp
)

target_include_directories(DartDetectLib PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${OpenCV_INCLUDE_DIRS}
)

target_link_libraries(DartDetectLib PRIVATE ${OpenCV_LIBS})

# Export symbols on Windows
target_compile_definitions(DartDetectLib PRIVATE DART_DETECT_EXPORTS)

# Output to a known location
set_target_properties(DartDetectLib PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin
    LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib
)

# Test executable
add_executable(test_detect test/test_detect.cpp)
target_link_libraries(test_detect PRIVATE DartDetectLib ${OpenCV_LIBS})
target_include_directories(test_detect PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/include)