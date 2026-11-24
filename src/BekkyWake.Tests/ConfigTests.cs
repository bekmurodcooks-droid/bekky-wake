using System.Text.Json;
using BekkyWake.Core.Models;
using BekkyWake.Core.Store;
using Xunit;

namespace BekkyWake.Tests;

public sealed class ConfigTests
{
    [Fact]
    public void Config_ToleratesMissingKeysAndUnknownEnumValues()
    {
        const string json = """
        {
          "trayMode": "futureUnknownMode",
          "lowPowerFloor": 55
        }
        """;

        var cfg = JsonSerializer.Deserialize<Config>(json, JsonStore.JsonOptions);
        Assert.NotNull(cfg);
        Assert.Equal(TrayMode.Popover, cfg!.TrayMode);
        Assert.Equal(55, cfg.LowPowerFloor);
        Assert.Equal(10, cfg.EndFloor);
        Assert.True(cfg.MuteOnLidClose);
        Assert.True(cfg.KeepScreenAwake);
        Assert.False(cfg.LowPowerOnLidClose);
    }

    [Fact]
    public void Floors_EnforceOrderingBetweenEndAndLowPower()
    {
        var cfg = new Config();
        cfg.SetEndFloor(50);
        Assert.Equal(50, cfg.EndFloor);
        Assert.Equal(55, cfg.LowPowerFloor);

        cfg.SetLowPowerFloor(40);
        Assert.Equal(40, cfg.LowPowerFloor);
        Assert.Equal(35, cfg.EndFloor);

        // Hand-edited out-of-order floors normalize by raising LowPowerFloor.
        cfg.LowPowerFloor = 20;
        cfg.EndFloor = 30;
        var note = cfg.NormalizeFloors();
        Assert.NotNull(note);
        Assert.Equal(30, cfg.EndFloor);
        Assert.Equal(35, cfg.LowPowerFloor);
    }

    [Fact]
    public void AppRules_AgentListAndHardDeniedTakePrecedenceOverFreezeList()
    {
        var cfg = new Config();
        cfg.FreezeList.Add("Code.exe");
        Assert.Equal(AppRule.KeepRunning, cfg.RuleFor("Code"));

        cfg.SetRule(AppRule.Freeze, "explorer");
        Assert.Equal(AppRule.KeepRunning, cfg.RuleFor("explorer"));
    }
}
