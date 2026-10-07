using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Hosting;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class McpHostConfigurationContractTests
{
    [TestMethod]
    public void DefaultJsonSourcesDoNotRegisterReloadWatchersAndCliStateWins()
    {
        const string stateDirectory = "/tmp/kicad-host-config-cli-state";
        HostApplicationBuilder builder = McpHostConfiguration.CreateBuilder(
            ["--state-directory", stateDirectory]);

        JsonConfigurationSource[] jsonSources = builder.Configuration.Sources
            .OfType<JsonConfigurationSource>().ToArray();
        Assert.IsTrue(jsonSources.Length > 0, "The ordinary host must still load JSON configuration sources.");
        Assert.IsTrue(jsonSources.All(source => source.ReloadOnChange != true),
            "Default host JSON sources must not allocate reload watchers.");
        Assert.AreEqual(stateDirectory, builder.Configuration["state-directory"],
            "The application CLI source must retain its normal final precedence.");

        using IHost host = builder.Build();
        Assert.IsNotNull(host.Services.GetService(typeof(IHostApplicationLifetime)));
    }

    [TestMethod]
    public void ReservedReloadSwitchCannotBeReenabledByEarlierArguments()
    {
        string[] rewritten = McpHostConfiguration.WithReloadWatchersDisabled(
            ["--hostBuilder:reloadConfigOnChange=true", "--state-directory=/tmp/host-state"]);

        Assert.AreEqual("--hostBuilder:reloadConfigOnChange=true", rewritten[0]);
        Assert.AreEqual("--state-directory=/tmp/host-state", rewritten[1]);
        Assert.AreEqual("--hostBuilder:reloadConfigOnChange=false", rewritten[^1]);
    }
}
