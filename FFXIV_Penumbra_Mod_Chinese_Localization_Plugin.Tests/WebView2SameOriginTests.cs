using FFXIVPenumbraHanhua.Services;
using Xunit;

namespace FFXIVPenumbraHanhua.Tests;

/// <summary>
/// 回归测试：WebView2 同源取数改造（2026-10-13 实测定位并修复）。
/// <para>
/// 背景：WebView2 起手停在 <c>about:blank</c>（不透明源），页内 fetch wiki 属<b>跨源</b>、必被 CORS 拒，
/// <c>ExecuteScriptAsync</c> 返回非字符串（典型 <c>null</c>）——旧代码直接 <c>Deserialize&lt;string&gt;</c>
/// 抛 JsonException，静默回退 curl 又被 CF 拦，且「验证弹窗只在拿到挑战页时才出现」与「fetch 永远回不到
/// 挑战页」两半永不相交（弹窗流程实际不可达）。
/// </para>
/// <para>
/// 修复：① <see cref="WebView2WikiFetcher.ClassifyRaw"/> 把非字符串返回值判为 SameOriginNeeded；
/// ② <see cref="WikiExportService.DecideAfterWebView2"/> 据此返回 NeedCaptcha（不打 curl，避免延长 IP 冷却）；
/// ③ UI 弹验证窗口导航到 wiki 同源页，用户关窗后从断点自动续跑。
/// 本文件锁住 ①②的纯函数判据（直调产品代码，含反例）。
/// </para>
/// </summary>
public class WebView2SameOriginTests
{
    // ============ A. ExecuteScriptAsync 返回值判别 ============

    [Fact]
    public void ClassifyRaw_正常JSON字符串_判为Data且文本原样返回()
    {
        var outcome = WebView2WikiFetcher.ClassifyRaw("\"{\\\"query\\\":1}\"", out var text);
        Assert.Equal(WebView2WikiFetcher.Wv2FetchOutcome.Data, outcome);
        Assert.Equal("{\"query\":1}", text);
    }

    [Fact]
    public void ClassifyRaw_页内fetch抛错被catch_判为JsError()
    {
        var outcome = WebView2WikiFetcher.ClassifyRaw("\"__WV2ERR__TypeError: Failed to fetch\"", out var text);
        Assert.Equal(WebView2WikiFetcher.Wv2FetchOutcome.JsError, outcome);
        Assert.StartsWith("__WV2ERR__", text);
    }

    [Fact]
    public void ClassifyRaw_返回null_判为需要同源_今日实测形态()
    {
        // 2026-10-13 12:19:47 日志：[wv2] fetch 失败：JsonException The JSON value could not be
        // converted to System.String. Path: $ —— ExecuteScriptAsync 返回的就是 JSON null
        var outcome = WebView2WikiFetcher.ClassifyRaw("null", out var text);
        Assert.Equal(WebView2WikiFetcher.Wv2FetchOutcome.SameOriginNeeded, outcome);
        Assert.Null(text);
    }

    [Theory]
    [InlineData("123")]   // 数字令牌
    [InlineData("true")]  // 布尔令牌
    [InlineData("{}")]    // 对象令牌
    [InlineData("[]")]    // 数组令牌
    [InlineData("")]      // 空串（WebView2 约定 null 亦可能呈现为空）
    [InlineData(null)]    // C# 侧 null
    public void ClassifyRaw_非字符串返回值_一律判为需要同源(string? raw)
    {
        Assert.Equal(WebView2WikiFetcher.Wv2FetchOutcome.SameOriginNeeded,
            WebView2WikiFetcher.ClassifyRaw(raw, out _));
    }

    [Fact]
    public void ClassifyRaw_空JSON字符串_仍算取到数据()
    {
        // 反例：空正文也是「取到了」（由上层按非 JSON 响应报错），不能误判成需要同源
        var outcome = WebView2WikiFetcher.ClassifyRaw("\"\"", out var text);
        Assert.Equal(WebView2WikiFetcher.Wv2FetchOutcome.Data, outcome);
        Assert.Equal("", text);
    }

    // ============ B. WebView2 取数后的分流决策 ============

    [Fact]
    public void Decide_WebView2取到挑战页_判为需人工验证()
    {
        // 挑战页：有 WebView2 验证机制可救 → 弹窗等验证，而非当 -3 直接中止
        Assert.Equal(WikiExportService.Wv2Decision.NeedCaptcha,
            WikiExportService.DecideAfterWebView2("<script>window._cf_chl_opt={};</script>", captchaPending: false));
    }

    [Fact]
    public void Decide_WebView2取到正常正文_判为采用()
    {
        Assert.Equal(WikiExportService.Wv2Decision.Accept,
            WikiExportService.DecideAfterWebView2("{\"batchcomplete\":\"\"}", captchaPending: false));
    }

    [Fact]
    public void Decide_跨源失败且抓取器报等待验证_判为需人工验证()
    {
        Assert.Equal(WikiExportService.Wv2Decision.NeedCaptcha,
            WikiExportService.DecideAfterWebView2(null, captchaPending: true));
    }

    [Fact]
    public void Decide_未启用或未就绪_仍回退curl保底_反例()
    {
        // WebView2 没开/没初始化好时 body=null 且无等待验证标记 → 必须走旧 curl 路径，不能当成要验证
        Assert.Equal(WikiExportService.Wv2Decision.FallbackCurl,
            WikiExportService.DecideAfterWebView2(null, captchaPending: false));
    }
}

/// <summary>
/// 回归测试：抓取器窗口类名必须**每实例唯一**（2026-10-13 12:47 实测事故的根因）。
/// 窗口类是进程级全局命名资源：固定类名在插件热重载后 RegisterClassEx 必撞名（1410），
/// 新窗口消息被派发到旧实例的 WndProc、排空旧 DLL 实例的静态队列 → 新实例 await 续体
/// （含 15s 超时兜底）全部饿死且毫无日志。游戏外复现程序（90_补丁与中间产物\wv2_class_repro.cs）
/// 已证实该机制：固定类名第二个实例就绪=False，唯一类名=True。
/// </summary>
public class WebView2WindowClassNameTests
{
    [Fact]
    public void 窗口类名_每次生成都不同_防热重载撞名()
    {
        var a = WebView2WikiFetcher.NewWindowClassName();
        var b = WebView2WikiFetcher.NewWindowClassName();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void 窗口类名_带固定前缀与32位Guid段()
    {
        var name = WebView2WikiFetcher.NewWindowClassName();
        const string prefix = "PMHWebView2FetcherV2_";
        Assert.StartsWith(prefix, name);
        var suffix = name[prefix.Length..];
        Assert.Equal(32, suffix.Length); // Guid.ToString("N") 为 32 位十六进制
        Assert.All(suffix, c => Assert.True(char.IsAsciiHexDigit(c), $"Guid 段含非十六进制字符：{c}"));
    }
}

/// <summary>
/// 回归测试：fetch 僵死时的「失能」机制（2026-10-13 14:16 实测事故）。
/// 背景：卸载链漏放 WebView2 抓取器 → 僵尸控制器滞留 msedgewebview2 进程并拖住<b>共享</b>
/// 浏览器进程 → 新实例 <c>ExecuteScriptAsync</c> 永不返回，每次提取白等 30s。
/// 修复：① 超时/控制器异常即 <see cref="WebView2WikiFetcher.MarkUnavailable"/> 置失能，
/// 后续请求直接回退 curl；② <see cref="WikiExportService"/> 实现 IDisposable，
/// <see cref="Plugin"/> 卸载链显式关闭 fetcher。本文件锁住①的判据与②的释放契约。
/// </summary>
public class WebView2FetchUnavailableTests
{
    private static WebView2WikiFetcher NewFetcher()
    {
        // 不调用 Start()：只测字段级逻辑，不真起 STA 线程/WebView2 环境
        return new WebView2WikiFetcher(new AppLog(), System.IO.Path.GetTempPath());
    }

    [Fact]
    public void MarkUnavailable_置失能并记录原因()
    {
        var f = NewFetcher();
        f.MarkUnavailable("fetch 等待超时（30s）");
        Assert.False(f.IsAvailable);
        Assert.Equal("fetch 等待超时（30s）", f.LastError);
    }

    [Fact]
    public void MarkUnavailable_同原因幂等_异原因更新()
    {
        var f = NewFetcher();
        f.MarkUnavailable("fetch 等待超时（30s）");
        f.MarkUnavailable("fetch 等待超时（30s）"); // 同原因：幂等，不刷日志
        Assert.Equal("fetch 等待超时（30s）", f.LastError);
        f.MarkUnavailable("fetch 调用失败（COMException）"); // 异原因：更新，便于诊断
        Assert.Equal("fetch 调用失败（COMException）", f.LastError);
        Assert.False(f.IsAvailable);
    }

    [Fact]
    public void MarkUnavailable_不清除CaptchaPending_两状态相互独立()
    {
        // 反例：失能是「抓取器坏了」，等待人工验证是「需要用户操作」——不能互相顶掉。
        // CaptchaPending 无公开置位入口，此处锁「失能不会误碰它」
        var f = NewFetcher();
        f.MarkUnavailable("fetch 等待超时（30s）");
        Assert.False(f.CaptchaPending);
    }

    [Fact]
    public void Dispose_未启动的fetcher_不抛异常()
    {
        var f = NewFetcher();
        f.MarkUnavailable("测试");
        f.Dispose(); // _ctl/_hwnd/_watchdog 均为 null，应静默收尾
        Assert.False(f.IsAvailable);
    }

    [Fact]
    public void Service_Dispose_未启用WebView2_不抛异常()
    {
        using var svc = new WikiExportService(new AppLog()); // 默认不启用 wv2，只走 HttpClient 释放
        svc.Dispose();
    }

    [Fact]
    public void Service_重复Dispose_幂等不抛()
    {
        var svc = new WikiExportService(new AppLog());
        svc.Dispose();
        svc.Dispose();
    }
}
