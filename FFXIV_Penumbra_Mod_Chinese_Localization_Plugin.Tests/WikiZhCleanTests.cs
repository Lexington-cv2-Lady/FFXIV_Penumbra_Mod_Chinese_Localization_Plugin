using FFXIVPenumbraHanhua.Services;
using Xunit;

namespace FFXIVPenumbraHanhua.Tests;

/// <summary>
/// 回归测试：wiki 中文名清洗 <see cref="WikiExportService.CleanWikiZh"/>。
/// <para>
/// 背景（2026-10-14 用户报障）：灰机 wiki 的命名习惯是「&lt;前缀&gt;:&lt;英文曲名&gt; -&lt;中文译名&gt;-」，
/// 用连字符把中文译名括起来当编者注。物品「Answers Orchestrion Roll」的中文名因此是
/// <c>管弦乐琴乐谱:Answers\u3000-生死答问-</c>，而国服本地化译名其实只有「生死答问」
/// （wiki「Endwalker/Answers - Piano」页自述）。原样入库会让游戏内选项出现中英混杂。
/// </para>
/// 规则：① 全角空格 U+3000 → 普通空格；② 把「&lt;英文曲名&gt; -&lt;中文译名&gt;-」整段替换成「&lt;中文译名&gt;」——
/// **保留中文、剥掉英文曲名与编者连字符**。反向做法（只剥连字符留下英文）是错的，
/// 会把译名洗成纯英文，比不洗更糟（妾身第一版就栽在这，被测试逮住）。
/// 安全边界：正则要求英文曲名在前，故「A级精英杀手4」这类等级/型号标识、
/// 「生死答问 - 回响」这类本身就用连字符的中文主副标题都不会被误伤。
/// </para>
public class WikiZhCleanTests
{
    // ===== 用户报障的那两条（正例）=====

    [Fact]
    public void 全角空格加连字符括注_取中文译名()
        => Assert.Equal("管弦乐琴乐谱:生死答问",
            WikiExportService.CleanWikiZh("管弦乐琴乐谱:Answers\u3000-生死答问-"));

    [Fact]
    public void 半角连字符括注_同样取中文译名()
        => Assert.Equal("陈旧的乐谱:生死答问",
            WikiExportService.CleanWikiZh("陈旧的乐谱:Answers -生死答问-"));

    [Fact]
    public void 其他曲名同款格式_一并生效()
        => Assert.Equal("管弦乐琴乐谱:千年龙诗",
            WikiExportService.CleanWikiZh("管弦乐琴乐谱:Dragonsong -千年龙诗-"));

    // ===== 全角空格折叠 =====

    [Fact]
    public void 全角空格折叠为普通空格()
        => Assert.Equal("a b", WikiExportService.CleanWikiZh("a\u3000b"));

    [Fact]
    public void 首尾全角空格被清掉()
        => Assert.Equal("abc", WikiExportService.CleanWikiZh("\u3000abc\u3000"));

    // ===== 反例：不许误伤正常译名 =====

    [Fact]
    public void 正常纯中文译名_原样返回()
        => Assert.Equal("欧米茄M架势", WikiExportService.CleanWikiZh("欧米茄M架势"));

    [Fact]
    public void 等级型号标识_不当作括注剥离()
        // 中间是 ASCII，正则要求中文段，故不匹配——等级/型号必须保住
        => Assert.Equal("A级精英杀手4", WikiExportService.CleanWikiZh("A级精英杀手4"));

    [Fact]
    public void 中文主副标题自带连字符_不许误剥()
        // 「生死答问 - 回响」是国服译名本身的主副标题（wiki ID 17845 实况），
        // 正则要求英文曲名在前，此处没有英文，故必须原样保留
        => Assert.Equal("管弦乐琴乐谱:生死答问 - 回响",
            WikiExportService.CleanWikiZh("管弦乐琴乐谱:生死答问 - 回响"));

    [Fact]
    public void 冒号加英文但无括注_保持原样()
        => Assert.Equal("管弦乐琴乐谱:Answers",
            WikiExportService.CleanWikiZh("管弦乐琴乐谱:Answers"));

    [Fact]
    public void 空串_原样返回不抛()
        => Assert.Equal("", WikiExportService.CleanWikiZh(""));

    // ===== 反例：URL 不许被清洗规则削坏 =====
    //
    // 2026-10-15 国服译名体检（dry-run）时发现：若某条译文混进了形如
    // 「https://x.com/a-中文-b」的链接，下方那条「英文-中文-」规则会把 "a-中文-"
    // 当成编者注整段削掉，链接当场报废。故含 "://" 的串一律放行。

    [Fact]
    public void 含链接的译文_原样返回不予清洗()
        => Assert.Equal("参考页 https://ffxiv.huijiwiki.com/wiki/Answers_-_乐谱",
            WikiExportService.CleanWikiZh("参考页 https://ffxiv.huijiwiki.com/wiki/Answers_-_乐谱"));

    [Fact]
    public void 链接里夹中文连字段_不被当成编者注剥除()
        => Assert.Equal("https://x.com/a-中文-b",
            WikiExportService.CleanWikiZh("https://x.com/a-中文-b"));

    [Fact]
    public void 全角冒号后的中文译名_正常清洗不受链接守卫影响()
        // 与实际落盘数据一致（用的是全角冒号「：」，见 wiki_术语对照/物品.json）；
        // IsValidChinese 不排斥全角冒号，故重抓时也能入库
        => Assert.Equal("管弦乐琴乐谱：生死答问",
            WikiExportService.CleanWikiZh("管弦乐琴乐谱：Answers\u3000-生死答问-"));
}
