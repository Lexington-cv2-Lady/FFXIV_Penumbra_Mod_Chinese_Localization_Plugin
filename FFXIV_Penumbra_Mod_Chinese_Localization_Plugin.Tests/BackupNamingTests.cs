using System;
using System.IO;
using System.Linq;
using FFXIVPenumbraHanhua.Services;
using Xunit;

namespace FFXIV_Penumbra_Mod_Chinese_Localization_Plugin.Tests;

/// <summary>
/// 备份命名与轮转：新命名「{来源}_{yyyy-MM-dd_HH-mm-ss}.zip」的识别、时间解析、
/// 创建（含来源名）与按时间轮转保留；同时兼容旧命名「{时间戳}备份.zip」。
/// </summary>
public class BackupNamingTests : IDisposable
{
    private readonly string _dir;

    public BackupNamingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pmh_bak_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "meta.json"),
            "{\"FileVersion\":4,\"Name\":\"T\",\"Groups\":[{\"Name\":\"G\",\"Options\":[{\"Name\":\"O\"}]}]}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败不影响测试结论 */ }
    }

    [Theory]
    [InlineData("2026-09-29_05-38-48备份.zip", true)]
    [InlineData("后台汉化备份_2026-09-30_12-00-00.zip", true)]
    [InlineData("自动备份_2026-09-30_12-34-56.zip", true)]
    [InlineData("还原前备份_2026-09-30_00-00-01.zip", true)]
    [InlineData("meta.json", false)]
    [InlineData("group_001.json", false)]
    [InlineData("meta.json.json", false)]
    public void IsBackupZip_识别新旧命名(string name, bool expected)
        => Assert.Equal(expected, ModFileService.IsBackupZip(name));

    [Fact]
    public void BackupTime_解析新旧命名_非法返回null()
    {
        Assert.Equal(new DateTime(2026, 9, 29, 5, 38, 48),
            ModFileService.BackupTime("2026-09-29_05-38-48备份.zip"));
        Assert.Equal(new DateTime(2026, 9, 30, 12, 0, 0),
            ModFileService.BackupTime("后台汉化备份_2026-09-30_12-00-00.zip"));
        Assert.Null(ModFileService.BackupTime("meta.json"));
    }

    [Fact]
    public void CreateModZip_命名含来源且可被识别()
    {
        var svc = new ModFileService();
        var zip = svc.CreateModZip(_dir, 5, "后台汉化备份");

        Assert.NotNull(zip);
        var name = Path.GetFileName(zip!);
        Assert.StartsWith("后台汉化备份_", name);
        Assert.EndsWith(".zip", name);
        Assert.True(ModFileService.IsBackupZip(name));
        Assert.True(File.Exists(zip!));
    }

    [Fact]
    public void CreateModZip_轮转按解析时间只保留最新份数()
    {
        // 造 6 个不同时间的旧备份（含新旧两种命名混存），maxBackups=5
        File.WriteAllText(Path.Combine(_dir, "手动备份_2026-09-20_01-00-00.zip"), "x");
        File.WriteAllText(Path.Combine(_dir, "手动备份_2026-09-21_01-00-00.zip"), "x");
        File.WriteAllText(Path.Combine(_dir, "手动备份_2026-09-22_01-00-00.zip"), "x");
        File.WriteAllText(Path.Combine(_dir, "手动备份_2026-09-23_01-00-00.zip"), "x");
        File.WriteAllText(Path.Combine(_dir, "手动备份_2026-09-24_01-00-00.zip"), "x");
        File.WriteAllText(Path.Combine(_dir, "2026-09-25_01-00-00备份.zip"), "x"); // 旧命名

        var svc = new ModFileService();
        svc.CreateModZip(_dir, 5, "自动备份");

        var all = Directory.GetFiles(_dir)
            .Where(f => ModFileService.IsBackupZip(Path.GetFileName(f)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(5, all.Count); // 7 份 -> 轮转留 5
        Assert.Contains(all, n => n!.StartsWith("自动备份_")); // 最新的必留
        Assert.DoesNotContain("手动备份_2026-09-20_01-00-00.zip", all); // 最旧的被删
        Assert.DoesNotContain("手动备份_2026-09-21_01-00-00.zip", all);
    }
}
