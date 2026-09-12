using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class CompilerCacheTests
{
    [TestMethod, TestCategory("CompilerCache")]
    public async Task NativeCompilationIsReusableAcrossWorktreesWithoutStaleInputs()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("KICAD_TEST_COMPILER_CACHE") is not { Length: > 0 } cache)
        { Assert.Inconclusive("Requires the explicit Linux compiler-cache qualification environment."); return; }
        string root = FindRoot();
        string fixture = Directory.CreateTempSubdirectory("kicad-compiler-cache-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(root, "automation/artifacts/compiler-cache", Guid.NewGuid().ToString("N"))).FullName;
        var phases = new List<object>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var token = deadline.Token;
        try
        {
            string first = await MakeProject("first"), second = await MakeProject("second");
            await Run("configure-first", first, "cmake", Configure(first, 0));
            await Run("configure-second", second, "cmake", Configure(second, 0));
            var cold = await Build("cold", first);
            Assert.IsTrue(cold.Misses >= 3, "Cold probe and real KiCad journal units must actually compile.");
            Assert.AreEqual(0L, cold.Hits);
            await CheckPrograms(first, 17);
            string coldObject = Hash(Path.Combine(first, "build/CMakeFiles/probe.dir/common/probe.cpp.o"));

            await Run("clean-first", first, "cmake", ["--build", "build", "--target", "clean"]);
            var warm = await Build("warm", first);
            Assert.IsTrue(warm.Hits >= 3 && warm.Misses == 0, "A real rebuild must be served from the compiler cache.");
            Assert.AreEqual(coldObject, Hash(Path.Combine(first, "build/CMakeFiles/probe.dir/common/probe.cpp.o")));
            await CheckPrograms(first, 17);

            var cross = await Build("cross-worktree", second);
            Assert.IsTrue(cross.Hits >= 3, "The second directory must reuse actual compilations, not only link old objects.");
            Assert.AreEqual(coldObject, Hash(Path.Combine(second, "build/CMakeFiles/probe.dir/common/probe.cpp.o")));
            await CheckPrograms(second, 17);

            await File.WriteAllTextAsync(Path.Combine(second, "common/value.h"), "#pragma once\ninline constexpr int cache_value = 23;\n", token);
            var header = await Build("changed-header", second);
            Assert.IsTrue(header.Misses >= 1, "Changed header bytes must invalidate the object.");
            await CheckPrograms(second, 23);
            await Run("configure-flags", second, "cmake", Configure(second, 5));
            var flags = await Build("changed-flags", second);
            Assert.IsTrue(flags.Misses >= 1, "Changed compiler definitions must invalidate the object.");
            await CheckPrograms(second, 28);
            Assert.IsTrue(cold.Disabled > 0 && warm.Disabled > 0 && cross.Disabled > 0,
                "Timestamp-bearing compilation must bypass ccache even when PCH caching is enabled.");

            string changedSource = "#include \"value.h\"\n#include <iostream>\nint main() { std::cout << cache_value + CACHE_BIAS + 1; }\n";
            await File.WriteAllTextAsync(Path.Combine(second, "common/probe.cpp"), changedSource, token);
            var source = await Build("changed-source", second);
            Assert.IsTrue(source.Misses >= 1, "Changed source bytes must invalidate the object.");
            await CheckPrograms(second, 29);
            await File.WriteAllTextAsync(Path.Combine(second, "common/value.h"),
                "#pragma once\ninline constexpr int cache_value = 23;\ninline const char* cache_time = __TIME__;\n", token);
            var incrementalRejected = await Run("reject-incremental-time", second, "cmake", ["--build", "build"], false);
            Assert.AreNotEqual(0, incrementalRejected.Code,
                "Time macros added through a header must fail even without rerunning configuration.");
            StringAssert.Contains(incrementalRejected.Output + incrementalRejected.Error, "date-time");
            await File.WriteAllTextAsync(Path.Combine(second, "common/value.h"), "#pragma once\ninline constexpr int cache_value = 23;\n", token);
            await Build("recovery", second);
            await CheckPrograms(second, 29);

            await File.WriteAllTextAsync(Path.Combine(second, "common/unguarded.cpp"), "const char* build_time = __TIME__;\n", token);
            var rejected = await Run("reject-unguarded-time", second, "cmake", Configure(second, 5), false);
            Assert.AreNotEqual(0, rejected.Code);
            StringAssert.Contains(rejected.Error, "Timestamp-bearing source must bypass reusable caching");
            await File.WriteAllTextAsync(Path.Combine(evidence, "receipt.json"), JsonSerializer.Serialize(new
            {
                status = "passed", compilerCache = cache, coldMilliseconds = cold.Milliseconds,
                warmMilliseconds = warm.Milliseconds, crossWorktreeMilliseconds = cross.Milliseconds,
                measuredScope = "CMake PCH fixture plus real KiCad document-change-journal test sources; includes linking",
                phases
            }, new JsonSerializerOptions { WriteIndented = true }), token);
        }
        finally { Directory.Delete(fixture, true); }

        async Task<string> MakeProject(string name)
        {
            string directory = Directory.CreateDirectory(Path.Combine(fixture, name)).FullName;
            Directory.CreateDirectory(Path.Combine(directory, "common"));
            Directory.CreateDirectory(Path.Combine(directory, "include/api"));
            File.Copy(Path.Combine(root, "cmake/KiCadCompilerCache.cmake"), Path.Combine(directory, "CompilerCache.cmake"));
            File.Copy(Path.Combine(root, "include/api/document_change_journal.h"), Path.Combine(directory, "include/api/document_change_journal.h"));
            File.Copy(Path.Combine(root, "qa/tests/common/test_document_change_journal.cpp"), Path.Combine(directory, "journal.cpp"));
            File.Copy(Path.Combine(root, "qa/tests/common/test_document_change_journal_main.cpp"), Path.Combine(directory, "journal-main.cpp"));
            await File.WriteAllTextAsync(Path.Combine(directory, "common/value.h"), "#pragma once\ninline constexpr int cache_value = 17;\n", token);
            await File.WriteAllTextAsync(Path.Combine(directory, "common/probe.cpp"), "#include \"value.h\"\n#include <iostream>\nint main() { std::cout << cache_value + CACHE_BIAS; }\n", token);
            await File.WriteAllTextAsync(Path.Combine(directory, "common/stamp.cpp"), "// ccache:disable\n#pragma GCC diagnostic ignored \"-Wdate-time\"\n#include <cstdio>\nint main() { puts(__TIME__); }\n", token);
            await File.WriteAllTextAsync(Path.Combine(directory, "CMakeLists.txt"), """
                cmake_minimum_required(VERSION 3.25)
                project(CacheProbe LANGUAGES CXX)
                set(KICAD_USE_PCH ON)
                include(CompilerCache.cmake)
                find_program(CCACHE ccache REQUIRED)
                set(CMAKE_CXX_COMPILER_LAUNCHER ${CCACHE} ${KICAD_CCACHE_ARGUMENTS})
                # Isolate the cold comparison without clearing anyone's cache.
                list(APPEND CMAKE_CXX_COMPILER_LAUNCHER "namespace=kicad-qualification-${CACHE_RUN}")
                find_package(Boost REQUIRED COMPONENTS unit_test_framework)
                add_executable(probe common/probe.cpp)
                target_compile_features(probe PRIVATE cxx_std_20)
                target_compile_definitions(probe PRIVATE CACHE_BIAS=${CACHE_BIAS} CACHE_RUN=${CACHE_RUN})
                target_precompile_headers(probe PRIVATE <vector> <string> <map>)
                add_executable(stamp common/stamp.cpp)
                add_executable(journal journal-main.cpp journal.cpp)
                target_compile_features(journal PRIVATE cxx_std_20)
                target_compile_definitions(journal PRIVATE CACHE_RUN=${CACHE_RUN})
                target_include_directories(journal PRIVATE include)
                target_link_libraries(journal PRIVATE Boost::unit_test_framework)
                """, token);
            return directory;
        }

        string[] Configure(string source, int bias) => ["-S", source, "-B", Path.Combine(source, "build"), "-G", "Ninja",
            "-DCMAKE_BUILD_TYPE=Debug", "-DKICAD_CCACHE_DIR=" + cache, "-DCACHE_BIAS=" + bias.ToString(CultureInfo.InvariantCulture),
            "-DCACHE_RUN=" + Path.GetFileName(fixture).Replace('-', '_')];

        async Task CheckPrograms(string directory, int expected)
        {
            var probe = await Run("probe-" + phases.Count, directory, Path.Combine(directory, "build/probe"), []);
            Assert.AreEqual(expected.ToString(CultureInfo.InvariantCulture), probe.Output.Trim());
            await Run("journal-" + phases.Count, directory, Path.Combine(directory, "build/journal"), []);
        }

        async Task<(long Hits, long Misses, long Disabled, double Milliseconds)> Build(string name, string directory)
        {
            string stats = Path.Combine(evidence, name + ".stats");
            var build = await Run(name, directory, "cmake", ["--build", "build"], statsLog: stats);
            var counters = await Run(name + "-stats", directory, "ccache", ["--print-log-stats"], statsLog: stats);
            var values = counters.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length == 2).ToDictionary(parts => parts[0], parts => long.Parse(parts[1], CultureInfo.InvariantCulture));
            long Get(string key)
            {
                Assert.IsTrue(values.ContainsKey(key), "Missing ccache counter: " + key);
                return values[key];
            }
            long hits = Get("direct_cache_hit") + Get("preprocessed_cache_hit"), misses = Get("cache_miss"), disabled = Get("disabled");
            phases.Add(new { name, hits, misses, disabled, milliseconds = build.Milliseconds });
            return (hits, misses, disabled, build.Milliseconds);
        }

        async Task<(int Code, string Output, string Error, double Milliseconds)> Run(string name, string directory,
            string executable, string[] arguments, bool success = true, string? statsLog = null)
        {
            var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["CCACHE_DIR"] = cache;
            if (statsLog is not null) start.Environment["CCACHE_STATSLOG"] = statsLog;
            var watch = Stopwatch.StartNew();
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
            try { await process.WaitForExitAsync(token); }
            finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
            string stdout = await output, stderr = await error;
            await File.WriteAllTextAsync(Path.Combine(evidence, name + ".stdout.log"), stdout, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(evidence, name + ".stderr.log"), stderr, CancellationToken.None);
            if (success) Assert.AreEqual(0, process.ExitCode, name + " failed; evidence: " + evidence);
            return (process.ExitCode, stdout, stderr, watch.Elapsed.TotalMilliseconds);
        }
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "cmake/KiCadCompilerCache.cmake"))) return directory.FullName;
        throw new DirectoryNotFoundException("KiCad source root not found.");
    }
}
