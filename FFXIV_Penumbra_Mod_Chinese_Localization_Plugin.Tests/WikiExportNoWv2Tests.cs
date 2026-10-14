using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FFXIVPenumbraHanhua.Services;
using Xunit;

namespace FFXIVPenumbraHanhua.Tests;

/// <summary>
/// 回归测试：锁住「wiki 提取不再依赖内嵌 WebView2」这个 2026-10-14 定型结论。
/// <para>
/// 背景：内嵌 WebView2 在游戏里 <c>ExecuteScriptAsync</c> 恒返回 <c>{}</c>（renderer 活着、导航成功，
/// 但脚本压根不执行），控制器重建直接 0x80070270；更糟的是取数一旦回退到它，
/// 就会弹一个<b>永远白屏</b>的验证窗口（2026-10-14 13:12 实录：取消提取 → 回退内嵌 → raw={{}} → 弹白窗）。
/// 于是整条链被移除，取数只留：外部抓取器（游戏外独立进程）→ curl → HttpClient。
/// </para>
/// 这些用例用反射直接查产品代码：**不新增/不恢复任何内嵌 WebView2 依赖**。
/// </summary>
public class WikiExportNoWv2Tests
{
    private static readonly Type Service = typeof(WikiExportService);

    /// <summary> 程序集里不该再有 WebView2WikiFetcher / WebView2RuntimeProbe 这两个类型（已删文件）。 </summary>
    /// <remarks>
    /// ⚠ 不能用 <c>Assembly.GetTypes()</c> 全量枚举：测试环境加载不到 Dalamud，
    /// 枚举会抛 ReflectionTypeLoadException，把「类型不存在」误报成「测试炸了」。
    /// 改为按全名精确查询单个类型——查不到就返回 null，不触碰其他类型的依赖解析。
    /// </remarks>
    [Fact]
    public void 程序集内不再有内嵌WebView2抓取器类型()
    {
        var asm = typeof(WikiExportService).Assembly;
        Assert.Null(asm.GetType("FFXIVPenumbraHanhua.Services.WebView2WikiFetcher", throwOnError: false));
        Assert.Null(asm.GetType("FFXIVPenumbraHanhua.Services.WebView2RuntimeProbe", throwOnError: false));
    }

    /// <summary> WikiExportService 身上不该再挂任何 WebView2 抓取器字段/属性。 </summary>
    [Fact]
    public void WikiExportService不再持有内嵌WebView2字段()
    {
        var members = Service
            .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Concat<MemberInfo>(Service.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain("_wv2", members);
        Assert.DoesNotContain("CurrentUrl", members);
        Assert.DoesNotContain("OnCaptchaNeeded", members);
        Assert.DoesNotContain("OnCaptchaWindowClosed", members);
        Assert.DoesNotContain("CaptchaPending", members);
        Assert.DoesNotContain("CaptchaWindowVisible", members);
    }

    /// <summary> 不该再有「弹/藏验证窗口」「导入 cf_clearance」这类对外 API。 </summary>
    [Fact]
    public void WikiExportService不再暴露验证窗口与Cookie导入API()
    {
        var methods = Service
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain("ShowCaptcha", methods);
        Assert.DoesNotContain("HideCaptchaWindow", methods);
        Assert.DoesNotContain("ImportCfClearanceAsync", methods);
        Assert.DoesNotContain("FetchViaWebView2", methods);
        Assert.DoesNotContain("DecideAfterWebView2", methods);
        Assert.DoesNotContain("ShouldSkipCurlFallback", methods);
    }

    /// <summary> 构造函数只应接受 (AppLog, string?, string?)——不再有 WebView2 开关与 profile 参数。 </summary>
    [Fact]
    public void 构造函数签名不再含WebView2参数()
    {
        var ctors = Service.GetConstructors();
        Assert.Single(ctors);
        var ps = ctors[0].GetParameters();
        // ① AppLog 日志；② 插件 DLL 目录（定位随插件发布的抓取器 exe，Dalamud 字节流加载时必传）；
        // ③ 插件配置目录（抓取器独立 profile 落点，随启动器自适应，不硬编码 XIVLauncherCN）。
        Assert.Equal(3, ps.Length);
        Assert.Equal(typeof(AppLog), ps[0].ParameterType);
        Assert.Equal(typeof(string), ps[1].ParameterType);
        Assert.Equal(typeof(string), ps[2].ParameterType);
    }

    /// <summary>
    /// 反例锁：取数失败（helper / curl / HttpClient 全失败）时必须**如实返回 null**，
    /// 绝不能伪装成「需要人工过 CF 验证」——那正是弹白窗的老路。
    /// </summary>
    [Fact]
    public async Task 词典目录未配置时_如实报错_不进入验证分支()
    {
        var wiki = new WikiExportService(new AppLog());
        var n = await wiki.ExportAsync(new[] { "Item/" }, true, "  ", null, default);
        Assert.Equal(-1, n); // -1=参数错误；-3/-4 是风控/验证分支，都不该出现在这里
        Assert.Contains("词典目录", wiki.LastResult);
    }
}
