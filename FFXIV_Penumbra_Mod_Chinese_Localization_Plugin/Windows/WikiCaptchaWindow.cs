using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace FFXIVPenumbraHanhua.Windows;

/// <summary>
/// 独立的 Cloudflare 验证窗口：用原生 Win32 窗口承载 WebView2，加载被拦的 wiki URL，
/// 用户手动过点后从 WebView2 的 CookieManager 读出 cf_clearance 交回插件（由 <see cref="WikiExportService.SetCaptchaCookie"/> 注入）。
/// <para>
/// 设计要点：
/// ① 必须在 **Dalamud 主线程**（ImGui Draw 所在、且跑 Win32 消息泵的线程）创建与初始化 WebView2；
/// ② <see cref="Start"/> 内以 fire-and-forget 方式启动异步初始化，**不阻塞**主线程渲染——
///    异步期间主线程继续每帧泵消息，WebView2 初始化在多帧间完成，用户可在窗口内点验证；
/// ③ 取得 cookie 后由 UI 每帧 <see cref="TakeCookie"/> 轮询；④ 全路径 try/catch，
///    WebView2 运行时缺失/初始化失败时置 <see cref="Failed"/>，由 UI 回退到旧行为（被拦即中止），绝不抛出到游戏帧循环。
/// </para>
/// <para>
/// ⚠ 实机限制：本类型的运行时行为（Dalamud 主线程 + WebView2 消息泵协作）无法在无游戏环境验证，
///   需在游戏内实测。故 UI 侧默认**关闭**该功能（配置 WikiCaptchaVerify），出问题一键关掉即回到现状。
/// </para>
/// </summary>
internal sealed class WikiCaptchaWindow : IDisposable
{
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string? lpClassName, string? lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private const string ClassName = "PMHWikiCaptchaWindow";
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int SW_SHOW = 5;
    private const int IDC_ARROW = 32512;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;

    private static bool _classRegistered;
    private static WndProcDelegate? _wndProcKeepAlive; // 委托须保持引用，否则被 GC 后窗口过程失效
    private static WikiCaptchaWindow? _current;         // 当前活动实例（窗口过程为静态，回调经此访问状态）

    private IntPtr _hwnd;
    private CoreWebView2Controller? _controller;
    private volatile string? _cookie;
    private volatile bool _failed;
    private volatile bool _closedByUser;

    /// <summary> 窗口是否仍在（等待用户过点）。 </summary>
    public bool IsActive => _hwnd != IntPtr.Zero;

    /// <summary> 是否已拿到 cf_clearance。 </summary>
    public bool HasCookie => _cookie != null;

    /// <summary> WebView2 初始化/运行失败（应回退旧行为并关闭本窗口）。 </summary>
    public bool Failed => _failed;

    /// <summary> 用户主动关闭了窗口（放弃验证）。 </summary>
    public bool ClosedByUser => _closedByUser;

    /// <summary> 启动验证窗口并异步初始化 WebView2；立即返回，不阻塞调用方。须在主线程调用。 </summary>
    public void Start(string url)
    {
        try
        {
            _current = this;
            if (!EnsureClass())
            {
                _failed = true;
                return;
            }
            var hInst = GetModuleHandle(null);
            _hwnd = CreateWindowEx(0, ClassName, "wiki 验证 — 请在窗口内完成 Cloudflare 验证",
                WS_OVERLAPPEDWINDOW, 220, 160, 680, 820, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                _failed = true;
                return;
            }
            ShowWindow(_hwnd, SW_SHOW);
            _ = InitAsync(url); // fire-and-forget：异步初始化，主线程继续渲染/泵消息
        }
        catch
        {
            _failed = true;
        }
    }

    private static bool EnsureClass()
    {
        if (_classRegistered) return true;
        var hInst = GetModuleHandle(null);
        _wndProcKeepAlive = WndProcImpl;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProcKeepAlive,
            hInstance = hInst,
            hCursor = LoadCursor(IntPtr.Zero, (IntPtr)IDC_ARROW),
            lpszClassName = ClassName,
        };
        if (RegisterClassEx(ref wc) == 0) return false;
        _classRegistered = true;
        return true;
    }

    private static IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _current;
        switch (msg)
        {
            case WM_CLOSE:
                if (self != null) self._closedByUser = true;
                DestroyWindow(hWnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                if (self != null) self._hwnd = IntPtr.Zero;
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private async Task InitAsync(string url)
    {
        try
        {
            var dataFolder = Path.Combine(Path.GetTempPath(), "pmh_wiki_captcha_webview");
            Directory.CreateDirectory(dataFolder);
            var env = await CoreWebView2Environment.CreateAsync(dataFolder);
            if (_hwnd == IntPtr.Zero) return;
            _controller = await env.CreateCoreWebView2ControllerAsync(_hwnd);
            var settings = _controller.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            _controller.CoreWebView2.Navigate(url);
            _ = PollCookieAsync(url);
        }
        catch
        {
            _failed = true;
        }
    }

    private async Task PollCookieAsync(string url)
    {
        try
        {
            // 最多轮询约 5 分钟（600×500ms），期间用户可在窗口内完成 Cloudflare 验证
            for (var i = 0; i < 600 && _cookie == null && !_failed && !_closedByUser && _hwnd != IntPtr.Zero; i++)
            {
                await Task.Delay(500);
                if (_controller?.CoreWebView2 == null) continue;
                var cookies = await _controller.CoreWebView2.CookieManager.GetCookiesAsync(url);
                foreach (var c in cookies)
                {
                    if (c.Name == "cf_clearance" && !string.IsNullOrEmpty(c.Value))
                    {
                        _cookie = c.Value;
                        break;
                    }
                }
            }
        }
        catch
        {
            _failed = true;
        }
    }

    /// <summary> 取已拿到的 cf_clearance 值（未拿到返回 null）。 </summary>
    public string? TakeCookie() => _cookie;

    public void Dispose()
    {
        try { _controller?.Close(); } catch { /* 忽略 */ }
        try { if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd); } catch { /* 忽略 */ }
        _hwnd = IntPtr.Zero;
        _controller = null;
        _cookie = null;
        if (_current == this) _current = null;
    }
}
