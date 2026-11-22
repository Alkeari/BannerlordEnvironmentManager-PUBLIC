using BannerlordEnvironmentManager.Core.Butr;
using BannerlordEnvironmentManager.Core.DryRun;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Report;

// THE RULE THIS FILE EXISTS TO KEEP.
//
// A report drafted here states evidence of causation on this machine, and nothing else. It is not our
// job to tell a mod author how to fix their mod, how to write their code, how to name anything, or what
// they should have done. It is solely our job to give them evidential proof that a crash on this system
// was caused by their mod.
//
// So nothing in here may ever emit:
//   - a suggested fix, a patch, or a code sample offered as a correction
//   - an opinion about how the mod is written, named or structured
//   - a guess at the author's intent or competence
//   - anything phrased as advice
//
// Do not add a "Suggested fix" section back. BugReportAdviceTests fails the build if fix-advice
// vocabulary appears anywhere in the generated text, and that test is the rule, not a formality.
//
// Pasting evidence is not advice and stays in. A decompiled method, a stack trace, a block of XML: the
// point is to show what was found. The line is advice versus evidence, not code versus prose.
//
// The second rule, from the same conversation: every report says on its face that BEM drafted it.
public static class BugReportDrafter
{
    public const string Attribution = "Bug report drafted with Bannerlord Environment Manager (BEM).";

    private const int MaxNamesInLine = 6;

    private const int MaxFrames = 12;

    private const int MaxClearedNames = 30;

    public static ModBugReport Draft(BugReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var names = Names(request.LoadOrder);
        var subject = request.LoadOrder.FirstOrDefault(entry => entry.Id == request.Subject);
        var subjectName = Clean(subject?.DisplayName ?? request.Subject.Value);
        var subjectVersion = Version(subject);
        var search = Search.Read(request.Bisection, request.Subject);
        var quality = Judge(request, search, subjectName);

        var blocks = new List<BugReportBlock> { BugReportBlock.Paragraph(Attribution), BugReportBlock.Rule() };

        if (quality.Strength is not BugReportStrength.Strong)
            blocks.Add(BugReportBlock.Paragraph(UpFront(quality)));

        blocks.Add(BugReportBlock.Paragraph(Opening(request, search, subjectName, subjectVersion)));
        blocks.AddRange(Exception(request));
        blocks.AddRange(Decompiled(request));
        blocks.AddRange(Static(request));

        blocks.Add(BugReportBlock.Rule());
        blocks.AddRange(Isolation(request, search, names, subjectName));

        blocks.Add(BugReportBlock.Rule());
        blocks.Add(BugReportBlock.Paragraph("My setup:"));
        blocks.Add(BugReportBlock.Bullets(Setup(request, subjectName, subjectVersion)));
        blocks.Add(BugReportBlock.Paragraph(OnRequest(request, search)));

        return new ModBugReport(
            Title(request, search, subjectName, subjectVersion),
            $"{subjectName} {subjectVersion} - bug report",
            blocks,
            quality);
    }

    // Everything that reaches the report goes through here, because a bug report is a public post and
    // the redactor that already strips a Windows user name, a file path and a key out of anything bound
    // for a third party is the same redactor this needs. There is not a second one.
    private static string Clean(string? value) => ButrRedaction.Text(value).Trim();

    private static IReadOnlyList<string> CleanLines(string? value) =>
        [.. ButrRedaction.Text(value).Split('\n').Select(line => line.TrimEnd())];

    private static string Version(ModuleEntry? entry)
    {
        if (entry is null)
            return "an unknown version";

        if (entry.Version.ToString() is { Length: > 0 } parsed)
            return parsed;

        return string.IsNullOrWhiteSpace(entry.Manifest?.VersionText)
            ? "an unknown version"
            : Clean(entry.Manifest!.VersionText);
    }

    private static Dictionary<ModuleId, string> Names(IReadOnlyList<ModuleEntry> loadOrder)
    {
        var names = new Dictionary<ModuleId, string>();

        foreach (var entry in loadOrder)
            names.TryAdd(entry.Id, Clean(entry.DisplayName));

        return names;
    }

    private static string Name(Dictionary<ModuleId, string> names, ModuleId id) =>
        names.TryGetValue(id, out var name) ? name : Clean(id.Value);

    private static string List(Dictionary<ModuleId, string> names, IReadOnlyList<ModuleId> modules, int cap) =>
        Join([.. modules.Select(id => Name(names, id))], cap);

    private static string Join(IReadOnlyList<string> values, int cap)
    {
        if (values.Count == 0)
            return "nothing";

        if (values.Count == 1)
            return values[0];

        if (values.Count <= cap)
            return string.Join(", ", values.Take(values.Count - 1)) + " and " + values[^1];

        return string.Join(", ", values.Take(cap)) + $" and {values.Count - cap} more";
    }

    private static string Title(
        BugReportRequest request,
        Search search,
        string subjectName,
        string subjectVersion)
    {
        var what = request.Crash is { TypeFullName.Length: > 0 } crash
            ? Simple(Clean(crash.TypeFullName))
            : "a crash";

        var when = request.ReproductionStep.Length > 0 || search.ReproductionStep.Length > 0
            ? " when I " + Clean(request.ReproductionStep.Length > 0 ? request.ReproductionStep : search.ReproductionStep)
            : string.Empty;

        return $"{subjectName} {subjectVersion}: {what}{when}";
    }

    private static string Simple(string typeFullName)
    {
        var lastDot = typeFullName.LastIndexOf('.');

        return lastDot < 0 || lastDot == typeFullName.Length - 1 ? typeFullName : typeFullName[(lastDot + 1)..];
    }

    private static string UpFront(BugReportQuality quality) => "Up front, so nobody wastes time on it: "
        + quality.EnglishHeadline;

    private static string Opening(
        BugReportRequest request,
        Search search,
        string subjectName,
        string subjectVersion)
    {
        var step = request.ReproductionStep.Length > 0 ? request.ReproductionStep : search.ReproductionStep;
        var when = step.Length > 0 ? " when I " + Clean(step) : string.Empty;

        return search.NamesSubject
            ? $"{subjectName} {subjectVersion} crashes my game{when}. Turning it off and changing nothing else "
              + "stops it."
            : $"I think {subjectName} {subjectVersion} is behind a crash on my game{when}, and here is "
              + "everything I have on it.";
    }

    private static IReadOnlyList<BugReportBlock> Exception(BugReportRequest request)
    {
        // No crash report means no exception section. An empty one, or one filled in from what the
        // crash probably was, is the failure this whole file is written against.
        if (request.Crash is not { } crash || (crash.TypeFullName.Length == 0 && crash.Frames.Count == 0))
            return [];

        var header = crash.Message.Length > 0
            ? $"{Clean(crash.TypeFullName)}: {Clean(crash.Message)}"
            : Clean(crash.TypeFullName);

        var lines = new List<string>();

        if (header.Length > 0)
            lines.Add(header);

        // A frame arrives either as "at Foo.Bar()" or as "Foo.Bar()" depending on which crash writer
        // produced the report, and printing "at at Foo.Bar()" makes the one thing an author reads first
        // look mangled.
        foreach (var frame in crash.Frames.Take(MaxFrames))
        {
            var text = Clean(frame);

            lines.Add("  at " + (text.StartsWith("at ", StringComparison.Ordinal) ? text[3..].TrimStart() : text));
        }

        if (crash.Frames.Count > MaxFrames)
            lines.Add($"  ... and {crash.Frames.Count - MaxFrames} more frames");

        var blocks = new List<BugReportBlock>
        {
            BugReportBlock.Paragraph(crash.Frames.Count == 0
                ? "The exception is, and this report carried no stack trace with it:"
                : "The exception is"),
            BugReportBlock.Fixed(lines)
        };

        if (FrameNote(crash) is { Length: > 0 } note)
            blocks.Add(BugReportBlock.Paragraph(note));

        return blocks;
    }

    // The one thing a plain stack trace loses. Two overloads that differ only in their parameters print
    // identically, so a reader looking at the frame above cannot tell which of them threw. BUTR's
    // enhanced trace carries the parameter count and the IL offset, and both are quoted rather than
    // interpreted.
    private static string FrameNote(BugReportCrash crash)
    {
        if (crash.Enhanced?.Fault is not { } fault)
            return string.Empty;

        var parameters = fault.IsParameterless
            ? $"Worth noting the frame is the parameterless `{Clean(fault.Signature)}`, not an overload that "
              + "takes arguments. Those print exactly the same way in an ordinary stack trace, so it is easy "
              + "to read past."
            : $"The frame is `{Clean(fault.Signature)}`, taking {Count(fault.ParameterCount, "argument")}.";

        return fault.IlOffset.Length > 0
            ? parameters + $" IL offset was {Clean(fault.IlOffset)}."
            : parameters;
    }

    private static IReadOnlyList<BugReportBlock> Decompiled(BugReportRequest request)
    {
        if (request.Decompiled is not { Succeeded: true, Source.Length: > 0 } method)
            return [];

        var file = Clean(Path.GetFileName(method.AssemblyPath));
        var member = Clean($"{Simple(method.TypeFullName)}.{method.MethodName}");

        var lead = file.Length > 0
            ? $"Decompiling {file}, this is what {member} does:"
            : $"Decompiled, this is what {member} does:";

        return
        [
            BugReportBlock.Paragraph(lead),
            BugReportBlock.Fixed(CleanLines(method.Source))
        ];
    }

    // Deliberately after the launches in weight and before them in position, exactly as the reference
    // report has it: it is why the mod was looked at, and it says in its own words that it is not the
    // evidence.
    private static IReadOnlyList<BugReportBlock> Static(BugReportRequest request)
    {
        if (request.StaticFinding.Trim().Length == 0)
            return [];

        return
        [
            BugReportBlock.Paragraph(
                $"For what it is worth, BEM also read the files on my disk without running them and found this: "
                + Clean(request.StaticFinding)),
            BugReportBlock.Paragraph(
                "That last part is static analysis of what is on disk, not something I watched happen. The "
                + "launches below are what I actually observed.")
        ];
    }

    private static IReadOnlyList<BugReportBlock> Isolation(
        BugReportRequest request,
        Search search,
        Dictionary<ModuleId, string> names,
        string subjectName)
    {
        var blocks = new List<BugReportBlock>();

        if (search.Snapshot is null)
        {
            blocks.Add(BugReportBlock.Paragraph(
                $"I have not isolated this by experiment. I have not yet run my load order with only "
                + $"{subjectName} disabled and everything else on, so nothing here shows from a launch that "
                + "removing it stops the crash. Everything above is what was captured and what BEM read off "
                + "my files."));

            return blocks;
        }

        blocks.Add(BugReportBlock.Paragraph(Verdict(search, names, subjectName)));

        if (search.Runs.Count == 0)
            return blocks;

        blocks.Add(BugReportBlock.Paragraph(
            "BEM ran a delta debugging search over my load order. Every line below is the result of a launch "
            + "that actually happened on my machine, not a guess."));

        blocks.Add(BugReportBlock.Numbered([.. search.Runs.Select((run, index) => Describe(run, index, names, search))]));

        if (Decisive(search, names) is { Length: > 0 } decisive)
            blocks.Add(BugReportBlock.Paragraph(decisive));

        if (Cleared(search, names) is { Length: > 0 } cleared)
            blocks.Add(BugReportBlock.Paragraph(cleared));

        return blocks;
    }

    // One sentence per result the engine can reach, and never the wrong one. Only Caused earns the word
    // caused, and only when the search named the module this report is about.
    private static string Verdict(Search search, Dictionary<ModuleId, string> names, string subjectName) =>
        search.Result switch
        {
            BisectionResult.Caused when search.NamesSubject && search.Cause.Count == 1 =>
                "How I know it is this mod and not something else:",

            BisectionResult.Caused when search.NamesSubject =>
                $"How I know it is this set of mods and not something else. The search settled on "
                + $"{List(names, search.Cause, MaxNamesInLine)} together: turning off any one of them stopped "
                + "the crash, so this is about how they meet rather than a ranking.",

            BisectionResult.Caused =>
                $"A search did run, and it named {List(names, search.Cause, MaxNamesInLine)} rather than "
                + $"{subjectName}. Nothing below shows {subjectName} responsible for this.",

            BisectionResult.NoModuleResponsible =>
                "A search did run, and the crash still happened with every mod turned off, so no mod is "
                + "responsible for it on my machine.",

            BisectionResult.NotInScope =>
                $"A search did run, but the crash still happened with all {search.ScopeCount} of the mods I was "
                + "searching turned off, so it is not inside that set. Nothing was settled about "
                + $"{subjectName} either way.",

            BisectionResult.RefusedNondeterministic =>
                $"BEM refused to search this one. The crash reproduced {Count(search.ControlReproduced, "time")} in "
                + $"{Count(search.ControlAttempts, "try", "tries")} with my load order as it is, and a search over something that "
                + "unreliable reads a run that happened not to crash as proof, which sends it into the wrong "
                + $"half. So this is not isolated: I have not shown by launch that {subjectName} is necessary.",

            BisectionResult.Inconclusive =>
                $"A search ran and did not settle on anything, so {subjectName} is not isolated by experiment. "
                + "The runs below are still real, and they are all I have.",

            BisectionResult.RefusedEmptyScope =>
                "No search ran: the set I handed BEM had nothing in it that a search could turn off, so this is "
                + "not isolated by experiment.",

            _ => $"The search is not finished, so nothing here is a result yet and {subjectName} is not "
                 + "isolated by experiment. The runs made so far:"
        };

    private static string Describe(
        BisectionRunRecord run,
        int index,
        Dictionary<ModuleId, string> names,
        Search search)
    {
        var outcome = run.Outcome switch
        {
            BisectionOutcome.Reproduced => "it crashed.",
            BisectionOutcome.NotReproduced => "clean boot.",
            _ => "that run told me nothing, so it was run again."
        };

        var recalled = run.FromRecord ? " (a run I had already made and told BEM about)" : string.Empty;
        var setup = Setup(run, names, search);

        return $"{setup}: {outcome}{recalled}";
    }

    private static string Setup(BisectionRunRecord run, Dictionary<ModuleId, string> names, Search search)
    {
        if (run.Disabled.Count == 0)
            return $"My load order exactly as it is, all {run.Enabled.Count} modules enabled";

        // The run that asks whether a mod is involved at all. Naming the mods it turned off would be
        // true and would read as an arbitrary subset, when what it actually did was turn off every one
        // a search is allowed to touch.
        if (!run.IsControl && run.UnderTest.Count == 0)
        {
            return search.Scoped
                ? $"Every one of the {run.Disabled.Count} mods I was searching off, everything else on, "
                  + $"{run.Enabled.Count} modules loading"
                : $"Every mod off, leaving only the game's own modules and the libraries the others need, "
                  + $"{run.Enabled.Count} modules loading";
        }

        if (run.Disabled.Count <= 8)
        {
            return $"{List(names, run.Disabled, MaxNamesInLine)} off, the other {run.Enabled.Count} modules on";
        }

        if (run.UnderTest.Count is > 0 and <= 8)
        {
            return $"Only {List(names, run.UnderTest, MaxNamesInLine)} on out of the mods being searched, the "
                + $"rest of them off, {run.Enabled.Count} modules loading in total";
        }

        return search.Scoped
            ? $"All {run.Disabled.Count} of the mods being searched off, {run.Enabled.Count} modules still on"
            : $"{run.Disabled.Count} modules off, {run.Enabled.Count} on";
    }

    // The proof, when there is one. Two runs whose enabled sets differ by exactly the mods the search
    // named: one crashed, one did not, and nothing else moved. That pair is the whole argument, so it
    // is found from the recorded runs rather than asserted, and when no such pair exists the report
    // says what the closest one actually was instead of pretending.
    private static string Decisive(Search search, Dictionary<ModuleId, string> names)
    {
        if (search.Cause.Count == 0 || !search.NamesSubject || search.Pair is not { } pair)
            return string.Empty;

        var (crashed, clean, difference) = pair;
        var first = Math.Min(crashed, clean) + 1;
        var second = Math.Max(crashed, clean) + 1;
        var cleanRun = search.Runs[clean];
        var others = Math.Max(cleanRun.Enabled.Count, 0);

        var exact = difference.Count == search.Cause.Count
            && difference.All(id => search.Cause.Contains(id));

        if (exact)
        {
            return $"Run {first} and run {second} are the pair that matters. The only difference between them "
                + $"is {List(names, search.Cause, MaxNamesInLine)}: on in run {crashed + 1} and the game "
                + $"crashed, off in run {clean + 1} and the game booted with the other {others} modules still "
                + "loading. Nothing else changed between the two.";
        }

        return $"The closest pair I have is run {first} and run {second}. They differ by "
            + $"{Count(difference.Count, "module")}, {List(names, [.. difference], MaxNamesInLine)} among them. Run "
            + $"{crashed + 1} crashed and run {clean + 1} booted clean with {others} modules loading.";
    }

    private static string Cleared(Search search, Dictionary<ModuleId, string> names)
    {
        var cleared = search.Cleared;

        return cleared.Count == 0
            ? string.Empty
            : "Cleared along the way, every one of them confirmed working on my setup by a boot that actually "
              + $"happened: {List(names, cleared, MaxClearedNames)}.";
    }

    private static IReadOnlyList<string> Setup(
        BugReportRequest request,
        string subjectName,
        string subjectVersion)
    {
        var environment = request.Environment;
        var lines = new List<string>();

        var game = Clean(environment.GameVersion);
        var platform = Clean(environment.PlatformFolder);

        if (game.Length > 0 || platform.Length > 0)
        {
            lines.Add(platform.Length > 0
                ? $"Mount & Blade II: Bannerlord {Or(game, "version unknown")}, {platform}"
                : $"Mount & Blade II: Bannerlord {game}");
        }

        if (Clean(environment.OperatingSystem) is { Length: > 0 } os)
            lines.Add(os);

        if (Clean(environment.LaunchedVia) is { Length: > 0 } via)
            lines.Add("Launched through " + via);

        foreach (var library in environment.Libraries)
            lines.Add($"{Clean(library.Name)} {Clean(library.Version)}".Trim());

        foreach (var assembly in request.SubjectAssemblies)
            lines.Add(Describe(assembly, subjectName, subjectVersion));

        if (request.SubjectAssemblies.Count == 0)
            lines.Add($"{subjectName} {subjectVersion}");

        var total = request.LoadOrder.Count;
        var enabled = request.LoadOrder.Count(entry => entry.IsEnabled);

        if (total > 0)
            lines.Add($"{enabled} of {total} modules in my load order enabled");

        return lines;
    }

    private static string Describe(BugReportAssembly assembly, string subjectName, string subjectVersion)
    {
        var parts = new List<string> { $"{subjectName} {subjectVersion}", Clean(assembly.FileName) };

        if (assembly.AssemblyVersion.Length > 0)
            parts.Add("assembly version " + Clean(assembly.AssemblyVersion));

        if (assembly.SizeBytes > 0)
            parts.Add($"{assembly.SizeBytes:N0} bytes");

        if (assembly.Written is { } written)
            parts.Add($"file date {written.ToLocalTime():yyyy-MM-dd}");

        return string.Join(", ", parts);
    }

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;

    // English, like everything else in the body, and correct at one: a report that says "1 module(s)"
    // is read as a machine's output before it is read as evidence.
    private static string Count(int count, string singular, string? plural = null) =>
        count == 1 ? $"{count} {singular}" : $"{count} {plural ?? singular + "s"}";

    // Only things BEM actually holds. Offering a stack trace nobody captured turns a courtesy into a
    // promise that cannot be kept.
    private static string OnRequest(BugReportRequest request, Search search)
    {
        var offers = new List<string>();

        if (request.Crash?.Enhanced is { IsEmpty: false })
            offers.Add("the full BUTR enhanced stack trace");

        if (request.Crash is { Frames.Count: > MaxFrames })
            offers.Add("the whole stack trace rather than the top of it");

        if (search.Runs.Count > 0)
            offers.Add($"the exact enabled module list for any of the {search.Runs.Count} launches above");

        if (request.Decompiled is { Succeeded: true })
            offers.Add("more of the decompiled assembly");

        offers.Add("my full load order");

        return $"Happy to supply {string.Join(", ", offers)}, or anything else that helps.";
    }

    private static BugReportQuality Judge(BugReportRequest request, Search search, string subjectName)
    {
        var present = new List<string>();
        var missing = new List<string>();

        if (request.Crash is { TypeFullName.Length: > 0 })
            present.Add(Strings.Current["Core.Report.Quality.Present.CrashReportWithType"]);
        else
            missing.Add(Strings.Current["Core.Report.Quality.Missing.CrashReport"]);

        if (request.Crash is { Frames.Count: > 0 })
            present.Add(Strings.Current["Core.Report.Quality.Present.StackTrace"]);
        else
            missing.Add(Strings.Current["Core.Report.Quality.Missing.StackTrace"]);

        if (search.Snapshot is null)
            missing.Add(Strings.Current["Core.Report.Quality.Missing.NoBisection"]);
        else if (search.Runs.Count > 0)
            present.Add(Strings.Current.Plural("Core.Report.Quality.Present.RecordedLaunches", search.Runs.Count));

        if (search.NamesSubject)
            present.Add(Strings.Current["Core.Report.Quality.Present.SearchNamedSubject"]);
        else if (search.Snapshot is not null)
            missing.Add(Strings.Current.Format("Core.Report.Quality.Missing.SearchNamedSubject", subjectName));

        if (search.Pair is not null)
            present.Add(Strings.Current["Core.Report.Quality.Present.PairOfRuns"]);
        else if (search.NamesSubject)
            missing.Add(Strings.Current["Core.Report.Quality.Missing.PairOfRuns"]);

        if (request.Decompiled is { Succeeded: true })
            present.Add(Strings.Current["Core.Report.Quality.Present.DecompiledMethod"]);

        if (request.StaticFinding.Trim().Length > 0)
            present.Add(Strings.Current["Core.Report.Quality.Present.StaticFinding"]);

        var strength = !search.NamesSubject
            ? BugReportStrength.Thin
            : search.Pair is not null && request.Crash is { TypeFullName.Length: > 0 }
                ? BugReportStrength.Strong
                : BugReportStrength.Moderate;

        return new BugReportQuality(
            strength,
            Headline(strength, search, subjectName),
            EnglishHeadline(strength, search, subjectName),
            present,
            missing,
            Strengthen(strength, search, subjectName));
    }

    private static string Headline(BugReportStrength strength, Search search, string subjectName) =>
        strength switch
        {
            BugReportStrength.Strong =>
                Strings.Current.Format("Core.Report.Quality.Headline.Strong", subjectName),

            BugReportStrength.Moderate =>
                Strings.Current.Format("Core.Report.Quality.Headline.Moderate", subjectName),

            _ when search.Snapshot is null =>
                Strings.Current.Format("Core.Report.Quality.Headline.ThinUnisolated", subjectName),

            _ => Strings.Current.Format("Core.Report.Quality.Headline.ThinNamed", subjectName)
        };

    // Headline's twin, in the four literals the catalog keys were written from. Nothing here may read
    // the catalog: the report body this feeds is pasted outside BEM, so it is English in every
    // language BEM runs in.
    private static string EnglishHeadline(BugReportStrength strength, Search search, string subjectName) =>
        strength switch
        {
            BugReportStrength.Strong =>
                $"a search proved {subjectName} is necessary for this crash on my machine, and the crash "
                + "itself was captured.",

            BugReportStrength.Moderate =>
                $"a search did show that removing {subjectName} stops the crash, but not everything a "
                + "reader would ask for is here.",

            _ when search.Snapshot is null =>
                $"{subjectName} has not been isolated by experiment, so this is what I saw rather than "
                + "proof that this mod is responsible.",

            _ => $"the search that ran did not name {subjectName}, so this is what I saw rather than proof "
                 + "that this mod is responsible."
        };

    private static string Strengthen(BugReportStrength strength, Search search, string subjectName) =>
        strength switch
        {
            BugReportStrength.Strong => Strings.Current["Core.Report.Quality.Strengthen.Strong"],

            BugReportStrength.Moderate when search.Pair is null =>
                Strings.Current.Format("Core.Report.Quality.Strengthen.ModerateOneMoreLaunch", subjectName),

            BugReportStrength.Moderate =>
                Strings.Current["Core.Report.Quality.Strengthen.ModerateCaptureCrash"],

            _ => Strings.Current.Format("Core.Report.Quality.Strengthen.Thin", subjectName)
        };

    // Everything a report is allowed to say about a search, read out of the saved session once so that
    // nothing downstream has to re-derive it and get it subtly different.
    private sealed record Search(
        BisectionSnapshot? Snapshot,
        BisectionResult Result,
        IReadOnlyList<ModuleId> Cause,
        IReadOnlyList<BisectionRunRecord> Runs,
        IReadOnlyList<ModuleId> Cleared,
        (int Crashed, int Clean, IReadOnlyList<ModuleId> Difference)? Pair,
        bool Scoped,
        int ScopeCount,
        int ControlAttempts,
        int ControlReproduced,
        string ReproductionStep,
        bool NamesSubject)
    {
        public static Search Read(BisectionSnapshot? snapshot, ModuleId subject)
        {
            if (snapshot is null)
                return new Search(null, BisectionResult.InProgress, [], [], [], null, false, 0, 0, 0, string.Empty, false);

            var result = Enum.TryParse<BisectionResult>(snapshot.Result, out var parsed)
                ? parsed
                : BisectionResult.InProgress;

            // Cause exists only for Caused. Reading Failing for any other result would turn "still under
            // test" into "named", which is the exact overstatement this feature must never make.
            var cause = result is BisectionResult.Caused
                ? snapshot.Failing.Select(id => new ModuleId(id)).ToList()
                : [];

            var runs = (snapshot.Runs ?? []).Select(run => new BisectionRunRecord(
                run.Number,
                run.IsControl,
                [.. run.Enabled.Select(id => new ModuleId(id))],
                [.. run.Disabled.Select(id => new ModuleId(id))],
                [.. run.UnderTest.Select(id => new ModuleId(id))],
                Enum.TryParse<BisectionOutcome>(run.Outcome, out var outcome) ? outcome : BisectionOutcome.Invalid,
                run.FromRecord)).ToList();

            return new Search(
                snapshot,
                result,
                cause,
                runs,
                ClearedBy(runs, cause),
                Closest(runs),
                snapshot.Scope is not null,
                snapshot.Scope?.Count ?? 0,
                snapshot.ControlAttempts,
                snapshot.ControlReproduced,
                snapshot.ReproductionStep ?? string.Empty,
                cause.Contains(subject));
        }

        // Generous, and no more generous than the runs allow: a module qualifies only if the search
        // actually turned it off at some point, which is what makes it something that was investigated,
        // and only if it was enabled during a launch that did not crash.
        private static IReadOnlyList<ModuleId> ClearedBy(
            IReadOnlyList<BisectionRunRecord> runs,
            IReadOnlyList<ModuleId> cause)
        {
            var investigated = new HashSet<ModuleId>(runs.SelectMany(run => run.Disabled));
            var onDuringCleanRun = new HashSet<ModuleId>(runs
                .Where(run => run.Outcome is BisectionOutcome.NotReproduced)
                .SelectMany(run => run.Enabled));

            return
            [
                .. investigated
                    .Where(onDuringCleanRun.Contains)
                    .Where(id => !cause.Contains(id))
                    .OrderBy(id => id.Value, StringComparer.OrdinalIgnoreCase)
            ];
        }

        // The two runs that sit closest together across the crash: one that crashed, one that did not,
        // with the fewest modules changed between them.
        private static (int, int, IReadOnlyList<ModuleId>)? Closest(IReadOnlyList<BisectionRunRecord> runs)
        {
            (int Crashed, int Clean, IReadOnlyList<ModuleId> Difference)? best = null;

            for (var i = 0; i < runs.Count; i++)
            {
                if (runs[i].Outcome is not BisectionOutcome.Reproduced)
                    continue;

                for (var j = 0; j < runs.Count; j++)
                {
                    if (runs[j].Outcome is not BisectionOutcome.NotReproduced)
                        continue;

                    var crashed = new HashSet<ModuleId>(runs[i].Enabled);
                    var clean = new HashSet<ModuleId>(runs[j].Enabled);
                    var difference = new HashSet<ModuleId>(crashed);
                    difference.SymmetricExceptWith(clean);

                    if (difference.Count == 0)
                        continue;

                    if (best is null || difference.Count < best.Value.Difference.Count)
                        best = (i, j, [.. difference]);
                }
            }

            return best;
        }
    }
}
