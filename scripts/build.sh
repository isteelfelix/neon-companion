#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<USAGE
Usage: scripts/build.sh --target <windows|linux|android> [--version <semver>] [--unity <path>]

Builds neon-companion with Unity in batch mode and prints the artifact path on success.
USAGE
}

TARGET=""
VERSION=""
UNITY_BIN="${UNITY_PATH:-}"
PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOG_DIR="${PROJECT_ROOT}/Builds/logs"
LOG_FILE=""

cleanup() {
  if [[ -n "${LOG_FILE}" && -f "${LOG_FILE}" ]]; then
    :
  fi
}
trap cleanup EXIT

while [[ $# -gt 0 ]]; do
  case "$1" in
    --target)
      TARGET="${2:-}"
      shift 2
      ;;
    --version)
      VERSION="${2:-}"
      shift 2
      ;;
    --unity)
      UNITY_BIN="${2:-}"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1
      ;;
  esac
done

if [[ -z "${TARGET}" ]]; then
  echo "--target is required" >&2
  usage
  exit 1
fi

case "${TARGET}" in
  windows|linux|android)
    ;;
  *)
    echo "Invalid target '${TARGET}'. Use windows, linux, or android." >&2
    exit 1
    ;;
esac

if [[ -z "${UNITY_BIN}" ]]; then
  if command -v unity-editor >/dev/null 2>&1; then
    UNITY_BIN="$(command -v unity-editor)"
  elif command -v Unity >/dev/null 2>&1; then
    UNITY_BIN="$(command -v Unity)"
  else
    echo "Unity binary not found. Provide --unity <path> or set UNITY_PATH." >&2
    exit 1
  fi
fi

mkdir -p "${LOG_DIR}"
TIMESTAMP="$(date +%Y%m%d-%H%M%S)"
LOG_FILE="${LOG_DIR}/build-${TARGET}-${TIMESTAMP}.log"

BUILD_METHOD="BuildScript.Build"

cmd=(
  "${UNITY_BIN}"
  -batchmode
  -quit
  -nographics
  -projectPath "${PROJECT_ROOT}"
  -executeMethod "${BUILD_METHOD}"
  -logFile "${LOG_FILE}"
)

echo "Running Unity build for target=${TARGET}"
if [[ -n "${VERSION}" ]]; then
  echo "Using version=${VERSION}"
fi

set +e
(
  export BUILD_TARGET="${TARGET}"
  if [[ -n "${VERSION}" ]]; then
    export BUILD_VERSION="${VERSION}"
  fi
  "${cmd[@]}"
)
status=$?
set -e

if [[ ${status} -ne 0 ]]; then
  echo "Unity build failed (exit=${status}). See log: ${LOG_FILE}" >&2
  exit ${status}
fi

ARTIFACT_PATH="$(grep -Eo 'BUILD_ARTIFACT_PATH=.*' "${LOG_FILE}" | tail -n 1 | sed 's/^BUILD_ARTIFACT_PATH=//')"
if [[ -z "${ARTIFACT_PATH}" ]]; then
  echo "Build completed but artifact path marker was not found in ${LOG_FILE}" >&2
  exit 1
fi

if [[ ! -e "${PROJECT_ROOT}/${ARTIFACT_PATH}" && ! -e "${ARTIFACT_PATH}" ]]; then
  echo "Artifact path reported but not found: ${ARTIFACT_PATH}" >&2
  exit 1
fi

printf '%s\n' "${ARTIFACT_PATH}"
