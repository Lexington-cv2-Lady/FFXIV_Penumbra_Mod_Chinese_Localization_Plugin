using System;
using Microsoft.Win32;

namespace FFXIVPenumbraHanhua.Services;

/// <summary>
/// 检测本机 WebView2 Runtime（Evergreen）是否安装及版本。
/// <para>
/// 零依赖：只读注册表，**不引用 WebView2 SDK**——所以「用户机子有没有 WebView2」这个预检，
/// 无论最终是否启用 WebView2 直抓都能独立工作；官方下载链接由 UI 呈现给用户自行决定是否安装。
/// </para>
/// <para>
/// 注册表路径：WebView2 Runtime 以 EdgeUpdate 客户端形式注册，Client GUID 为
/// {F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}，值 <c>pv</c> 即版本（如 150.0.4078.105）。
/// </para>
/// </summary>
public static class WebView2RuntimeProbe
{
    /// <summary> WebView2 Runtime 的 EdgeUpdate Client GUID。 </summary>
    private const string ClientGuid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private static readonly string[] KeyPaths =
    {
        $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{ClientGuid}",
        $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{ClientGuid}",
    };

    /// <summary> 官方 WebView2 下载/文档页（用户手动安装入口）。 </summary>
    public const string OfficialDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary> Evergreen 静默安装器直链（x64）。 </summary>
    public const string EvergreenInstallerUrl = "https://go.microsoft.com/fwlink/?LinkId=2124703";

    /// <summary> 读取已安装的 WebView2 Runtime 版本字符串（如 "150.0.4078.105"）；未安装返回 null。 </summary>
    public static string? TryGetInstalledVersion()
    {
        foreach (var path in KeyPaths)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                var pv = key?.GetValue("pv") as string;
                if (!string.IsNullOrWhiteSpace(pv)) return pv;
            }
            catch
            {
                /* 注册表不可读（权限/平台）→ 视为未检出，继续下一路径 */
            }
        }
        return null;
    }

    /// <summary> 把版本字符串解析为 <see cref="Version"/>；无法解析返回 null。 </summary>
    public static Version? ParseVersion(string? versionString)
    {
        if (string.IsNullOrWhiteSpace(versionString)) return null;
        var s = versionString.Trim();
        if (Version.TryParse(s, out var v)) return v;
        // 容忍 "150.0.4078.105-abc" 之类后缀：取前导点分数字段
        var end = s.Length;
        for (var i = 0; i < s.Length; i++)
            if (!(char.IsAsciiDigit(s[i]) || s[i] == '.')) { end = i; break; }
        var head = s[..end].TrimEnd('.');
        return Version.TryParse(head, out var v2) ? v2 : null;
    }

    /// <summary>
    /// 判断已装版本是否**至少**达到最低要求（纯函数，便于单测）。
    /// 未安装 / 无法解析版本 → false（视为不满足）。
    /// </summary>
    public static bool MeetsMinimum(string? installedVersionString, Version minimum)
    {
        var v = ParseVersion(installedVersionString);
        if (v == null) return false;
        return v >= minimum;
    }
}
