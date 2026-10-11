using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace KiCad.Automation.Validation;

public sealed record HostedDeliveryRequest(string Repository, string Commit, string Architecture,
    string Output, string DependencyCommit, string Phase = "all");

/// <summary>Finite native build on a disposable GitHub runner, never a remote-control worker.</summary>
public static class HostedDelivery
{
    public static void ValidateTarget(string architecture, bool mac, bool windows, Architecture processArchitecture)
    {
        if (mac) MacArchitecture.RequireExecutionTarget(architecture, processArchitecture);
        else if (!windows || architecture != "x64" || processArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Hosted delivery requires native Mac arm64/x64 or Windows x64 execution.");
    }

    public static void RequireWindowsX64(string path)
    {
        using var stream = File.OpenRead(path);
        using var image = new PEReader(stream);
        if (image.PEHeaders.CoffHeader.Machine != Machine.Amd64)
            throw new InvalidDataException("The packaged Windows binary is not x64: " + Path.GetFileName(path));
    }

    public static void ConfigureWindowsTransportCheck(IDictionary<string, string?> environment,
        string check, string library)
    {
        // Source test binaries are not installed beside the packaged DLLs.
        // The installed executable must instead prove its ordinary DLL search.
        if (check == "managed-contracts") environment["KICAD_AUTOMATION_NNG_LIBRARY"] = library;
        else if (check == "managed-runtime") environment.Remove("KICAD_AUTOMATION_NNG_LIBRARY");
    }

    public static async Task<bool> RunAsync(HostedDeliveryRequest request, CancellationToken cancellationToken)
    {
        Evidence.RequireCommit(request.Commit);
        Evidence.RequireCommit(request.DependencyCommit);
        bool mac = OperatingSystem.IsMacOS();
        HostedWindowsCheckpoint.RequirePhase(request.Phase, mac);
        ValidateTarget(request.Architecture, mac, OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture);
        // SA-09 authorizes clean dependency setup only on disposable hosted runners.
        // Existing personal Macs continue to use the non-bootstrap `mac` command.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("Use this bootstrap only on a GitHub-hosted runner; use the manual mac command on an existing Mac.");
        string repository = Path.GetFullPath(request.Repository);
        string output = Path.GetFullPath(request.Output);
        bool finishPrepared = request.Phase == "build";
        HostedWindowsCheckpoint? checkpoint = finishPrepared
            ? await HostedWindowsCheckpoint.ReadAsync(request, cancellationToken) : null;
        if (finishPrepared) await HostedWindowsCheckpoint.ClaimAsync(output, cancellationToken);
        if (!finishPrepared && (Directory.Exists(output) || File.Exists(output)))
            throw new ArgumentException("Hosted output must be a new directory; an existing build is never cleaned.");
        string evidence = Directory.CreateDirectory(Path.Combine(output, "evidence")).FullName;
        string packages = Path.Combine(output, "packages");
        var steps = checkpoint?.Steps.ToList() ?? new List<ValidationStep>();
        string status = "failed";
        string? failure = null;
        UpstreamProvenance? upstream = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        DateTimeOffset deadlineAt = checkpoint?.DeadlineAt ?? DateTimeOffset.UtcNow.AddMinutes(330);
        TimeSpan remaining = deadlineAt - DateTimeOffset.UtcNow;
        deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        CancellationToken token = deadline.Token;
        try
        {
            string prefix = finishPrepared ? "resume-" : "";
            string actual = (await Run(prefix + "source-commit", "git", ["rev-parse", "HEAD"], capture: true)).Trim();
            if (actual != request.Commit) throw new InvalidDataException("Checkout differs from the exact requested source.");
            upstream = await UpstreamProvenance.RequireAsync(repository, request.Commit, token);
            await Run(prefix + "pinned-ancestry", "git", ["merge-base", "--is-ancestor", upstream.Commit, "HEAD"]);
            await Run(prefix + "source-clean", "git", ["diff", "--exit-code", "HEAD"]);
            if (!finishPrepared)
            {
                await Run("dotnet", "dotnet", ["--info"]);
                await Run("cmake", "cmake", ["--version"]);
                var drive = new DriveInfo(Path.GetPathRoot(output)!);
                await File.WriteAllTextAsync(Path.Combine(evidence, "storage.json"),
                    JsonSerializer.Serialize(new { drive.TotalSize, drive.AvailableFreeSpace }, Evidence.JsonOptions), token);
                if (drive.AvailableFreeSpace < 30L * 1024 * 1024 * 1024)
                    throw new IOException("Native KiCad build needs at least 30 GiB free. This runner is too small; no existing runner tools were deleted.");
                await Run("managed-restore", "dotnet", ["restore", "automation/KiCad.Automation.slnx", "--locked-mode"]);
            }
            if (mac) await BuildMac();
            else await BuildWindows();
            await Run(request.Phase == "prepare" ? "prepared-source-clean" : "source-still-clean",
                "git", ["diff", "--exit-code", "HEAD"]);
            if (request.Phase == "prepare")
                await HostedWindowsCheckpoint.WriteAsync(request, deadlineAt, steps, token);
            status = request.Phase == "prepare" ? "prepared" : "candidate_built";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            status = error is OperationCanceledException ? "cancelled" : "failed";
            failure = error.Message;
        }
        if (!mac && status is "failed" or "cancelled")
        {
            using var preservation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await WindowsDiagnosticPackage.RetainAsync(output, request.Commit, steps, preservation.Token); }
            catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException)
            { failure += " Diagnostic package retention failed: " + error.Message; }
        }
        var artifacts = Directory.Exists(packages)
            ? Directory.GetFiles(packages).Order(StringComparer.Ordinal).Select(path => new EvidenceFile(
                Path.GetFileName(path), new FileInfo(path).Length, Evidence.Hash(path))).ToArray() : [];
        string diagnostics = Path.Combine(output, "diagnostics");
        var diagnosticArtifacts = Directory.Exists(diagnostics)
            ? Directory.GetFiles(diagnostics).Order(StringComparer.Ordinal).Select(path => new EvidenceFile(
                Path.GetFileName(path), new FileInfo(path).Length, Evidence.Hash(path))).ToArray() : [];
        await File.WriteAllTextAsync(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, SourceCommit = request.Commit, DependencyCommit = request.DependencyCommit, Upstream = upstream,
            request.Phase,
            Platform = mac ? "macos" : "windows", request.Architecture,
            OS = RuntimeInformation.OSDescription, Framework = RuntimeInformation.FrameworkDescription,
            RunId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            RunAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            WorkflowRef = Environment.GetEnvironmentVariable("GITHUB_WORKFLOW_REF"),
            CompletedAt = DateTimeOffset.UtcNow, Status = status, Failure = failure, Steps = steps,
            Artifacts = artifacts, DiagnosticArtifacts = diagnosticArtifacts,
            QualifyingDelivery = false, CrossPlatformReady = false,
            Limitations = new[] { "Not native Codex Desktop journey evidence", "Mac/Windows automatic updating not qualified",
                "Mac ad-hoc signature only; not Apple notarization", "Windows preview is not Authenticode signed" }
        }, Evidence.JsonOptions), CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(new { status, failure, receipt = Path.Combine(output, "receipt.json") }));
        return status is "candidate_built" or "prepared";

        async Task BuildMac()
        {
            await Run("platform", "sw_vers", []);
            await Run("compiler", "xcrun", ["clang", "--version"]);
            // Reviewed subset of the official builder's brew_deps.sh, plus native
            // source-document tools. No clean-slate bootstrap, SSH changes or new runtime.
            await Run("brew-dependencies", "brew", ["install", "glew", "bison", "opencascade", "glm", "boost",
                "harfbuzz", "cairo", "doxygen", "gettext", "wget", "libgit2", "libtool", "autoconf", "automake",
                "cmake", "openssl", "unixodbc", "ninja", "protobuf", "nng", "zstd", "libomp", "jpeg", "fftw", "poppler"]);
            await Run("brew-inventory", "brew", ["list", "--versions"]);
            string builder = Path.Combine(output, "mac-builder");
            await Checkout("mac-builder", "https://gitlab.com/kicad/packaging/kicad-mac-builder.git", request.DependencyCommit, builder);
            // Only dependency targets run. The default origin/master KiCad build
            // and the multi-gigabyte library/docs packaging targets never run.
            await Run("official-dependencies", "python3", ["build.py", "--arch", MacArchitecture.CmakeName(request.Architecture),
                "--kicad-source-dir", repository, "--build-dir", Path.Combine(output, "mac-dependencies"),
                "--build-type", "Debug", "--no-retry-failed-build", "--target", "setup-kicad-dependencies"], cwd: builder);
            string dependencySource = Path.Combine(output, "mac-dependencies");
            foreach (string dependency in new[] { "wxwidgets", "ngspice" })
                await Run(dependency + "-source", "git", ["-C", Path.Combine(dependencySource, dependency, "src", dependency), "rev-parse", "HEAD"]);
            string macOutput = Path.Combine(output, "mac");
            ValidationResult result = await MacValidation.RunAsync(new(repository, request.Commit, builder,
                Path.Combine(builder, "toolchain", "kicad-mac-builder.cmake"), macOutput,
                "^(qa_document_change_journal|qa_symbol_graphic_identity|qa_schematic_symbol_library_identity|qa_schematic_formatting)$", request.Architecture), token);
            if (result.Status != "checks_passed")
            {
                // Keep a failed installed tree separate from distributable
                // packages so relocation/audit repairs do not lose the inputs.
                if (!token.IsCancellationRequested && Directory.Exists(Path.Combine(macOutput, "install"))
                    && Directory.Exists(Path.Combine(macOutput, "managed")))
                {
                    string diagnosticRoot = Directory.CreateDirectory(Path.Combine(output, "diagnostics")).FullName;
                    await Run("retain-unqualified-install", "tar", ["-czf",
                        Path.Combine(diagnosticRoot, Name("unqualified-macos-" + request.Architecture, ".tar.gz")),
                        "-C", macOutput, "install", "managed"]);
                }
                throw new InvalidDataException("Native Mac validation failed; see mac/evidence and mac/result.json. Any diagnostic install archive is unqualified and must not be published.");
            }
            Directory.CreateDirectory(packages);
            await Run("package-native", "tar", ["-czf", Path.Combine(packages, Name("macos-" + request.Architecture, ".tar.gz")),
                "-C", macOutput, "install", "managed", "kicad-mcp"]);
            await PackageSource();
        }

        async Task BuildWindows()
        {
            string build = Path.Combine(output, "build");
            string install = Path.Combine(output, "install");
            string bin = Path.Combine(install, "bin");
            string managedPublish = Path.Combine(output, "managed");
            string managed = bin;
            if (!finishPrepared)
            {
                await Run("compiler", "cl.exe", ["/?"]);
                string vcpkg = Path.Combine(output, "vcpkg");
                await Checkout("vcpkg", "https://github.com/microsoft/vcpkg.git", request.DependencyCommit, vcpkg);
                await Run("vcpkg-bootstrap", "cmd.exe", ["/d", "/c", Path.Combine(vcpkg, "bootstrap-vcpkg.bat"), "-disableMetrics"]);
                await Run("vcpkg-version", Path.Combine(vcpkg, "vcpkg.exe"), ["version"]);
                // One application directory gives KiCad and MCP the same native
                // dependencies and CRT, without depending on the runner's PATH.
                await Run("managed-publish", "dotnet", ["publish", "automation/src/KiCad.Automation.Mcp", "--configuration", "Release",
                    "--runtime", "win-x64", "--self-contained", "true", "-p:RestoreLockedMode=true", "--output", managedPublish]);
                await Run("native-configure", "cmake", ["-S", repository, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release",
                    "-DCMAKE_TOOLCHAIN_FILE=" + Path.Combine(vcpkg, "scripts", "buildsystems", "vcpkg.cmake"),
                    "-DVCPKG_TARGET_TRIPLET=x64-windows", "-DVCPKG_OVERLAY_TRIPLETS=" + Path.Combine(repository, "tools", "custom_vcpkg_triplets"),
                    "-DVCPKG_BUILD_TYPE=release", "-DKICAD_BUILD_QA_TESTS=ON", "-DKICAD_SCRIPTING_MODULES=OFF",
                    "-DKICAD_WIN32_INSTALL_PDBS=OFF", "-DCMAKE_INSTALL_PREFIX=" + install]);
                File.Copy(Path.Combine(build, "CMakeCache.txt"), Path.Combine(evidence, "CMakeCache.txt"));
            }
            if (request.Phase == "prepare") return;
            // Collect independent compiler/linker failures in this one candidate
            // instead of paying for a fresh full build after each first error.
            await Run("native-build", "cmake", ["--build", build, "--", "-k", "0"]);
            // The focused QA binaries resolve api.v1.schema.json through KiCad's
            // installed stock-data path on Windows. Install before ctest so the
            // test process sees the same data root as the packaged application.
            await Run("native-install", "cmake", ["--install", build]);
            await Run("native-tests", "ctest", ["--test-dir", build, "--no-tests=error", "--output-on-failure",
                "-R", "^(qa_document_change_journal|qa_symbol_graphic_identity|qa_schematic_symbol_library_identity|qa_schematic_formatting)$", "--output-junit", Path.Combine(evidence, "native-tests.xml")]);
            string dependencies = Path.Combine(build, "vcpkg_installed", "x64-windows");
            Directory.CreateDirectory(bin);
            CopyDlls(Path.Combine(dependencies, "bin"), bin);
            string redist = Environment.GetEnvironmentVariable("VCToolsRedistDir")
                ?? throw new InvalidDataException("The runner did not expose its MSVC redistributable directory.");
            string[] crt = Directory.GetDirectories(Path.Combine(redist, "x64"), "Microsoft.VC*.CRT");
            if (crt.Length != 1) throw new InvalidDataException("Select exactly one matching MSVC x64 CRT.");
            CopyDlls(crt[0], bin);
            CopyTree(managedPublish, managed);
            string launcherBuild = Path.Combine(output, "launchers");
            await Run("launcher-configure", "cmake", ["-S", Path.Combine(repository, "automation/native/windows-launcher"),
                "-B", launcherBuild, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DCMAKE_INSTALL_PREFIX=" + install]);
            await Run("launcher-build", "cmake", ["--build", launcherBuild]);
            await Run("launcher-install", "cmake", ["--install", launcherBuild]);
            // Include the dependency licenses and native KiCad license with the preview.
            CopyTree(Path.Combine(dependencies, "share"), Path.Combine(install, "dependency-notices"));
            foreach (string license in Directory.GetFiles(repository, "LICENSE*"))
                File.Copy(license, Path.Combine(install, Path.GetFileName(license)));
            string nng = Path.Combine(bin, "nng.dll");
            foreach (string file in new[] { Path.Combine(bin, "kicad.exe"), Path.Combine(bin, "kicad-cli.exe"),
                Path.Combine(managed, "kicad-mcp.exe"), Path.Combine(managed, "coreclr.dll"), nng }) RequireWindowsX64(file);
            string nativeCommit = (await Run("installed-native-commit", Path.Combine(bin, "kicad-cli.exe"),
                ["version", "--format", "commit"], capture: true)).Trim();
            if (nativeCommit != request.Commit) throw new InvalidDataException("Installed Windows KiCad has the wrong source identity.");
            string probe = await Run("managed-runtime", Path.Combine(managed, "kicad-mcp.exe"), ["--runtime-info"], capture: true);
            using (JsonDocument document = JsonDocument.Parse(probe))
            {
                if (document.RootElement.GetProperty("status").GetString() != "runtime_available"
                    || document.RootElement.GetProperty("processArchitecture").GetString() != "X64"
                    || string.IsNullOrWhiteSpace(document.RootElement.GetProperty("nngVersion").GetString()))
                    throw new InvalidDataException("Packaged Windows NNG transport could not open its native sockets.");
            }
            await Run("managed-contracts", "dotnet", ["test", "automation/KiCad.Automation.slnx", "--configuration", "Release",
                "--filter", "FullyQualifiedName~HostedDeliveryTests|FullyQualifiedName~RuntimeInfoTests|FullyQualifiedName~NngTransportTests|FullyQualifiedName~NativeIpcEndpointTests",
                "--logger", "trx", "--results-directory", Path.Combine(evidence, "managed-tests")]);
            await Run("installed-editor-journey", "dotnet", ["test", "automation/KiCad.Automation.slnx", "--configuration", "Release",
                "--filter", "TestCategory=NativeWindowsPackage", "--logger", "trx",
                "--results-directory", Path.Combine(evidence, "installed-editor-tests")]);
            Directory.CreateDirectory(packages);
            ZipFile.CreateFromDirectory(install, Path.Combine(packages, Name("windows-x64", ".zip")), CompressionLevel.Fastest, false);
            await PackageSource();
        }

        async Task Checkout(string name, string url, string commit, string directory)
        {
            await Run(name + "-init", "git", ["init", directory]);
            await Run(name + "-remote", "git", ["-C", directory, "remote", "add", "origin", url]);
            await Run(name + "-fetch", "git", ["-C", directory, "fetch", "--depth", "1", "origin", commit]);
            await Run(name + "-checkout", "git", ["-C", directory, "checkout", "--detach", "FETCH_HEAD"]);
            if ((await Run(name + "-identity", "git", ["-C", directory, "rev-parse", "HEAD"], capture: true)).Trim() != commit)
                throw new InvalidDataException(name + " dependency checkout has the wrong identity.");
        }

        string Name(string target, string extension) => "kicad-codex-" + request.Commit + "-" + target + extension;
        Task<string> PackageSource() => Run("package-source", "git", ["archive", "--format=tar.gz",
            "--prefix=kicad-source/", "--output=" + Path.Combine(packages, Name("source", ".tar.gz")), request.Commit]);

        async Task<string> Run(string name, string executable, string[] arguments, bool capture = false, string? cwd = null)
        {
            token.ThrowIfCancellationRequested();
            string prefix = $"{steps.Count:D2}-{name}";
            string stdout = Path.Combine(evidence, prefix + ".stdout.log");
            string stderr = Path.Combine(evidence, prefix + ".stderr.log");
            Console.WriteLine("Starting " + name);
            var start = new ProcessStartInfo(executable) { WorkingDirectory = cwd ?? repository, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            string windowsNng = Path.Combine(output, "install", "bin", "nng.dll");
            if (!mac) ConfigureWindowsTransportCheck(start.Environment, name, windowsNng);
            if (!mac && name == "installed-editor-journey")
            {
                start.Environment["KICAD_TEST_WINDOWS_INSTALL"] = Path.Combine(output, "install");
                start.Environment["KICAD_HOSTED_FIXTURE_EVIDENCE"] = Path.Combine(evidence, "installed-editor-tests");
            }
            // The official ngspice source uses git://; use its same HTTPS origin
            // on hosted runners that do not permit the unauthenticated git port.
            start.Environment["GIT_CONFIG_COUNT"] = "1";
            start.Environment["GIT_CONFIG_KEY_0"] = "url.https://git.code.sf.net/.insteadOf";
            start.Environment["GIT_CONFIG_VALUE_0"] = "git://git.code.sf.net/";
            var began = DateTimeOffset.UtcNow;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
            bool editorJourney = !mac && name == "installed-editor-journey";
            if (editorJourney) operation.CancelAfter(TimeSpan.FromMinutes(12));
            using var logLifetime = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
            using var process = Process.Start(start) ?? throw new IOException("Cannot start " + name);
            await using var outFile = File.Create(stdout);
            await using var errFile = File.Create(stderr);
            Task<bool> outCopy = Copy(process.StandardOutput.BaseStream, outFile);
            Task<bool> errCopy = Copy(process.StandardError.BaseStream, errFile);
            try { await process.WaitForExitAsync(operation.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(killed.Token);
                throw;
            }
            finally
            {
                if (editorJourney) logLifetime.CancelAfter(TimeSpan.FromSeconds(3));
                if (editorJourney) await Task.WhenAll(outCopy, errCopy).WaitAsync(TimeSpan.FromSeconds(8));
                else await Task.WhenAll(outCopy, errCopy);
                await outFile.FlushAsync(CancellationToken.None);
                await errFile.FlushAsync(CancellationToken.None);
                steps.Add(new(name, process.ExitCode, began, DateTimeOffset.UtcNow,
                    Path.GetFileName(stdout), Path.GetFileName(stderr)));
                if (editorJourney)
                    await File.WriteAllTextAsync(Path.Combine(evidence, prefix + ".capture.json"), JsonSerializer.Serialize(new
                    { schemaVersion = 1, stdoutReachedEof = outCopy.Result, stderrReachedEof = errCopy.Result }), CancellationToken.None);
            }
            if (!outCopy.Result || !errCopy.Result) throw new IOException(name + " diagnostic streams did not close within their bounded drain.");
            if (process.ExitCode != 0) throw new IOException($"{name} exited {process.ExitCode}; see {prefix} logs.");
            if (!capture) return "";
            if (outFile.Length > 65536) throw new InvalidDataException(name + " metadata exceeds 64 KiB.");
            await outFile.DisposeAsync();
            return await File.ReadAllTextAsync(stdout, token);

            async Task<bool> Copy(Stream source, Stream destination)
            {
                try { await source.CopyToAsync(destination, logLifetime.Token); return true; }
                catch (OperationCanceledException) when (logLifetime.IsCancellationRequested) { return false; }
            }
        }
    }

    private static void CopyDlls(string source, string destination)
    {
        string[] files = Directory.GetFiles(source, "*.dll");
        if (files.Length == 0) throw new InvalidDataException("No runtime DLLs found at " + source);
        foreach (string file in files)
        {
            string target = Path.Combine(destination, Path.GetFileName(file));
            if (File.Exists(target))
            {
                if (Evidence.Hash(file) != Evidence.Hash(target)) throw new InvalidDataException("Conflicting runtime DLL: " + Path.GetFileName(file));
            }
            else File.Copy(file, target);
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string directory in Directory.GetDirectories(source)) CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
