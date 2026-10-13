using System;
using System.IO;
using FFXIVPenumbraHanhua.Services;
using Xunit;

namespace FFXIVPenumbraHanhua.Tests;

/// <summary>
/// 回归测试：外部抓取器（wv2_fetch_helper）的**部署完整性判定**。
/// <para>
/// 背景（2026-10-14 连续两轮白查）：插件在游戏里启动 helper 一直失败，日志
/// <c>exit=-2147450726</c>（0x8000809A）＋ stderr「The application to execute does not exist: '…wv2_fetch_helper.dll'」。
/// 根因是**只复制了 exe**——.NET 的 exe 只是 apphost（原生启动器），真正的托管应用在同名 dll。
/// 更糟的是探测函数当时只看 <c>File.Exists(exe)</c>，于是「明明找到了、却还是起不来」，
/// 且日志只写「未找到」，把排查方向带到 Cloudflare / TLS 指纹上去，白耗两轮。
/// </para>
/// 故锁三条：① 完整部署（子目录/平铺）必须能定位；② **只有 exe 缺 dll 时必须判为不可用**（反例）；
/// ③ 部署不完整要能被单独识别出来，好让日志说清「是部署漏文件」而不是「没找到」。
/// 全部直接调用产品代码 <see cref="WikiExportService.LocateHelperIn"/> / <see cref="WikiExportService.HelperDeployIncomplete"/>
/// / <see cref="WikiExportService.HelperSearchDirs"/>，不复刻逻辑。
/// </summary>
public class ExternalHelperDeployTests
{
    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "helper_probe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Touch(string path) => File.WriteAllText(path, "x");

    [Fact]
    public void LocateHelperIn_子目录完整部署_返回子目录exe()
    {
        var dir = NewTempDir();
        var sub = Path.Combine(dir, "wv2_fetch_helper");
        Directory.CreateDirectory(sub);
        Touch(Path.Combine(sub, "wv2_fetch_helper.exe"));
        Touch(Path.Combine(sub, "wv2_fetch_helper.dll"));

        Assert.Equal(Path.Combine(sub, "wv2_fetch_helper.exe"), WikiExportService.LocateHelperIn(dir));
    }

    [Fact]
    public void LocateHelperIn_平铺完整部署_返回根目录exe()
    {
        var dir = NewTempDir();
        Touch(Path.Combine(dir, "wv2_fetch_helper.exe"));
        Touch(Path.Combine(dir, "wv2_fetch_helper.dll"));

        Assert.Equal(Path.Combine(dir, "wv2_fetch_helper.exe"), WikiExportService.LocateHelperIn(dir));
    }

    [Fact]
    public void LocateHelperIn_两种布局并存_优先独立子目录()
    {
        var dir = NewTempDir();
        var sub = Path.Combine(dir, "wv2_fetch_helper");
        Directory.CreateDirectory(sub);
        Touch(Path.Combine(sub, "wv2_fetch_helper.exe"));
        Touch(Path.Combine(sub, "wv2_fetch_helper.dll"));
        Touch(Path.Combine(dir, "wv2_fetch_helper.exe")); // 根目录那份是旧版残留孤岛，不应被选中
        Touch(Path.Combine(dir, "wv2_fetch_helper.dll"));

        Assert.Equal(Path.Combine(sub, "wv2_fetch_helper.exe"), WikiExportService.LocateHelperIn(dir));
    }

    // ===== 反例：锁住 0x8000809A 的教训——exe 在场但缺托管 dll，必须判为不可用 =====

    [Fact]
    public void LocateHelperIn_只有exe缺dll_视为不可用_避免apphost启动失败()
    {
        var dir = NewTempDir();
        Touch(Path.Combine(dir, "wv2_fetch_helper.exe")); // 没有同名 .dll

        Assert.Null(WikiExportService.LocateHelperIn(dir));
        Assert.True(WikiExportService.HelperDeployIncomplete(dir));
    }

    [Fact]
    public void LocateHelperIn_子目录只有exe缺dll_同样视为不可用()
    {
        var dir = NewTempDir();
        var sub = Path.Combine(dir, "wv2_fetch_helper");
        Directory.CreateDirectory(sub);
        Touch(Path.Combine(sub, "wv2_fetch_helper.exe"));

        Assert.Null(WikiExportService.LocateHelperIn(dir));
        Assert.True(WikiExportService.HelperDeployIncomplete(dir));
    }

    [Fact]
    public void LocateHelperIn_空目录或非法路径_返回null且不算不完整()
    {
        var dir = NewTempDir();
        Assert.Null(WikiExportService.LocateHelperIn(dir));
        Assert.False(WikiExportService.HelperDeployIncomplete(dir));

        Assert.Null(WikiExportService.LocateHelperIn(""));
        Assert.Null(WikiExportService.LocateHelperIn("   "));
    }

    [Fact]
    public void HelperDeployIncomplete_完整部署时为false()
    {
        var dir = NewTempDir();
        var sub = Path.Combine(dir, "wv2_fetch_helper");
        Directory.CreateDirectory(sub);
        Touch(Path.Combine(sub, "wv2_fetch_helper.exe"));
        Touch(Path.Combine(sub, "wv2_fetch_helper.dll"));

        Assert.False(WikiExportService.HelperDeployIncomplete(dir)); // 有 exe 但也有 dll → 完整，不算残缺
    }

    [Fact]
    public void HelperSearchDirs_至少含一个候选目录_且不含空串()
    {
        var dirs = WikiExportService.HelperSearchDirs();

        Assert.NotEmpty(dirs);
        Assert.All(dirs, d => Assert.False(string.IsNullOrWhiteSpace(d)));
    }
}
