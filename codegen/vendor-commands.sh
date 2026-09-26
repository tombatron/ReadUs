#!/usr/bin/env bash
# Refreshes codegen/redis-commands/ from the redis/redis command table (project spec §3).
#
# Usage: ./codegen/vendor-commands.sh [tag]   (default: 8.10.1)
set -euo pipefail

TAG="${1:-8.10.1}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEST="$REPO_ROOT/codegen/redis-commands"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo "Fetching src/commands/ from redis/redis@$TAG..."
cd "$WORK"
git init -q
git remote add origin https://github.com/redis/redis.git
git fetch --depth 1 origin "tag" "$TAG"
git sparse-checkout init --cone
git sparse-checkout set src/commands
git checkout -q FETCH_HEAD

COMMIT="$(git rev-parse FETCH_HEAD)"
DATE="$(git log -1 --format=%ci FETCH_HEAD)"

rm -f "$DEST"/*.json
cp src/commands/*.json "$DEST"/

cat > "$DEST/SOURCE.md" <<EOF
# Vendored Redis command table

Source: https://github.com/redis/redis, tag \`$TAG\`, commit
\`$COMMIT\` ($DATE), path \`src/commands/\`.

This is the ground truth the command-table source generator
(\`ReadUs.SourceGenerators\`) reads at build time (project spec §3).

Per project spec §11, this snapshot is what versions the generated command
surface: retargeting to a different Redis major later means re-running
\`codegen/vendor-commands.sh\` against a different tag and regenerating, not
rewriting call sites.

To refresh: \`./codegen/vendor-commands.sh <tag>\`.
EOF

echo "Vendored $(ls "$DEST"/*.json | wc -l) command definitions from redis/redis@$TAG into $DEST"
