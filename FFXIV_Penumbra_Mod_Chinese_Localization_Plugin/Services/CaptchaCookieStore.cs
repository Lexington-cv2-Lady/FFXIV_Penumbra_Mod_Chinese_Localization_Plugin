using System;

namespace FFXIVPenumbraHanhua.Services;

/// <summary>
/// Cloudflare <c>cf_clearance</c> cookie 的缓存与过期判定（纯逻辑、无 IO，便于单测）。
/// <para>
/// 用途：用户手动通过 Cloudflare 验证（弹独立 WebView2 窗口过点）后，插件把拿到的 cookie 存进来；
/// 后续 wiki 抓取请求自动带上它（见 <see cref="Header"/>），直到过期需重新验证。
/// </para>
/// <para>
/// 抽成独立类型的理由：宿主 <see cref="WikiExportService"/> 依赖 HttpClient/AppLog，测试内不便实例化；
/// 而 cookie 的"存取/过期/生成头"是纯状态逻辑，独立后测试可直接调用产品代码本身。
/// </para>
/// </summary>
internal sealed class CaptchaCookieStore
{
    private string? _value;
    private DateTime _expiryUtc;

    /// <summary> 当前是否持有**未过期**的 cookie（传入当前 UTC 时间，便于单测注入）。 </summary>
    public bool Has(DateTime nowUtc) => _value != null && nowUtc < _expiryUtc;

    /// <summary> 生成用于请求头的 Cookie 值（如 <c>cf_clearance=xxx</c>）；无有效 cookie 返回 null。 </summary>
    public string? Header(DateTime nowUtc) => Has(nowUtc) ? "cf_clearance=" + _value : null;

    /// <summary>
    /// 存入 cookie 并设定过期时刻。空值忽略；<paramref name="ttlMinutes"/> &lt;= 0 时按 30 分钟兜底
    /// （Cloudflare 的 cf_clearance 典型有效期数小时~数天，此处由调用方保守估计）。
    /// </summary>
    public void Set(string value, int ttlMinutes, DateTime nowUtc)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return;
        _value = v;
        _expiryUtc = nowUtc.AddMinutes(ttlMinutes <= 0 ? 30 : ttlMinutes);
    }

    /// <summary> 清空 cookie（用户放弃验证、或带 cookie 仍被拦时判定其失效）。 </summary>
    public void Clear()
    {
        _value = null;
        _expiryUtc = default;
    }
}
