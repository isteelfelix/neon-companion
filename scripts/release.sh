#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<USAGE
Usage: scripts/release.sh <version> [--unity <path>] [--notes <file>]

Builds windows/linux/android, creates git tag, and publishes a GitHub release with artifacts.
USAGE
}

if [[ $# -lt 1 ]]; then
  usage
  exit 1
fi

VERSION="$1"
shift

UNITY_BIN="${UNITY_PATH:-}"
NOTES_FILE=""
PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TAG="v${VERSION#v}"
VERSION="${VERSION#v}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --unity)
      UNITY_BIN="${2:-}"
      shift 2
      ;;
    --notes)
      NOTES_FILE="${2:-}"
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

cd "${PROJECT_ROOT}"

if ! command -v gh >/dev/null 2>&1; then
  echo "gh CLI is required but not installed." >&2
  exit 1
fi

if ! gh auth status >/dev/null 2>&1; then
  echo "gh CLI is not authenticated. Run 'gh auth login'." >&2
  exit 1
fi

if [[ -n "$(git status --porcelain)" ]]; then
  echo "Working tree is not clean. Commit or stash changes before releasing." >&2
  exit 1
fi

if git rev-parse -q --verify "refs/tags/${TAG}" >/dev/null; then
  echo "Tag ${TAG} already exists." >&2
  exit 1
fi

if [[ -z "${NOTES_FILE}" ]]; then
  PREV_TAG="$(git describe --tags --abbrev=0 2>/dev/null || true)"
  NOTES_FILE="$(mktemp)"
  if [[ -n "${PREV_TAG}" ]]; then
    {
      echo "Release ${TAG}"
      echo
      echo "Changes since ${PREV_TAG}:"
      git log --pretty='- %s (%h)' "${PREV_TAG}..HEAD"
    } > "${NOTES_FILE}"
  else
    {
      echo "Release ${TAG}"
      echo
      echo "Initial published release."
    } > "${NOTES_FILE}"
  fi
  trap 'rm -f "${NOTES_FILE}"' EXIT
fi

artifacts=()
for target in windows linux android; do
  echo "Building ${target}..."
  if [[ -n "${UNITY_BIN}" ]]; then
    artifact="$("${PROJECT_ROOT}/scripts/build.sh" --target "${target}" --version "${VERSION}" --unity "${UNITY_BIN}")"
  else
    artifact="$("${PROJECT_ROOT}/scripts/build.sh" --target "${target}" --version "${VERSION}")"
  fi

  if [[ -z "${artifact}" ]]; then
    echo "No artifact returned for ${target}" >&2
    exit 1
  fi

  if [[ -e "${artifact}" ]]; then
    artifacts+=("${artifact}")
  elif [[ -e "${PROJECT_ROOT}/${artifact}" ]]; then
    artifacts+=("${PROJECT_ROOT}/${artifact}")
  else
    echo "Artifact not found for ${target}: ${artifact}" >&2
    exit 1
  fi
done

echo "Creating git tag ${TAG}"
git tag -a "${TAG}" -m "Release ${TAG}"

echo "Publishing GitHub release ${TAG}"
gh release create "${TAG}" "${artifacts[@]}" \
  --title "${TAG}" \
  --notes-file "${NOTES_FILE}"

echo "Release published: ${TAG}"
