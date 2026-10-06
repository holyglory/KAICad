#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 ]]; then
    echo "usage: $0 RESULTS_ROOT COMMAND [ARG ...]" >&2
    exit 2
fi

results_root=$1
shift
mkdir -p "$results_root"

run_directory=$(mktemp -d "$results_root/.run-XXXXXX")
current_directory="$results_root/current"
archive_directory="$results_root/_archive-$(date -u +%Y%m%dT%H%M%S)-$$"

if [[ -e "$current_directory" ]]; then
    mv -- "$current_directory" "$archive_directory"
fi

status=0
"$@" --results-directory "$run_directory" || status=$?
mv -- "$run_directory" "$current_directory"
exit "$status"
