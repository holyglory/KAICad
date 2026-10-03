using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    // Item drc-complete-inputs (ledger p23deb822a36256a6). An ordinary check captures every input its tests read at the
    // revision it was asked for, so a completed check reports a complete snapshot and fresh results. Each kind of input
    // change makes it stale, with no findings and with the reason KiCad observed; a new check is complete and fresh
    // again. The kinds exercised here: the custom rules file edited in place with the same size and modification time,
    // the project's text variables (with a schematic-parity check, whose schematic state includes them), the drawing
    // sheet edited in place with the same size and modification time, a committed board edit, a reloaded board whose
    // notifications a check can no longer rely on, and teardrops, which KiCad keeps without an identity in the board file:
    // a check's finding must name the open board's teardrop, also after a reload gives it a new identity. Also the
    // project's net classes, and board text that reads the clock: the date and the day an @{...} expression reads with
    // today() are captured, and the time of day and expressions that read the time, chance or the version-control
    // repository leave the snapshot incomplete with their reason. A candidate check follows the same rule. A check never
    // claims a complete snapshot before it has finished (RunPcbDrcJob). Every fresh and stale state here is read as an
    // agent reads it, through the STDIO MCP server's kicad_pcb_drc_job, and KiCad's own read must agree (PcbDrcAgent). The
    // router settings, the board's current variant, the activation checkpoint, an exclusion comment and a footprint
    // library edited in place are exercised in the rendered editor (VerifyPcbDrcJobActivation, VerifyPcbDrcJobRouterChange,
    // VerifyPcbDrcJobVariantChange, VerifyPcbDrcJobLibraryEdit, VerifyPcbDrcExclusions).
    private static async Task VerifyPcbDrcJobs(NativeClient client, DocumentSpecifier board,
        string evidence, CancellationToken token)
    {
        await using var agent = await PcbDrcAgent.StartAsync(client, Path.Combine(evidence, "pcb-drc-job-mcp.stderr.log"), token);
        Task<(StartPcbDrcJob Request, PcbDrcJobState State)> Run(DocumentLifecycleState at, Action<StartPcbDrcJob>? configure = null) =>
            RunPcbDrcJob(client, board, at, token, configure, agent);
        Task<PcbDrcJobState> Read(PcbDrcJobState job, CancellationToken cancellation) => agent.Read(board, job, cancellation);
        static void AssertStale(PcbDrcJobState job, string code, string because) => AssertPcbDrcJobStale(job, code, because);
        static void AssertCurrent(PcbDrcJobState job, string because) => AssertPcbDrcJobCurrent(job, because);
        Task Evidence(string name, PcbDrcJobState state) => File.WriteAllTextAsync(
            Path.Combine(evidence, name), SchematicJson.Formatter.Format(state), token);
        Task<DocumentLifecycleState> Observe() => ObserveLifecycleState(client, board, token);
        // KiCad handles file notifications and API requests on its UI thread and runs ready file notifications before
        // queued requests. The pause only gives a busy UI thread its turn; the assertions after it prove the result.
        async Task Settle()
        {
            await Observe();
            await Task.Delay(TimeSpan.FromMilliseconds(250), token);
            await Observe();
        }
        Task EditInPlace(string path, string from, string to) => EditPcbDrcInputInPlace(path, from, to, token);

        // 1. A check of the saved board: complete, fresh, and still fresh when read again. The live board is untouched.
        var before = await Observe();
        var (_, state) = await Run(before);
        await Evidence("pcb-drc-job.json", state);
        AssertCurrent(state, "A completed check of an unchanged board must be complete and fresh.");
        Assert.AreEqual(state, await Read(state, token), "Reading an unchanged check again must not change it.");
        Assert.AreEqual(before, await Observe(), "A detached DRC job must not change the live board or its files.");

        // 2. A candidate check follows the same rule. Its candidate never reaches the board.
        var header = new ItemHeader { Document = board };
        var track = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            { Header = header, Items = { new KIID { Value = "33333333-3333-4333-8333-333333333333" } } }, token))
            .Items.Single().Unpack<Track>();
        string candidateId = Guid.NewGuid().ToString("D");
        var candidate = track.Clone();
        candidate.Id = new KIID { Value = candidateId };
        candidate.Start.YNm += 5_000_000; candidate.End.YNm += 5_000_000;
        var (_, dryRun) = await Run(before, request => request.CandidateItems.Add(Any.Pack(candidate)));
        await Evidence("pcb-drc-job-candidate.json", dryRun);
        AssertCurrent(dryRun, "A completed candidate check follows the ordinary rule.");
        Assert.IsTrue(dryRun.CandidateDryRun);
        CollectionAssert.AreEqual(new[] { candidateId }, dryRun.CandidateItemIds.ToArray());
        Assert.AreEqual(before, await Observe(), "A candidate check must not add its candidate to the live board.");

        // 3. The custom rules file: created, then edited in place with the same size and modification time.
        string rules = Path.Combine(board.Project.Path, Path.ChangeExtension(board.BoardFilename, ".kicad_dru"));
        byte[]? savedRules = File.Exists(rules) ? await File.ReadAllBytesAsync(rules, token) : null;
        try
        {
            await File.WriteAllTextAsync(rules,
                "(version 1)\n(rule \"fixture_event\" (constraint clearance (min 0.3mm)))\n", token);
            await Settle();
            var created = await Read(state, token);
            await Evidence("pcb-drc-job-rules-stale.json", created);
            AssertStale(created, "project_inputs_changed", "Creating a custom rules file must make the finished check stale.");
            var (_, ruled) = await Run(await Observe());
            await Evidence("pcb-drc-job-rules.json", ruled);
            AssertCurrent(ruled, "A check with the new rules file must be complete and fresh.");
            await EditInPlace(rules, "(min 0.3mm)", "(min 0.4mm)");
            await Settle();
            var edited = await Read(ruled, token);
            await Evidence("pcb-drc-job-rules-in-place-stale.json", edited);
            AssertStale(edited, "project_inputs_changed",
                "Editing the rules in place, with the same size and modification time, must make the check stale.");
        }
        finally
        {
            if (savedRules is null) File.Delete(rules); // Only the rules file this journey created.
            else await File.WriteAllBytesAsync(rules, savedRules, CancellationToken.None);
        }
        await Settle();
        AssertStale(await Read(state, token), "project_inputs_changed", "Putting the rules back must not revive the first check.");
        var (secondRequest, second) = await Run(await Observe());
        AssertCurrent(second, "A check after the rules were put back must be complete and fresh.");
        Assert.AreEqual(state.Findings.Count, second.Findings.Count,
            "A new check of the same board and restored rules must report the same findings.");

        // 4. The project's text variables, which KiCad keeps in memory and every read compares. A schematic-parity check
        //    reads the schematic state, which includes the project settings.
        var schematic = (await client.InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(
            new() { Type = DocumentType.DoctypeSchematic }, token)).Documents.Single();
        var schematicState = await ObserveLifecycleState(client, schematic, token);
        var (_, parity) = await Run(await Observe(), request =>
        {
            request.TestFootprints = true;
            request.ExpectedSchematicState = schematicState.Clone();
        });
        await Evidence("pcb-drc-job-parity.json", parity);
        AssertCurrent(parity, "A completed schematic-parity check must be complete and fresh.");
        Assert.AreEqual(schematicState, parity.CheckedSchematicState);
        AssertCurrent(await Read(second, token), "The parity check must not change the ordinary check's inputs.");
        var projectDocument = new DocumentSpecifier { Type = DocumentType.DoctypeProject, Project = board.Project.Clone() };
        var variables = await client.InvokeAsync<GetTextVariables, Kiapi.Common.Project.TextVariables>(
            new() { Document = projectDocument }, token);
        bool variablesChanged = false;
        try
        {
            var change = new SetTextVariables { Document = projectDocument, MergeMode = MapMergeMode.MmmMerge, Variables = new() };
            change.Variables.Variables["DRC_JOURNEY_CHECK"] = "changed";
            variablesChanged = true;
            await client.InvokeAsync<SetTextVariables, Empty>(change, token);
            var projectStale = await Read(second, token);
            await Evidence("pcb-drc-job-project-stale.json", projectStale);
            AssertStale(projectStale, "project_inputs_changed", "A changed project text variable must make the check stale.");
            var parityStale = await Read(parity, token);
            await Evidence("pcb-drc-job-parity-stale.json", parityStale);
            AssertStale(parityStale, "schematic_changed",
                "A changed project text variable changes the schematic the parity check compared, so it must be stale.");
        }
        finally
        {
            if (variablesChanged)
                await client.InvokeAsync<SetTextVariables, Empty>(new()
                    { Document = projectDocument, MergeMode = MapMergeMode.MmmReplace, Variables = variables.Clone() }, CancellationToken.None);
        }
        Assert.AreEqual(variables, await client.InvokeAsync<GetTextVariables, Kiapi.Common.Project.TextVariables>(
            new() { Document = projectDocument }, token));
        AssertStale(await Read(second, token), "project_inputs_changed", "Putting the variables back must not revive the check.");

        // 5. The drawing sheet: a sheet file of the project, then edited in place with the same size and modification time.
        var originalPage = await client.InvokeAsync<GetPageSettings, PageSettings>(new() { Document = board }, token);
        string sheet = Path.Combine(board.Project.Path, "drc-journey.kicad_wks");
        Assert.IsFalse(File.Exists(sheet), "Only this journey writes its drawing sheet.");
        await File.WriteAllTextAsync(sheet,
            "(kicad_wks (version 20220228) (generator pl_editor) " +
            "(setup (textsize 1.5 1.5) (linewidth 0.15) (textlinewidth 0.15) " +
            "(left_margin 10) (right_margin 10) (top_margin 10) (bottom_margin 10)) " +
            "(rect (name \"Frame\") (start 0 0 ltcorner) (end 0 0)) " +
            "(tbtext \"DRC JOURNEY SHEET A\" (name \"Label\") (pos 10 10 ltcorner)))", token);
        try
        {
            var custom = originalPage.Clone();
            custom.DrawingSheet = sheet;
            await client.InvokeAsync<SetPageSettings, PageSettings>(new() { Document = board, PageSettings = custom }, token);
            var (_, sheetCheck) = await Run(await Observe());
            await Evidence("pcb-drc-job-drawing.json", sheetCheck);
            AssertCurrent(sheetCheck, "A check with the project's drawing sheet must be complete and fresh.");
            await EditInPlace(sheet, "SHEET A", "SHEET B");
            await Settle();
            var sheetStale = await Read(sheetCheck, token);
            await Evidence("pcb-drc-job-drawing-stale.json", sheetStale);
            AssertStale(sheetStale, "auxiliary_inputs_changed",
                "Editing the drawing sheet in place, with the same size and modification time, must make the check stale.");
        }
        finally
        {
            await client.InvokeAsync<SetPageSettings, PageSettings>(
                new() { Document = board, PageSettings = originalPage }, CancellationToken.None);
            File.Delete(sheet);
        }
        Assert.AreEqual(originalPage, await client.InvokeAsync<GetPageSettings, PageSettings>(new() { Document = board }, token));

        // 6. A committed edit through KiCad's own board transaction. An agent reading the check through the STDIO MCP
        //    server sees it fresh before the edit and stale after it.
        var (_, fourth) = await Run(await Observe());
        AssertCurrent(fourth, "A check after the drawing sheet was put back must be complete and fresh.");
        {
            var current = await Read(fourth, token);
            await Evidence("pcb-drc-job-mcp-fresh.json", current);
            AssertCurrent(current, "An agent reading the unchanged check over MCP must see it complete and fresh.");
            Assert.AreEqual(fourth, current);

            var moved = track.Clone(); moved.End.XNm += 1_000_000;
            var begin = await client.InvokeAsync<BeginCommit, BeginCommitResponse>(new() { Header = header }, token);
            bool committed = false;
            try
            {
                var update = new UpdateItems { Header = header };
                update.Items.Add(Any.Pack(moved));
                var updated = await client.InvokeAsync<UpdateItems, UpdateItemsResponse>(update, token);
                Assert.IsTrue(updated.UpdatedItems.Count == 1
                    && updated.UpdatedItems.All(item => item.Status.Code == ItemStatusCode.IscOk), updated.ToString());
                await client.InvokeAsync<EndCommit, EndCommitResponse>(new()
                {
                    Id = begin.Id, Action = CommitAction.CmaCommit, Header = header,
                    Message = "DRC freshness journey edit"
                }, token);
                committed = true;
            }
            finally
            {
                if (!committed)
                    await client.InvokeAsync<EndCommit, EndCommitResponse>(new()
                        { Id = begin.Id, Action = CommitAction.CmaDrop, Header = header }, CancellationToken.None);
            }
            var edited = await Read(fourth, token);
            await Evidence("pcb-drc-job-edit-stale.json", edited);
            AssertStale(edited, "document_changed", "A committed board edit must make the finished check stale for an agent.");

            // The project's net classes, which KiCad keeps in memory and every read compares: a wider clearance for the
            // Default class makes the check stale for the agent, and a new check is complete and fresh again.
            var (_, classed) = await Run(await Observe());
            await Evidence("pcb-drc-job-netclass.json", classed);
            AssertCurrent(classed, "A check before the net class changes must be complete and fresh.");
            AssertCurrent(await Read(classed, token), "An agent must see the check before the net class change complete and fresh.");
            var classes = await client.InvokeAsync<GetNetClasses, NetClassesResponse>(new(), token);
            var defaultClass = classes.NetClasses.Single(netClass => netClass.Name == "Default");
            Assert.IsNotNull(defaultClass.Board?.Clearance, "The Default net class declares its clearance.");
            bool classesChanged = false;
            try
            {
                var widened = defaultClass.Clone();
                widened.Board.Clearance.ValueNm += 50_000;
                var change = new SetNetClasses { MergeMode = MapMergeMode.MmmMerge };
                change.NetClasses.Add(widened);
                classesChanged = true;
                await client.InvokeAsync<SetNetClasses, Empty>(change, token);
                var classStale = await Read(classed, token);
                await Evidence("pcb-drc-job-netclass-stale.json", classStale);
                AssertStale(classStale, "project_inputs_changed",
                    "An agent reading the check over MCP after the Default net class changed must see it stale.");
                var (_, reclassed) = await Run(await Observe());
                await Evidence("pcb-drc-job-netclass-new.json", reclassed);
                AssertCurrent(reclassed, "A new check with the changed net class must be complete and fresh.");
                AssertCurrent(await Read(reclassed, token), "An agent must see the new check complete and fresh.");
            }
            finally
            {
                if (classesChanged)
                {
                    var restore = new SetNetClasses { MergeMode = MapMergeMode.MmmMerge };
                    restore.NetClasses.Add(defaultClass.Clone());
                    await client.InvokeAsync<SetNetClasses, Empty>(restore, CancellationToken.None);
                }
            }
            Assert.AreEqual(classes, await client.InvokeAsync<GetNetClasses, NetClassesResponse>(new(), token),
                "The net classes are put back exactly.");
            AssertStale(await Read(classed, token), "project_inputs_changed", "Putting the net class back must not revive the check.");
        }

        // 7. A check of the edited board that is still current when the board is reloaded. The editor detaches it before
        //    it frees the edited board: its notifications are gone, so it becomes stale for good instead of listening to
        //    a board that no longer exists, and only a new check, captured afresh, is current.
        var (_, edit) = await Run(await Observe());
        await Evidence("pcb-drc-job-edited-board.json", edit);
        AssertCurrent(edit, "A check of the edited board must be complete and fresh.");
        Assert.AreEqual(edit, await Read(edit, token), "Nothing changed the edited board before it was reloaded.");
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = board }, token);
        var detached = await Read(edit, token);
        await Evidence("pcb-drc-job-reload-detached.json", detached);
        AssertStale(detached, "input_events_lost",
            "Reloading the board must detach the check that was still current, not compare it with the new board.");
        AssertStale(await Read(fourth, token), "document_changed", "Reloading the board must not revive an older check.");
        var replay = await client.InvokeAsync<StartPcbDrcJob, PcbDrcJobState>(secondRequest, token);
        Assert.AreEqual(second.JobId, replay.JobId);
        AssertStale(replay, "project_inputs_changed", "Replaying a start request must return its stale check.");
        var (_, third) = await Run(await Observe());
        await Evidence("pcb-drc-job-reverted.json", third);
        AssertCurrent(third, "A new check of the reloaded board must be complete and fresh.");
        Assert.AreEqual(state.Findings.Count, third.Findings.Count,
            "A new check of the reloaded board must report the findings of the saved board.");

        // 8. Teardrops: KiCad writes them without an identity and gives each a new one when it loads the board. A check's
        //    copy of the board must still name the open board's teardrop, so a rule that fails for every track (rules
        //    match a teardrop as a track) yields a finding about exactly that teardrop, before and after another reload
        //    changes its identity.
        string boardPath = Path.Combine(board.Project.Path, board.BoardFilename);
        Assert.IsFalse((await Observe()).NativeContentDirty, "The board must be saved before this step replaces its file.");
        byte[] savedBoard = await File.ReadAllBytesAsync(boardPath, token);
        Assert.IsFalse(File.Exists(rules), "Only this journey writes custom rules.");
        try
        {
            // Test data only: the teardrop reaches the open editor through KiCad's own board loader.
            string text = System.Text.Encoding.UTF8.GetString(savedBoard);
            int end = text.LastIndexOf(')');
            Assert.IsGreaterThan(0, end);
            await File.WriteAllTextAsync(boardPath, text[..end] +
                "  (zone (net \"DRC_JOURNEY_TEARDROP\") (layer \"F.Cu\") (hatch edge 0.5) (priority 30000) " +
                "(attr (teardrop (type padvia))) (connect_pads yes (clearance 0)) (min_thickness 0.0254) " +
                "(fill yes (thermal_gap 0.5) (thermal_bridge_width 0.5) (island_removal_mode 1) (island_area_min 10)) " +
                "(polygon (pts (xy 180 180) (xy 181 180.4) (xy 181 180.6) (xy 180 181))))\n" + text[end..], token);
            // Rules match a teardrop as a track, so every track and the teardrop fail this assertion.
            await File.WriteAllTextAsync(rules,
                "(version 1)\n(rule \"every_track\" (condition \"A.Type == 'Track'\") (constraint assertion \"A.Type != 'Track'\"))\n", token);
            // Reloads the board and returns its teardrop's identity.
            async Task<string> Load()
            {
                await client.InvokeAsync<RevertDocument, Empty>(new() { Document = board }, token);
                return (await client.InvokeAsync<GetItems, GetItemsResponse>(
                    new() { Header = header, Types_ = { KiCadObjectType.KotPcbZone } }, token))
                    .Items.Select(item => item.Unpack<Zone>()).Single(zone => zone.Type == ZoneType.ZtTeardrop).Id.Value;
            }
            async Task<PcbDrcJobState> CheckTeardrop(string teardrop, string name)
            {
                var (_, zoneCheck) = await Run(await Observe());
                await Evidence(name, zoneCheck);
                AssertCurrent(zoneCheck, "A check of a board with a teardrop must be complete and fresh.");
                var named = zoneCheck.Findings.Where(finding => finding.Marker.ErrorType == Kiapi.Board.DrcErrorType.DrcetAssertionFailure)
                    .SelectMany(finding => finding.Marker.Items.Select(item => item.Value)).ToList();
                CollectionAssert.Contains(named, teardrop, "The rule's finding must name the open board's teardrop.");
                CollectionAssert.IsSubsetOf(named, new[] { teardrop, "11111111-1111-4111-8111-111111111111", "33333333-3333-4333-8333-333333333333" },
                    "Every finding of the rule must name the teardrop or a track of the open board, never an identity private to the check.");
                return zoneCheck;
            }
            string loaded = await Load();
            var firstLoad = await CheckTeardrop(loaded, "pcb-drc-job-teardrop.json");
            string reloaded = await Load();
            Assert.AreNotEqual(loaded, reloaded, "KiCad gives a loaded teardrop a new identity.");
            AssertStale(await Read(firstLoad, token), "input_events_lost", "Reloading must make the teardrop check stale.");
            await CheckTeardrop(reloaded, "pcb-drc-job-teardrop-reloaded.json");
        }
        finally
        {
            await File.WriteAllBytesAsync(boardPath, savedBoard, CancellationToken.None);
            File.Delete(rules); // Only the rules file this step created.
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = board }, CancellationToken.None);
        }
        var (_, restored) = await Run(await Observe());
        await Evidence("pcb-drc-job-restored.json", restored);
        AssertCurrent(restored, "A check of the restored board must be complete and fresh.");
        Assert.AreEqual(state.Findings.Count, restored.Findings.Count,
            "A check of the restored board must report the findings of the saved board.");

        // 9. Text that reads the clock. A check lays out the board's texts, so it reads what they show. A silkscreen text
        //    shows a project text variable. When the variable gives the date, or the day an @{...} expression reads with
        //    today() under date formatting, KiCad captures the day with the check, which is complete and fresh. When it
        //    gives the time of day, which changes while the check runs, or an expression that reads the time (now()),
        //    chance (random()) or the version-control repository (vcsbranch()), the check's snapshot is incomplete with its
        //    reason and its results are never fresh. Without that text a new check is complete and fresh again.
        Assert.IsFalse((await Observe()).NativeContentDirty, "The board must be saved before this step replaces its file.");
        byte[] savedClockBoard = await File.ReadAllBytesAsync(boardPath, token);
        const string stamp = "DRC_JOURNEY_STAMP";
        bool stamped = false;
        async Task Stamp(string value)
        {
            var set = new SetTextVariables { Document = projectDocument, MergeMode = MapMergeMode.MmmMerge, Variables = new() };
            set.Variables.Variables[stamp] = value;
            stamped = true;
            await client.InvokeAsync<SetTextVariables, Empty>(set, token);
        }
        try
        {
            // Test data only: the text reaches the open editor through KiCad's own board loader.
            string text = System.Text.Encoding.UTF8.GetString(savedClockBoard);
            int end = text.LastIndexOf(')');
            Assert.IsGreaterThan(0, end);
            await File.WriteAllTextAsync(boardPath, text[..end] +
                "  (gr_text \"Checked ${" + stamp + "}\" (at 60 60) (layer \"F.SilkS\") " +
                "(uuid \"44444444-4444-4444-8444-444444444444\") (effects (font (size 1 1) (thickness 0.15))))\n" +
                text[end..], token);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = board }, token);
            await Stamp("${CURRENT_DATE}");
            var (_, dated) = await Run(await Observe());
            await Evidence("pcb-drc-job-date.json", dated);
            AssertCurrent(dated, "A check whose board text shows the date, which KiCad captures with the check, must be complete and fresh.");
            await Stamp("@{dateformat(today())}");
            AssertStale(await Read(dated, token), "project_inputs_changed", "Changing what the text shows must make the check stale.");
            var (_, today) = await Run(await Observe());
            await Evidence("pcb-drc-job-today.json", today);
            AssertCurrent(today, "A check whose board text shows the day through today(), which KiCad captures with the check, must be complete and fresh.");
            Assert.AreEqual(today, await Read(today, token), "Nothing the check read has changed: it stays current for an agent.");
            await Stamp("${CURRENT_TIME_HH_MM_SS}");
            AssertStale(await Read(today, token), "project_inputs_changed", "Changing what the text shows must make the check stale.");
            var (_, timed) = await Run(await Observe());
            await Evidence("pcb-drc-job-time-of-day.json", timed);
            AssertPcbDrcJobIncomplete(timed, "current_time_text", "${CURRENT_TIME_HH_MM_SS}",
                "A board text that shows the time of day is no input a check can capture.");
            foreach (var (expression, call, name) in new[]
                { ("@{now()}", "now()", "now"), ("@{round(random() * 100)}", "random()", "random"), ("@{vcsbranch()}", "vcsbranch()", "vcsbranch") })
            {
                await Stamp(expression);
                var (_, evaluated) = await Run(await Observe());
                await Evidence("pcb-drc-job-expression-" + name + ".json", evaluated);
                AssertPcbDrcJobIncomplete(evaluated, "volatile_text_expression", call,
                    $"A board text that evaluates {call} is no input a check can capture.");
            }
        }
        finally
        {
            if (stamped)
                await client.InvokeAsync<SetTextVariables, Empty>(new()
                    { Document = projectDocument, MergeMode = MapMergeMode.MmmReplace, Variables = variables.Clone() }, CancellationToken.None);
            await File.WriteAllBytesAsync(boardPath, savedClockBoard, CancellationToken.None);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = board }, CancellationToken.None);
        }
        Assert.AreEqual(variables, await client.InvokeAsync<GetTextVariables, Kiapi.Common.Project.TextVariables>(
            new() { Document = projectDocument }, token));
        var (_, unstamped) = await Run(await Observe());
        await Evidence("pcb-drc-job-clock-restored.json", unstamped);
        AssertCurrent(unstamped, "Without the clock text, a new check must be complete and fresh again.");
        Assert.AreEqual(state.Findings.Count, unstamped.Findings.Count,
            "A check of the restored board must report the findings of the saved board.");
    }

    // Window activation is the PCB editor's checkpoint for changes that reach it without any notification. Here the
    // board's custom rules file is a link to a file in another folder: KiCad watches the folder of the link, so it hears
    // nothing when the linked file changes, and the check says so in its input warnings. Only activating the PCB editor
    // (the person switches back to it) can then make the check stale before an agent reads it again.
    private static async Task VerifyPcbDrcJobActivation(NativeClient client, DocumentSpecifier board, int processId,
        string display, string evidence, PcbDrcAgent agent, CancellationToken token)
    {
        const string uncovered = "Some native input notifications are unavailable; status reads recheck content";
        const string original = "(version 1)\n(rule \"fixture_linked\" (constraint clearance (min 0.3mm)))\n";
        const string changed = "(version 1)\n(rule \"fixture_linked\" (constraint clearance (min 0.4mm)))\n";
        var schematic = (await client.InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(
            new() { Type = DocumentType.DoctypeSchematic }, token)).Documents.Single();
        string rules = Path.Combine(board.Project.Path, Path.ChangeExtension(board.BoardFilename, ".kicad_dru"));
        Task Evidence(string name, PcbDrcJobState state) => File.WriteAllTextAsync(
            Path.Combine(evidence, name), SchematicJson.Formatter.Format(state), token);
        // KiCad handles notifications, activation and API requests on its UI thread. The pause only gives a busy UI thread
        // its turn; the assertions after it prove the result.
        async Task Settle()
        {
            await ObserveLifecycleState(client, board, token);
            await Task.Delay(TimeSpan.FromMilliseconds(250), token);
            await ObserveLifecycleState(client, board, token);
        }
        // The schematic editor's canvas holds the keyboard focus exactly while its window is the active one.
        async Task SchematicFocus(bool focused, string because)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(8));
            int delay = 25;
            try
            {
                while (true)
                {
                    try
                    {
                        var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                            new() { Document = schematic }, limit.Token);
                        if (observation.Preview.Viewport.CanvasHasKeyboardFocus == focused) return;
                    }
                    catch (NativeApiException error) when (error.Status is 4 or 7) { }
                    await Task.Delay(delay, limit.Token);
                    delay = Math.Min(delay * 2, 200);
                }
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested && !token.IsCancellationRequested)
            {
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, processId + "-drc-job-activation-focus.png"), token);
                throw new AssertFailedException(because);
            }
        }

        byte[]? saved = File.Exists(rules) ? await File.ReadAllBytesAsync(rules, token) : null;
        string outside = Directory.CreateTempSubdirectory("kicad-drc-activation-").FullName;
        string target = Path.Combine(outside, "linked.kicad_dru");
        bool linked = false;
        try
        {
            if (saved is not null) File.Delete(rules);
            await File.WriteAllTextAsync(target, original, token);
            File.CreateSymbolicLink(rules, target);
            linked = true;
            await Settle(); // The link's own creation notification runs before the checks start.

            // Without an activation, a change that reached no notification and was put back before the read is not
            // seen: the check's inputs are again exactly what it checked.
            var (_, control) = await RunPcbDrcJob(client, board, await ObserveLifecycleState(client, board, token), token, agent: agent);
            await Evidence("pcb-drc-job-activation-control.json", control);
            AssertPcbDrcJobCurrent(control, "A check whose rules file is a link still captures the rules it reads exactly.");
            CollectionAssert.Contains(control.InputWarnings.ToArray(), uncovered,
                "A check whose rules file is a link must say that notifications do not cover all of its inputs.");
            await File.WriteAllTextAsync(target, changed, token);
            await Settle();
            await File.WriteAllTextAsync(target, original, token);
            var unobserved = await agent.Read(board, control, token);
            AssertPcbDrcJobCurrent(unobserved,
                "Nothing but a read observed the linked rules, and they were put back first: the check's inputs are again exactly what it checked.");
            Assert.AreEqual(control.Findings.Count, unobserved.Findings.Count);

            // The same change, now with the PCB editor activated while the linked rules are changed: the person moves
            // to the schematic editor and back.
            var (_, check) = await RunPcbDrcJob(client, board, await ObserveLifecycleState(client, board, token), token, agent: agent);
            AssertPcbDrcJobCurrent(check, "A new check with the linked rules must be complete and fresh.");
            CollectionAssert.Contains(check.InputWarnings.ToArray(), uncovered);
            await File.WriteAllTextAsync(target, changed, token);
            NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false); // The schematic canvas background.
            await SchematicFocus(true, "The schematic editor did not become the active window.");
            NativeKeyboard.SchematicShortcut(display, processId, "motion", "PCB Editor", false, false);
            await SchematicFocus(false, "The PCB editor did not become the active window again.");
            await Settle();
            await File.WriteAllTextAsync(target, original, token);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, processId + "-drc-job-activation.png"), token);
            var activated = await agent.Read(board, check, token);
            await Evidence("pcb-drc-job-activation-stale.json", activated);
            AssertPcbDrcJobStale(activated, "project_inputs_changed",
                "Activating the PCB editor after its linked rules changed must make the check stale before any read, and putting the rules back must not revive it.");
        }
        finally
        {
            if (linked) File.Delete(rules); // Only the link this step created.
            if (saved is not null) await File.WriteAllBytesAsync(rules, saved, CancellationToken.None);
            Directory.Delete(outside, true);
        }
    }

    // The interactive router's settings live only in the PCB editor's memory and reach no notification. A check reads
    // them (a refill regenerates tuning patterns with them), so changing them in the rendered Interactive Router Settings
    // dialog makes a completed check stale at the next read, and a new check is complete and fresh again. The step puts
    // the original routing mode back the same way before the next step.
    private static async Task VerifyPcbDrcJobRouterChange(NativeClient client, DocumentSpecifier board, int processId,
        string display, string evidence, PcbDrcAgent agent, CancellationToken token)
    {
        const string dialog = "Interactive Router Settings";
        Task Evidence(string name, PcbDrcJobState state) => File.WriteAllTextAsync(
            Path.Combine(evidence, name), SchematicJson.Formatter.Format(state), token);
        Task Picture(string phase) => NativeKeyboard.CaptureAsync(display,
            Path.Combine(evidence, processId + "-drc-job-router-" + phase + ".png"), token);
        void Key(string key, string window) => NativeKeyboard.SchematicShortcut(display, processId, key, window, false, false);
        async Task Window(bool visible)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(8));
            int delay = 25;
            try
            {
                while (NativeKeyboard.HasWindow(display, processId, dialog) != visible)
                { await Task.Delay(delay, limit.Token); delay = Math.Min(delay * 2, 200); }
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested && !token.IsCancellationRequested)
            {
                await Picture(visible ? "dialog-missing" : "dialog-open");
                throw new AssertFailedException($"The {dialog} dialog did not {(visible ? "open" : "close")}.");
            }
            if (visible) await NativeSetupUi.StableGeometry(display, processId, limit.Token, dialog);
        }

        // Route > Interactive Router Settings... is the last item of the Route menu (Alt+U). The dialog opens on its routing
        // mode buttons; an arrow key selects the neighbouring mode, and Return confirms it.
        async Task SelectMode(string arrow, string phase)
        {
            NativeKeyboard.SchematicShortcut(display, processId, "u", "PCB Editor", false, false, altKey: true);
            await NativeSetupUi.WaitForPopup(display, processId, true, token, "PCB Editor");
            Key("End", "PCB Editor");
            Key("Return", "PCB Editor");
            await Window(true);
            await Picture(phase + "-dialog");
            Key(arrow, dialog);
            await Picture(phase);
            Key("Return", dialog);
            await Window(false);
        }

        var (_, check) = await RunPcbDrcJob(client, board, await ObserveLifecycleState(client, board, token), token, agent: agent);
        await Evidence("pcb-drc-job-router.json", check);
        AssertPcbDrcJobCurrent(check, "A check before the router settings change must be complete and fresh.");
        await SelectMode("Down", "mode-changed");
        var changed = await agent.Read(board, check, token);
        await Evidence("pcb-drc-job-router-stale.json", changed);
        AssertPcbDrcJobStale(changed, "auxiliary_inputs_changed",
            "Changing the interactive router settings in the rendered dialog must make the finished check stale.");
        var (_, again) = await RunPcbDrcJob(client, board, await ObserveLifecycleState(client, board, token), token, agent: agent);
        await Evidence("pcb-drc-job-router-new.json", again);
        AssertPcbDrcJobCurrent(again, "A new check with the changed router settings must be complete and fresh.");
        Assert.AreEqual(check.Findings.Count, again.Findings.Count, "Router settings do not change what a check without refill finds.");

        // Put the original mode back for the steps after this one: the opposite arrow key selects it again. That too is a
        // router change, and a new check is complete and fresh.
        await SelectMode("Up", "mode-restored");
        var reverted = await agent.Read(board, again, token);
        await Evidence("pcb-drc-job-router-restored-stale.json", reverted);
        AssertPcbDrcJobStale(reverted, "auxiliary_inputs_changed",
            "Putting the router mode back in the rendered dialog must make the check of the changed mode stale.");
        var (_, restored) = await RunPcbDrcJob(client, board, await ObserveLifecycleState(client, board, token), token, agent: agent);
        await Evidence("pcb-drc-job-router-restored.json", restored);
        AssertPcbDrcJobCurrent(restored, "A new check with the original router mode must be complete and fresh.");
        Assert.AreEqual(check.Findings.Count, restored.Findings.Count, "Router settings do not change what a check without refill finds.");
        AssertPcbDrcJobStale(await agent.Read(board, check, token), "auxiliary_inputs_changed",
            "The check before the change never becomes current again, even with the original mode back.");
    }

    // The board's current variant selects variant field values and ${VARIANT}. KiCad keeps it only in memory, and choosing
    // another variant in the PCB editor's toolbar is no board edit. A check reads it, so choosing a variant in the rendered
    // toolbar makes a completed check stale at the next read, and a new check is complete and fresh again. The step
    // chooses the default variant again the same way, and puts the board back, before the next step.
    private static async Task VerifyPcbDrcJobVariantChange(NativeClient client, DocumentSpecifier board, int processId,
        string display, string evidence, PcbDrcAgent agent, CancellationToken token)
    {
        const string variant = "DrcJourney";
        Task Evidence(string name, PcbDrcJobState state) => File.WriteAllTextAsync(
            Path.Combine(evidence, name), SchematicJson.Formatter.Format(state), token);
        Task Picture(string phase) => NativeKeyboard.CaptureAsync(display,
            Path.Combine(evidence, processId + "-drc-job-variant-" + phase + ".png"), token);
        // The variant choice is the last control of the PCB editor's top toolbar ("< Default >" with the default variant).
        // Its list opens under the pointer; Home picks the default variant, the first entry, and End the board's own
        // variant, the last one.
        async Task Choose(string key, string phase)
        {
            NativeKeyboard.SchematicShortcut(display, processId, "click", "PCB Editor", false, false,
                clickFromLeft: 1073, clickFromTop: 43);
            await NativeSetupUi.WaitForPopup(display, processId, true, token, "PCB Editor",
                Path.Combine(evidence, processId + "-drc-job-variant-" + phase + "-missing.png"));
            await Picture(phase + "-list");
            NativeKeyboard.SchematicShortcut(display, processId, key, "PCB Editor", false, false);
            NativeKeyboard.SchematicShortcut(display, processId, "Return", "PCB Editor", false, false);
            await NativeSetupUi.WaitForPopup(display, processId, false, token, "PCB Editor",
                Path.Combine(evidence, processId + "-drc-job-variant-" + phase + "-open.png"));
            await Picture(phase);
        }

        string boardPath = Path.Combine(board.Project.Path, board.BoardFilename);
        Assert.IsFalse((await ObserveLifecycleState(client, board, token)).NativeContentDirty,
            "The board must be saved before this step replaces its file.");
        byte[] saved = await File.ReadAllBytesAsync(boardPath, token);
        try
        {
            // Test data only: the board's variant reaches the open editor through KiCad's own board loader.
            string text = System.Text.Encoding.UTF8.GetString(saved);
            int end = text.LastIndexOf(')');
            Assert.IsGreaterThan(0, end);
            await File.WriteAllTextAsync(boardPath, text[..end] +
                "  (variants (variant (name \"" + variant + "\") (description \"DRC journey variant\")))\n" + text[end..], token);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = board }, token);
            var (_, check) = await RunPcbDrcJob(client, board, await ObserveLifecycleState(client, board, token), token, agent: agent);
            await Evidence("pcb-drc-job-variant.json", check);
            AssertPcbDrcJobCurrent(check, "A check of the default variant must be complete and fresh.");
            await Choose("End", "chosen");
            var chosen = await agent.Read(board, check, token);
            await Evidence("pcb-drc-job-variant-stale.json", chosen);
            AssertPcbDrcJobStale(chosen, "project_inputs_changed",
                "Choosing the board's variant in the rendered toolbar must make the check of the default variant stale.");
            var (_, again) = await RunPcbDrcJob(client, board, await ObserveLifecycleState(client, board, token), token, agent: agent);
            await Evidence("pcb-drc-job-variant-new.json", again);
            AssertPcbDrcJobCurrent(again, "A new check of the chosen variant must be complete and fresh.");
            await Choose("Home", "default");
            var restored = await agent.Read(board, again, token);
            await Evidence("pcb-drc-job-variant-default-stale.json", restored);
            AssertPcbDrcJobStale(restored, "project_inputs_changed",
                "Choosing the default variant again must make the check of the board's variant stale.");
            var (_, defaulted) = await RunPcbDrcJob(client, board, await ObserveLifecycleState(client, board, token), token, agent: agent);
            await Evidence("pcb-drc-job-variant-default.json", defaulted);
            AssertPcbDrcJobCurrent(defaulted, "A new check of the default variant must be complete and fresh.");
            AssertPcbDrcJobStale(await agent.Read(board, check, token), "project_inputs_changed",
                "The first check never becomes current again, even with the default variant chosen again.");
        }
        finally
        {
            await File.WriteAllBytesAsync(boardPath, saved, CancellationToken.None);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = board }, CancellationToken.None);
        }
    }

    // Starts a PCB DRC job at the given revision and reads it until its worker finished. configure adds options to the
    // ordinary request, such as candidate items or a schematic-parity source. With an agent, the finished check is read
    // once more as the agent reads it, through the STDIO MCP server, and must be exactly what KiCad reported.
    private static async Task<(StartPcbDrcJob Request, PcbDrcJobState State)> RunPcbDrcJob(NativeClient client,
        DocumentSpecifier board, DocumentLifecycleState at, CancellationToken token, Action<StartPcbDrcJob>? configure = null,
        PcbDrcAgent? agent = null)
    {
        var request = new StartPcbDrcJob
        {
            Document = board, ProcessEpoch = client.Epoch,
            ExpectedRevision = at.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D")
        };
        configure?.Invoke(request);
        var started = await client.InvokeAsync<StartPcbDrcJob, PcbDrcJobState>(request, token);
        Assert.AreEqual(board, started.Document);
        Assert.AreEqual(request.OperationId, started.OperationId);
        Assert.AreEqual(client.Epoch, started.ProcessEpoch);
        Assert.AreEqual(at.Revision, started.CheckedRevision);
        Assert.IsTrue(Guid.TryParseExact(started.JobId, "D", out _));
        Assert.AreEqual(request.CandidateItems.Count != 0, started.CandidateDryRun);
        Assert.HasCount(request.CandidateItems.Count, started.CandidateItemIds);
        AssertPcbDrcJobPending(started, "A check that has just started");
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromMinutes(2));
        int delay = 25;
        var state = started;
        while (!state.WorkerFinished)
        {
            await Task.Delay(delay, wait.Token);
            delay = Math.Min(delay * 2, 500);
            state = await ReadPcbDrcJobState(client, board, started, wait.Token);
            Assert.AreEqual(started.JobId, state.JobId);
            AssertPcbDrcJobPending(state, "A running check");
        }
        if (agent is not null)
        {
            var read = await agent.Read(board, state, token);
            Assert.AreEqual(state, read, "An agent reading the finished check over MCP must see what KiCad reported when it finished.");
            state = read;
        }
        return (request, state);
    }

    // The reason every unfinished check gives; a finished check that cannot name an object gives generated_item_identity.
    internal const string PcbDrcPendingIdentity = "snapshot_incomplete: finding_identity_pending: ";

    // Until its worker has finished, a check has not matched its findings to objects of the open board: it claims neither
    // a complete snapshot nor fresh results, and says so. A finished check no longer carries that reason.
    internal static void AssertPcbDrcJobPending(PcbDrcJobState job, string because)
    {
        int pending = job.InputWarnings.Count(warning => warning.StartsWith(PcbDrcPendingIdentity, StringComparison.Ordinal));
        if (job.WorkerFinished)
        {
            Assert.AreEqual(0, pending, because + " has finished and must no longer wait for its finding identities.");
            return;
        }
        Assert.AreEqual(1, pending, because + " must say that its findings are not yet matched to the open board. "
            + string.Join(" | ", job.InputWarnings));
        Assert.IsFalse(job.SnapshotComplete, because + " must not claim a complete snapshot before it has finished.");
        Assert.IsFalse(job.ResultsFresh, because + " must not claim fresh results.");
    }

    private static Task<PcbDrcJobState> ReadPcbDrcJobState(NativeClient client, DocumentSpecifier board, PcbDrcJobState job,
        CancellationToken token) => client.InvokeAsync<ReadPcbDrcJob, PcbDrcJobState>(new()
            { Document = board, JobId = job.JobId, ProcessEpoch = client.Epoch }, token);

    // A stale check keeps the complete snapshot it was captured from; only its results are no longer current.
    private static void AssertPcbDrcJobStale(PcbDrcJobState job, string code, string because)
    {
        Assert.AreEqual(PcbDrcJobStatus.PdrcjsStale, job.Status, because + " " + job.ErrorCode + ": " + job.ErrorMessage);
        Assert.AreEqual(code, job.ErrorCode, because);
        Assert.IsEmpty(job.Findings, because + " A stale check must not expose its old findings.");
        Assert.IsTrue(job.WorkerFinished, because);
        Assert.IsFalse(job.ResultsFresh, because);
        Assert.IsTrue(job.SnapshotComplete, because + " " + string.Join(" | ", job.InputWarnings));
    }

    // A completed check whose every input KiCad captured exactly, and none of which has changed since: the only case in
    // which a check may claim a complete snapshot and fresh results (decision n39a51a3a2ff84c55, ledger p23deb822a36256a6).
    private static void AssertPcbDrcJobCurrent(PcbDrcJobState job, string because)
    {
        Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, job.Status, because + " " + job.ErrorCode + ": " + job.ErrorMessage);
        Assert.AreEqual(1.0, job.Progress, because);
        Assert.IsTrue(job.WorkerFinished, because);
        Assert.IsFalse(job.CancellationRequested, because);
        Assert.IsTrue(job.SnapshotComplete, because + " " + string.Join(" | ", job.InputWarnings));
        Assert.IsTrue(job.ResultsFresh, because);
        Assert.IsFalse(job.InputWarnings.Any(warning => warning.StartsWith("snapshot_incomplete: ", StringComparison.Ordinal)), because);
        Assert.AreEqual(job.Findings.Count, job.Findings.Select(finding => finding.NativeId).Distinct().Count(), because);
    }

    // A completed check whose snapshot is incomplete: it gives exactly one reason, with the stable code and naming the input
    // it could not capture, and its results are never fresh.
    private static void AssertPcbDrcJobIncomplete(PcbDrcJobState job, string code, string naming, string because)
    {
        Assert.AreEqual(PcbDrcJobStatus.PdrcjsCompleted, job.Status, because + " " + job.ErrorCode + ": " + job.ErrorMessage);
        Assert.IsTrue(job.WorkerFinished, because);
        Assert.IsFalse(job.SnapshotComplete, because);
        Assert.IsFalse(job.ResultsFresh, because + " An incomplete snapshot never has fresh results.");
        var reason = job.InputWarnings.Single(warning => warning.StartsWith("snapshot_incomplete: ", StringComparison.Ordinal));
        StringAssert.StartsWith(reason, "snapshot_incomplete: " + code + ": ", because);
        StringAssert.Contains(reason, naming, because);
    }

    // Rewrites a file in place with text of the same length and puts its modification time back, so only its content says
    // that it changed.
    private static async Task EditPcbDrcInputInPlace(string path, string from, string to, CancellationToken token)
    {
        Assert.AreEqual(from.Length, to.Length);
        var info = new FileInfo(path);
        long length = info.Length;
        DateTime modified = info.LastWriteTimeUtc;
        string text = await File.ReadAllTextAsync(path, token);
        Assert.AreEqual(1, text.Split(from).Length - 1, $"{path} must hold '{from}' once.");
        await File.WriteAllTextAsync(path, text.Replace(from, to, StringComparison.Ordinal), token);
        File.SetLastWriteTimeUtc(path, modified);
        info.Refresh();
        Assert.AreEqual(length, info.Length, "The edit must keep the file size.");
        Assert.AreEqual(modified, info.LastWriteTimeUtc, "The edit must keep the modification time.");
    }

    // The project footprint library of the PCB check journeys: the library DrcJourney in the project folder, named by the
    // project's own library table, with the one footprint DrcJourneyPart. KiCad reads a project's library table only when it
    // loads the project, so NativeSessionTests writes both before KiCad starts. VerifyPcbDrcJobLibraryEdit edits the
    // footprint's description in place.
    internal const string PcbDrcJourneyLibraryPart = "DRC journey part A";

    internal static async Task WritePcbDrcJourneyLibrary(string projectDirectory, CancellationToken token)
    {
        string library = Path.Combine(projectDirectory, "drc-journey.pretty");
        Directory.CreateDirectory(library);
        await File.WriteAllTextAsync(Path.Combine(library, "DrcJourneyPart.kicad_mod"),
            "(footprint \"DrcJourneyPart\"\n" +
            "\t(version 20241229)\n\t(generator \"pcbnew\")\n\t(layer \"F.Cu\")\n" +
            "\t(descr \"" + PcbDrcJourneyLibraryPart + "\")\n" +
            "\t(property \"Reference\" \"REF**\" (at 0 -2 0) (layer \"F.SilkS\") (uuid \"66666666-6666-4666-8666-666666666661\") " +
            "(effects (font (size 1 1) (thickness 0.15))))\n" +
            "\t(property \"Value\" \"DrcJourneyPart\" (at 0 2 0) (layer \"F.Fab\") (uuid \"66666666-6666-4666-8666-666666666662\") " +
            "(effects (font (size 1 1) (thickness 0.15))))\n" +
            "\t(attr smd)\n" +
            "\t(pad \"1\" smd rect (at 0 0) (size 1 1) (layers \"F.Cu\" \"F.Mask\" \"F.Paste\") (uuid \"66666666-6666-4666-8666-666666666663\"))\n" +
            ")\n", token);
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "fp-lib-table"),
            "(fp_lib_table\n\t(version 7)\n" +
            "\t(lib (name \"DrcJourney\") (type \"KiCad\") (uri \"${KIPRJMOD}/drc-journey.pretty\") (options \"\") (descr \"\"))\n)\n", token);
    }

    // A footprint library the board uses, edited in place. The board places DrcJourney:DrcJourneyPart through KiCad's own
    // loader in the rendered PCB editor, and a check captures the footprint from the project's library. Rewriting the
    // library's footprint file in place, with the same size and modification time, makes the completed check stale with
    // library_inputs_changed at the agent's next read, made at once without waiting for KiCad's file notification
    // (n456d6b796cd7a9a3); a new check is complete and fresh again. The step puts the library and the board back.
    private static async Task VerifyPcbDrcJobLibraryEdit(NativeClient client, DocumentSpecifier board, int processId,
        string display, string evidence, PcbDrcAgent agent, CancellationToken token)
    {
        const string part = "55555555-5555-4555-8555-555555555555";
        string library = Path.Combine(board.Project.Path, "drc-journey.pretty", "DrcJourneyPart.kicad_mod");
        Assert.IsTrue(File.Exists(library), $"The project's footprint library {library} must exist before KiCad starts.");
        string boardPath = Path.Combine(board.Project.Path, board.BoardFilename);
        Assert.IsFalse((await ObserveLifecycleState(client, board, token)).NativeContentDirty,
            "The board must be saved before this step replaces its file.");
        byte[] savedBoard = await File.ReadAllBytesAsync(boardPath, token);
        byte[] savedLibrary = await File.ReadAllBytesAsync(library, token);
        DateTime libraryModified = File.GetLastWriteTimeUtc(library);
        Task Evidence(string name, PcbDrcJobState state) => File.WriteAllTextAsync(
            Path.Combine(evidence, name), SchematicJson.Formatter.Format(state), token);
        try
        {
            // Test data only: the footprint reaches the open editor through KiCad's own board loader.
            string text = Encoding.UTF8.GetString(savedBoard);
            int end = text.LastIndexOf(')');
            Assert.IsGreaterThan(0, end);
            await File.WriteAllTextAsync(boardPath, text[..end] +
                "  (footprint \"DrcJourney:DrcJourneyPart\" (layer \"F.Cu\") (uuid \"" + part + "\") (at 70 70)\n" +
                "    (property \"Reference\" \"U90\" (at 0 -2 0) (layer \"F.SilkS\") (uuid \"55555555-5555-4555-8555-555555555556\") " +
                "(effects (font (size 1 1) (thickness 0.15))))\n" +
                "    (property \"Value\" \"DrcJourneyPart\" (at 0 2 0) (layer \"F.Fab\") (uuid \"55555555-5555-4555-8555-555555555557\") " +
                "(effects (font (size 1 1) (thickness 0.15))))\n" +
                "    (attr smd)\n" +
                "    (pad \"1\" smd rect (at 0 0) (size 1 1) (layers \"F.Cu\" \"F.Mask\" \"F.Paste\") (uuid \"55555555-5555-4555-8555-555555555558\")))\n" +
                text[end..], token);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = board }, token);
            Assert.HasCount(1, (await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
                { Header = new() { Document = board }, Items = { new KIID { Value = part } } }, token)).Items,
                "The rendered editor must hold the library footprint.");
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, processId + "-drc-job-library-placed.png"), token);

            var (_, check) = await RunPcbDrcJob(client, board, await ObserveLifecycleState(client, board, token), token, agent: agent);
            await Evidence("pcb-drc-job-library.json", check);
            AssertPcbDrcJobCurrent(check, "A check of a board that places a library footprint must be complete and fresh.");
            var libraryIssues = check.Findings.Where(finding => finding.Marker.ErrorType == Kiapi.Board.DrcErrorType.DrcetLibFootprintIssues
                && finding.Marker.Items.Any(item => item.Value == part)).ToList();
            Assert.IsEmpty(libraryIssues, "The check must read DrcJourney:DrcJourneyPart from the project's loaded library.");
            Assert.AreEqual(check, await agent.Read(board, check, token), "Nothing the check read has changed: it stays current.");

            await EditPcbDrcInputInPlace(library, PcbDrcJourneyLibraryPart, "DRC journey part B", token);
            var edited = await agent.Read(board, check, token);
            await Evidence("pcb-drc-job-library-stale.json", edited);
            AssertPcbDrcJobStale(edited, "library_inputs_changed",
                "Editing DrcJourney:DrcJourneyPart in place, with the same size and modification time, must make the check stale at the next read.");
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, processId + "-drc-job-library-edited.png"), token);
            var (_, again) = await RunPcbDrcJob(client, board, await ObserveLifecycleState(client, board, token), token, agent: agent);
            await Evidence("pcb-drc-job-library-new.json", again);
            AssertPcbDrcJobCurrent(again, "A new check with the edited library must be complete and fresh.");
            string[] Others(PcbDrcJobState job) => job.Findings.Where(finding => !finding.Marker.Items.Any(item => item.Value == part))
                .Select(finding => finding.Marker.ErrorType + "|" + string.Join(",", finding.Marker.Items.Select(item => item.Value)))
                .Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(Others(check), Others(again), "Only findings about the library footprint may change with its library.");
        }
        finally
        {
            await File.WriteAllBytesAsync(library, savedLibrary, CancellationToken.None);
            File.SetLastWriteTimeUtc(library, libraryModified);
            await File.WriteAllBytesAsync(boardPath, savedBoard, CancellationToken.None);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = board }, CancellationToken.None);
        }
        Assert.IsFalse((await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = board }, Types_ = { KiCadObjectType.KotPcbFootprint } }, token))
            .Items.Any(item => item.Unpack<FootprintInstance>().Id.Value == part),
            "The board must be put back without the library footprint.");
    }

    // An agent's reads of PCB checks: through the compiled STDIO MCP server's kicad_pcb_drc_job tool, as an agent reads
    // them, each followed by KiCad's own read of the same check, which must report exactly the same state. The journeys
    // make their fresh and stale assertions on these reads, so each kind of input change is proven where an agent sees it,
    // and every state passes the MCP tool's own state contract (PcbDrcTools.ValidateJobState).
    private sealed class PcbDrcAgent : IAsyncDisposable
    {
        private readonly NativeClient client;
        private readonly string statePath;

        private PcbDrcAgent(NativeClient client, string statePath, StdioMcpFixture mcp, string instanceId)
        {
            this.client = client;
            this.statePath = statePath;
            Mcp = mcp;
            InstanceId = instanceId;
        }

        internal StdioMcpFixture Mcp { get; }
        internal string InstanceId { get; }

        internal static bool Failed(JsonElement reply) => reply.TryGetProperty("isError", out var error) && error.GetBoolean();

        internal static string Text(JsonElement reply) => reply.GetProperty("content").EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;

        internal static async Task<PcbDrcAgent> StartAsync(NativeClient client, string stderrLog, CancellationToken token)
        {
            string statePath = Directory.CreateTempSubdirectory("kicad-drc-agent-").FullName;
            StdioMcpFixture? mcp = null;
            try
            {
                mcp = await StdioMcpFixture.StartAsync(statePath, stderrLog, token);
                string instanceId = (await client.HandshakeAsync(token)).InstanceId;
                var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
                Assert.IsFalse(Failed(attached), attached.GetRawText());
                return new PcbDrcAgent(client, statePath, mcp, instanceId);
            }
            catch
            {
                if (mcp is not null) await mcp.DisposeAsync();
                Directory.Delete(statePath, true);
                throw;
            }
        }

        internal async Task<PcbDrcJobState> Read(DocumentSpecifier board, PcbDrcJobState job, CancellationToken token)
        {
            var reply = await Mcp.Tool("kicad_pcb_drc_job", new
            {
                instanceId = InstanceId, documentJson = SchematicJson.Formatter.Format(board),
                jobId = job.JobId, processEpoch = client.Epoch
            });
            Assert.IsFalse(Failed(reply), reply.GetRawText());
            var agent = SchematicJson.Parser.Parse<PcbDrcJobState>(Text(reply));
            var native = await ReadPcbDrcJobState(client, board, job, token);
            Assert.AreEqual(native, agent, "KiCad's own read must report the check exactly as the agent read it over MCP.");
            return agent;
        }

        public async ValueTask DisposeAsync()
        {
            await Mcp.DisposeAsync();
            Directory.Delete(statePath, true);
        }
    }
}
