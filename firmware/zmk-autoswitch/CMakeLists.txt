# AutoSwitch KVM host channel for ZMK.
if (CONFIG_ZMK_AUTOSWITCH)
  zephyr_library()
  zephyr_library_sources(src/autoswitch.c)
  zephyr_include_directories(${APPLICATION_SOURCE_DIR}/include)
endif()
