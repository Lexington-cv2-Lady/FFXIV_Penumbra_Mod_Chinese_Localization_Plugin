using System;
using System.Text;
using FFXIVPenumbraHanhua.Services;
using Xunit;

namespace FFXIVPenumbraHanhua.Tests;

/// <summary>
/// 回归测试：Cloudflare 风控挑战页判定（审查方 2026-09-25 报告 F1【低】）。
/// 该函数属「文本判定与过滤判据」，是通用工作记忆 §B.26 列举的高危逻辑，改动必须配回归用例；
/// 此前产品代码内有实现、测试工程零用例，判定被改坏或特征串失效都无法被发现。
/// 判定已按 §B.39 抽为 internal sealed 纯类型 <see cref="CloudflareChallengeDetector"/>，
/// 本测试直接调用产品代码本身（§B.33：绝不用另一套语言"复刻"逻辑当验证）。
/// </summary>
public class CloudflareChallengeDetectorTests
{
    // ============ A. 命中 Cloudflare 特征 → true ============

    public static TheoryData<string, string> ChallengeSamples => new()
    {
        { "_cf_chl_opt 配置对象",
          "<script>window._cf_chl_opt={cvId:'2',cZone:'cdn.huijiwiki.com',cType:'interactive'};</script>" },
        { "challenges.cloudflare.com 挑战脚本域",
          "<script src=\"/cdn-cgi/challenge-platform/h/b/orchestrate/chl_page/v1?ray=7c1a\"></script>" +
          "<script src=\"https://challenges.cloudflare.com/turnstile/v0/api.js\"></script>" },
        { "challenges.cloudflare-cn.com 变体",
          "<script src=\"https://challenges.cloudflare-cn.com/turnstile/v0/api.js\"></script>" },
        { "中文「请稍候」提示语",
          "<html><head><title>请稍候…</title></head><body>正在检查您的浏览器，请稍候。</body></html>" },
        { "交互式挑战 cType:'interactive'",
          "/* challenge */ cType:'interactive', cNounce:'88213' /* /cdn-cgi/challenge-platform */" },
        { "真实混合挑战页（多特征同时出现）",
          "<!DOCTYPE html><html><head><title>请稍候…</title>" +
          "<script src=\"/cdn-cgi/challenge-platform/h/b/orchestrate/jsch/v1\"></script></head>" +
          "<body><div id=\"cf-wrapper\">Checking if the site connection is secure</div>" +
          "<script>window._cf_chl_opt={cUP:'https://challenges.cloudflare.com/...',cType:'interactive'};" +
          "</script></body></html>" }
    };

    [Theory]
    [MemberData(nameof(ChallengeSamples))]
    public void IsChallenge_ReturnsTrue_ForCloudflareChallengePage(string desc, string body)
        => Assert.True(CloudflareChallengeDetector.IsChallenge(body), "应判为挑战页：" + desc);

    // ============ B. 普通正常响应 → false ============

    public static TheoryData<string, string> NormalSamples => new()
    {
        { "wiki API 正常 JSON",
          "{\"batchcomplete\":\"\",\"query\":{\"pages\":{\"12345\":{\"pageid\":12345,\"ns\":3500," +
          "\"title\":\"Data:Item/1\",\"revisions\":[{\"slots\":{\"main\":{\"*\":\"{\\\"中文名\\\":\\\"新月岛全景图\\\"," +
          "\\\"英文名\\\":\\\"Occult Crescent Map\\\"}\"}}}]}}}}" },
        { "wiki 正文 HTML 页（含中文，无特征串）",
          "<!DOCTYPE html><html lang=\"zh\"><head><title>新月岛 - 灰机wiki</title></head>" +
          "<body><div class=\"mw-parser-output\"><p>新月岛是 7.x 版本新增的探索内容。</p></div></body></html>" },
        { "正文里单纯提到 cloudflare 单词（不构成特征）",
          "{\"terms\":{\"Cloudflare\":\"Cloudflare\"},\"note\":\"本站 CDN 由 cloudflare 提供加速\"}" },
        { "仅空白字符",
          "   \r\n\t  " }
    };

    [Theory]
    [MemberData(nameof(NormalSamples))]
    public void IsChallenge_ReturnsFalse_ForNormalResponse(string desc, string body)
        => Assert.False(CloudflareChallengeDetector.IsChallenge(body), "不应误判为挑战页：" + desc);

    [Fact]
    public void IsChallenge_ReturnsFalse_ForTruncatedMarker()
    {
        // 边界：特征串截断一字符（_cf_chl_o）不应命中，防止判定被放宽成"包含 cf 即挑战页"
        Assert.False(CloudflareChallengeDetector.IsChallenge("window._cf_chl_o={};"));
    }

    [Fact]
    public void IsChallenge_IsCaseSensitive_Ordinal()
    {
        // 现状固化：按 Ordinal 大小写敏感匹配。真实挑战页恒为小写；
        // 若将来改为忽略大小写，需在此显式确认并同步用例。
        Assert.False(CloudflareChallengeDetector.IsChallenge("window._CF_CHL_OPT={};"));
        Assert.False(CloudflareChallengeDetector.IsChallenge("请稍侯")); // 形近字（侯≠候）不命中
    }

    // ============ C. 异常输入 → false 且不抛异常 ============

    [Fact]
    public void IsChallenge_ReturnsFalse_ForNullOrEmpty_WithoutThrowing()
    {
        Assert.False(AssertNoThrow(() => CloudflareChallengeDetector.IsChallenge(null)));
        Assert.False(AssertNoThrow(() => CloudflareChallengeDetector.IsChallenge("")));
    }

    [Fact]
    public void IsChallenge_ReturnsFalse_ForHugeInput_WithoutThrowing()
    {
        // 超长异常输入（约 100 万字符正常正文）：不得抛异常，且不得误判
        var sb = new StringBuilder();
        for (var i = 0; i < 40_000; i++) sb.Append("<p>这里是灰机 wiki 的正常正文内容，第 ").Append(i).Append(" 段。</p>\n");
        var huge = sb.ToString();

        var ex = Record.Exception(() => CloudflareChallengeDetector.IsChallenge(huge));
        Assert.Null(ex);
        Assert.False(CloudflareChallengeDetector.IsChallenge(huge));
        // 确认样本确实够长（否则本用例失去意义）；消息带实际长度，便于失败时定位
        Assert.True(huge.Length >= 1_000_000, $"超长样本长度不足：{huge.Length}");
    }

    [Fact]
    public void IsChallenge_DetectsMarkerAtTailOfHugeInput()
    {
        // 超长输入尾部才出现特征：不得因长度被截断/跳过而漏判
        var huge = new string('a', 1_000_000) + "window._cf_chl_opt={};";
        Assert.True(CloudflareChallengeDetector.IsChallenge(huge));
    }

    private static bool AssertNoThrow(Func<bool> f)
    {
        var ex = Record.Exception(() => { f(); });
        Assert.Null(ex); // 抓取循环内调用，抛异常会中断整轮 wiki 提取
        return f();
    }
}
