using System;
using FFXIVPenumbraHanhua.Services;
using Xunit;

namespace FFXIVPenumbraHanhua.Tests;

/// <summary>
/// 回归测试：cf_clearance cookie 导入（B 路径，2026-10-14 火绒拒关、游戏内验证窗白屏无解时采用）。
/// 锁住纯函数 <see cref="WebView2WikiFetcher.TryParseCfClearance"/> 的解析判据（直调产品代码，含反例）。
/// </summary>
public class WebView2CookieImportTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("cf_clearance=abc123", true, "abc123")]
    [InlineData("ABC123", true, "ABC123")]
    [InlineData("cf_clearance=  xyz  ", true, "xyz")]
    [InlineData("\"cf_clearance=quoted\"", true, "cf_clearance=quoted")]
    [InlineData("cf_clearance=", false)]
    public void TryParseCfClearance_各情形(string? input, bool expectOk, string? expectValue = null)
    {
        var ok = WebView2WikiFetcher.TryParseCfClearance(input, out var value);
        Assert.Equal(expectOk, ok);
        if (expectOk) Assert.Equal(expectValue, value);
    }
}
