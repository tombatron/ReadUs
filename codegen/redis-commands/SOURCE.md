# Vendored Redis command table

Source: https://github.com/redis/redis, tag `8.10.1`, commit
`3399357e7c17b668289386b8a15a3037bc4527b1` (2026-08-17), path `src/commands/`.

This is the ground truth the command-table source generator
(`ReadUs.SourceGenerators`) reads at build time (project spec §3). Matches the
Redis version this project targets (§1) — the local dev server used
throughout `ReadUs.Tests.Integration` reports `redis_version:8.10.1`.

Per project spec §11, this snapshot is what versions the generated command
surface: retargeting to a different Redis major later means re-running
`codegen/vendor-commands.sh` against a different tag and regenerating, not
rewriting call sites.

To refresh: `./codegen/vendor-commands.sh <tag>`.
