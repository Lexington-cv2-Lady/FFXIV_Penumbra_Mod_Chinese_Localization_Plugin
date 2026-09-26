using System;

namespace FFXIVPenumbraHanhua.Services;

/// <summary>
/// Cloudflare 风控挑战页判定（抽自 <see cref="WikiExportService"/>，纯文本匹配、无 IO、无状态）。
/// <para>
/// 为何独立成类型：宿主 <see cref="WikiExportService"/> 依赖 <c>AppLog</c> 与 <c>HttpClient</c>，
/// 测试内不便实例化；而判定本身是纯函数，独立后测试可直接调用产品代码本身
/// （通用工作记忆 §B.33：验证手段要同源，禁止用别的语言"复刻"判定当验证）。
/// 抽出 internal sealed 纯类型作测试 seam 的做法见 §B.39。
/// </para>
/// <para>
/// 灰机 wiki 的 CDN 走 Cloudflare：本插件的 .NET SChannel TLS 指纹（JA3/JA4）被风控打分后，
/// 返回的不是数据 JSON 而是挑战页 HTML；若不识别，会被当成"非 JSON 响应"误报，掩盖真正的拦截原因。
/// </para>
/// </summary>
internal sealed class CloudflareChallengeDetector
{
    /// <summary>
    /// 挑战页特征串（命中任一即判为风控页）。增补特征属于"过滤判据"变更，
    /// 按通用工作记忆 §B.26 必须同步补回归用例（见测试工程 CloudflareChallengeDetectorTests）。
    /// </summary>
    private static readonly string[] Markers =
    {
        "_cf_chl_opt",             // 挑战页注入的 challenge 配置对象（window._cf_chl_opt）
        "challenges.cloudflare",   // 挑战脚本域名（覆盖 cloudflare.com / cloudflare-cn.com 等变体）
        "请稍候",                   // 中文挑战页「请稍候…」提示语
        "cType:'interactive'",     // 交互式挑战（Managed/Interactive Challenge）标记
    };

    /// <summary>
    /// 判定响应体是否为 Cloudflare 风控挑战页。
    /// null / 空串 / 超长异常输入一律返回 false，且不抛异常（调用点在抓取循环内，异常会中断整轮提取）。
    /// </summary>
    public static bool IsChallenge(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return false;
        foreach (var marker in Markers)
        {
            if (body.Contains(marker, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
