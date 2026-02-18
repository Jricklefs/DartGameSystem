/**
 * test_detect.cpp - Basic tests for DartDetectLib
 */
#include "dart_detect.h"
#include <iostream>
#include <cassert>
#include <cstring>

int main()
{
    std::cout << "DartDetectLib Tests" << std::endl;
    std::cout << "Version: " << dd_version() << std::endl;
    
    // Test 1: Init without calibration should fail
    int ret = dd_init("{}");
    assert(ret != 0);
    std::cout << "[PASS] Init with empty JSON returns error" << std::endl;
    
    // Test 2: Detect without init should return error
    const char* result = dd_detect(1, "default", 0, nullptr, nullptr, nullptr, nullptr);
    assert(result != nullptr);
    assert(std::strstr(result, "error") != nullptr || std::strstr(result, "no_detection") != nullptr);
    std::cout << "[PASS] Detect without init returns error: " << result << std::endl;
    dd_free_string(result);
    
    // Test 3: Board cache operations
    dd_init_board("test_board");
    dd_clear_board("test_board");
    std::cout << "[PASS] Board cache init/clear" << std::endl;
    
    // Test 4: dd_free_string with nullptr (shouldn't crash)
    // Note: dd_free_string(nullptr) would be undefined, so skip
    
    std::cout << std::endl << "All tests passed!" << std::endl;
    return 0;
}
