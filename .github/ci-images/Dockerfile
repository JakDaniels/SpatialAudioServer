# Prepared CI images (PACKAGING-PLAN.md "Caching"): GStreamer and the .NET SDK installed once, not on every run.
# Built and pushed by .github/workflows/ci-images.yml as ghcr.io/<owner>/sas-ci:<tag>:
#   1.28   ubuntu:26.04, GStreamer 1.28, .NET SDK 10
#   1.24   ubuntu:24.04, GStreamer 1.24 (the server's floor), .NET SDK 10
#   22.04  ubuntu:22.04, GStreamer 1.20, no .NET (installs the self-contained load tool .deb)
ARG BASE=ubuntu:26.04
FROM ${BASE}
ARG WITH_DOTNET=1

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_ROOT=/usr/share/dotnet \
    PATH=/usr/share/dotnet:$PATH \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

# What the server, the load tool and the tests need at run time (as in scripts/setup-ubuntu.sh, without the -dev
# packages), plus git (MinVer reads the tags) and ICU (.NET globalisation; its package name changes per release).
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates git curl \
      gstreamer1.0-tools gstreamer1.0-nice \
      gstreamer1.0-plugins-base gstreamer1.0-plugins-good gstreamer1.0-plugins-bad \
      libgstreamer-plugins-bad1.0-0 \
      $(apt-cache search --names-only '^libicu[0-9]+$' | cut -d' ' -f1) \
 && rm -rf /var/lib/apt/lists/* \
 && gst-inspect-1.0 --version

# The latest .NET 10 SDK at build time; the monthly rebuild picks up patch releases.
RUN if [ "$WITH_DOTNET" = 1 ]; then \
      curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
      && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
      && rm /tmp/dotnet-install.sh \
      && dotnet --version; \
    fi

# The checkout belongs to another user than the container's root; without this git (and MinVer) refuse it.
RUN git config --system --add safe.directory '*'
