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
/// 架构（2026-10-11 经游戏外独立复现程序实测校正，三条铁律）：
/// ① <b>线程亲和</b>：<c>CoreWebView2Controller</c> 的 COM 接口绑定创建它的 OS 线程，
///    跨线程访问抛 <c>E_NOINTERFACE</c>。故给该 STA 线程装 <b>专用 SynchronizationContext</b>，
///    使所有 await 续体都回到本线程——否则必跨线程失败。
/// ② <b>投递=排空</b>：自定义 SyncContext 的 Post 必须投到<b>消息泵真正会排空的那个队列</b>，
///    且每次投递后必须 <b>PostMessage 唤醒</b>（消息泵阻塞在 GetMessage，不唤醒则永不消费）。
///    —— 前一版就是踩了「投 A 队列、泵排 B 队列」导致 await 永久卡死。
/// ③ <b>起手 about:blank</b>：直接 Navigate 到 wiki 页面会被 CF 拦（NavigationCompleted IsSuccess=False）；
///    先停在 about:blank（必定成功）待命。但 about:blank 是<b>不透明源</b>，页内 fetch wiki 属<b>跨源</b>、
///    必被 CORS 拒（2026-10-13 实测：<c>ExecuteScriptAsync</c> 返回非字符串 → 旧代码抛 JsonException）——
///    故取数前须先导航到 <b>wiki 同源页</b>（见 <see cref="ShowCaptchaWindow"/>：弹可见窗口请司令官过 CF，
///    导航完成后 WebView2 即停在同源页，后续 fetch 同源可取，cf_clearance 亦在持久 profile 内复用）。
/// </para>
/// <para>
/// 失败一律置 <see cref="IsAvailable"/>=false，由调用方回退 curl 路径——不会让功能更差。
/// </para>
/// </summary>
internal sealed class WebView2WikiFetcher : IDisposable
{
    private delegate IntPtr WndProcDelegate(IntPtr h, uint m, IntPtr w, IntPtr l);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
        public uint time; public int px, py;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int ex, string? cls, string? name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr i, IntPtr c);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? n);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private const string WndClassName = "PMHWebView2FetcherV2";
    private const uint WM_APP = 0x8000;
    private const uint WM_APP_PUMP = WM_APP + 1;
    private const uint WM_CLOSE = 0x0010;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;
    private const int IDC_ARROW = 32512;

    /// <summary> 验证窗口尺寸。窗口按此尺寸创建但默认隐藏（WS_OVERLAPPEDWINDOW 无 WS_VISIBLE）；
    /// WebView2 内容大小随宿主窗口而定，故验证时只需 MoveWindow + ShowWindow，无需重设控制器 Bounds。 </summary>
    private const int CaptchaWidth = 1100;
    private const int CaptchaHeight = 800;

    /// <summary> 消息泵唯一队列：SyncContext 投递与消息泵排空必须用同一个（铁律②）。 </summary>
    private static readonly ConcurrentQueue<Action> PumpQueue = new();

    private static WndProcDelegate? _wndProcKeep;
    private IntPtr _hwnd;

    private CoreWebView2Controller? _ctl;
    private volatile bool _available;
    private volatile string? _lastError;
    private Thread? _thread;
    private volatile bool _disposed;
    private volatile bool _captchaPending;
    private readonly AppLog _log;

    public bool IsAvailable => _available;
    public string? LastError => _lastError;

    /// <summary>
    /// 用户数据目录（WebView2 profile）。<b>必须是持久目录</b>：cf_clearance 等 cookie 存在 profile 里，
    /// 用临时/每次新建的目录等于每次都是「零 cookie 全新浏览器」→ 被 CF 当 bot 拦（2026-10-11 实测）。
    /// 持久化后，司令官手动过一次验证留下的 cookie 会一直复用，后续抓取自动带上。
    /// </summary>
    private readonly string _profileDir;

    public WebView2WikiFetcher(AppLog log, string profileDir)
    {
        _log = log;
        _profileDir = profileDir;
    }

    /// <summary> 抓取时发现是 Cloudflare 验证页、且当前没有可用 cookie 时触发，供 UI 弹窗请司令官手动过一次。 </summary>
    public Action? OnCaptchaNeeded { get; set; }

    /// <summary> 验证窗口被用户关闭时触发（供 UI 从断点自动续跑）。 </summary>
    public Action? OnCaptchaWindowClosed { get; set; }

    /// <summary> 是否正处于「等待人工验证」状态（fetch 因跨源/挑战页失败且已通知 UI 弹窗）。 </summary>
    public bool CaptchaPending => _captchaPending;

    /// <summary> 启动抓取器（独立 STA 线程）；立即返回，不阻塞调用方。 </summary>
    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "PMH-WebView2Fetcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>
    /// 用 WebView2 取一个 URL 的文本响应（JSON）。不可用/失败返回 null（调用方回退 curl）。
    /// <para>
    /// 关键：整个请求通过 <b>BlockingCollection</b> 同步「投递到 WebView2 线程 → 等结果」，
    /// 保证 ExecuteScriptAsync 在该线程执行（铁律①）；但**投递的 async lambda 内部
    /// 必须 await 后回到同线程**——靠专用 SyncContext（铁律②）。
    /// </para>
    /// </summary>
    public string? Fetch(string url, CancellationToken ct)
    {
        if (_disposed || !_available || _ctl == null) return null;

        using var done = new ManualResetEventSlim(false);
        string? result = null;
        Exception? error = null;

        // 投递到 WebView2 线程执行
        PumpQueue.Enqueue(async () =>
        {
            try
            {
                var script = $"fetch({JsonSerializer.Serialize(url)}).then(r=>r.text()).catch(e=>'__WV2ERR__'+e)";
                var raw = await _ctl!.CoreWebView2.ExecuteScriptAsync(script).ConfigureAwait(true);
                switch (ClassifyRaw(raw, out var text))
                {
                    case Wv2FetchOutcome.JsError:
                        error = new InvalidOperationException(text!.Substring("__WV2ERR__".Length));
                        break;
                    case Wv2FetchOutcome.SameOriginNeeded:
                        // 返回值不是 JSON 字符串（典型为 null）：当前页（起手 about:blank 是不透明源）与 wiki
                        // 不同源，页内 fetch 被跨源策略拒，请求根本没到达服务器（2026-10-13 实测形态）。
                        // 解法：弹验证窗口先导航到 wiki 同源页（请司令官过 CF），之后重试即同源可取。
                        EnterCaptchaPending("fetch 未取得文本响应（当前页与 wiki 不同源，跨源 fetch 被拒，需先导航到 wiki 同源页）");
                        result = null;
                        break;
                    default:
                        result = text;
                        if (text != null && CloudflareChallengeDetector.IsChallenge(text))
                        {
                            // 仍是挑战页 → 本 profile 还没有通过验证的 cookie。
                            // 触发人工验证：请司令官在可见的 WebView2 窗口里点一次 CF 验证，
                            // cookie 存进持久 profile，之后自动抓取即可复用。
                            EnterCaptchaPending("返回 Cloudflare 挑战页");
                        }
                        else
                        {
                            _captchaPending = false; // 取到数据：同源与 cookie 均已就绪
                            _log.Info($"[wv2] fetch 成功，返回 {text?.Length ?? 0} 字符");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                error = ex;
                _log.Error($"[wv2] fetch 失败：{ex.GetType().Name} {Truncate(ex.Message, 120)}");
            }
            finally
            {
                done.Set();
            }
        });
        WakePump(); // 铁律②：不唤醒则消息泵阻塞在 GetMessage，队列永不被消费

        // 在本线程等结果（最多 30s），不占用 WebView2 线程
        if (!done.Wait(TimeSpan.FromSeconds(30), ct))
        {
            _log.Warn("[wv2] fetch 等待超时（30s）");
            return null;
        }
        if (error != null) return null;
        return result;
    }

    /// <summary>
    /// 在 fetcher 的 WebView2 线程显示窗口并导航到 wiki 页，请司令官手动过一次 Cloudflare 验证。
    /// 验证通过后 cf_clearance cookie 存入<b>持久 profile</b>，且 WebView2 停在 wiki <b>同源页</b>——
    /// 之后页内 fetch 不再受跨源限制，自动抓取可直接复用，不再需要验证。
    /// 立即返回（异步导航）；日志会记录验证结果。
    /// </summary>
    public void ShowCaptchaWindow(string url)
    {
        if (_disposed || _ctl == null) return;
        PumpQueue.Enqueue(() =>
        {
            try
            {
                _log.Info("[wv2] 显示验证窗口，请司令官在窗口内完成 Cloudflare 验证…");
                // 窗口按验证尺寸创建但默认隐藏：居中放大并显示（WebView2 内容已随窗口大小就绪）
                var x = Math.Max(0, (GetSystemMetrics(0) - CaptchaWidth) / 2);
                var y = Math.Max(0, (GetSystemMetrics(1) - CaptchaHeight) / 2);
                MoveWindow(_hwnd, x, y, CaptchaWidth, CaptchaHeight, true);
                ShowWindow(_hwnd, SW_SHOW);
                _captchaPending = false; // 窗口已弹出，进入「用户操作中」；关闭时再清一次并通知 UI 续跑
                _ctl!.CoreWebView2.Navigate(url);
                // 监听：一旦导航到非挑战内容，视为验证通过
                _ctl.CoreWebView2.NavigationCompleted += OnCaptchaNavCompleted;
            }
            catch (Exception ex)
            {
                _log.Error($"[wv2] 显示验证窗口失败：{ex.GetType().Name} {Truncate(ex.Message, 120)}");
            }
        });
        WakePump();
    }

    /// <summary> 进入「等待人工验证」状态并通知 UI 弹窗（每轮只通知一次；弹窗/关窗/取到数据时清状态）。 </summary>
    private void EnterCaptchaPending(string reason)
    {
        if (_captchaPending) return;
        _captchaPending = true;
        _log.Warn($"[wv2] {reason}——需要司令官手动过一次 Cloudflare 验证（将弹出验证窗口）");
        try { OnCaptchaNeeded?.Invoke(); } catch { /* UI 未挂载则忽略 */ }
    }

    internal enum Wv2FetchOutcome
    {
        /// <summary> 取到文本（可能是数据，也可能是挑战页——由调用方再判）。 </summary>
        Data,
        /// <summary> 页内 fetch 抛错被 .catch 捕获（文本以 __WV2ERR__ 开头）。 </summary>
        JsError,
        /// <summary> 返回值不是 JSON 字符串（典型为 null）：跨源 fetch 被拒/整体拒绝，需先导航到同源页。 </summary>
        SameOriginNeeded,
    }

    /// <summary>
    /// 判别 <c>ExecuteScriptAsync</c> 的原始返回值（纯函数，可单测；2026-10-13 实测据此定位跨源问题）。
    /// WebView2 约定：脚本返回 Promise 时等其兑现后返回「兑现值的 JSON」；若被拒/无值则返回 <c>null</c>。
    /// </summary>
    internal static Wv2FetchOutcome ClassifyRaw(string? raw, out string? text)
    {
        text = null;
        if (string.IsNullOrEmpty(raw)) return Wv2FetchOutcome.SameOriginNeeded;
        try
        {
            var s = JsonSerializer.Deserialize<string>(raw);
            if (s == null) return Wv2FetchOutcome.SameOriginNeeded;
            text = s;
            return s.StartsWith("__WV2ERR__", StringComparison.Ordinal)
                ? Wv2FetchOutcome.JsError
                : Wv2FetchOutcome.Data;
        }
        catch (JsonException)
        {
            // 非字符串 JSON 令牌（null/数字/布尔/对象…）：典型即跨源 fetch 被拒后 ExecuteScriptAsync 返回 null
            return Wv2FetchOutcome.SameOriginNeeded;
        }
    }

    private void OnCaptchaNavCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            if (e.IsSuccess)
            {
                _log.Info("[wv2] 验证页导航完成——若页面已是 wiki 内容则验证通过（cookie 已存入 profile）");
            }
        }
        catch { /* 忽略 */ }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary> 专用线程主体：装专用 SyncContext → 起手窗口 → 启动 WebView2 初始化 → 跑消息泵。 </summary>
    private void ThreadMain()
    {
        try
        {
            // 铁律①：先装专用 context，让本线程上所有 await 续体都回到本线程（不跨线程碰 COM）
            SynchronizationContext.SetSynchronizationContext(new PumpSyncContext(this));
            EnsureWindow();
            _ = InitWebView2Async();
            Pump();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _available = false;
            _log.Error($"[wv2] 抓取线程异常：{ex.GetType().Name} {Truncate(ex.Message, 150)}");
        }
    }

    private void EnsureWindow()
    {
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
        RegisterClassEx(ref wc); // 可能已注册（热重载残留），返回 0 不致命
        // 按验证窗口尺寸创建（默认不可见；WebView2 内容大小随宿主窗口而定，验证时只需 MoveWindow+Show）
        _hwnd = CreateWindowEx(0, WndClassName, "wv2", WS_OVERLAPPEDWINDOW,
            0, 0, CaptchaWidth, CaptchaHeight, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
    }

    private IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == WM_APP_PUMP)
        {
            while (PumpQueue.TryDequeue(out var a))
            {
                try { a(); } catch (Exception ex) { _log.Error($"[wv2] 队列任务异常：{ex.GetType().Name}"); }
            }
        }
        else if (m == WM_CLOSE)
        {
            // 用户关闭验证窗口：不销毁（WebView2 与持久 profile 要留着复用），只隐藏，
            // 并通知外部「验证窗口已关闭」→ UI 从断点自动续跑（此时 WebView2 已停在 wiki 同源页）。
            ShowWindow(h, SW_HIDE);
            _captchaPending = false;
            try { OnCaptchaWindowClosed?.Invoke(); } catch { /* UI 未挂载则忽略 */ }
            return IntPtr.Zero;
        }
        return DefWindowProc(h, m, w, l);
    }

    private void Pump()
    {
        var deadline = DateTime.UtcNow.AddSeconds(300);
        while (!_disposed && DateTime.UtcNow < deadline)
        {
            if (GetMessage(out var m, IntPtr.Zero, 0, 0) <= 0) break;
            TranslateMessage(ref m);
            DispatchMessage(ref m);
        }
    }

    /// <summary> 在 WebView2 线程完成初始化：环境 → 控制器 → 停在 about:blank（铁律③）。 </summary>
    private async Task InitWebView2Async()
    {
        try
        {
            // 持久 profile 目录（插件配置目录内，随插件存续）
            Directory.CreateDirectory(_profileDir);
            _log.Info("[wv2] 使用持久 profile：" + _profileDir);

            var options = WebView2RuntimeProbe.TryGetRuntimeExecutableFolder();
            _log.Info("[wv2] 正在创建 WebView2 环境…"
                      + (options != null ? "（显式指定 runtime：" + options + "）" : "（自动探测 runtime）"));

            CoreWebView2Environment env;
            if (options != null) env = await CoreWebView2Environment.CreateAsync(options, _profileDir);
            else env = await CoreWebView2Environment.CreateAsync(_profileDir);

            _ctl = await env.CreateCoreWebView2ControllerAsync(_hwnd);
            _log.Info("[wv2] 控制器已绑定，停在 about:blank…");

            var blank = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ctl.CoreWebView2.NavigationCompleted += (_, a) => blank.TrySetResult(a.IsSuccess);
            _ctl.CoreWebView2.Navigate("about:blank");
            try { await blank.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch { _log.Warn("[wv2] about:blank 就绪等待超时（仍尝试 fetch）"); }

            _available = true;
            _log.Info("[wv2] WebView2 直抓已就绪（真实浏览器指纹，CDN 应放行）");
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _available = false;
            _log.Error($"[wv2] WebView2 初始化失败：{ex.GetType().Name} {Truncate(ex.Message, 150)}");
        }
    }

    private void WakePump()
    {
        var h = _hwnd;
        if (h != IntPtr.Zero) PostMessage(h, WM_APP_PUMP, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        _disposed = true;
        try { if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; } } catch { /* 忽略 */ }
        _available = false;
    }

    /// <summary> 把 await 续体投递回 WebView2 线程（铁律②：投 PumpQueue + 唤醒消息泵）。 </summary>
    private sealed class PumpSyncContext : SynchronizationContext
    {
        private readonly WebView2WikiFetcher _owner;
        public PumpSyncContext(WebView2WikiFetcher owner) => _owner = owner;

        public override void Post(SendOrPostCallback d, object? state)
        {
            PumpQueue.Enqueue(() => d(state));
            _owner.WakePump();
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (ReferenceEquals(Current, this)) { d(state); return; }
            Post(d, state);
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}