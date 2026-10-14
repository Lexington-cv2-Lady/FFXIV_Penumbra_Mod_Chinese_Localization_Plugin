using System.Collections.Generic;
using Xunit;

namespace FFXIVPenumbraHanhua.Tests;

/// <summary>
/// 回归测试：统计行「打开」按钮 ID 唯一性（ImGui ID 冲突防护）。
/// <para>
/// 背景（2026-10-14 用户报障）：「目录和词典管理」窗口的「各来源词条统计」有 5 行，
/// 每行一个「打开」按钮。旧写法标签是光秃秃一个 <c>"打开"</c>——ImGui 拿**标签文本**当 ID 源，
/// 5 个同标签 → ID 完全相同 → 点击全部错位到第一行，表现为「只有我的翻译.json 点得动，其余四个毫无反应」。
/// </para>
/// 同文件的 <c>DrawDirectoryRow</c> 一直带着 <c>##{idPrefix}Open</c> 后缀，规矩本是有的，只是统计行漏了。
/// 生成逻辑收口在 <see cref="UiIds.StatOpen"/>（放在不依赖 Dalamud 的静态类里，好让本测试直接验证）。
/// </summary>
public class DictionaryStatButtonIdTests
{
    /// <summary> 五条统计行的显示名（与 Draw() 里的调用一一对应）。 </summary>
    private static readonly string[] StatNames =
    {
        "我的翻译.json（mods + terms）",
        "个性翻译.json（词级覆盖层）",
        "wiki_术语对照\\ 分类文件夹",
        "AI知识库\\AI知识库.json（只读底料）",
        "单词黑名单.json（保留英文专名）",
    };

    [Fact]
    public void 五个统计行的打开按钮ID必须互不相同()
    {
        var ids = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var n in StatNames)
        {
            var id = UiIds.StatOpen(n);
            Assert.True(ids.Add(id), $"ID 重复：{id}（来源「{n}」）——ImGui 会把它当成同一个按钮，点击错位");
        }
        Assert.Equal(StatNames.Length, ids.Count);
    }

    [Fact]
    public void ID必须带stat后缀_不能是光秃秃的打开()
    {
        foreach (var n in StatNames)
        {
            var id = UiIds.StatOpen(n);
            Assert.StartsWith("打开##stat_", id);
            Assert.NotEqual("打开", id); // 反例：正是这个裸标签造成了 ID 冲突
        }
    }

    [Fact]
    public void 同名生成同ID_不同名生成不同ID()
    {
        Assert.Equal(UiIds.StatOpen("a"), UiIds.StatOpen("a"));
        Assert.NotEqual(UiIds.StatOpen("a"), UiIds.StatOpen("b"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 名称为空时不抛_且有确定占位(string? name)
    {
        var id = UiIds.StatOpen(name!);
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.StartsWith("打开##stat_", id);
    }
}
