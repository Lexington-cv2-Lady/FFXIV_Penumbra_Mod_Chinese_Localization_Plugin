using System;
using System.IO;
using FFXIVPenumbraHanhua.Services;
using Xunit;

namespace FFXIVPenumbraHanhua.Tests;

/// <summary>
/// 回归测试：自动重覆盖（更新重覆盖）整改三项（2026-10-10）。
/// ① <see cref="ModSignatureTracker.ClearBaselines"/>：词典重载后清基线，让新词条能重新入队评估；
/// ② <see cref="ImportService.CanTranslate"/>：整串查表未命中时回退分块翻译（Translator），
///    覆盖 "WAR Animation" / "WHM/BLM" 这类「词典只存单词、没存整串」的复合文本与页名；
/// ③ 边界：已中文 / 黑名单 / 纯符号仍不得被判为可翻译（避免自动回填乱改用户文件）。
/// 用例直接调用产品代码本身，不另建一套复刻逻辑。
/// </summary>
public class AutoReHanhuaCoverageTests
{
    private static string MakeTempDict()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pmh_rehan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        // 单词黑名单：命中词保留英文不译
        File.WriteAllText(Path.Combine(dir, "单词黑名单.json"), "ÆTHERIS");

        // 我的翻译：只存「单词」，故意不存 "WAR Animation" / "WHM/BLM" 这样的整串
        File.WriteAllText(Path.Combine(dir, "我的翻译.json"),
            "{\"terms\":{\"WAR\":\"战士\",\"Animation\":\"动画\",\"WHM\":\"白魔法师\",\"BLM\":\"黑魔法师\"}}");

        return dir;
    }

    // ============ ① 清基线后重新入队 ============

    [Fact]
    public void ClearBaselines_清空后所有模组重新入队()
    {
        var t = new ModSignatureTracker();

        // 首轮：无基线 -> 全部入队
        t.Observe("modA", (100, 10));
        t.Observe("modB", (200, 20));
        Assert.True(t.HasPending);

        // 消费并同步基线（模拟处理完成后 SetBaseline）
        t.Drain(10);
        t.SetBaseline("modA", (100, 10));
        t.SetBaseline("modB", (200, 20));
        Assert.False(t.HasPending);

        // 签名未变 -> 不入队
        t.Observe("modA", (100, 10));
        Assert.False(t.HasPending);

        // 词典重载：清空基线 -> 即使文件没动，也全部重新入队（这正是新增词条能生效的关键）
        t.ClearBaselines();
        Assert.Equal(0, t.BaselineCount);
        t.Observe("modA", (100, 10));
        t.Observe("modB", (200, 20));
        Assert.True(t.HasPending);
        Assert.Equal(2, t.Drain(10).Count);
    }

    // ============ ② 整串未命中 -> 分块翻译兜底 ============

    [Fact]
    public void CanTranslate_复合词整串未命中_靠分块翻译判为可翻译()
    {
        var dir = MakeTempDict();
        try
        {
            var dict = new DictionaryService(new AppLog());
            Assert.True(dict.Load(dir));

            // 词典里只有 WAR / Animation，没有 "WAR Animation" 整串
            Assert.Null(dict.LookupMod("meta.json||Name||WAR Animation"));
            Assert.Null(dict.LookupTerm("WAR Animation"));

            // 但自动重覆盖仍应认得它「有得翻」，否则复合文本永远不会被写回
            Assert.True(ImportService.CanTranslate(dict, "meta.json", "Name", "WAR Animation"));
            // 页名同理（斜杠复合）
            Assert.True(ImportService.CanTranslate(dict, "meta.json", "Page", "WHM/BLM"));
            // 整串命中当然也算
            Assert.True(ImportService.CanTranslate(dict, "meta.json", "Name", "WAR"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CanTranslate_词典完全翻不出的文本_判为不可翻译()
    {
        var dir = MakeTempDict();
        try
        {
            var dict = new DictionaryService(new AppLog());
            Assert.True(dict.Load(dir));

            // 词典里一个词都没有的句子 -> 不动它（避免自动回填乱改模组原文）
            Assert.False(ImportService.CanTranslate(dict, "meta.json", "Name", "Some Unknown Phrase"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ============ ③ 边界：黑名单 / 已中文 / 空串 不得被兜底带偏 ============

    [Fact]
    public void CanTranslate_黑名单与已中文_即使分块能翻也不动()
    {
        var dir = MakeTempDict();
        try
        {
            var dict = new DictionaryService(new AppLog());
            Assert.True(dict.Load(dir));

            // 黑名单专名：即便 Translator 也能拆出 WAR，也不能翻
            Assert.True(dict.IsBlacklisted("ÆTHERIS"));

            // 已中文条目：永不重复覆盖
            Assert.False(ImportService.CanTranslate(dict, "meta.json", "Name", "战士"));

            // 空串
            Assert.False(ImportService.CanTranslate(dict, "meta.json", "Name", ""));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}