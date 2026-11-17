#!/bin/sh
# After removing spatialaudioserver. The data (/var/lib/spatialaudioserver: rooms, keys, history) and the service user
# stay, so a reinstall carries on; "apt purge" removes the data too (dpkg removes /etc/spatialaudioserver itself).
set -e
if [ -d /run/systemd/system ]; then
  systemctl daemon-reload || true
fi
if [ "${1:-}" = purge ]; then
  rm -rf /var/lib/spatialaudioserver
fi
