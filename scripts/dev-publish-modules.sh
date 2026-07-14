#!/usr/bin/env bash
#
# Publishes every module into the Host's ./modules/ directory, mirroring the production layout
# at /usr/lib/easyhomeserver/modules/ so `dotnet run` on the Host loads them exactly as the
# installed service would.
#
# Usage:
#   scripts/dev-publish-modules.sh                       # publish all modules
#   scripts/dev-publish-modules.sh systeminfo            # publish one module
#   SDK_VERSION=2.0.0.0 scripts/dev-publish-modules.sh   # build against a bumped SDK contract
#                                                        # (to prove the host rejects it)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODULES_SRC="${REPO_ROOT}/src/modules"

# Repo root, deliberately NOT src/EasyHomeServer.Host/modules: the host's own source lives in
# src/EasyHomeServer.Host/Modules/, and on a case-insensitive filesystem (macOS by default)
# those are the same directory — publishing there drops built modules into the source tree and
# a `rm -rf` of the target would delete the loader's source.
TARGET_ROOT="${REPO_ROOT}/modules"
CONFIGURATION="${CONFIGURATION:-Debug}"

# Deriving the id from the manifest would mean running the module, so the convention is the
# project name: EasyHomeServer.Modules.SystemInfo -> systeminfo.
module_id_of() {
  local project_name="$1"
  echo "${project_name#EasyHomeServer.Modules.}" | tr '[:upper:]' '[:lower:]'
}

publish_module() {
  local project_dir="$1"
  local project_name
  project_name="$(basename "${project_dir}")"

  local module_id
  module_id="$(module_id_of "${project_name}")"

  local target="${TARGET_ROOT}/${module_id}"

  echo "==> ${project_name} -> modules/${module_id}"

  # Wipe first: a stale dll from a previous build would still be picked up by the loader, and
  # a renamed assembly would leave two .deps.json behind and fail the scan.
  rm -rf "${target}"
  mkdir -p "${target}"

  local extra_args=()
  if [[ -n "${SDK_VERSION:-}" ]]; then
    extra_args+=("-p:EasyHomeServerSdkAssemblyVersion=${SDK_VERSION}")
    echo "    building against SDK contract ${SDK_VERSION}"
  fi

  # ${arr[@]+"${arr[@]}"} rather than "${arr[@]}": under `set -u`, bash 3.2 (still the default
  # /bin/bash on macOS) treats an empty array as unbound and aborts.
  dotnet publish "${project_dir}/${project_name}.csproj" \
    --configuration "${CONFIGURATION}" \
    --output "${target}" \
    --nologo \
    --verbosity quiet \
    ${extra_args[@]+"${extra_args[@]}"}

  # The SDK and MudBlazor are supplied by the host at runtime. If a copy lands here it loads
  # into the plugin context as a different type and module discovery silently finds nothing,
  # so treat it as a build failure rather than shipping a subtly broken module.
  local leaked=0
  for forbidden in EasyHomeServer.Sdk.dll MudBlazor.dll; do
    if [[ -f "${target}/${forbidden}" ]]; then
      echo "    ERROR: ${forbidden} was published into the module directory." >&2
      leaked=1
    fi
  done

  if [[ "${leaked}" -eq 1 ]]; then
    echo "    Check that the .csproj references it with Private=\"false\" / ExcludeAssets=\"runtime\"." >&2
    exit 1
  fi

  echo "    $(find "${target}" -maxdepth 1 -name '*.dll' | wc -l | tr -d ' ') assembly(ies) published"
}

main() {
  local requested="${1:-}"
  local found=0

  for project_dir in "${MODULES_SRC}"/*/; do
    project_dir="${project_dir%/}"
    [[ -d "${project_dir}" ]] || continue

    local module_id
    module_id="$(module_id_of "$(basename "${project_dir}")")"

    if [[ -n "${requested}" && "${requested}" != "${module_id}" ]]; then
      continue
    fi

    publish_module "${project_dir}"
    found=1
  done

  if [[ "${found}" -eq 0 ]]; then
    echo "No module matching '${requested}' under ${MODULES_SRC}" >&2
    exit 1
  fi

  echo
  echo "Modules published to ${TARGET_ROOT}"
  echo "Run the host with:  dotnet run --project src/EasyHomeServer.Host"
  echo "(appsettings.Development.json points ModulesPath at this directory.)"
}

main "$@"
