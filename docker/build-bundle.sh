#!/usr/bin/env bash
set -euo pipefail

TAG="${1:-file-server-bundle}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
FILES_ROOT="$REPO_ROOT/FilesRoot"

if [ ! -d "$FILES_ROOT" ]; then
    echo "ERROR: 'FilesRoot/' directory not found at the repository root." >&2
    echo "Create it and populate it with files to bake into the image, then re-run." >&2
    exit 1
fi

FILE_COUNT=$(find "$FILES_ROOT" -type f | wc -l | tr -d ' ')
echo "Baking $FILE_COUNT file(s) from FilesRoot/ into image '$TAG'..."

docker build -f "$SCRIPT_DIR/Dockerfile.bundle" -t "$TAG" "$REPO_ROOT"
