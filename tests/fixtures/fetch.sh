#!/usr/bin/env bash
# Downloads Toolsmith, for ToolsmithTests, into tests/fixtures/Toolsmith. It is another author's
# work, so it is fetched rather than kept in this repository, and kept out of fixtures/Mods so the
# other suites run on a vanilla game. The hash is of the release as downloaded on 2026-10-22;
# ModDB publishes none.
set -euo pipefail

dir="$(cd "$(dirname "$0")" && pwd)/Toolsmith"
mkdir -p "$dir"

fetch() {
    [ -f "$dir/$1" ] && { echo "have  $1"; return; }
    echo "fetch $1"
    curl -fsSL -o "$dir/$1.part" "$2"
    echo "$3  $dir/$1.part" | sha256sum -c --quiet
    mv "$dir/$1.part" "$dir/$1"
}

fetch toolsmith_1.2.19.zip \
    "https://moddbcdn.vintagestory.at/toolsmith_1.2.19_1de2759331b979e6dd37568bab636cc5.zip?dl=toolsmith_1.2.19.zip" \
    325ead64a9192ea7426d017e1c5141286f860f95c3af5dd34b79ad14f060974b
