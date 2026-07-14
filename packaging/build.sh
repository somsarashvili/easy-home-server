#!/usr/bin/env bash
#
# Builds the Debian packages:
#
#   easyhomeserver_<version>_<arch>.deb
#       Self-contained publish of the host (no .NET runtime needed on the target), the SDK and
#       MudBlazor assemblies, and the systemd unit. Declares Provides: easyhomeserver-sdk-1.
#
#   easyhomeserver-module-<id>_<version>_<arch>.deb   (one per module)
#       The module assembly and its non-SDK dependencies, into
#       /usr/lib/easyhomeserver/modules/<id>/. Declares Depends: easyhomeserver-sdk-1.
#
# Usage:
#   packaging/build.sh                          # amd64
#   packaging/build.sh --arch arm64             # arm64 (the UTM test VM)
#   packaging/build.sh --arch arm64 --version 0.2.0
#   packaging/build.sh --module systeminfo      # host + one module
#
# Requires: dotnet SDK, dpkg-deb, fakeroot (or run as root). Builds on any OS that can
# cross-publish for Linux; only installation needs Debian.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PACKAGING="${REPO_ROOT}/packaging"
OUTPUT_DIR="${REPO_ROOT}/artifacts/deb"
STAGE_ROOT="${REPO_ROOT}/artifacts/stage"

ARCH="amd64"
VERSION=""
ONLY_MODULE=""
MAINTAINER="${MAINTAINER:-EasyHomeServer <root@localhost>}"

# Bumping this means the host will refuse every module built against the old contract. It must
# match the major of EasyHomeServerSdkAssemblyVersion in Directory.Build.props, and it is the
# "1" in the easyhomeserver-sdk-1 virtual package.
SDK_CONTRACT_MAJOR="1"

usage() {
  sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//; $d'
  exit "${1:-0}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch) ARCH="${2:?--arch needs a value}"; shift 2 ;;
    --version) VERSION="${2:?--version needs a value}"; shift 2 ;;
    --module) ONLY_MODULE="${2:?--module needs a value}"; shift 2 ;;
    --output) OUTPUT_DIR="${2:?--output needs a value}"; shift 2 ;;
    -h | --help) usage 0 ;;
    *) echo "Unknown argument: $1" >&2; usage 1 ;;
  esac
done

case "${ARCH}" in
  amd64) RID="linux-x64" ;;
  arm64) RID="linux-arm64" ;;
  *) echo "Unsupported architecture '${ARCH}'. Use amd64 or arm64." >&2; exit 1 ;;
esac

for tool in dotnet dpkg-deb; do
  if ! command -v "${tool}" > /dev/null 2>&1; then
    echo "Required tool '${tool}' is not on PATH." >&2
    [[ "${tool}" == "dpkg-deb" ]] && echo "  macOS: brew install dpkg   Debian: apt install dpkg-dev" >&2
    exit 1
  fi
done

# Single source of truth for the version: Directory.Build.props, unless overridden.
if [[ -z "${VERSION}" ]]; then
  VERSION="$(sed -n 's/.*<EasyHomeServerVersion[^>]*>\([^<]*\)<.*/\1/p' "${REPO_ROOT}/Directory.Build.props" | head -1)"
  VERSION="${VERSION:-0.1.0}"
fi

# dpkg-deb writes files owned by whoever runs it. Without fakeroot the package would install
# files owned by the building user's uid instead of root.
FAKEROOT=""
if command -v fakeroot > /dev/null 2>&1; then
  FAKEROOT="fakeroot"
elif [[ "$(id -u)" != "0" ]]; then
  echo "note: fakeroot not found; forcing root ownership via dpkg-deb --root-owner-group."
fi

# These functions return the built .deb path on stdout, so every progress message they emit
# must go to stderr or it would be captured as part of the return value.
build_deb() {
  local package_dir="$1"
  local package_name="$2"
  local package_arch="$3"
  local deb="${OUTPUT_DIR}/${package_name}_${VERSION}_${package_arch}.deb"

  chmod 0755 "${package_dir}/DEBIAN"
  find "${package_dir}/DEBIAN" -type f -exec chmod 0755 {} \;

  if [[ -n "${FAKEROOT}" ]]; then
    ${FAKEROOT} dpkg-deb --build --root-owner-group "${package_dir}" "${deb}" > /dev/null
  else
    dpkg-deb --build --root-owner-group "${package_dir}" "${deb}" > /dev/null
  fi

  echo "${deb}"
}

echo "EasyHomeServer ${VERSION} for ${ARCH} (${RID}), SDK contract ${SDK_CONTRACT_MAJOR}"
echo

rm -rf "${STAGE_ROOT}"
mkdir -p "${OUTPUT_DIR}"

# ---------------------------------------------------------------------------------------------
# Host package
# ---------------------------------------------------------------------------------------------
build_host() {
  local stage="${STAGE_ROOT}/host"
  local app_dir="${stage}/usr/lib/easyhomeserver"

  echo "==> Host: publishing self-contained (${RID})" >&2
  mkdir -p "${app_dir}" "${stage}/lib/systemd/system" "${stage}/DEBIAN" \
           "${stage}/usr/share/doc/easyhomeserver"

  # Self-contained so the target needs no .NET installed — Debian's packaged .NET lags and
  # pinning the runtime to the app is what makes this a single-file-drop appliance install.
  # Not trimmed: Blazor and the plugin loader resolve types by reflection, which the trimmer
  # cannot see and would silently strip.
  dotnet publish "${REPO_ROOT}/src/EasyHomeServer.Host/EasyHomeServer.Host.csproj" \
    --configuration Release \
    --runtime "${RID}" \
    --self-contained true \
    --output "${app_dir}" \
    -p:PublishTrimmed=false \
    -p:PublishSingleFile=false \
    -p:DebugType=none \
    -p:GenerateDocumentationFile=false \
    --nologo --verbosity quiet

  # The modules directory must exist and be listed by the host package: the module packages
  # drop into it, and the host scans it on every start whether or not anything is installed.
  mkdir -p "${app_dir}/modules"

  install -m 0644 "${PACKAGING}/easyhomeserver.service" "${stage}/lib/systemd/system/easyhomeserver.service"
  chmod 0755 "${app_dir}/EasyHomeServer.Host"

  # Sanity check: if the SDK is missing the host cannot offer the contract it Provides.
  if [[ ! -f "${app_dir}/EasyHomeServer.Sdk.dll" ]]; then
    echo "    ERROR: EasyHomeServer.Sdk.dll is missing from the host publish." >&2
    exit 1
  fi

  cat > "${stage}/usr/share/doc/easyhomeserver/copyright" <<'EOF'
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: easyhomeserver
EOF

  local installed_kb
  installed_kb="$(du -sk "${stage}" | cut -f1)"

  cat > "${stage}/DEBIAN/control" <<EOF
Package: easyhomeserver
Version: ${VERSION}
Section: admin
Priority: optional
Architecture: ${ARCH}
Maintainer: ${MAINTAINER}
Installed-Size: ${installed_kb}
Depends: libc6, libgcc-s1, libstdc++6, zlib1g
Provides: easyhomeserver-sdk-${SDK_CONTRACT_MAJOR}
Description: Web management tool for a Debian home server
 A modular web interface for managing a single home server: system metrics,
 and — via separately installable module packages — Docker, disks, DNS, file
 sharing and mDNS advertising.
 .
 This package provides the host process and the plugin SDK contract
 (easyhomeserver-sdk-${SDK_CONTRACT_MAJOR}). Modules are shipped as their own
 packages and are discovered at startup from /usr/lib/easyhomeserver/modules.
 .
 The service listens on port 5000 and asks for an admin password on first use.
EOF

  # conffiles: appsettings.json is the admin's to edit. Without this, dpkg silently overwrites
  # local changes on upgrade; with it, dpkg prompts on conflict.
  cat > "${stage}/DEBIAN/conffiles" <<'EOF'
/usr/lib/easyhomeserver/appsettings.json
EOF

  install -m 0755 "${PACKAGING}/host/postinst" "${stage}/DEBIAN/postinst"
  install -m 0755 "${PACKAGING}/host/prerm" "${stage}/DEBIAN/prerm"
  install -m 0755 "${PACKAGING}/host/postrm" "${stage}/DEBIAN/postrm"

  echo "==> Host: building package" >&2
  build_deb "${stage}" "easyhomeserver" "${ARCH}"
}

# ---------------------------------------------------------------------------------------------
# Module packages
# ---------------------------------------------------------------------------------------------
build_module() {
  local project_dir="$1"
  local project_name
  project_name="$(basename "${project_dir}")"

  local module_id
  module_id="$(echo "${project_name#EasyHomeServer.Modules.}" | tr '[:upper:]' '[:lower:]')"

  if [[ -n "${ONLY_MODULE}" && "${ONLY_MODULE}" != "${module_id}" ]]; then
    return 0
  fi

  local stage="${STAGE_ROOT}/module-${module_id}"
  local module_dir="${stage}/usr/lib/easyhomeserver/modules/${module_id}"

  echo "==> Module ${module_id}: publishing" >&2
  mkdir -p "${module_dir}" "${stage}/DEBIAN" "${stage}/usr/share/doc/easyhomeserver-module-${module_id}"

  # Framework-dependent and architecture-independent by construction: the module is pure IL that
  # runs on the runtime the host already ships. Only its own non-SDK dependencies come with it.
  dotnet publish "${project_dir}/${project_name}.csproj" \
    --configuration Release \
    --output "${module_dir}" \
    -p:DebugType=none \
    -p:GenerateDocumentationFile=false \
    --nologo --verbosity quiet

  # The host supplies these at runtime. A copy here loads into the plugin context as a distinct
  # type: IModule discovery finds nothing and MudBlazor components render against a theme
  # provider the host cannot see. Both fail quietly, so fail loudly here instead.
  local leaked=0
  for forbidden in EasyHomeServer.Sdk.dll MudBlazor.dll; do
    if [[ -f "${module_dir}/${forbidden}" ]]; then
      echo "    ERROR: ${forbidden} was published into the module package." >&2
      leaked=1
    fi
  done

  if [[ "${leaked}" -eq 1 ]]; then
    echo "    Reference it with Private=\"false\" / ExcludeAssets=\"runtime\" in the .csproj." >&2
    exit 1
  fi

  cat > "${stage}/usr/share/doc/easyhomeserver-module-${module_id}/copyright" <<'EOF'
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: easyhomeserver
EOF

  local installed_kb
  installed_kb="$(du -sk "${stage}" | cut -f1)"

  # Architecture: all — the module is IL, so one package serves amd64 and arm64 alike. The
  # dependency on the versioned SDK contract, not on a host version, is what lets host and
  # modules be upgraded independently as long as the contract holds.
  cat > "${stage}/DEBIAN/control" <<EOF
Package: easyhomeserver-module-${module_id}
Version: ${VERSION}
Section: admin
Priority: optional
Architecture: all
Maintainer: ${MAINTAINER}
Installed-Size: ${installed_kb}
Depends: easyhomeserver-sdk-${SDK_CONTRACT_MAJOR}
Description: ${module_id} module for EasyHomeServer
 Adds the ${module_id} module to the EasyHomeServer management tool.
 .
 Installed into /usr/lib/easyhomeserver/modules/${module_id} and loaded by the
 host at startup. Requires a host providing SDK contract
 ${SDK_CONTRACT_MAJOR}; a host offering a different contract version will
 refuse to load it and report why in the interface.
EOF

  sed "s/@MODULE_ID@/${module_id}/g" "${PACKAGING}/module/postinst" > "${stage}/DEBIAN/postinst"
  sed "s/@MODULE_ID@/${module_id}/g" "${PACKAGING}/module/postrm" > "${stage}/DEBIAN/postrm"
  chmod 0755 "${stage}/DEBIAN/postinst" "${stage}/DEBIAN/postrm"

  echo "==> Module ${module_id}: building package" >&2
  build_deb "${stage}" "easyhomeserver-module-${module_id}" "all"
}

BUILT=()
BUILT+=("$(build_host)")

for project_dir in "${REPO_ROOT}"/src/modules/*/; do
  project_dir="${project_dir%/}"
  [[ -d "${project_dir}" ]] || continue

  result="$(build_module "${project_dir}")"
  [[ -n "${result}" ]] && BUILT+=("${result}")
done

echo
echo "Built:"
for deb in "${BUILT[@]}"; do
  printf "  %-58s %s\n" "$(basename "${deb}")" "$(du -h "${deb}" | cut -f1 | tr -d ' ')"
done

echo
echo "Install on Debian (host first — the modules depend on the SDK contract it provides):"
echo "  sudo dpkg -i ${OUTPUT_DIR##*/}/easyhomeserver_${VERSION}_${ARCH}.deb"
echo "  sudo dpkg -i ${OUTPUT_DIR##*/}/easyhomeserver-module-*_${VERSION}_all.deb"
