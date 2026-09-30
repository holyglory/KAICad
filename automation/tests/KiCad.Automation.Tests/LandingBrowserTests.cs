using System.Text.Json;
using KiCad.Automation.Downloads;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass, DoNotParallelize]
public sealed class LandingBrowserTests
{
    [TestMethod, TestCategory("ExternalIntegration"), TestCategory("LandingBrowser")]
    public async Task PublicThemesReleaseChoicesAndKeyboardActionsPersist()
    {
        string origin = Environment.GetEnvironmentVariable("KICAD_LANDING_URL")
            ?? throw new AssertFailedException("An explicit landing-page origin is required.");
        Assert.IsTrue(Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https");
        string session = "lp-" + Guid.NewGuid().ToString("N")[..16];
        string executable = Environment.GetEnvironmentVariable("KICAD_BROWSER_EXECUTABLE") ?? "agent-browser";
        bool started = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await Browser("open", origin.TrimEnd('/') + "/?theme=dark&platform=win-x64");
            started = true;
            await Browser("set", "viewport", "390", "844");
            await State("document.documentElement.dataset.theme", "dark");
            await Browser("focus", "#theme-toggle");
            await Browser("press", "Enter");
            await State("document.documentElement.dataset.theme", "light");
            await Browser("reload");
            await State("document.documentElement.dataset.theme", "light");
            await State("localStorage.getItem('kicad-theme')", "light");
            await State("document.querySelector('#recommended a').getAttribute('href').startsWith('/artifacts/')", true);
            await Browser("click", "a[href='#releases']");
            await State("document.querySelector('#release-list').open", true);
            await Browser("select", "#platform-filter", "win-x64");
            await State("[...document.querySelectorAll('.release-row')].filter(e=>!e.hidden).every(e=>e.dataset.platform==='win-x64')", true);
            await State("[...document.querySelectorAll('.release-row')].filter(e=>!e.hidden).length > 1", true);
            await Browser("select", "#platform-filter", "all");
            await State("[...document.querySelectorAll('.release-row')].every(e=>!e.hidden)", true);
            await Browser("click", "#release-list > summary");
            await State("document.querySelector('#release-list').open", false);
            await Browser("click", ".install-notes summary");
            await State("document.querySelector('.install-notes').open", true);
            await Browser("click", ".install-notes summary");
            await State("document.querySelector('.install-notes').open", false);
            await State("document.documentElement.scrollWidth <= innerWidth", true);
            await Browser("set", "viewport", "1440", "1700");
            await Browser("click", "#theme-toggle");
            await State("document.documentElement.dataset.theme", "dark");
            await Browser("reload");
            await State("document.documentElement.dataset.theme", "dark");
            await State("[...document.images].every(i=>i.complete && i.naturalWidth>0)", true);
            string? candidatePath = Environment.GetEnvironmentVariable("KICAD_CAPTION_CANDIDATE_CATALOGUE");
            string platform = Environment.GetEnvironmentVariable("KICAD_LANDING_PLATFORM") ?? "linux-x64";
            string? candidateName = Environment.GetEnvironmentVariable("KICAD_LANDING_CANDIDATE_NAME");
            string? candidateVersion = Environment.GetEnvironmentVariable("KICAD_LANDING_CANDIDATE_VERSION");
            if (candidatePath is not null)
            {
                var catalogue = await DownloadCatalogue.LoadAsync(candidatePath, deadline.Token);
                var candidate = catalogue.Manifest.Artifacts.Single(item => item.Platform == platform);
                candidateName = candidate.FileName; candidateVersion = candidate.Version;
            }
            if (candidateName is not null)
            {
                Assert.IsNotNull(candidateVersion);
                foreach (string theme in new[] { "light", "dark" })
                {
                    await Browser("open", origin.TrimEnd('/') + "/?theme=" + theme + "&platform=" + Uri.EscapeDataString(platform));
                    await State("document.documentElement.dataset.theme", theme);
                    await State("document.querySelector('#recommended a').getAttribute('href')",
                        "/artifacts/" + candidateName);
                    await Browser("click", "a[href='#releases']");
                    await Browser("select", "#platform-filter", platform);
                    await State("[...document.querySelectorAll('.release-row')].filter(e=>!e.hidden)"
                        + ".some(e=>e.textContent.includes(" + JsonSerializer.Serialize(candidateVersion)
                        + ") && [...e.querySelectorAll('a')].some(a=>a.getAttribute('href')==="
                        + JsonSerializer.Serialize("/artifacts/" + candidateName) + "))", true);
                }
            }
            Assert.IsTrue(string.IsNullOrWhiteSpace(await Browser("errors")), "The browser reported page errors.");
            await Browser("click", "a.source-link");
            await State("location.origin + location.pathname", "https://github.com/holyglory/KAICad");
            await State("document.title.includes('KAICad')", true);
        }
        finally { if (started) await Browser("close"); }

        async Task State<T>(string expression, T expected)
        {
            // Observe after actual browser events paint; no arbitrary sleep.
            string output = await Browser("eval", "new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(" + expression + "))))");
            Assert.AreEqual(expected, JsonSerializer.Deserialize<T>(output));
        }
        async Task<string> Browser(params string[] arguments)
        {
            var result = await WindowsLauncherTests.Invoke(executable, ["--namespace", "kicad-governed-web", "--session", session, .. arguments],
                Environment.CurrentDirectory, arguments[0] == "close" ? CancellationToken.None : deadline.Token, input: null);
            Assert.AreEqual(0, result.ExitCode, result.Error);
            return result.Output.Trim();
        }
    }
}
