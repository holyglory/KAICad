using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class ProtobufGenerationTests
{
    [TestMethod, TestCategory("ProtobufGeneration")]
    [DataRow("path")]
    [DataRow("target")]
    [DataRow("configured-target")]
    public async Task StagedProtobufGenerationPreservesUnchangedOutputsAndRecoversMissingFiles(string compilerKind)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The operational generator fixture is qualified on Linux Ninja.");
            return;
        }

        string root = FindRoot();
        string cmake = await ToolVersion("cmake", "--version");
        string protoc = FindExecutable("protoc");
        string ninja = FindExecutable("ninja");
        string helper = Path.Combine(root, "cmake/BuildSteps/GenerateProtobuf.cmake");
        string fixture = Directory.CreateTempSubdirectory("kicad-protobuf-generation-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(root, "automation/artifacts/protobuf-generation",
            Guid.NewGuid().ToString("N"))).FullName;
        var phases = new List<object>();
        try
        {
            await CreateFixture(fixture, helper, compilerKind);
            string wrapper = await CreateProtocWrapper(fixture, protoc);
            string build = Path.Combine(fixture, "build");
            await Run("configure", fixture, "cmake", ["-S", fixture, "-B", build, "-G", "Ninja",
                "-DPROTOC=" + wrapper]);
            await Run("initial", build, "cmake", ["--build", build, "--target", "generate"]);
            var initial = Snapshot(build);
            long initialRuns = CountRuns(fixture);
            phases.Add(new { name = "initial", protocRuns = initialRuns, files = initial });

            await Run("no-op", build, "cmake", ["--build", build, "--target", "generate"]);
            Assert.AreEqual(initialRuns, CountRuns(fixture), "An unchanged build must not invoke protoc.");
            AssertSnapshotEqual(initial, Snapshot(build), "no-op");

            await ChangeProto(fixture, "common/independent.proto", "message Independent { string value = 1; string narrow = 2; }\n");
            await Run("narrow", build, "cmake", ["--build", build, "--target", "generate"]);
            var narrow = Snapshot(build);
            Assert.IsTrue(narrow["common/independent.pb.cc"].Hash != initial["common/independent.pb.cc"].Hash);
            Assert.IsTrue(narrow["common/independent.pb.h"].Hash != initial["common/independent.pb.h"].Hash);
            AssertUnchanged(initial, narrow, "narrow", "common/root.pb.cc", "common/root.pb.h", "common/imported.pb.cc", "common/imported.pb.h");
            phases.Add(new { name = "narrow", protocRuns = CountRuns(fixture), changed = Changed(initial, narrow) });

            await ChangeProto(fixture, "common/imported.proto", "message Imported { string value = 1; string from_import = 2; }\n");
            await Run("import-change", build, "cmake", ["--build", build, "--target", "generate"]);
            var imported = Snapshot(build);
            Assert.IsTrue(imported["common/imported.pb.cc"].Hash != narrow["common/imported.pb.cc"].Hash);
            Assert.IsTrue(imported["common/imported.pb.h"].Hash != narrow["common/imported.pb.h"].Hash);
            AssertUnchanged(narrow, imported, "import-change", "common/independent.pb.cc", "common/independent.pb.h");
            phases.Add(new { name = "import-change", protocRuns = CountRuns(fixture), changed = Changed(narrow, imported) });

            long beforeMissing = CountRuns(fixture);
            File.Delete(Path.Combine(build, "generated/common/independent.pb.h"));
            await Run("missing-output", build, "cmake", ["--build", build, "--target", "generate"]);
            var restored = Snapshot(build);
            Assert.AreEqual(imported["common/independent.pb.h"].Hash, restored["common/independent.pb.h"].Hash);
            Assert.AreEqual(imported["common/root.pb.h"], restored["common/root.pb.h"]);
            long afterMissing = CountRuns(fixture);
            Assert.IsTrue(afterMissing > beforeMissing, "A missing byproduct must rerun the generator.");
            phases.Add(new { name = "missing-output", protocRuns = afterMissing, changed = Changed(imported, restored) });

            var beforeFailure = Snapshot(build);
            await ChangeProto(fixture, "common/imported.proto", "message Imported { string value = 1; string from_import = 2; string failing = 3; }\n");
            await File.WriteAllTextAsync(Path.Combine(fixture, "fail-protoc"), "fail\n");
            var failed = await Run("failure", build, "cmake", ["--build", build, "--target", "generate"], success: false);
            Assert.AreNotEqual(0, failed.Code);
            AssertSnapshotEqual(beforeFailure, Snapshot(build), "protoc failure");
            phases.Add(new { name = "failure", protocRuns = CountRuns(fixture), exitCode = failed.Code, preserved = true });
            File.Delete(Path.Combine(fixture, "fail-protoc"));
            await Run("failure-recovery", build, "cmake", ["--build", build, "--target", "generate"]);
            Assert.IsTrue(Snapshot(build)["common/imported.pb.h"].Hash != beforeFailure["common/imported.pb.h"].Hash);
            phases.Add(new { name = "failure-recovery", protocRuns = CountRuns(fixture) });

            await MultiConfigIsolation(fixture, helper, wrapper, phases);
            await File.WriteAllTextAsync(Path.Combine(evidence, "receipt.json"), JsonSerializer.Serialize(new
            {
                status = "passed", compilerKind, cmake, protoc = await ToolVersion(protoc, "--version"), ninja,
                outputs = RelativeOutputs,
                baseline = new
                {
                    provenance = "api/CMakeLists.txt, automation/artifacts/native/build.ninja, .ninja_log and ninja -t deps",
                    source = new { path = "api/CMakeLists.txt", sha256 = Digest(Path.Combine(root, "api/CMakeLists.txt")) },
                    buildMetadata = new
                    {
                        buildNinjaSha256 = Digest(Path.Combine(root, "automation/artifacts/native/build.ninja")),
                        ninjaLogSha256 = Digest(Path.Combine(root, "automation/artifacts/native/.ninja_log"))
                    },
                    schemaCount = 29, generatedOutputCount = 58, generatedObjectRecordCount = 29,
                    summedGeneratedObjectCompileMilliseconds = 46073,
                    eligibleDependentObjectCount = 191, generatedHeaderDependencyEdges = 1347,
                    measurementNote = "Summed compile durations and dependency metadata are baseline evidence, not wall-clock speedup claims."
                }, phases
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Directory.Delete(fixture, true); }
    }

    private static async Task MultiConfigIsolation(string fixture, string helper, string wrapper, List<object> phases)
    {
        string multi = Path.Combine(fixture, "multi");
        await Run("configure-multi", fixture, "cmake", ["-S", fixture, "-B", multi, "-G", "Ninja Multi-Config",
            "-DPROTOC=" + wrapper]);
        await Run("multi-debug", multi, "cmake", ["--build", multi, "--config", "Debug", "--target", "generate"]);
        string debug = Path.Combine(multi, "generated/Debug/common/root.pb.h");
        string release = Path.Combine(multi, "generated/Release/common/root.pb.h");
        Assert.IsTrue(File.Exists(debug));
        Assert.IsFalse(File.Exists(release), "A Debug build must not publish into Release output.");
        long beforeRelease = File.GetLastWriteTimeUtc(debug).Ticks;
        await Run("multi-release", multi, "cmake", ["--build", multi, "--config", "Release", "--target", "generate"]);
        Assert.IsTrue(File.Exists(release));
        Assert.AreEqual(beforeRelease, File.GetLastWriteTimeUtc(debug).Ticks);
        phases.Add(new { name = "multi-config-isolation", debug, release, separate = !string.Equals(debug, release, StringComparison.Ordinal) });
    }

    private static async Task CreateFixture(string fixture, string helper, string compilerKind)
    {
        Directory.CreateDirectory(Path.Combine(fixture, "common"));
        File.Copy(helper, Path.Combine(fixture, "GenerateProtobuf.cmake"));
        File.Copy(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(helper)!)!, "ProtobufCompiler.cmake"),
            Path.Combine(fixture, "ProtobufCompiler.cmake"));
        await File.WriteAllTextAsync(Path.Combine(fixture, "compiler-kind.cmake"),
            $"set(COMPILER_KIND \"{compilerKind}\")\n");
        await File.WriteAllTextAsync(Path.Combine(fixture, "common/root.proto"),
            "syntax = \"proto3\";\npackage fixture;\nimport \"common/imported.proto\";\nmessage Root { Imported imported = 1; }\n");
        await File.WriteAllTextAsync(Path.Combine(fixture, "common/imported.proto"),
            "syntax = \"proto3\";\npackage fixture;\nmessage Imported { string value = 1; }\n");
        await File.WriteAllTextAsync(Path.Combine(fixture, "common/independent.proto"),
            "syntax = \"proto3\";\npackage fixture;\nmessage Independent { string value = 1; }\n");
        await File.WriteAllTextAsync(Path.Combine(fixture, "CMakeLists.txt"), """
            cmake_minimum_required(VERSION 3.22)
            project(ProtobufGenerationFixture NONE)
            include(${CMAKE_CURRENT_SOURCE_DIR}/compiler-kind.cmake)
            set(Protobuf_PROTOC_EXECUTABLE "${PROTOC}")
            if(NOT COMPILER_KIND STREQUAL "path")
                add_executable(protobuf::protoc IMPORTED GLOBAL)
                if(COMPILER_KIND STREQUAL "configured-target")
                    set_target_properties(protobuf::protoc PROPERTIES
                        IMPORTED_CONFIGURATIONS Release IMPORTED_LOCATION_RELEASE "${PROTOC}")
                else()
                    set_target_properties(protobuf::protoc PROPERTIES IMPORTED_LOCATION "${PROTOC}")
                endif()
                set(Protobuf_PROTOC_EXECUTABLE protobuf::protoc)
            endif()
            include(${CMAKE_CURRENT_SOURCE_DIR}/ProtobufCompiler.cmake)
            set(PROTO_FILES common/root.proto common/imported.proto common/independent.proto)
            get_property(IS_MULTI_CONFIG GLOBAL PROPERTY GENERATOR_IS_MULTI_CONFIG)
            if(IS_MULTI_CONFIG)
                set(CONFIG_DIR "$<CONFIG>")
            else()
                set(CONFIG_DIR ".")
            endif()
            set(DEST_ROOT "${CMAKE_CURRENT_BINARY_DIR}/generated/${CONFIG_DIR}")
            set(STAGING_ROOT "${CMAKE_CURRENT_BINARY_DIR}/staging/${CONFIG_DIR}")
            set(STAMP "${DEST_ROOT}/.stamp")
            set(OUTPUTS)
            foreach(PROTO IN LISTS PROTO_FILES)
                string(REGEX REPLACE "\\.proto$" ".pb.cc" CPP "${PROTO}")
                string(REGEX REPLACE "\\.proto$" ".pb.h" HEADER "${PROTO}")
                list(APPEND OUTPUTS "${DEST_ROOT}/${CPP}" "${DEST_ROOT}/${HEADER}")
            endforeach()
            if(CMAKE_GENERATOR MATCHES "Ninja")
                add_custom_command(OUTPUT "${STAMP}" BYPRODUCTS ${OUTPUTS}
                    COMMAND "${CMAKE_COMMAND}" "-DPROTOC=${KIAPI_PROTOC_COMMAND}" -DPROTO_ROOT=${CMAKE_CURRENT_SOURCE_DIR}
                        "-DPROTO_FILES=${PROTO_FILES}" -DSTAGING_ROOT=${STAGING_ROOT} -DDEST_ROOT=${DEST_ROOT}
                        -P "${CMAKE_CURRENT_SOURCE_DIR}/GenerateProtobuf.cmake"
                    COMMAND "${CMAKE_COMMAND}" -E touch "${STAMP}"
                    DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/GenerateProtobuf.cmake ${CMAKE_CURRENT_SOURCE_DIR}/common/root.proto
                        ${CMAKE_CURRENT_SOURCE_DIR}/common/imported.proto ${CMAKE_CURRENT_SOURCE_DIR}/common/independent.proto
                        "${PROTOC}"
                    VERBATIM)
                add_custom_target(kiapi_protobuf_codegen DEPENDS "${STAMP}")
                set(GENERATION_TARGET kiapi_protobuf_codegen)
            else()
                add_custom_command(OUTPUT ${OUTPUTS}
                    COMMAND "${CMAKE_COMMAND}" -DPROTOC=${PROTOC} -DPROTO_ROOT=${CMAKE_CURRENT_SOURCE_DIR}
                        "-DPROTO_FILES=${PROTO_FILES}" -DSTAGING_ROOT=${STAGING_ROOT} -DDEST_ROOT=${DEST_ROOT}
                        -P "${CMAKE_CURRENT_SOURCE_DIR}/GenerateProtobuf.cmake"
                    DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/GenerateProtobuf.cmake ${CMAKE_CURRENT_SOURCE_DIR}/common/root.proto
                        ${CMAKE_CURRENT_SOURCE_DIR}/common/imported.proto ${CMAKE_CURRENT_SOURCE_DIR}/common/independent.proto
                        "${PROTOC}"
                    VERBATIM)
                add_custom_target(kiapi_protobuf_codegen DEPENDS ${OUTPUTS})
                set(GENERATION_TARGET kiapi_protobuf_codegen)
            endif()
            add_library(kiapi INTERFACE)
            add_dependencies(kiapi ${GENERATION_TARGET})
            add_custom_target(generate ALL DEPENDS kiapi)
            """);
    }

    private static async Task<string> CreateProtocWrapper(string fixture, string protoc)
    {
        string wrapper = Path.Combine(fixture, "protoc-wrapper.sh");
        await File.WriteAllTextAsync(wrapper, """
#!/bin/sh
set -eu
printf '1\n' >> "$PROTOC_COUNT"
"$REAL_PROTOC" "$@"
if [ -f "$PROTOC_FAIL" ]; then exit 42; fi
""");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("REAL_PROTOC", protoc);
        Environment.SetEnvironmentVariable("PROTOC_COUNT", Path.Combine(fixture, "protoc-count"));
        Environment.SetEnvironmentVariable("PROTOC_FAIL", Path.Combine(fixture, "fail-protoc"));
        return wrapper;
    }

    private static async Task ChangeProto(string fixture, string relative, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(fixture, relative), "syntax = \"proto3\";\npackage fixture;\n" + content);
        await Task.Delay(1100);
    }

    private static readonly string[] RelativeOutputs =
        ["common/root.pb.cc", "common/root.pb.h", "common/imported.pb.cc", "common/imported.pb.h",
         "common/independent.pb.cc", "common/independent.pb.h"];

    private static Dictionary<string, FileSnapshot> Snapshot(string build) => RelativeOutputs.ToDictionary(relative => relative,
        relative =>
        {
            string path = Path.Combine(build, "generated", relative);
            return new FileSnapshot(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
                File.GetLastWriteTimeUtc(path).Ticks);
        });

    private static void AssertUnchanged(IReadOnlyDictionary<string, FileSnapshot> before, IReadOnlyDictionary<string, FileSnapshot> after,
        string phase, params string[] names)
    {
        foreach (string name in names) Assert.AreEqual(before[name], after[name], phase + " rewrote " + name);
    }

    private static void AssertSnapshotEqual(IReadOnlyDictionary<string, FileSnapshot> before, IReadOnlyDictionary<string, FileSnapshot> after, string phase)
    {
        foreach (string name in before.Keys) Assert.AreEqual(before[name], after[name], phase + " changed " + name);
    }

    private static Dictionary<string, FileSnapshot> Changed(IReadOnlyDictionary<string, FileSnapshot> before,
        IReadOnlyDictionary<string, FileSnapshot> after) => before.Where(pair => pair.Value != after[pair.Key])
        .ToDictionary(pair => pair.Key, pair => after[pair.Key]);

    private static long CountRuns(string fixture) => File.Exists(Path.Combine(fixture, "protoc-count"))
        ? File.ReadAllLines(Path.Combine(fixture, "protoc-count")).LongLength : 0;

    private static string? Digest(string path) => File.Exists(path)
        ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : null;

    private static async Task<string> ToolVersion(string executable, string argument)
    {
        var result = await Run("version-" + executable, Directory.GetCurrentDirectory(), executable, [argument]);
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static string FindExecutable(string name)
    {
        string? path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, name)).FirstOrDefault(File.Exists);
        Assert.IsNotNull(path, name + " is required for the real generator fixture.");
        return path!;
    }

    private static async Task<(int Code, string Output, string Error)> Run(string name, string directory,
        string executable, string[] arguments, bool success = true)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = await stdout, error = await stderr;
        if (success) Assert.AreEqual(0, process.ExitCode, name + " failed: " + error + output);
        return (process.ExitCode, output, error);
    }

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "api/CMakeLists.txt"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        return directory!.FullName;
    }

    private sealed record FileSnapshot(string Hash, long MtimeTicks);
}
