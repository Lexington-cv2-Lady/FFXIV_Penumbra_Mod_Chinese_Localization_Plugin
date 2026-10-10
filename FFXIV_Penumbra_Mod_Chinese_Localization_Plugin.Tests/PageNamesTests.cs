using System;
using System.Collections.Generic;
using System.IO;
using FFXIVPenumbraHanhua.Services;
using Xunit;

namespace FFXIVPenumbraHanhua.Tests;

/// <summary>
/// 回归测试：FV4 多页面名（meta.json 顶层 PageNames，游戏内「页码」下拉显示的名字）。
/// 解析须按页索引对齐（乱序键、缺失页占位）；写回只更新已有 PageNames 节点里的页，
/// 不为无页面模组凭空造字段；pageNames 为空时不动原文件。
/// </summary>
public class PageNamesTests
{
    private static string NewModDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pmh_pn_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly string MetaWithPages =
        "{\"FileVersion\":4,\"Name\":\"M\"," +
        "\"PageNames\":{\"2\":\"WHM/BLM\",\"0\":\"WAR\",\"1\":\"DRK\"}," +
        "\"Groups\":[{\"Name\":\"G\",\"Options\":[{\"Name\":\"O\"}]}]}";

    [Fact]
    public void ParseMeta_ReadsPageNames_ByIndex()
    {
        var dir = NewModDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "meta.json"), MetaWithPages);

            var files = new ModFileService().ReadModFiles(dir);

            Assert.Single(files);
            var meta = files[0];
            Assert.True(meta.IsMeta);
            Assert.Equal(3, meta.PageNames.Count);
            Assert.Equal("WAR", meta.PageNames[0]);
            Assert.Equal("DRK", meta.PageNames[1]);
            Assert.Equal("WHM/BLM", meta.PageNames[2]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ParseMeta_NoPageNames_Node_EmptyList()
    {
        var dir = NewModDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "meta.json"),
                "{\"FileVersion\":4,\"Groups\":[{\"Name\":\"G\",\"Options\":[]}]}");

            var files = new ModFileService().ReadModFiles(dir);

            Assert.Single(files);
            Assert.Empty(files[0].PageNames);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ParseMeta_NonNumericKeys_Ignored()
    {
        var dir = NewModDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "meta.json"),
                "{\"FileVersion\":4,\"PageNames\":{\"0\":\"WAR\",\"x\":\"bad\",\"-1\":\"neg\"}," +
                "\"Groups\":[{\"Name\":\"G\",\"Options\":[]}]}");

            var files = new ModFileService().ReadModFiles(dir);

            Assert.Single(files);
            var meta = files[0];
            Assert.Single(meta.PageNames); // 只有合法页索引 "0" 入表
            Assert.Equal("WAR", meta.PageNames[0]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WriteTranslation_UpdatesExistingPageNames_AndKeepsOthers()
    {
        var dir = NewModDir();
        try
        {
            var path = Path.Combine(dir, "meta.json");
            File.WriteAllText(path, MetaWithPages);
            var info = new ModFileService().ReadModFiles(dir)[0];

            var ok = new ModFileService().WriteTranslation(path, info,
                new Dictionary<int, string> { [0] = "战士组" },
                new Dictionary<(int, int), string> { [(0, 0)] = "选项甲" },
                new Dictionary<(int, int), string>(),
                new Dictionary<int, string> { [1] = "暗黑骑士" });

            Assert.True(ok);
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            // 组名/选项名照常写回
            Assert.Equal("战士组", root.GetProperty("Groups")[0].GetProperty("Name").GetString());
            Assert.Equal("选项甲", root.GetProperty("Groups")[0].GetProperty("Options")[0].GetProperty("Name").GetString());
            // 页名：提及的页更新，未提及的页不动
            Assert.Equal("暗黑骑士", root.GetProperty("PageNames").GetProperty("1").GetString());
            Assert.Equal("WAR", root.GetProperty("PageNames").GetProperty("0").GetString());
            Assert.Equal("WHM/BLM", root.GetProperty("PageNames").GetProperty("2").GetString());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WriteTranslation_NoPageNamesNode_DoesNotCreateOne()
    {
        var dir = NewModDir();
        try
        {
            var path = Path.Combine(dir, "meta.json");
            File.WriteAllText(path, "{\"FileVersion\":4,\"Groups\":[{\"Name\":\"G\",\"Options\":[]}]}");
            var info = new ModFileService().ReadModFiles(dir)[0];

            var ok = new ModFileService().WriteTranslation(path, info,
                new Dictionary<int, string>(), new Dictionary<(int, int), string>(),
                new Dictionary<(int, int), string>(),
                new Dictionary<int, string> { [0] = "战士" });

            Assert.True(ok);
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Assert.False(doc.RootElement.TryGetProperty("PageNames", out _)); // 不凭空造字段
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WriteTranslation_EmptyPageNames_LeavesFileUntouched()
    {
        var dir = NewModDir();
        try
        {
            var path = Path.Combine(dir, "meta.json");
            File.WriteAllText(path, MetaWithPages);
            var before = File.ReadAllText(path);
            var info = new ModFileService().ReadModFiles(dir)[0];

            var ok = new ModFileService().WriteTranslation(path, info,
                new Dictionary<int, string>(), new Dictionary<(int, int), string>(),
                new Dictionary<(int, int), string>(),
                new Dictionary<int, string>()); // 空：不更新任何页名

            Assert.True(ok);
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("WAR", doc.RootElement.GetProperty("PageNames").GetProperty("0").GetString());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
