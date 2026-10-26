#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
input_readme="${1:-$repo_root/README.md}"
output_readme="${2:-$repo_root/artifacts/nuget/README.md}"

if [ ! -f "$input_readme" ]; then
  echo "Input README not found: $input_readme" >&2
  exit 1
fi

mkdir -p "$(dirname "$output_readme")"

cat <<'EOF' > "$output_readme"
# Tunnelite

**Your localhost. One link away.**

Share a local app, test a webhook, or connect your phone to your desktop.

[![Latest release](https://img.shields.io/github/v/release/cristipufu/tunnelite?style=flat-square&label=release&labelColor=0f172a&color=0ea5e9)](https://github.com/cristipufu/tunnelite/releases/latest)
[![NuGet version](https://img.shields.io/nuget/v/Tunnelite?style=flat-square&logo=nuget&logoColor=white&label=NuGet&labelColor=0f172a&color=0284c7)](https://www.nuget.org/packages/Tunnelite/)
[![Build status](https://img.shields.io/github/actions/workflow/status/cristipufu/tunnelite/ci.yml?branch=master&style=flat-square&label=build&logo=github&logoColor=white&labelColor=0f172a)](https://github.com/cristipufu/tunnelite/actions/workflows/ci.yml)
[![MIT license](https://img.shields.io/badge/license-MIT-64748b?style=flat-square&labelColor=0f172a)](https://github.com/cristipufu/tunnelite/blob/main/LICENSE)

EOF

awk 'found { print } /^---$/ && !found { found=1; print }' "$input_readme" >> "$output_readme"
