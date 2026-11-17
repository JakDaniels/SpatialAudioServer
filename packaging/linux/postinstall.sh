#!/bin/sh
# Copyright 2026 Simon Annetts/Jak Daniels
# SPDX-License-Identifier: Apache-2.0

# After installing or upgrading spatialaudioserver (deb: "configure [old-version]"; rpm: 1 = install, 2 = upgrade).
set -e

first_install=false
case "${1:-}" in
  configure) [ -z "${2:-}" ] && first_install=true ;;
  1) first_install=true ;;
esac

# The service user: a system account with no login and no home of its own.
if ! getent passwd spatialaudio >/dev/null; then
  useradd --system --user-group --no-create-home --home-dir /var/lib/spatialaudioserver \
    --shell /usr/sbin/nologin --comment "SpatialAudioServer" spatialaudio
fi

# Site configuration may hold secrets (TURN secret, certificate password): root writes it, the service reads it.
chown root:spatialaudio /etc/spatialaudioserver /etc/spatialaudioserver/appsettings.json /etc/spatialaudioserver/environment
chmod 0750 /etc/spatialaudioserver
chmod 0640 /etc/spatialaudioserver/appsettings.json /etc/spatialaudioserver/environment

# The data directory (systemd's StateDirectory= makes it too, but the command wrapper may run first).
install -d -o spatialaudio -g spatialaudio -m 0750 /var/lib/spatialaudioserver

if [ -d /run/systemd/system ]; then
  systemctl daemon-reload
  if $first_install; then
    systemctl enable spatialaudioserver.service >/dev/null 2>&1 || true
  else
    systemctl try-restart spatialaudioserver.service || true
  fi
fi

if $first_install; then
  cat <<'MSG'

SpatialAudioServer is installed and enabled, but not started yet. Next:
  sudo spatialaudioserver admin set-password          the admin UI's password (https://<host>:5001/admin/)
  sudo spatialaudioserver apikey create <label>       a key for the control API (e.g. the OpenSim module)
  sudo editor /etc/spatialaudioserver/appsettings.json   ports, certificate, network (optional)
  sudo systemctl start spatialaudioserver
Logs: journalctl -u spatialaudioserver -f. Documentation: /usr/share/doc/spatialaudioserver/

MSG
fi
