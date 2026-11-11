#!/usr/bin/env bash
# Prints the MigrationTester release on nuget.org that a package of version $1 must stay compatible with, following
# semantic versioning: the newest stable release with the same major version, not newer than $1. Prints nothing for
# the first release of a major version, which may break the API.
#
#   bash .github/scripts/package-validation-baseline.sh 0.3.0   ->  0.2.0
#   bash .github/scripts/package-validation-baseline.sh 1.0.0   ->  (nothing: no 1.x release yet)
set -euo pipefail

version="${1:?usage: package-validation-baseline.sh <version>}"
core="${version%%-*}" # 0.3.0-preview.1 -> 0.3.0
major="${core%%.*}"

# Stable versions only: a prerelease such as 0.1.0-preview.1 does not match.
published="$(curl --fail --silent --show-error --retry 3 https://api.nuget.org/v3-flatcontainer/migrationtester/index.json \
  | grep -oE '"[0-9]+\.[0-9]+\.[0-9]+"' | tr -d '"')"

baseline=""
for candidate in $(printf '%s\n' $published | sort -V); do
  [[ "${candidate%%.*}" == "$major" ]] || continue
  # Not newer than this version.
  [[ "$(printf '%s\n%s\n' "$candidate" "$core" | sort -V | tail -n 1)" == "$core" ]] || continue
  baseline="$candidate"
done

printf '%s\n' "$baseline"
