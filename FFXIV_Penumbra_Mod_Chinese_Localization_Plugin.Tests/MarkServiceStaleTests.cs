using System;
using System.IO;
using FFXIVPenumbraHanhua.Services;
using Xunit;

namespace FFXIVPenumbraHanhua.Tests;

/// <summary>
/// 回归测试：MarkService 标记失效判据（审查方第三轮【低】②）。
/// 判据 = 有「已翻译」标记 且 曾译过（zh &gt; 0）且 英文可译字段数 ≥ 中文字段数（en &gt;= zh）。
/// 最关键的一条：**不能**把「绝大部分已译好、仅余零星英文」的正常模组误判为失效（否则反复重译）。
/// </summary>
public class MarkServiceStaleTests
{
    private static string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pmh_stale_{tag}_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary> 造一份词典目录并加载：terms 里 Attack/Defend 可译，黑名单含两个专名。 </summary>
    private static DictionaryService MakeDict(string dictDir)
    {
        File.WriteAllText(Path.Combine(dictDir, "我的翻译.json"),
            "{\"terms\":{\"Attack\":\"攻击\",\"Defend\":\"防御\"}}");
        File.WriteAllText(Path.Combine(dictDir, "单词黑名单.json"), "TBSE, YAB");
        var dict = new DictionaryService(new AppLog());
        Assert.True(dict.Load(dictDir));
        return dict;
    }

    /// <summary> 在 root 下建 modA（可选带「已翻译」标记）并写入 meta.json。 </summary>
    private static void MakeMod(string root, string metaJson, bool withMark)
    {
        var mod = Path.Combine(root, "modA");
        Directory.CreateDirectory(mod);
        if (withMark) File.WriteAllText(Path.Combine(mod, MarkService.MarkName), "mark");
        File.WriteAllText(Path.Combine(mod, "meta.json"), metaJson);
    }

    private static void Run(string tag, string metaJson, bool withMark, Action<bool> assert)
    {
        var root = NewTempDir(tag);
        var dictDir = NewTempDir(tag + "_dict");
        try
        {
            var dict = MakeDict(dictDir);
            MakeMod(root, metaJson, withMark);
            assert(new MarkService(() => root).IsStale("modA", dict, new ModFileService()));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(dictDir, true);
        }
    }

    [Fact]
    public void IsStale_True_WhenContentRestoredToEnglish()
    {
        // 1 个中文（曾被译过）+ 2 个可译英文（已被还原）→ en >= zh 且 zh > 0 → 失效
        Run("restored", "{\"FileVersion\":4,\"Groups\":[{\"Name\":\"Attack\"," +
                        "\"Options\":[{\"Name\":\"Defend\"},{\"Name\":\"格挡\"}]}]}", true,
            stale => Assert.True(stale));
    }

    [Fact]
    public void IsStale_False_WhenMostlyTranslated()
    {
        // 3 个中文 + 1 个零星可译英文 → en < zh → 正常模组，不得误清标记
        Run("mostly", "{\"FileVersion\":4,\"Groups\":[{\"Name\":\"技能组\"," +
                      "\"Options\":[{\"Name\":\"Attack\"},{\"Name\":\"格挡\"},{\"Name\":\"治疗\"}]}]}", true,
            stale => Assert.False(stale));
    }

    [Fact]
    public void IsStale_False_WhenNoChineseAtAll()
    {
        // zh = 0（从未译过 / 无残留中文）→ 不判失效（宁可漏判，不误杀）
        Run("nozh", "{\"FileVersion\":4,\"Groups\":[{\"Name\":\"Attack\"," +
                    "\"Options\":[{\"Name\":\"Defend\"}]}]}", true,
            stale => Assert.False(stale));
    }

    [Fact]
    public void IsStale_False_WithoutMark()
    {
        // 内容全英文但无「已翻译」标记 → 没有标记可清
        Run("nomark", "{\"FileVersion\":4,\"Groups\":[{\"Name\":\"Attack\"," +
                      "\"Options\":[{\"Name\":\"Defend\"}]}]}", false,
            stale => Assert.False(stale));
    }

    [Fact]
    public void IsStale_False_WhenOnlyBlacklistedProperNounsRemain()
    {
        // 中文 1 个 + 黑名单专名 2 个（不计入 en）→ en = 0 → 不失效
        Run("blacklist", "{\"FileVersion\":4,\"Groups\":[{\"Name\":\"中文组名\"," +
                         "\"Options\":[{\"Name\":\"TBSE\"},{\"Name\":\"YAB\"}]}]}", true,
            stale => Assert.False(stale));
    }
}
