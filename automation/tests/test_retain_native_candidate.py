#!/usr/bin/env python3
"""Exercise the identity CLI with disposable Git source and tiny test-only outputs."""

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


class NativeCandidateIdentityTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix="kicad identity ")
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name)
        self.helper = self.root / "automation/tools/retain_native_candidate.py"
        source = Path(__file__).resolve().parents[1] / "tools/retain_native_candidate.py"
        self.write("automation/tools/retain_native_candidate.py", source.read_bytes())
        self.write(".gitignore", b"automation/artifacts/\nautomation/**/bin/\n")
        self.write("source.txt", b"frozen test-only source\n")
        self.write(".devcoordinator.toml", b'''
[test.pcb-simulation-checkpoint]
[[test.pcb-simulation-checkpoint.check]]
name="native-build"
command=["cmake","--build","automation/artifacts/native","--target","kicad"]
''')
        self.git("init", "--quiet")
        self.git("add", ".")
        self.git("-c", "user.name=Identity fixture", "-c", "user.email=fixture@example.invalid",
                 "-c", "core.hooksPath=/dev/null", "commit", "--quiet", "-m", "Frozen test source")
        self.commit = self.git("rev-parse", "HEAD")
        self.build = "automation/artifacts/native/"
        self.native_files = (
            "kicad/kicad", "kicad/kicad-cli", "eeschema/eeschema", "eeschema/_eeschema.kiface",
            "pcbnew/pcbnew", "pcbnew/_pcbnew.kiface", "cvpcb/_cvpcb.kiface",
            "api/libkiapi.so", "common/libkicommon.so", "common/gal/libkigal.so",
        )
        self.assemblies = (
            "automation/src/KiCad.Automation.Mcp/bin/Debug/net10.0/kicad-mcp.dll",
            "automation/tests/KiCad.Automation.Tests/bin/Debug/net10.0/KiCad.Automation.Tests.dll",
        )
        self.schema_names = (
            "api.v1.schema.json", "pcm.v1.schema.json", "pcm.v2.schema.json",
            "kicad-remote-provider-metadata-v1.schema.json",
            "kicad-remote-symbol-manifest-v1.schema.json",
        )
        for name in self.native_files:
            self.write(self.build + name, b"test-only native bytes: " + name.encode())
        for name in self.assemblies:
            self.write(name, b"test-only managed bytes: " + name.encode())
        for name in self.schema_names:
            self.write(self.build + "schemas/" + name, b"{}\n")
        for name in ("build.ninja", ".ninja_log", "resources/images.tar.gz"):
            self.write(self.build + name, b"test-only build metadata\n")
        self.write(self.build + "CMakeCache.txt",
                   ("CMAKE_HOME_DIRECTORY:INTERNAL=" + str(self.root) + "\n").encode())
        self.write_version(self.commit)
        self.output = self.root / "automation/artifacts/identity"

    def git(self, *arguments):
        return subprocess.check_output(
            ["git", "-C", str(self.root), *arguments], text=True, stderr=subprocess.PIPE
        ).strip()

    def write(self, relative_path, data):
        path = self.root / relative_path
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    def write_version(self, commit, suffix=""):
        self.write(self.build + "kicad_build_version.h",
                   (f'#define KICAD_COMMIT_HASH "{commit}"\n'
                    f'#define KICAD_VERSION_FULL "10.99.0{suffix}"\n').encode())

    def invoke(self):
        return subprocess.run(
            [sys.executable, str(self.helper), "--output", str(self.output)],
            text=True, capture_output=True, timeout=20,
        )

    def assert_rejected(self, expected):
        result = self.invoke()
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn(expected, result.stderr)
        self.assertEqual("", result.stdout)
        self.assertFalse((self.output / "identity.json").exists(),
                         "A refused candidate must not retain a success identity.")

    def test_compatible_candidate_with_ignored_outputs_is_accepted(self):
        self.assertEqual("", self.git("status", "--porcelain=v1", "--untracked-files=normal"))
        self.write(self.build + "ignored-cache.bin", b"ordinary ignored build cache")
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stderr)
        summary = json.loads(result.stdout)
        identity = json.loads((self.output / "identity.json").read_text())
        self.assertEqual(self.commit, summary["sourceCommit"])
        self.assertEqual(self.commit, identity["generatedNativeCommit"])
        self.assertEqual(self.git("rev-parse", "HEAD^{tree}"), identity["sourceTree"])
        self.assertEqual(str(self.root), identity["nativeSourceDirectory"])
        self.assertEqual("clean", identity["sourceStatus"])
        self.assertEqual(len(self.native_files), len(identity["nativeBinaries"]))
        self.assertEqual(len(self.assemblies), len(identity["managedAssemblies"]))
        self.assertEqual(len(self.schema_names), len(identity["schemas"]))
        for row in identity["nativeBinaries"] + identity["managedAssemblies"] + identity["schemas"]:
            data = (self.root / row["path"]).read_bytes()
            self.assertEqual(len(data), row["bytes"])
            self.assertEqual(hashlib.sha256(data).hexdigest(), row["sha256"])

    def test_wrong_source_directory_is_rejected(self):
        self.write(self.build + "CMakeCache.txt",
                   ("CMAKE_HOME_DIRECTORY:INTERNAL=" + str(self.root / "another checkout") + "\n").encode())
        self.assert_rejected("Native build directory belongs to another source checkout.")

    def test_missing_source_directory_is_rejected(self):
        self.write(self.build + "CMakeCache.txt", b"CMAKE_BUILD_TYPE:STRING=Debug\n")
        self.assert_rejected("Native build directory belongs to another source checkout.")

    def test_wrong_generated_commit_is_rejected(self):
        self.write_version("0" * 40)
        self.assert_rejected("Regenerated native commit does not match the frozen candidate.")

    def test_generated_dirty_version_is_rejected(self):
        self.write_version(self.commit, "-dirty")
        self.assert_rejected("Native build metadata reports unfrozen source.")

    def test_changed_tracked_source_is_rejected(self):
        self.write("source.txt", b"changed source\n")
        self.assert_rejected("Candidate source is not frozen:")

    def test_meaningful_untracked_source_is_rejected(self):
        self.write("new-source.txt", b"uncommitted source\n")
        self.assert_rejected("Candidate source is not frozen:")

    def test_each_missing_native_binary_is_rejected(self):
        for name in self.native_files:
            with self.subTest(binary=name):
                path = self.root / (self.build + name)
                data = path.read_bytes()
                path.unlink()
                try:
                    self.assert_rejected(str(path))
                finally:
                    path.write_bytes(data)

    def test_each_missing_managed_assembly_is_rejected(self):
        for name in self.assemblies:
            with self.subTest(assembly=name):
                path = self.root / name
                data = path.read_bytes()
                path.unlink()
                try:
                    self.assert_rejected(str(path))
                finally:
                    path.write_bytes(data)

    def test_each_missing_startup_schema_is_rejected_while_other_schemas_remain(self):
        for name in self.schema_names:
            with self.subTest(schema=name):
                path = self.root / (self.build + "schemas/" + name)
                data = path.read_bytes()
                path.unlink()
                try:
                    self.assert_rejected(str(path))
                finally:
                    path.write_bytes(data)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results-directory", type=Path, required=True)
    arguments = parser.parse_args()
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(NativeCandidateIdentityTests)
    test_ids = [test.id() for test in suite]
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    arguments.results_directory.mkdir(parents=True, exist_ok=True)
    (arguments.results_directory / "results.json").write_text(json.dumps({
        "scope": "test_only", "proof": "fixture_only", "passed": result.wasSuccessful(),
        "existingTestsInspected": [
            "automation/tests/KiCad.Automation.Tests/LinuxPayloadFingerprintTests.cs",
            "automation/tests/KiCad.Automation.Tests/HostedWindowsCheckpointTests.cs",
            "qa/tests/cli/test_common.py",
        ],
        "newScenarioReason": "Existing tests exercise other packaging contracts or kicad-cli; "
                             "this scenario exercises the identity CLI without a native build.",
        "tests": test_ids, "testCount": result.testsRun,
        "failedCases": [test.id() for test, _ in result.failures],
        "errorCases": [test.id() for test, _ in result.errors],
    }, indent=2) + "\n")
    sys.exit(0 if result.wasSuccessful() else 1)
