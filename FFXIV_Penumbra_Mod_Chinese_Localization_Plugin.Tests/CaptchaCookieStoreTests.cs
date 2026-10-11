using System;
using FFXIVPenumbraHanhua.Services;
using Xunit;

namespace FFXIVPenumbraHanhua.Tests;

/// <summary>
/// 回归测试：Cloudflare cf_clearance cookie 缓存（2026-10-11「wiki 绕 CF 验证」功能）。
/// <para>
/// CaptchaCookieStore 是纯逻辑类（无 IO），测试直接调用产品代码本身（通用工作记忆 §B.33：
/// 验证手段要同源，禁止用别的语言"复刻"逻辑当验证）。时间均注入固定 UTC，避免用例随真实时钟漂移。
/// </para>
/// </summary>
public class CaptchaCookieStoreTests
{
    private static readonly DateTime T0 = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 存入后_有效期内_Has为真且Header为_cf_clearance_值()
    {
        var s = new CaptchaCookieStore();
        s.Set("abc123", 60, T0);
        Assert.True(s.Has(T0));
        Assert.Equal("cf_clearance=abc123", s.Header(T0));
    }

    [Fact]
    public void 过期后_Has为假且Header为null()
    {
        var s = new CaptchaCookieStore();
        s.Set("abc123", 60, T0);
        var later = T0.AddMinutes(61);
        Assert.False(s.Has(later));
        Assert.Null(s.Header(later));
    }

    [Fact]
    public void 边界_恰好等于过期时刻_视为已失效()
    {
        var s = new CaptchaCookieStore();
        s.Set("abc", 60, T0);
        Assert.False(s.Has(T0.AddMinutes(60))); // now == expiry → 过期（严格小于）
    }

    [Fact]
    public void ttl非正_按30分钟兜底()
    {
        var s = new CaptchaCookieStore();
        s.Set("x", 0, T0);
        Assert.True(s.Has(T0.AddMinutes(29)));
        Assert.False(s.Has(T0.AddMinutes(31)));
    }

    [Fact]
    public void 存空白_被忽略_不覆盖已有cookie()
    {
        var s = new CaptchaCookieStore();
        s.Set("good", 60, T0);
        s.Set("   ", 60, T0); // 空白应忽略
        Assert.Equal("cf_clearance=good", s.Header(T0));
    }

    [Fact]
    public void Clear_清空cookie()
    {
        var s = new CaptchaCookieStore();
        s.Set("abc", 60, T0);
        s.Clear();
        Assert.False(s.Has(T0));
        Assert.Null(s.Header(T0));
    }

    [Fact]
    public void Header_对cookie值去除首尾空白()
    {
        var s = new CaptchaCookieStore();
        s.Set("  token123  ", 60, T0);
        Assert.Equal("cf_clearance=token123", s.Header(T0));
    }
}
