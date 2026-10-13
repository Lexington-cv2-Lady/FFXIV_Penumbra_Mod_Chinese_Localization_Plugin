using System;
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

    // ============ 消息泵寿命判据（2026-10-13 16:16:55 事故） ============

    [Fact]
    public void PumpShouldReap_验证窗口可见_再不空闲也不退_一等公民()
    {
        // 反例中的反例：用户正在过 Cloudflare 验证（turnstile 可能看半天），
        // 哪怕空闲计数爆表也绝不允许收线程——16:16 事故正是线程寿终时用户正在验证
        var now = new DateTime(2026, 10, 13, 16, 30, 0, DateTimeKind.Utc);
        var stale = now.AddHours(-2);
        Assert.False(WebView2WikiFetcher.PumpShouldReap(now, stale, captchaWindowVisible: true, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void PumpShouldReap_窗口隐藏且未超时_不退()
    {
        var now = new DateTime(2026, 10, 13, 16, 30, 0, DateTimeKind.Utc);
        var recent = now.AddMinutes(-9);
        Assert.False(WebView2WikiFetcher.PumpShouldReap(now, recent, captchaWindowVisible: false, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void PumpShouldReap_窗口隐藏且超时_退()
    {
        var now = new DateTime(2026, 10, 13, 16, 30, 0, DateTimeKind.Utc);
        var stale = now.AddMinutes(-10).AddSeconds(-1);
        Assert.True(WebView2WikiFetcher.PumpShouldReap(now, stale, captchaWindowVisible: false, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void PumpShouldReap_边界恰好等于阈值_退()
    {
        // >= 边界含等号：恰好闲置满 10 分钟也算（省内存不该差这一秒）
        var now = new DateTime(2026, 10, 13, 16, 30, 0, DateTimeKind.Utc);
        var exactly = now.AddMinutes(-10);
        Assert.True(WebView2WikiFetcher.PumpShouldReap(now, exactly, captchaWindowVisible: false, TimeSpan.FromMinutes(10)));
    }

    // ============ 懒重启前的状态重置（2026-10-13 16:56 实测三连炸） ============

    [Fact]
    public void ResetForRestart_换窗口类名_防与未退完旧线程自撞()
    {
        // 实录：重启复用旧类名 → 旧线程未退完（窗口/类名释放中）→ RegisterClassEx 1410
        var f = new WebView2WikiFetcher(new AppLog(), System.IO.Path.GetTempPath());
        var before = f.WindowClassName;
        f.ResetForRestart();
        Assert.NotEqual(before, f.WindowClassName);
        Assert.StartsWith("PMHWebView2FetcherV2_", f.WindowClassName);
    }

    [Fact]
    public void ResetForRestart_清就绪与验证态_防假就绪骗过等待()
    {
        // 实录：旧 _available/_ctl 死值让「重启后就绪」瞬间误报，随后拿死窗口建控制器 → COMException
        var f = new WebView2WikiFetcher(new AppLog(), System.IO.Path.GetTempPath());
        f.ResetForRestart();
        Assert.False(f.IsAvailable);
        Assert.False(f.CaptchaPending);
        Assert.Null(f.LastError);
    }

    [Fact]
    public void ResetForRestart_重复调用_每次类名都不同()
    {
        var f = new WebView2WikiFetcher(new AppLog(), System.IO.Path.GetTempPath());
        f.ResetForRestart();
        var a = f.WindowClassName;
        f.ResetForRestart();
        Assert.NotEqual(a, f.WindowClassName);
    }

    // ============ 验证窗口多屏居中（2026-10-13 17:04 实录「弹出来不明显」） ============

    [Fact]
    public void CenterInWorkArea_主屏工作区_窗口居中()
    {
        // 1920x1080 屏幕、任务栏占 40 → 工作区 0,0,1920,1040；放 1100x800
        var (x, y) = WebView2WikiFetcher.CenterInWorkArea(0, 0, 1920, 1040, 1100, 800);
        Assert.Equal(410, x); // (1920-1100)/2
        Assert.Equal(120, y); // (1040-800)/2
    }

    [Fact]
    public void CenterInWorkArea_副屏在右_按副屏工作区居中()
    {
        // 双屏横排：副屏工作区 1920..3840。旧代码按主屏尺寸算，窗口落到副屏用户看不见（实录）
        var (x, y) = WebView2WikiFetcher.CenterInWorkArea(1920, 0, 3840, 1040, 1100, 800);
        Assert.Equal(2330, x); // 1920+(1920-1100)/2
        Assert.Equal(120, y);
    }

    [Fact]
    public void CenterInWorkArea_副屏在左_坐标为负也合法()
    {
        // 副屏在主屏左侧：工作区 -1920..0，窗口中心 -410 处，x 为负——负坐标合法，不能贴 0
        var (x, _) = WebView2WikiFetcher.CenterInWorkArea(-1920, 0, 0, 1040, 1100, 800);
        Assert.Equal(-1510, x); // -1920+(1920-1100)/2
    }

    [Fact]
    public void CenterInWorkArea_窗口比工作区大_贴左上角不外溢()
    {
        // 反例：小工作区（800x600）放大窗口（1100x800）——必须贴工作区左上角，
        // 不能算出负中心把窗口弹到屏幕外
        var (x, y) = WebView2WikiFetcher.CenterInWorkArea(0, 0, 800, 600, 1100, 800);
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }
}

/// <summary>
/// 回归测试：验证窗口「逃生门」语义 + 「验证窗口打开期间屏蔽 curl 回退」（2026-10-13 18:53 反馈 + 火绒环境实证）。
/// <para>
/// 背景：18:53 实录两个漏改——①「隐藏验证窗口」曾是「与点 X 等效（隐藏 + 通知续跑）」，
/// 导致隐藏后立刻被「关窗自动续跑」重新弹回（「隐藏功能无效」）；②「停止等待」只清 UI 标志、从不收窗
/// （「停止也没自动关闭窗口」）；③ 验证窗口打开期间 fetch 仍回退 curl，必 -3 且延长出口 IP 冷却。
/// 本文件锁住①②的 fetcher 级语义与③的纯函数判据。
/// </para>
/// </summary>
public class WebView2CaptchaWindowEscapeTests
{
    [Fact]
    public void CaptchaWindowVisible_新建fetcher_默认false()
    {
        var f = new WebView2WikiFetcher(new AppLog(), System.IO.Path.GetTempPath());
        Assert.False(f.CaptchaWindowVisible);
    }

    [Fact]
    public void HideCaptchaWindow_仅隐藏_不触发关窗续跑信号()
    {
        // 关键回归：隐藏曾是「隐藏 + 通知续跑」，导致隐藏后被自动续跑重新弹回（18:53 实录「隐藏功能无效」）。
        // 现改为：隐藏只收窗，不触发 OnCaptchaWindowClosed（真实关窗续跑仍走 WM_CLOSE 路径）。
        var fired = false;
        var f = new WebView2WikiFetcher(new AppLog(), System.IO.Path.GetTempPath());
        f.OnCaptchaWindowClosed = () => fired = true;
        f.HideCaptchaWindow();
        Assert.False(fired);                 // 隐藏不触发续跑信号
        Assert.False(f.CaptchaWindowVisible); // 窗口可见态已收起
    }

    [Theory]
    [InlineData(null, true, true)]    // 窗口开着、WebView2 没取到 → 跳过 curl，等用户关窗续跑
    [InlineData(null, false, false)]  // 窗口没开 → 照旧回退 curl 保底
    [InlineData("{\"x\":1}", true, false)]   // 取到正文（数据）→ 不在此拦截，交 Decide 分流
    [InlineData("<script>cf_chl</script>", true, false)] // 拿到挑战页 → 由 Decide 判 NeedCaptcha，不在此拦
    public void ShouldSkipCurlFallback_各情形(string? body, bool windowVisible, bool expected)
    {
        Assert.Equal(expected, WikiExportService.ShouldSkipCurlFallback(body, windowVisible));
    }
}

/// <summary>
/// 回归测试：WebView2 应急渲染参数（2026-10-14 选项 A 应急兜底）。
/// <para>
/// 背景：本机实测火绒(HipsDaemon)注入 ffxiv_dx11.exe 进程树、在游戏内静默阻止 WebView2 渲染进程绘制
/// （renderer 不崩、导航成功、画面全白，无 ProcessFailed）。应急开关（环境变量 PMH_WV2_NO_SANDBOX=1）
/// 追加 --no-sandbox --disable-features=RendererCodeIntegrity，尝试绕过火绒对渲染进程的代码完整性钩子。
/// 本文件锁住参数构造的纯函数判据。
/// </para>
/// </summary>
public class WebView2EmergencyArgsTests
{
    [Fact]
    public void BuildWebView2Args_默认_仅软件渲染()
    {
        // 默认（关应急）：只强制软件渲染，绝不放松沙箱
        Assert.Equal("--disable-gpu --disable-gpu-compositing", WebView2WikiFetcher.BuildWebView2Args(false));
    }

    [Fact]
    public void BuildWebView2Args_应急_追加no_sandbox与RendererCodeIntegrity()
    {
        var a = WebView2WikiFetcher.BuildWebView2Args(true);
        Assert.Contains("--no-sandbox", a);
        Assert.Contains("--disable-features=RendererCodeIntegrity", a);
        // 软件渲染仍保留
        Assert.Contains("--disable-gpu --disable-gpu-compositing", a);
    }
}
