// 游戏外批量抓取器（2026-10-14）：把「真实浏览器引擎取数」从游戏进程里搬出来。
//
// 为什么必须有它（证据链，勿删）：
//   ① 游戏内嵌 WebView2 实测：`renderer` 进程活着（CPU 0.3s）、导航 `IsSuccess=True`，
//      但 `ExecuteScriptAsync` 恒返回 `{}` —— **脚本压根没执行**（05:20 诊断日志实证）。
//      1.1.9.6 加的四条防冻结参数（CalculateNativeWinOcclusion / -backgrounding 等）未奏效。
//   ② 同一引擎、同一 profile、同一 URL，**在游戏外 0.2 秒就取回真实词条 JSON**（wv2_probe 实证）。
//   ③ Browsingway（本机已装 1.7.2）印证：它在 `renderer/` 下用**独立进程** + `CefSharp.OffScreen`
//      离屏渲染，再经共享内存回传——**根本不在游戏窗口里建 HWND**，所以全屏与 G-SYNC 下都能用。
//   → 结论：游戏内嵌 WebView2 执行 JS 这条路走不通；抓取必须在游戏外独立进程做。
//     我们只要文本、连画面回传都不需要，比 Browsingway 更简单。
//
// 用法（两种模式）：
//   批量：wv2_fetch_helper --urls=<每行一个 URL 的文件> --outdir=<结果目录>   → 写 0.txt / 1.txt / …
//   单个：wv2_fetch_helper --url=<URL> --outfile=<结果文件>
// 退出码：0=全部成功；1=部分失败（成功的文件仍已落盘）；2=用法/环境错误。
//
// ⚠ 用**独立 profile**（不碰插件那个）：游戏可能正在跑，插件 profile 被占用会让 CreateAsync 静默挂起。
// ⚠ 窗口保持可见：WebView2 没有真正的离屏模式，窗口不可见会被 Chromium 冻结渲染与脚本
//    （这正是游戏内 JS 不执行的同一类成因），故只做成小窗口放屏幕右下角。
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

internal static class Program
{
    private delegate IntPtr WndProcDelegate(IntPtr h, uint m, IntPtr w, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr p, IntPtr m, IntPtr inst, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PeekMessage(out MSG m, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);

    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int SW_SHOW = 5;
    private const uint PM_REMOVE = 0x0001;
    private const uint WM_DESTROY = 0x0002;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int px, py; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate lpfnWndProc;
        public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName; public IntPtr hIconSm;
    }

    private static IntPtr _hwnd;
    private static CoreWebView2Controller? _ctl;
    private static readonly WndProcDelegate WndProcKeep = WndProc;
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "wv2_fetch_helper.log");
    private static bool _hadError;

    private static void Log(string s)
    {
        Console.WriteLine(s);
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss") + " " + s + Environment.NewLine); }
        catch { }
    }

    private static int Main(string[] args)
    {
        try { File.WriteAllText(LogPath, "=== fetch helper " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===" + Environment.NewLine); }
        catch { }

        // 看门狗：绝不让调用方（插件）被吊死
        var wd = new Thread(() => { Thread.Sleep(TimeSpan.FromSeconds(600)); Log("[helper] ★ 看门狗触发（10 分钟），强制退出"); Environment.Exit(3); })
        { IsBackground = true, Name = "helper-watchdog" };
        wd.Start();

        string? urlsFile = null, outDir = null, singleUrl = null, outFile = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--urls=", StringComparison.Ordinal)) urlsFile = a.Substring("--urls=".Length);
            else if (a.StartsWith("--outdir=", StringComparison.Ordinal)) outDir = a.Substring("--outdir=".Length);
            else if (a.StartsWith("--url=", StringComparison.Ordinal)) singleUrl = a.Substring("--url=".Length);
            else if (a.StartsWith("--outfile=", StringComparison.Ordinal)) outFile = a.Substring("--outfile=".Length);
        }

        var jobs = new System.Collections.Generic.List<(string Url, string Out)>();
        if (singleUrl != null && outFile != null)
        {
            jobs.Add((singleUrl, outFile));
        }
        else if (urlsFile != null && outDir != null)
        {
            if (!File.Exists(urlsFile)) { Log("[helper] URL 清单不存在：" + urlsFile); return 2; }
            var lines = File.ReadAllLines(urlsFile);
            Directory.CreateDirectory(outDir);
            var i = 0;
            foreach (var ln in lines)
            {
                var u = ln.Trim();
                if (u.Length == 0) continue;
                jobs.Add((u, Path.Combine(outDir, i + ".txt")));
                i++;
            }
        }
        else
        {
            Log("[helper] 用法：--urls=<清单文件> --outdir=<结果目录>  或  --url=<URL> --outfile=<文件>");
            return 2;
        }

        Log($"[helper] 待抓取 {jobs.Count} 个 URL");

        var code = 0;
        var t = new Thread(() => code = RunJobs(jobs)) { IsBackground = false, Name = "helper-sta" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return code;
    }

    private static IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        => m == WM_DESTROY ? IntPtr.Zero : DefWindowProc(h, m, w, l);

    private static int RunJobs(System.Collections.Generic.List<(string Url, string Out)> jobs)
    {
        // 独立 profile：游戏可能正在跑，插件那个 profile 被占用会让 CreateAsync 静默挂起。
        var profileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncherCN", "pluginConfigs", "FFXIV_Penumbra_Mod_Chinese_Localization_Plugin", "WebView2ProfileFetch");

        var hInst = GetModuleHandle(null);
        var cls = "Wv2Fetch_" + Guid.NewGuid().ToString("N");
        var wc = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = WndProcKeep, hInstance = hInst, lpszClassName = cls };
        if (RegisterClassEx(ref wc) == 0) { Log("[helper] RegisterClassEx 失败 " + Marshal.GetLastWin32Error()); return 2; }

        _hwnd = CreateWindowEx(0, cls, "FFXIV 汉化工具 · 正在抓取 wiki（抓完自动关闭，请勿手动关）",
            WS_OVERLAPPEDWINDOW, 0, 0, 420, 300, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        ShowWindow(_hwnd, SW_SHOW);
        PlaceBottomRight(_hwnd);
        Log("[helper] 宿主窗口 0x" + _hwnd.ToInt64().ToString("X"));

        try
        {
            // 与插件同款软件渲染 + 防冻结（窗口虽小但仍需可见，防 Chromium 冻结脚本）
            var opts = new CoreWebView2EnvironmentOptions("--disable-gpu --disable-gpu-compositing"
                + " --disable-features=CalculateNativeWinOcclusion"
                + " --disable-backgrounding-occluded-windows"
                + " --disable-renderer-backgrounding"
                + " --disable-background-timer-throttling");
            var env = PumpWait(CoreWebView2Environment.CreateAsync(null, profileDir, opts), 45);
            _ctl = PumpWait(env.CreateCoreWebView2ControllerAsync(_hwnd), 45);
            _ctl.Bounds = new System.Drawing.Rectangle(0, 0, 404, 261);
            _ctl.CoreWebView2.ProcessFailed += (_, a) => Log($"[helper] ★ ProcessFailed {a.ProcessFailedKind}/{a.Reason}");
            Log("[helper] 就绪，开始抓取");

            var ok = 0;
            for (var i = 0; i < jobs.Count; i++)
            {
                var (url, outPath) = jobs[i];
                try
                {
                    var text = FetchOne(url);
                    if (text == null)
                    {
                        _hadError = true;
                        Log($"[helper] ✗ [{i}] 失败：{url}");
                        continue;
                    }
                    File.WriteAllText(outPath, text);
                    ok++;
                    if (i % 10 == 0) Log($"[helper] 进度 {i + 1}/{jobs.Count}（成功 {ok}）");
                }
                catch (Exception ex)
                {
                    _hadError = true;
                    Log($"[helper] ✗ [{i}] 异常 {ex.GetType().Name}：{ex.Message}");
                }
            }
            Log($"[helper] 完成：成功 {ok}/{jobs.Count}");
        }
        catch (Exception ex)
        {
            Log("[helper] 致命异常：" + ex.GetType().Name + " " + ex.Message);
            return 2;
        }
        finally
        {
            try { _ctl?.Close(); } catch { }
            if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        }

        return _hadError ? 1 : 0;
    }

    /// <summary> 导航到 URL，取回正文文本（不依赖页内 fetch，避开跨源与 JS 执行的坑）。 </summary>
    private static string? FetchOne(string url)
    {
        var done = false;
        var ok = false;
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e) { done = true; ok = e.IsSuccess; }
        _ctl!.CoreWebView2.NavigationCompleted += Handler;
        try
        {
            _ctl.CoreWebView2.Navigate(url);
            var sw = Stopwatch.StartNew();
            while (!done && sw.Elapsed < TimeSpan.FromSeconds(45)) { PumpMessages(); Thread.Sleep(30); }
            if (!done || !ok) { Log("[helper] 导航失败/超时：" + url); return null; }

            // 给内容解析一点时间（JSON 很大）
            var sw2 = Stopwatch.StartNew();
            while (sw2.Elapsed < TimeSpan.FromSeconds(5)) { PumpMessages(); Thread.Sleep(30); }

            var raw = PumpWait(_ctl.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText : ''"), 30);
            if (string.IsNullOrEmpty(raw) || raw == "\"\"" || raw == "{}") return null;
            // ExecuteScriptAsync 返回的是 JSON 字符串（带引号），去掉首尾引号并反转义
            var text = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        finally
        {
            _ctl.CoreWebView2.NavigationCompleted -= Handler;
        }
    }

    private static void PlaceBottomRight(IntPtr h)
    {
        try
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            var mon = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
            if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
                SetWindowPos(h, IntPtr.Zero, mi.rcWork.right - 440, mi.rcWork.bottom - 340, 0, 0, SWP_NOSIZE | SWP_NOZORDER);
        }
        catch { /* 放不到右下角就算了，不影响抓取 */ }
    }

    private static void PumpMessages()
    {
        while (PeekMessage(out var m, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref m);
            DispatchMessage(ref m);
        }
    }

    private static T PumpWait<T>(Task<T> task, int seconds = 45)
    {
        var sw = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (sw.Elapsed.TotalSeconds > seconds)
                throw new TimeoutException($"WebView2 异步操作 {seconds}s 未返回（多半是 profile 被占用）");
            PumpMessages();
            Thread.Sleep(10);
        }
        if (task.IsFaulted) throw task.Exception!.InnerException!;
        return task.Result;
    }
}
