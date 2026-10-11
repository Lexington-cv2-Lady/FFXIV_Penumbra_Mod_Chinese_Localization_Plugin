using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace FFXIVPenumbraHanhua.Services;

/// <summary>
/// WebView2 版 wiki 抓取器：用**真实浏览器引擎**（而非 curl/.NET）发请求，
/// 携带浏览器 TLS 指纹，从而绕开灰机 CDN 对非浏览器客户端的 Cloudflare 拦截。
/// <para>
/// 架构（保守优先，2026-10-11）：
/// ① 跑在**独立 STA 线程 + 隐藏消息窗口 + 自有 Win32 消息泵**上，**完全不进入 Dalamud 主线程/ImGui 帧循环**——
///    即使 WebView2 出问题也只波及本线程，不影响游戏渲染与插件帧循环；
/// ② 抓取前先导航到 wiki 同源页，再在页内用 <c>fetch</c> 同源取数（规避 CORS）；
/// ③ 全程 try/catch，任何失败都置 <see cref="IsAvailable"/>=false，由调用方回退到原 curl 路径——
///    所以「WebView2 不可用」只会退回现状，不会让功能更差。
/// </para>
/// <para>
/// ⚠ 实机限制：WebView2 运行时行为无法在无游戏环境验证，需游戏内实测。
/// </para>
/// </summary>
internal sealed class WebView2WikiFetcher : IDisposable
{
    // ---- Win32 消息窗口与消息泵 ----
    private const string WndClassName = "PMHWebView2Fetcher";
    private const uint WM_APP = 0x8000;
    private const uint WM_APP_PUMP = WM_APP + 1;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int SW_HIDE = 0;
    private const int IDC_ARROW = 32512;

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
    private delegate IntPtr WndProcDelegate(IntPtr h, uint m, IntPtr w, IntPtr l);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int ex, string? cls, string? name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr i, IntPtr c);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? n);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public System.Drawing.Point pt;
    }
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);

    // 供 SyncContext 投递的回调队列 + 泵唤醒
    private static readonly ConcurrentQueue<Action> PumpQueue = new();
    private static IntPtr _pumpWnd;
    private static WndProcDelegate? _wndProcKeep;

    // ---- WebView2 状态（仅在本类 STA 线程访问，跨线程经由 FetchAsync 的调度）----
    private readonly ConcurrentQueue<Action> _initQueue = new();
    private CoreWebView2Controller? _ctl;
    private TaskCompletionSource<bool>? _readyTcs;
    private volatile bool _available;
    private volatile string? _lastError;
    private Thread? _thread;
    private IntPtr _hwnd;
    private bool _disposed;

    public bool IsAvailable => _available;
    public string? LastError => _lastError;

    /// <summary> wiki 同源页（先导航到它，后续 fetch 同源，避免 CORS）。 </summary>
    private const string WikiOriginUrl = "https://cdn.huijiwiki.com/ff14/";

    /// <summary> 启动抓取器（独立线程）；立即返回，不阻塞调用方。 </summary>
    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "PMH-WebView2Fetcher",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>
    /// 用 WebView2 取一��� URL 的文本响应（JSON）。不可用/失败返回 null（调用方回退 curl）。
    /// </summary>
    public async Task<string?> FetchAsync(string url, CancellationToken ct)
    {
        if (_disposed || !_available) return null;
        // 首次要等初始化 + 同源导航就绪
        var ready = _readyTcs?.Task;
        if (ready == null) return null;
        try
        {
            using var reg = ct.Register(() => { });
            var done = await ready.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (!done) return null;
        }
        catch
        {
            return null;
        }

        // 把 fetch 调度到 WebView2 线程（必须在该线程访问 Controller）
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _initQueue.Enqueue(async () =>
        {
            try
            {
                var script = $"fetch({JsonSerializer.Serialize(url)}).then(r=>r.text())";
                var raw = await _ctl!.CoreWebView2.ExecuteScriptAsync(script);
                // ExecuteScriptAsync 返回 JSON 序列化的 JS 结果（一个字符串）→ 反序列化还原
                tcs.TrySetResult(JsonSerializer.Deserialize<string>(raw));
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                tcs.TrySetResult(null);
            }
        });
        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary> 专用线程主体：设置同步上下文 → 启动 WebView2 初始化 → 跑消息泵。 </summary>
    private void ThreadMain()
    {
        try
        {
            SynchronizationContext.SetSynchronizationContext(new PumpSyncContext(this));
            EnsureWindow();
            _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // 初始化（async；其 await 会经 PumpSyncContext 回到本线程继续）
            _ = InitWebView2Async();
            MessagePump();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _available = false;
        }
    }

    private void EnsureWindow()
    {
        if (_pumpWnd != IntPtr.Zero) { _hwnd = _pumpWnd; return; }
        var hInst = GetModuleHandle(null);
        _wndProcKeep = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProcKeep,
            hInstance = hInst,
            hCursor = LoadCursor(IntPtr.Zero, (IntPtr)IDC_ARROW),
            lpszClassName = WndClassName,
        };
        // 类名可能已被本进程注册（热重载残留）→ RegisterClassEx 返回 0 不致命，继续建窗口
        RegisterClassEx(ref wc);
        _pumpWnd = CreateWindowEx(0, WndClassName, "wv2", WS_OVERLAPPEDWINDOW,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        ShowWindow(_pumpWnd, SW_HIDE);
        _hwnd = _pumpWnd;
    }

    private static IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == WM_APP_PUMP)
        {
            while (PumpQueue.TryDequeue(out var a))
            {
                try { a(); } catch { /* 忽略单条异常 */ }
            }
        }
        return DefWindowProc(h, m, w, l);
    }

    private void MessagePump()
    {
        while (!_disposed && GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref m);
            DispatchMessage(ref m);
        }
    }

    /// <summary> 在 WebView2 线程上完成初始化：建 controller → 导航同源页 → 就绪。 </summary>
    private async Task InitWebView2Async()
    {
        try
        {
            var dataFolder = Path.Combine(Path.GetTempPath(), "pmh_wiki_webview");
            Directory.CreateDirectory(dataFolder);
            var env = await CoreWebView2Environment.CreateAsync(dataFolder);
            _ctl = await env.CreateCoreWebView2ControllerAsync(_hwnd);
            var nav = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ctl.CoreWebView2.NavigationCompleted += (_, a) =>
            {
                if (a.IsSuccess) nav.TrySetResult(true);
                else nav.TrySetResult(false);
            };
            _ctl.CoreWebView2.Navigate(WikiOriginUrl);
            var ok = await nav.Task;
            if (!ok)
            {
                _lastError = "WebView2 导航同源页失败";
                _readyTcs?.TrySetResult(false);
                return;
            }
            _available = true;
            _readyTcs?.TrySetResult(true);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _available = false;
            _readyTcs?.TrySetResult(false);
        }
    }

    /// <summary> 唤醒消息泵取队列（须在 WebView2 线程外的调用点使用）。 </summary>
    private void WakePump()
    {
        var h = _pumpWnd;
        if (h != IntPtr.Zero) PostMessage(h, WM_APP_PUMP, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        _disposed = true;
        try { if (_pumpWnd != IntPtr.Zero) { DestroyWindow(_pumpWnd); _pumpWnd = IntPtr.Zero; } } catch { }
        _available = false;
    }

    /// <summary> 把 async 延续投递回 WebView2 线程的消息泵。 </summary>
    private sealed class PumpSyncContext : SynchronizationContext
    {
        private readonly WebView2WikiFetcher _owner;
        public PumpSyncContext(WebView2WikiFetcher owner) => _owner = owner;

        public override void Post(SendOrPostCallback d, object? state)
        {
            // async 延续
            _owner._initQueue.Enqueue(() => d(state));
            _owner.WakePump();
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            // 同线程调用直接执行
            if (Current == this) { d(state); return; }
            Post(d, state);
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
