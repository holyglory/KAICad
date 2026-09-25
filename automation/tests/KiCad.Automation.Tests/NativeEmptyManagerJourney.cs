using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Native;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    [TestMethod, TestCategory("NativeSourceSession")]
    [TestCategory("NativeEmptyManager")]
    [TestCategory("NativeUpdateOrigin")]
    public async Task EmptyUpdateManagerHasExactIdentityAndCannotInventAnEngineeringProject()
    {
        string root = FindRoot();
        string executable = Path.Combine(root, "automation/artifacts/native/kicad/kicad");
        string evidence = NativeEvidenceDirectory.Begin(Path.Combine(root, "automation/artifacts"));
        string temporary = Directory.CreateTempSubdirectory("kicad-empty-manager-").FullName;
        var processes = new List<Process>();
        var captures = new List<Task>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var displayStart = new ProcessStartInfo("Xvfb") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-displayfd", "1", "-screen", "0", "1280x900x24", "-nolisten", "tcp" }) displayStart.ArgumentList.Add(arg);
            var display = Process.Start(displayStart)!;
            processes.Add(display);
            captures.Add(Capture(display.StandardError, Path.Combine(evidence, "empty-xvfb.log")));
            string displayName = ":" + await display.StandardOutput.ReadLineAsync(deadline.Token);
            string instance = Guid.NewGuid().ToString("D");
            string socket = Path.Combine(temporary, "manager.sock");
            string project = Path.Combine(temporary, "untouched.kicad_pro");
            byte[] original = System.Text.Encoding.UTF8.GetBytes("{\"meta\":{\"version\":3}}");
            await File.WriteAllBytesAsync(project, original, deadline.Token);
            string[][] invalid =
            [
                ["--update-manager", instance, "--api-socket", socket],
                ["--new", "--update-manager", instance, "--automation", instance, "--api-socket", socket],
                ["--new", "--update-manager", instance, "--api-socket", socket, project],
                ["--new", "--update-manager", instance, "--api-socket", "relative.sock"],
                ["--new", "--automation", instance, "--api-socket", socket],
                ["--new", "--automation", instance, "--api-socket", socket, Path.Combine(temporary, "absent.kicad_pro")]
            ];
            for (int index = 0; index < invalid.Length; index++)
            {
                var rejected = Launch(invalid[index], "rejected-" + index);
                await rejected.WaitForExitAsync(deadline.Token);
                Assert.IsTrue(rejected.ExitCode is 1 or 255,
                    "Startup rejection must be a normal failure, not a native signal/crash: " + rejected.ExitCode);
                await Task.WhenAll(captures.TakeLast(2));
                string diagnostic = await File.ReadAllTextAsync(Path.Combine(evidence, "rejected-" + index + ".stderr.log"), deadline.Token);
                Assert.IsTrue(diagnostic.Contains("requires", StringComparison.Ordinal)
                    || diagnostic.Contains("require", StringComparison.Ordinal), "Keep an actionable startup error.");
                Assert.IsFalse(File.Exists(socket));
            }

            var native = Launch(["--new", "--update-manager", instance, "--api-socket", socket,
                "--automation-log", Path.Combine(evidence, "empty-native.log"), "--software-rendering"], "empty");
            var client = new NativeClient(new NngTransport(), "ipc://" + socket);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                Assert.IsFalse(native.HasExited);
                try
                {
                    var session = await client.HandshakeAsync(deadline.Token);
                    Assert.AreEqual(instance, session.InstanceId);
                    Assert.AreEqual("", session.ProjectPath);
                    break;
                }
                catch (NngException) { }
                catch (NativeApiException error) when (error.Status is 4 or 7) { }
                await Task.Delay(100, deadline.Token);
            }
            string forbidden = Path.Combine(temporary, "untouched.kicad_sch");
            var origin = new UpdateOrigin(client.Endpoint, client.Epoch);
            await UpdateOrigin.VerifyAsync(origin, Guid.Parse(instance), "", deadline.Token);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpdateOrigin.VerifyAsync(origin,
                Guid.NewGuid(), "", deadline.Token));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpdateOrigin.VerifyAsync(origin,
                Guid.Parse(instance), project, deadline.Token));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpdateOrigin.VerifyAsync(origin with { Epoch = "stale-origin" },
                Guid.Parse(instance), "", deadline.Token));
            Assert.IsFalse(native.HasExited, "Origin rejection must leave the original native window alive.");
            var managerSession = await client.HandshakeAsync(deadline.Token);
            Assert.AreEqual(origin.Epoch, managerSession.Epoch);
            // The update manager provides none of the document callbacks, so its handshake lists none
            // of those requests and it does not dispatch them at all.
            string[] managerRequests = managerSession.HandledRequests.ToArray();
            CollectionAssert.Contains(managerRequests, KiCad.Automation.Protocol.GetAutomationSession.Descriptor.FullName,
                "This build's handshake lists the requests it handles.");
            foreach (string type in new[] { Kiapi.Common.Commands.OpenDocument.Descriptor.FullName,
                         Kiapi.Common.Commands.CloseDocument.Descriptor.FullName, Kiapi.Common.Commands.CloseAllDocuments.Descriptor.FullName })
                CollectionAssert.DoesNotContain(managerRequests, type, "The update manager has no callback for " + type + ".");
            var rejectedCreate = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.CreateRootSchematicAsync(forbidden, deadline.Token));
            Assert.AreEqual(5, rejectedCreate.Status, "OpenDocument must not be dispatched by the update manager (AS_UNHANDLED).");
            Assert.IsFalse(File.Exists(forbidden));
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(project, deadline.Token));
            await NativeKeyboard.CaptureAsync(displayName, Path.Combine(evidence, "empty-manager.png"), deadline.Token);
            NativeKeyboard.SchematicShortcut(displayName, native.Id, "q", "KiCad", true, false);
            await native.WaitForExitAsync(deadline.Token);
            Assert.AreEqual(0, native.ExitCode);
            await File.WriteAllTextAsync(Path.Combine(evidence, "empty-manager-result.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, nativeIdentityVerified = true, projectPath = "",
                implicitDocumentCreationRejected = true, explicitProjectAutomationPreserved = true,
                invalidStartupCases = invalid.Length, normalFailureExitsVerified = true, nativeMacVerified = false,
                originalSessionVerified = true, wrongOriginTargetsRejected = true, originRejectionPreservedProcess = true,
                captionRestartVerified = false, mcpReconnectionVerified = false
            }), deadline.Token);

            Process Launch(string[] arguments, string name)
            {
                var start = new ProcessStartInfo(executable)
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = temporary };
                start.Environment["DISPLAY"] = displayName;
                start.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
                start.Environment["KICAD_CONFIG_HOME"] = Path.Combine(temporary, "profile-" + name);
                start.Environment["KICAD_CACHE_HOME"] = Path.Combine(temporary, "cache-" + name);
                start.Environment.Remove("KICAD_AUTOMATION_UPDATE_HELPER");
                start.Environment.Remove("KICAD_AUTOMATION_UPDATE_CONFIG");
                foreach (string argument in arguments) start.ArgumentList.Add(argument);
                Process process = Process.Start(start)!;
                processes.Add(process);
                captures.Add(Capture(process.StandardOutput, Path.Combine(evidence, name + ".stdout.log")));
                captures.Add(Capture(process.StandardError, Path.Combine(evidence, name + ".stderr.log")));
                return process;
            }
        }
        finally
        {
            foreach (var process in processes.AsEnumerable().Reverse())
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                process.Dispose();
            }
            await Task.WhenAll(captures);
            Directory.Delete(temporary, recursive: true);
        }
    }
}
