using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FFXIVPenumbraHanhua.Services;
using Xunit;

namespace FFXIVPenumbraHanhua.Tests;

/// <summary>
/// 回归测试：wiki 抓取的反风控改造（节流间隔计算 + 续抓进度读写）。
/// 背景：2026-10 实测灰机 wiki 的 Cloudflare 风控升级到「速率/行为」维度，密集探测会把出口 IP 拉黑数十分钟到数小时，
/// 故抓取侧加入节流/退避/全局中止/续抓。<see cref="WikiExportService.NextDelayMs"/>、LoadProgress/SaveProgress
/// 是这套机制里可纯函数化/可落盘验证的部分，改动必须配用例（通用工作记忆 §B.26 高危逻辑配回归用例）。
/// 全部用例直接调用产品代码本身，不用另一套语言复刻逻辑（§B.33）。
/// </summary>
public class WikiExportServiceTests
{
    // ============ A. 节流间隔计算 ============

    [Fact]
    public void NextDelayMs_下次时间已过_返回0()
    {
        var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(0, WikiExportService.NextDelayMs(now.AddSeconds(-1), now));
        Assert.Equal(0, WikiExportService.NextDelayMs(now, now));
    }

    [Fact]
    public void NextDelayMs_下次时间未到_返回向上取整毫秒()
    {
        var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(700, WikiExportService.NextDelayMs(now.AddMilliseconds(700), now));
        // 0.5 毫秒这类小数必须向上取整，不能截断成 0（否则节流失效）
        Assert.Equal(1, WikiExportService.NextDelayMs(now.AddTicks(TimeSpan.TicksPerMillisecond / 2), now));
    }

    // ============ B. 续抓进度读写 ============

    [Fact]
    public void Progress_无文件与空目录_返回空()
    {
        using var dir = new TempDir();
        Assert.Empty(WikiExportService.LoadProgress(dir.Path));
    }

    [Fact]
    public void Progress_写入后可原样读回()
    {
        using var dir = new TempDir();
        var map = new System.Collections.Generic.Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["Item"] = ("Item/A", ""),
            ["Quest"] = ("", "20261010120000|xyz")
        };
        WikiExportService.SaveProgress(dir.Path, map);

        var loaded = WikiExportService.LoadProgress(dir.Path);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(("Item/A", ""), loaded["Item"]);
        Assert.Equal(("", "20261010120000|xyz"), loaded["Quest"]);
    }

    [Fact]
    public void Progress_清空后删除进度文件()
    {
        using var dir = new TempDir();
        var map = new System.Collections.Generic.Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["Item"] = ("Item/A", "")
        };
        WikiExportService.SaveProgress(dir.Path, map);
        Assert.True(File.Exists(Path.Combine(dir.Path, "_抓取进度.json")));

        map.Clear();
        WikiExportService.SaveProgress(dir.Path, map);
        Assert.False(File.Exists(Path.Combine(dir.Path, "_抓取进度.json")));
    }

    [Fact]
    public void Progress_超过有效期_丢弃并从头抓取()
    {
        using var dir = new TempDir();
        // 直接伪造一份 13 小时前的进度（> ProgressValidHours=12）
        var stale = new JsonObject
        {
            ["savedAt"] = DateTime.UtcNow.AddHours(-13).ToString("o", CultureInfo.InvariantCulture),
            ["items"] = new JsonObject { ["Item"] = new JsonObject { ["gapcontinue"] = "Item/A", ["rvcontinue"] = "" } }
        };
        File.WriteAllText(Path.Combine(dir.Path, "_抓取进度.json"),
            stale.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

        Assert.Empty(WikiExportService.LoadProgress(dir.Path));
    }

    [Fact]
    public void Progress_文件损坏_不抛异常且返回空()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "_抓取进度.json"), "{ 这不是 JSON", Encoding.UTF8);
        Assert.Empty(WikiExportService.LoadProgress(dir.Path));
    }

    /// <summary> 临时目录（用例结束自动删除）。 </summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wiki_prog_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { /* 清理失败不影响用例 */ }
        }
    }
}
