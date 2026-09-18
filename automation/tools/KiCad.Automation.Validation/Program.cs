using System.Text.Json;
using KiCad.Automation.Validation;

try
{
    if (args.Length == 0 || args[0] is "--help" or "-h")
    {
        Console.WriteLine("kicad-validate upstream-detect --repository CHECKOUT --output NEW_JSON_FILE");
        Console.WriteLine("kicad-validate verify-build-isolation --repository CHECKOUT --output NEW_EVIDENCE_DIRECTORY");
        Console.WriteLine("kicad-validate upstream-prepare --repository CHECKOUT --tag STABLE_VERSION --commit UPSTREAM_SHA --output NEW_DIRECTORY");
        Console.WriteLine("kicad-validate upstream-verify --repository CHECKOUT --commit SOURCE_SHA --output NEW_JSON_FILE");
        Console.WriteLine("kicad-validate mac --repository LOCAL_CHECKOUT --commit FULL_SHA --architecture arm64|x64 --builder MAC_BUILDER_CHECKOUT --toolchain EXISTING_CMAKE_TOOLCHAIN --output NEW_DIRECTORY [--native-tests CTEST_REGEX]");
        Console.WriteLine("kicad-validate hosted --repository CHECKOUT --commit FULL_SHA --architecture arm64|x64 --output DIRECTORY --dependency-commit FULL_SHA [--phase all|prepare|build] (GitHub-hosted runners only; split phases are Windows same-job only)");
        Console.WriteLine("kicad-validate stage-hosted --candidate DOWNLOADED_CANDIDATE_ROOT --commit FULL_SHA --platform osx-arm64|osx-x64|win-x64 --run-id GITHUB_RUN_ID --version PREVIEW_VERSION --previous PUBLIC_ROOT --output NEW_PUBLIC_ROOT");
        Console.WriteLine("kicad-validate stage-requalified-windows --candidate ORIGINAL_FAILED_BUILD --qualification NATIVE_RERUN_ROOT --repository EXACT_NATIVE_SOURCE_WORKTREE --commit FULL_SHA --run-id QUALIFICATION_RUN_ID --version PREVIEW_VERSION --previous PUBLIC_ROOT --output NEW_PUBLIC_ROOT");
        Console.WriteLine("kicad-validate stage-signed-linux --candidate LINUX_PACKAGE_DIRECTORY --commit FULL_SHA --previous PUBLIC_ROOT --output NEW_PUBLIC_ROOT --feed SIGNED_PREVIEW_JSON --publisher TRUSTED_PUBLIC_SPKI");
        Console.WriteLine("kicad-validate stage-platform-feeds --previous PUBLIC_ROOT --output NEW_PUBLIC_ROOT --publisher TRUSTED_PUBLIC_SPKI --sources FEED_DECLARATION_JSON");
        Console.WriteLine("kicad-validate prune-staging-archives --declaration EXACT_ROOTS_AND_CATALOGUE_HASHES_JSON --output NEW_RECEIPT_JSON");
        Console.WriteLine("kicad-validate verify --result RESULT_JSON --archive EVIDENCE_TAR_GZ --commit FULL_SHA [--architecture arm64|x64]");
        Console.WriteLine("kicad-validate stage-linux --build NATIVE_BUILD --managed SELF_CONTAINED_PUBLISH --nng NNG_SHARED_LIBRARY --output EXISTING_STAGING_DIRECTORY");
        Console.WriteLine("kicad-validate package-linux --staging STAGING_RECEIPT --repository COMMITTED_SOURCE --commit FULL_SHA --version VERSION --output NEW_DIRECTORY");
        Console.WriteLine("kicad-validate package-debian --catalogue FROZEN_DOWNLOAD_CATALOGUE --output NEW_DIRECTORY");
        Console.WriteLine("kicad-validate publisher-create --private NEW_PRIVATE_KEY --public NEW_PUBLIC_SPKI");
        Console.WriteLine("kicad-validate publisher-export --private PRIVATE_KEY --public NEW_PUBLIC_SPKI");
        Console.WriteLine("kicad-validate release-sign --release RELEASE_JSON --artifacts ARTIFACT_DIRECTORY --private PRIVATE_KEY --output NEW_ENVELOPE [--previous PREVIOUS_ENVELOPE]");
        return 0;
    }
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int index = 1; index < args.Length; index += 2)
    {
        if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal)
            || !options.TryAdd(args[index][2..], args[index + 1]))
            throw new ArgumentException("Options require one value each and cannot be repeated.");
    }
    string Required(string name) => options.TryGetValue(name, out string? value) ? value
        : throw new ArgumentException($"Missing --{name}.");
    string[] allowed = args[0] is "upstream-detect" or "verify-build-isolation" ? ["repository", "output"]
        : args[0] == "upstream-prepare" ? ["repository", "tag", "commit", "output"]
        : args[0] == "upstream-verify" ? ["repository", "commit", "output"]
        : args[0] == "hosted" ? ["repository", "commit", "architecture", "output", "dependency-commit", "phase"]
        : args[0] == "stage-platform-feeds" ? ["previous", "output", "publisher", "sources"]
        : args[0] == "prune-staging-archives" ? ["declaration", "output"]
        : args[0] == "stage-signed-linux" ? ["candidate", "commit", "previous", "output", "feed", "publisher"]
        : args[0] == "stage-hosted" ? ["candidate", "commit", "platform", "run-id", "version", "previous", "output"]
        : args[0] == "stage-requalified-windows" ? ["candidate", "qualification", "repository", "commit", "run-id", "version", "previous", "output"]
        : args[0] == "mac"
        ? ["repository", "commit", "architecture", "builder", "toolchain", "output", "native-tests"]
        : args[0] == "stage-linux" ? ["build", "managed", "nng", "output"]
        : args[0] == "package-linux" ? ["staging", "repository", "commit", "version", "output"]
        : args[0] == "package-debian" ? ["catalogue", "output"]
        : args[0] is "publisher-create" or "publisher-export" ? ["private", "public"]
        : args[0] == "release-sign" ? ["release", "artifacts", "private", "output", "previous"]
        : ["result", "archive", "commit", "architecture"];
    foreach (string name in options.Keys)
        if (!allowed.Contains(name, StringComparer.Ordinal)) throw new ArgumentException($"Unknown option --{name}.");
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
    if (args[0] == "verify-build-isolation")
    {
        await HostedValidatorIsolation.VerifyAsync(Required("repository"), Required("output"), cancel.Token);
        Console.WriteLine("{\"status\":\"passed\",\"isolatedRebuild\":true}"); return 0;
    }
    if (args[0].StartsWith("upstream-", StringComparison.Ordinal))
    {
        object result = args[0] switch
        {
            "upstream-detect" => await UpstreamMaintenance.DetectAsync(Required("repository"), cancel.Token),
            "upstream-prepare" => await UpstreamMaintenance.PrepareAsync(Required("repository"), Required("tag"), Required("commit"), Required("output"), cancel.Token),
            "upstream-verify" => new { SourceCommit = Required("commit"), Upstream = await UpstreamProvenance.RequireAsync(Required("repository"), Required("commit"), cancel.Token) },
            _ => throw new ArgumentException("Unknown upstream command.")
        };
        string json = JsonSerializer.Serialize(result, UpstreamProvenance.Json);
        if (args[0] != "upstream-prepare")
        {
            await using var file = new FileStream(Required("output"), FileMode.CreateNew, FileAccess.Write);
            await using var writer = new StreamWriter(file);
            await writer.WriteLineAsync(json);
        }
        Console.WriteLine(json);
        return 0;
    }
    if (args[0] == "stage-platform-feeds")
    {
        var sources = await SignedPlatformFeedStaging.ReadSourcesAsync(Required("sources"), cancel.Token);
        var result = await SignedPlatformFeedStaging.RunAsync(new(Required("previous"), Required("output"), Required("publisher"), sources.Feeds), cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, Evidence.JsonOptions));
        return 0;
    }
    if (args[0] == "prune-staging-archives")
    {
        var request = await IntermediateArchivePruning.ReadAsync(Required("declaration"), cancel.Token);
        var receipt = await IntermediateArchivePruning.RunAsync(request, Required("output"), cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { receipt.Status, removedFiles = receipt.Archives.Count(a => a.Removed),
            receipt.RemovedLogicalBytes, retainedCopies = receipt.RetainedDirectory }, Evidence.JsonOptions));
        return 0;
    }
    if (args[0] == "stage-hosted")
    {
        var result = await HostedPreviewStaging.RunAsync(new(Required("candidate"), Required("commit"), Required("platform"),
            Required("run-id"), Required("version"), Required("previous"), Required("output")), cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, Evidence.JsonOptions));
        return 0;
    }
    if (args[0] == "stage-requalified-windows")
    {
        var result = await WindowsRequalifiedPreview.RunAsync(new(Required("candidate"), Required("qualification"), Required("repository"),
            Required("commit"), Required("run-id"), Required("version"), Required("previous"), Required("output")), cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, Evidence.JsonOptions)); return 0;
    }
    if (args[0] == "stage-signed-linux")
    {
        var result = await SignedLinuxPreviewStaging.RunAsync(new(Required("candidate"), Required("commit"),
            Required("previous"), Required("output"), Required("feed"), Required("publisher")), cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, Evidence.JsonOptions));
        return 0;
    }
    if (args[0] == "hosted")
        return await HostedDelivery.RunAsync(new(Required("repository"), Required("commit"), Required("architecture"),
            Required("output"), Required("dependency-commit"), options.GetValueOrDefault("phase", "all")), cancel.Token) ? 0 : 1;
    if (args[0] is "publisher-create" or "publisher-export")
    {
        var receipt = args[0] == "publisher-create"
            ? await UpdatePublishing.CreatePublisherAsync(Required("private"), Required("public"), cancel.Token)
            : await UpdatePublishing.ExportPublisherAsync(Required("private"), Required("public"), cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(receipt, Evidence.JsonOptions));
        return 0;
    }
    if (args[0] == "release-sign")
    {
        var release = await UpdatePublishing.ReadReleaseAsync(Required("release"), cancel.Token);
        var receipt = await UpdatePublishing.SignAsync(release, Required("artifacts"), Required("private"),
            Required("output"), options.GetValueOrDefault("previous"), cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(receipt, Evidence.JsonOptions));
        return 0;
    }
    if (args[0] == "package-debian")
    {
        var result = await DebianPackage.CreateAsync(Required("catalogue"), Required("output"), cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, Evidence.JsonOptions));
        return 0;
    }
    if (args[0] == "package-linux")
    {
        var manifest = await LinuxPackage.CreateAsync(new(Required("staging"), Required("repository"),
            Required("commit"), Required("version"), Required("output")), cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(manifest, Evidence.JsonOptions));
        return 0;
    }
    if (args[0] == "stage-linux")
    {
        var result = await LinuxStaging.RunAsync(new(Required("build"), Required("managed"),
            Required("nng"), Required("output")), cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { result.Status, result.Directory, result.Failure,
            FileCount = result.Files.Count, result.QualifyingDelivery }, Evidence.JsonOptions));
        return result.Status == "staged" ? 0 : 1;
    }
    if (args[0] == "mac")
    {
        var request = new MacRequest(Required("repository"), Required("commit"), Required("builder"),
            Required("toolchain"), Required("output"), options.GetValueOrDefault("native-tests", "."), Required("architecture"));
        ValidationResult result = await MacValidation.RunAsync(request, cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, Evidence.JsonOptions));
        return result.Status == "checks_passed" ? 0 : 1;
    }
    if (args[0] == "verify")
    {
        await Evidence.VerifyAsync(Required("result"), Required("archive"), Required("commit"), cancel.Token,
            options.GetValueOrDefault("architecture"));
        Console.WriteLine("Receipt/archive integrity verified for the requested commit. No Mac execution was performed by this command; this is not cross-platform readiness.");
        return 0;
    }
    throw new ArgumentException("Unknown command. Use --help for validation, packaging and publisher commands.");
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
