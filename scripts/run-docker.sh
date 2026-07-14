#!/usr/bin/env bash
#
# Runs the host in a Debian container, laid out exactly as the .deb installs it.
#
# The point is /proc: the System Info module reads it directly, so on a macOS or Windows
# workstation the module can only ever show its "unsupported platform" guard. A Linux container
# is the cheapest way to see the module actually working, and it rehearses the production
# layout (/usr/lib/easyhomeserver, /var/lib/easyhomeserver) at the same time.
#
# Usage:
#   scripts/run-docker.sh                 # build and run on http://localhost:5199
#   PORT=8080 scripts/run-docker.sh       # different port
#   scripts/run-docker.sh --fresh         # wipe the container's database first
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PORT="${PORT:-5199}"
IMAGE="easyhomeserver-dev"
CONTAINER="easyhomeserver-dev"
STAGE="${REPO_ROOT}/artifacts/docker"

FRESH=0
if [[ "${1:-}" == "--fresh" ]]; then
  FRESH=1
fi

# The container is Linux on whatever the Docker VM runs; match it so the self-contained
# publish actually executes.
case "$(docker info --format '{{.Architecture}}' 2>/dev/null)" in
  aarch64 | arm64) RID="linux-arm64" ;;
  *) RID="linux-x64" ;;
esac

echo "==> Publishing host ($RID, self-contained)"
rm -rf "${STAGE}"
mkdir -p "${STAGE}/app" "${STAGE}/modules"

dotnet publish "${REPO_ROOT}/src/EasyHomeServer.Host/EasyHomeServer.Host.csproj" \
  --configuration Release \
  --runtime "${RID}" \
  --self-contained true \
  --output "${STAGE}/app" \
  --nologo --verbosity quiet

echo "==> Publishing modules (framework-dependent; host supplies SDK + MudBlazor)"
for project_dir in "${REPO_ROOT}"/src/modules/*/; do
  project_dir="${project_dir%/}"
  project_name="$(basename "${project_dir}")"
  module_id="$(echo "${project_name#EasyHomeServer.Modules.}" | tr '[:upper:]' '[:lower:]')"

  dotnet publish "${project_dir}/${project_name}.csproj" \
    --configuration Release \
    --output "${STAGE}/modules/${module_id}" \
    --nologo --verbosity quiet

  echo "    ${module_id}"
done

echo "==> Building image"
cat > "${STAGE}/Dockerfile" <<'DOCKERFILE'
FROM debian:trixie-slim

# What a self-contained .NET app needs on Debian. InvariantGlobalization is on, so libicu is
# deliberately absent — the same reason the .deb does not depend on it either.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libstdc++6 zlib1g ca-certificates curl procps \
 && rm -rf /var/lib/apt/lists/*

# Mirrors the .deb layout so this rehearses the real install.
COPY app/     /usr/lib/easyhomeserver/
COPY modules/ /usr/lib/easyhomeserver/modules/

RUN mkdir -p /var/lib/easyhomeserver \
 && chmod +x /usr/lib/easyhomeserver/EasyHomeServer.Host

WORKDIR /usr/lib/easyhomeserver
EXPOSE 5000
ENTRYPOINT ["/usr/lib/easyhomeserver/EasyHomeServer.Host"]
DOCKERFILE

docker build --quiet --tag "${IMAGE}" "${STAGE}" > /dev/null
echo "    ${IMAGE}"

docker rm -f "${CONTAINER}" > /dev/null 2>&1 || true

if [[ "${FRESH}" -eq 1 ]]; then
  docker volume rm easyhomeserver-data > /dev/null 2>&1 || true
  echo "==> Wiped database (first-run setup will ask for a new password)"
fi

echo "==> Starting container"
# The data volume persists the SQLite database and data-protection keys across runs, so the
# admin password survives a rebuild.
docker run --detach \
  --name "${CONTAINER}" \
  --publish "${PORT}:5000" \
  --volume easyhomeserver-data:/var/lib/easyhomeserver \
  "${IMAGE}" > /dev/null

printf "    waiting for the host to listen"
for _ in $(seq 1 60); do
  if curl -fsS -o /dev/null "http://localhost:${PORT}/login" 2>/dev/null; then
    echo
    echo
    echo "  Ready:  http://localhost:${PORT}"
    echo "  Logs:   docker logs -f ${CONTAINER}"
    echo "  Stop:   docker rm -f ${CONTAINER}"
    exit 0
  fi
  printf "."
  sleep 1
done

echo
echo "The host did not come up. Recent logs:" >&2
docker logs --tail 40 "${CONTAINER}" >&2
exit 1
