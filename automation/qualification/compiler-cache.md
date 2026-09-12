# Local native compiler cache

Linux native development graphs use KiCad's existing `USE_CCACHE` hook with a
shared cache at `/mnt/build-storage/codex/kicad/compiler-cache`, bounded to 100 GB.
Install the distribution's `ccache` package before configuring. No cache is
published or sent to remote storage; each worktree keeps its own build outputs.

For another local GCC/Clang checkout, use:

```sh
cmake -S . -B build -G Ninja -DUSE_CCACHE=ON -DKICAD_CCACHE_DIR=/absolute/local/cache
cmake --build build
CCACHE_DIR=/absolute/local/cache ccache --show-stats
```

The profile normalizes source/debug paths, identifies the compiler by content,
does not hard-link mutable object files, and retains the existing parallel build
controls. PCH support uses ccache's documented `pch_defines,time_macros` setting;
timestamp-bearing native source must carry `ccache:disable` in its first 4096
bytes. The configure-time check rejects unguarded time macros in native source
roots. `common/build_version.cpp` bypasses caching so its build date stays real.
Compiler diagnostics also reject time macros introduced by incremental edits or
headers after configuration. The uncached build-version source explicitly permits
its intentional timestamp; other sources cannot silently cache stale timestamps.

Run `devcoordinator2 test start /absolute/worktree --test compiler-cache --tier
development` for compiled cache qualification. It compares a cold build, a warm
rebuild and a second-directory build, including actual KiCad journal-test sources;
it also checks changed headers/definitions and timestamp bypass. Receipts retain
hits, misses and elapsed build times. They do not claim faster linking or tests,
or qualify another platform/compiler. Ordinary native graphs use the same profile.

Keep caches out of tracked source, signed packages and evidence snapshots. Do not
clear the shared cache to measure a cold build; qualification uses unique input
identity and per-build statistics. To stop using it in a custom build, reconfigure
with `-DUSE_CCACHE=OFF`; existing frozen runs must not be reconfigured in place.
