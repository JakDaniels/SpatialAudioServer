#!/bin/sh
# Before removing spatialaudioserver (deb: "remove"; rpm: 0). Upgrades (deb "upgrade", rpm 1) leave it running.
set -e
case "${1:-}" in
  remove|0)
    if [ -d /run/systemd/system ]; then
      systemctl disable --now spatialaudioserver.service >/dev/null 2>&1 || true
    fi
    ;;
esac
