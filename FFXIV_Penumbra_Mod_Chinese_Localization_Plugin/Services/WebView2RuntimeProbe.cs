using System;
using System.IO;
using System.Linq;
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
    /// 探测 WebView2 Runtime 的**可执行文件目录**（含 msedgewebview2.exe 的版本目录，取最新）。
    /// 供 WebView2 创建环境时**显式指定**，绕过 loader 的自动探测——本机实测：
    /// 同一 SDK 在控制台能自动探测成功，但在游戏进程里报「找不到 runtime」，
    /// 原因是游戏进程命中了系统里旧版 native loader（1.0.864.35）的探测逻辑。
    /// 显式指定目录可跳过该探测。找不到返回 null（退回自动探测）。
    /// </summary>
    public static string? TryGetRuntimeExecutableFolder()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "EdgeWebView", "Application"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "EdgeWebView", "Application"),
        };
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                // 版本目录按名（版本号）降序，取第一个含 msedgewebview2.exe 的
                var best = Directory.EnumerateDirectories(root)
                    .Where(d => File.Exists(Path.Combine(d, "msedgewebview2.exe")))
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (best != null) return best;
            }
            catch
            {
                /* 忽略，继续下一个候选根目录 */
            }
        }
        return null;
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
