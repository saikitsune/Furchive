#!/bin/sh
# Optional unprivileged Linux sysroot; never used on standard installed systems.
# Set FURCHIVE_SYSROOT to the extracted dependency prefix before sourcing.
if [ -n "${FURCHIVE_SYSROOT:-}" ]; then
  export PKG_CONFIG_SYSROOT_DIR="$FURCHIVE_SYSROOT"
  export PKG_CONFIG_PATH="$FURCHIVE_SYSROOT/usr/lib/x86_64-linux-gnu/pkgconfig:$FURCHIVE_SYSROOT/usr/share/pkgconfig"
  export LIBRARY_PATH="$FURCHIVE_SYSROOT/usr/lib/x86_64-linux-gnu${LIBRARY_PATH:+:$LIBRARY_PATH}"
  export LD_LIBRARY_PATH="$FURCHIVE_SYSROOT/usr/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
  export WEBKIT_EXEC_PATH="$FURCHIVE_SYSROOT/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1"
  export GSETTINGS_SCHEMA_DIR="$FURCHIVE_SYSROOT/usr/share/glib-2.0/schemas"
  export GST_PLUGIN_SYSTEM_PATH_1_0="$FURCHIVE_SYSROOT/usr/lib/x86_64-linux-gnu/gstreamer-1.0"
  export GST_PLUGIN_SCANNER="$FURCHIVE_SYSROOT/usr/lib/x86_64-linux-gnu/gstreamer1.0/gstreamer-1.0/gst-plugin-scanner"
  export XDG_DATA_DIRS="$FURCHIVE_SYSROOT/usr/share:/usr/share"
  export PATH="$FURCHIVE_SYSROOT/usr/bin:$PATH"
fi
