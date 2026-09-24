using System;
using System.IO;
using FFXIVPenumbraHanhua.Services;
using Xunit;

namespace FFXIVPenumbraHanhua.Tests;

/// <summary>
/// 回归测试：DictionaryService 查询优先级与黑名单（审查方参考方案②）。
/// 优先级：单词黑名单 > 个性翻译(LookupCustom) > 我的翻译(LookupTerm) > wiki。
/// 通过临时词典目录走真实 Load，锁定各层语义不被回归破坏。
/// </summary>
public class DictionaryServicePriorityTests
{
    private static string MakeTempDict()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pmh_dict_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        // 单词黑名单（TextListFile 格式：逗号分隔、# 注释），后缀虽为 .json 实为清单文本
        File.WriteAllText(Path.Combine(dir, "单词黑名单.json"), "DPS, HP");

        // 我的翻译：terms 层（英文:中文）
        File.WriteAllText(Path.Combine(dir, "我的翻译.json"), "{\"terms\":{\"Attack\":\"我的攻击\"}}");

        // 个性翻译：最高覆盖层（数组 [{原文,译文}]）
        File.WriteAllText(Path.Combine(dir, "个性翻译.json"), "[{\"原文\":\"Attack\",\"译文\":\"个性攻击\"}]");

        // wiki 术语对照文件夹
        var wikiDir = Path.Combine(dir, "wiki_术语对照");
        Directory.CreateDirectory(wikiDir);
        File.WriteAllText(Path.Combine(wikiDir, "wiki_术语对照_黑名单.json"), "BlockedWiki");
        File.WriteAllText(Path.Combine(wikiDir, "Wiki.json"), "{\"terms\":{\"WikiWord\":\"维基译\"}}");

        return dir;
    }

    [Fact]
    public void Load_Priority_And_Blacklist_WorkAsDesigned()
    {
        var dir = MakeTempDict();
        try
        {
            var dict = new DictionaryService(new AppLog());
            Assert.True(dict.Load(dir));

            // 个性翻译（最高覆盖层）压过 我的翻译 的 terms 层
            Assert.Equal("个性攻击", dict.LookupCustom("Attack"));
            Assert.Equal("我的攻击", dict.LookupTerm("Attack"));

            // wiki 术语层正常收录
            Assert.Equal("维基译", dict.LookupTerm("WikiWord"));

            // 黑名单词不被收入任何词表
            Assert.True(dict.IsBlacklisted("DPS"));
            Assert.Null(dict.LookupTerm("DPS"));
            Assert.Null(dict.LookupCustom("DPS"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Load_MissingDirectory_ReturnsFalse()
    {
        var dict = new DictionaryService(new AppLog());
        Assert.False(dict.Load(Path.Combine(Path.GetTempPath(), "pmh_nonexist_" + Guid.NewGuid().ToString("N"))));
    }
}
