#!/bin/sh
# Installs the Linux packages in this (clean, root) system and checks they work, as far as a container allows (no
# systemd): the server package installs with its dependencies, its command wrapper creates an API key as the service
# user, the server starts as that user with the service's environment, finds every GStreamer element it needs, and
# the load tool passes a short run against it. Used by .github/workflows/drafts.yml on each distribution.
#   scripts/test-packages.sh DIR [server+loadtest|loadtest]
# DIR holds the packages (.deb or .rpm, whichever this system uses); the x64 ones are tested.
set -eu
dir="$1"
what="${2:-server+loadtest}"

if command -v apt-get >/dev/null; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -q
  apt-get install -y -q curl
  pkg_install() { apt-get install -y -q "$@"; }
  server=$(ls "$dir"/spatialaudioserver_*_amd64.deb)
  loadtest=$(ls "$dir"/spatialaudioserver-loadtest_*_amd64.deb)
elif command -v dnf >/dev/null; then
  pkg_install() { dnf install -y -q "$@"; }
  server=$(ls "$dir"/spatialaudioserver-[0-9]*.x86_64.rpm)
  loadtest=$(ls "$dir"/spatialaudioserver-loadtest-[0-9]*.x86_64.rpm)
else
  echo "Neither apt-get nor dnf here." >&2
  exit 2
fi

echo "== load tool"
pkg_install "$loadtest"
sas-loadtest --version

if [ "$what" = loadtest ]; then
  echo "PASS (load tool only)"
  exit 0
fi

echo "== server"
pkg_install "$server"
spatialaudioserver --version
ls -l /etc/spatialaudioserver /var/lib/spatialaudioserver
key=$(spatialaudioserver apikey create ci | grep '^sas_')
# Data written by the wrapper must belong to the service user.
[ "$(stat -c %U /var/lib/spatialaudioserver/spatialaudio.db)" = spatialaudio ]

echo "== start (as the service would, without systemd)"
set -a
. /etc/spatialaudioserver/environment
set +a
cd /var/lib/spatialaudioserver
runuser -u spatialaudio -- /usr/lib/spatialaudioserver/SpatialAudioServer > /tmp/server.log 2>&1 &
status=""
for _ in $(seq 1 30); do
  status=$(curl -sf -H "X-Api-Key: $key" http://localhost:5000/api/status || true)
  [ -n "$status" ] && break
  sleep 1
done
if [ -z "$status" ]; then
  echo "The server did not answer:" >&2
  cat /tmp/server.log >&2
  exit 1
fi
echo "$status"
echo "$status" | grep -q '"ok":true'
echo "$status" | grep -q '"missingElements":\[\]'

echo "== load run"
sas-loadtest --server http://localhost:5000 --api-key "$key" --participants 2 --duration 5 --warmup 3 | tee /tmp/loadtest.log
grep -q "RESULT: PASS" /tmp/loadtest.log
echo "PASS"
