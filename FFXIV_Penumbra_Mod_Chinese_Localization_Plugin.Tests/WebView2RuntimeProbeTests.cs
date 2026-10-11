using System;
using FFXIVPenumbraHanhua.Services;
using Xunit;

namespace FFXIVPenumbraHanhua.Tests;

/// <summary>
/// 回归测试：WebView2 Runtime 探测（2026-10-11「WebView2 直抓」前置环境检查）。
/// 纯函数（版本解析/最低版本判断）直调产品代码；注册表读取仅在本机断言「返回 null 或合法版本」。
/// </summary>
public class WebView2RuntimeProbeTests
{
    [Fact]
    public void ParseVersion_正常四段版本()
    {
        Assert.Equal(new Version(150, 0, 4078, 105), WebView2RuntimeProbe.ParseVersion("150.0.4078.105"));
    }

    [Fact]
    public void ParseVersion_两段也可()
    {
        Assert.Equal(new Version(120, 0), WebView2RuntimeProbe.ParseVersion("120.0"));
    }

    [Fact]
    public void ParseVersion_容忍后缀截断到点分数字段()
    {
        Assert.Equal(new Version(150, 0, 4078, 105), WebView2RuntimeProbe.ParseVersion("150.0.4078.105-beta"));
    }

    [Fact]
    public void ParseVersion_空或非法返回null()
    {
        Assert.Null(WebView2RuntimeProbe.ParseVersion(null));
        Assert.Null(WebView2RuntimeProbe.ParseVersion("   "));
        Assert.Null(WebView2RuntimeProbe.ParseVersion("not-a-version"));
    }

    [Fact]
    public void MeetsMinimum_高于最低版本为真()
    {
        Assert.True(WebView2RuntimeProbe.MeetsMinimum("150.0.4078.105", new Version(90, 0, 0, 0)));
    }

    [Fact]
    public void MeetsMinimum_恰好等于最低版本为真()
    {
        Assert.True(WebView2RuntimeProbe.MeetsMinimum("120.0.0.0", new Version(120, 0, 0, 0)));
    }

    [Fact]
    public void MeetsMinimum_低于最低版本为假()
    {
        Assert.False(WebView2RuntimeProbe.MeetsMinimum("80.0.0.0", new Version(90, 0, 0, 0)));
    }

    [Fact]
    public void MeetsMinimum_未安装或无法解析为假()
    {
        Assert.False(WebView2RuntimeProbe.MeetsMinimum(null, new Version(90, 0, 0, 0)));
        Assert.False(WebView2RuntimeProbe.MeetsMinimum("junk", new Version(90, 0, 0, 0)));
    }

    [Fact]
    public void 官方链接非空且指向微软域()
    {
        Assert.Contains("microsoft.com", WebView2RuntimeProbe.OfficialDownloadUrl);
        Assert.Contains("microsoft.com", WebView2RuntimeProbe.EvergreenInstallerUrl);
    }

    [Fact]
    public void 本机探测_返回null或可解析版本()
    {
        var v = WebView2RuntimeProbe.TryGetInstalledVersion();
        // 本机（Win10）实测装有 150.0.4078.105；无论装没装，这里只保证返回 null 或可解析版本
        if (v != null) Assert.NotNull(WebView2RuntimeProbe.ParseVersion(v));
    }
}
