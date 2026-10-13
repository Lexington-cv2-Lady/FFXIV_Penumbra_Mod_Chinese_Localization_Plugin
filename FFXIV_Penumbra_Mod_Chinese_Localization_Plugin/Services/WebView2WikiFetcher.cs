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
/// ④ <b>窗口类名每实例唯一</b>（<see cref="NewWindowClassName"/>）：窗口类是<b>进程级全局命名资源</b>，
///    固定类名在热重载后 RegisterClassEx 必撞名（1410）——新窗口沿用旧实例的 WndProc，排空的是旧 DLL
///    实例的静态队列，本实例 await 续体（含超时兜底）全部饿死且毫无日志（2026-10-13 12:47 实测，
///    游戏外复现程序证实）。与 10-11 验证窗口类名唯一化是同一课。
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

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
    [DllImport("kernel32.dll")] private static extern uint GetLastError();
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT rc);

    private const uint WM_APP = 0x8000;
    private const uint WM_APP_PUMP = WM_APP + 1;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_QUIT = 0x0012;
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

    /// <summary>
    /// 生成<b>每实例唯一</b>的窗口类名（铁律④）。窗口类是进程级全局命名资源，固定类名在热重载后
    /// 必撞名（RegisterClassEx 1410）→ 新窗口消息派发到旧实例 WndProc → 本实例队列饿死、await 静默卡死。
    /// </summary>
    internal static string NewWindowClassName() => "PMHWebView2FetcherV2_" + Guid.NewGuid().ToString("N");

    private readonly string _wndClassName = NewWindowClassName();

    private static WndProcDelegate? _wndProcKeep;
    private IntPtr _hwnd;

    private CoreWebView2Controller? _ctl;
    private volatile bool _available;
    private volatile string? _lastError;
    private Thread? _thread;
    private volatile bool _disposed;
    private volatile bool _captchaPending;
    private volatile bool _captchaWindowVisible; // 验证窗口正在显示（司令官操作中）：此时消息泵永不空闲自退
    private long _lastPumpActivityTicks;         // 上次业务投递（fetch/弹窗）时间：空闲自退判据（Interlocked 读写）
    private Timer? _watchdog;
    private int _watchdogCount;
    private Timer? _reaper;                       // 空闲回收定时器（线程池，不依赖消息泵）
    private readonly AppLog _log;

    /// <summary> 消息泵空闲自退阈值：超过此时长无业务投递且验证窗口不可见 → STA 线程自行退出省内存；下次提取自动重启。 </summary>
    private static readonly TimeSpan PumpIdleExitLimit = TimeSpan.FromMinutes(10);

    /// <summary> 上次业务投递时间（跨线程安全：Interlocked 读写 ticks——volatile 不适用于 DateTime 结构体）。 </summary>
    private DateTime LastPumpActivity
    {
        get => new(Interlocked.Read(ref _lastPumpActivityTicks));
        set => Interlocked.Exchange(ref _lastPumpActivityTicks, value.Ticks);
    }

    private void TouchPumpActivity() => LastPumpActivity = DateTime.UtcNow;

    public bool IsAvailable => _available;
    public string? LastError => _lastError;

    /// <summary>
    /// 把抓取器标记为不可用（失能）：后续请求直接回退 curl，不再每页白等 30 秒。
    /// 典型场景：WebView2 控制器僵死（僵尸实例拖住共享浏览器进程，2026-10-13 14:16 实测
    /// fetch 卡死 30s）——此时等待毫无意义，失能后 <see cref="IsAvailable"/> 为 false，
    /// 调用方立即走 curl 路径，整个提取不再被单点拖垮。幂等：重复标记不刷日志。
    /// </summary>
    internal void MarkUnavailable(string reason)
    {
        if (!_available && _lastError == reason) return;
        var was = _available;
        _available = false;
        _lastError = reason;
        if (was)
            _log.Error($"[wv2] {reason}——WebView2 已标记不可用，后续抓取回退 curl；重新加载插件可恢复直抓");
    }

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

    /// <summary> 启动抓取器（独立 STA 线程）；立即返回，不阻塞调用方。线程已死时自动重起（空闲自退后的懒重启）。 </summary>
    public void Start()
    {
        if (_thread?.IsAlive == true) return; // 判活而非判非 null：空闲自退后下次提取要能重起
        TouchPumpActivity();
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "PMH-WebView2Fetcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        // 看门狗（线程池定时器，**不依赖消息泵**）：初始化一旦因泵饿死之类的原因卡住，
        // await 续体连「15s 超时」日志都不会打（2026-10-13 12:47 静默 30 分钟的教训）——
        // 由它定期上报线程/窗口/错误态，把静默卡死变成即时诊断。就绪或卸载后自动停。
        _watchdog ??= new Timer(_ => WatchdogTick(), null, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(30));
        // 空闲回收（同为线程池定时器）：稳态下长期无提取时把 STA 线程与 WebView2 进程组收掉省内存；
        // 2026-10-13 16:16 事故的教训——线程寿命绝不能压过「用户正在过 CF 验证」的时长，
        // 故自退条件必须排除验证窗口可见期，且只由不依赖消息泵的定时器执行。
        _reaper ??= new Timer(_ => ReaperTick(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// 消息泵空闲自退判据（纯函数，可单测）。
    /// 2026-10-13 16:16 事故：原 Pump 带 300 秒绝对 deadline，用户正在过 Cloudflare 验证时
    /// （16:16:13 弹窗）线程于 16:16:55 到期静默退出 → Windows 销毁线程窗口 → WebView2 进程组
    /// 清零 → 全程无日志。故「线程活着」必须以业务活动为准，不得有与用户操作竞速的绝对上限。
    /// </summary>
    internal static bool PumpShouldReap(DateTime nowUtc, DateTime lastActivityUtc, bool captchaWindowVisible, TimeSpan idleLimit)
        => !captchaWindowVisible && (nowUtc - lastActivityUtc) >= idleLimit;

    /// <summary>
    /// 确保抓取线程在场：空闲自退（或初始化失败退出）后，下次提取自动重启并等待就绪。
    /// 线程活着但不重起（哪怕未就绪）——避免双线程双窗口；持久 profile 的 cookie 不随线程退出丢失。
    /// </summary>
    private void EnsureThread()
    {
        if (_disposed || _thread?.IsAlive == true) return;
        _log.Info("[wv2] 抓取线程不在（此前空闲自退），自动重启…");
        _captchaPending = false;
        Start();
        var deadline = DateTime.UtcNow.AddSeconds(20); // 初始化实测约 2 秒，给足余量
        while (!_disposed && DateTime.UtcNow < deadline)
        {
            if (_available && _ctl != null) { _log.Info("[wv2] 重启后就绪"); return; }
            Thread.Sleep(200);
        }
        _log.Warn("[wv2] 重启后 20 秒仍未就绪，本次提取回退 curl");
    }

    /// <summary> 空闲回收：超过 <see cref="PumpIdleExitLimit"/> 无业务投递且验证窗口不可见 → WM_QUIT 自退。 </summary>
    private void ReaperTick()
    {
        if (_disposed || _captchaWindowVisible) return;
        if (!PumpShouldReap(DateTime.UtcNow, LastPumpActivity, _captchaWindowVisible, PumpIdleExitLimit)) return;
        _reaper?.Dispose();
        _reaper = null;
        if (_thread?.IsAlive != true) return;
        _log.Info("[wv2] 消息泵空闲超过 10 分钟，STA 线程自退（WebView2 进程组已释放；下次提取自动重启，cookie 在持久 profile 内不丢失）");
        MarkUnavailable("消息泵空闲自退（下次提取自动重启）");
        _captchaPending = false;
        if (_hwnd != IntPtr.Zero) { try { PostMessage(_hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { /* 已退 */ } }
    }

    /// <summary> 初始化超过 45 秒仍未就绪时定期上报（约 3 分钟后停止刷屏）。 </summary>
    private void WatchdogTick()
    {
        if (_disposed || _available) return;
        _watchdogCount++;
        var waited = 45 + (_watchdogCount - 1) * 30;
        var err = string.IsNullOrEmpty(_lastError) ? "(无)" : _lastError;
        _log.Error($"[wv2] 初始化 {waited} 秒仍未就绪（STA 线程存活={_thread?.IsAlive}，" +
                   $"窗口=0x{(_hwnd == IntPtr.Zero ? 0 : _hwnd.ToInt64()):X}，最后错误={err}）——抓取将回退 curl");
        if (_watchdogCount >= 6) _watchdog?.Dispose();
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
        EnsureThread(); // 空闲自退后的懒重启（线程死才重起；活着哪怕未就绪也不动）
        if (_disposed || !_available || _ctl == null) return null;
        TouchPumpActivity();

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
            if (ct.IsCancellationRequested) return null; // 取消：正常收尾，不判失能
            // 超时多半意味着 WebView2 控制器已僵死（2026-10-13 14:16 实测：僵尸实例拖住共享
            // 浏览器进程，ExecuteScriptAsync 永不返回）——置失能，后续请求直接回退 curl，
            // 不再每页白等 30s。
            MarkUnavailable("fetch 等待超时（30s）");
            return null;
        }
        if (error != null)
        {
            // 控制器层面调用失败（COM/RPC 错误等）同样判失能：单页网络问题不会走这里
            MarkUnavailable($"fetch 调用失败（{error.GetType().Name}）");
            return null;
        }
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
        // 验证窗口一显示就进入「司令官操作中」：消息泵空闲自退必须为其让路
        // （2026-10-13 16:16 事故：过 CF 验证时线程被绝对 deadline 收掉，前功尽弃）。
        _captchaWindowVisible = true;
        TouchPumpActivity();
        PumpQueue.Enqueue(() =>
        {
            try
            {
                _log.Info("[wv2] 显示验证窗口，请司令官在窗口内完成 Cloudflare 验证…");
                // 窗口按验证尺寸创建但默认隐藏：居中放大并显示
                var x = Math.Max(0, (GetSystemMetrics(0) - CaptchaWidth) / 2);
                var y = Math.Max(0, (GetSystemMetrics(1) - CaptchaHeight) / 2);
                MoveWindow(_hwnd, x, y, CaptchaWidth, CaptchaHeight, true);
                ShowWindow(_hwnd, SW_SHOW);
                // WebView2 控制器是在「隐藏」宿主窗口上初始化的，不会自动合成画面；
                // 显示后必须按宿主客户区**显式重设 Bounds** 才会渲染
                // （2026-10-13 实测：只 MoveWindow+ShowWindow 会是整片空白，页面加载了但不合成）。
                SyncWebViewBounds();
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

    /// <summary> 把 WebView2 显示区域同步为宿主窗口客户区（隐藏窗口上初始化的控制器不会自动合成画面，必须显式重设）。 </summary>
    private void SyncWebViewBounds()
    {
        if (_ctl == null || _hwnd == IntPtr.Zero) return;
        try
        {
            if (GetClientRect(_hwnd, out var r))
                _ctl.Bounds = new System.Drawing.Rectangle(0, 0, r.Right - r.Left, r.Bottom - r.Top);
        }
        catch (Exception ex)
        {
            _log.Warn($"[wv2] 重设 WebView2 显示区域失败：{ex.GetType().Name} {Truncate(ex.Message, 100)}");
        }
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
            lpszClassName = _wndClassName, // 每实例唯一（铁律④）：撞名=消息派发到旧实例 WndProc=本实例队列饿死
        };
        if (RegisterClassEx(ref wc) == 0)
        {
            // 唯一类名下注册失败是真异常（固定类名时代的「热重载残留」容忍已移除）：
            // 没有自己的类，窗口消息就会派发到别人的 WndProc，本实例消息泵形同虚设。
            var err = GetLastError();
            _lastError = $"RegisterClassEx 失败（Win32 错误码 {err}）";
            _log.Error($"[wv2] 注册窗口类失败（类名 {_wndClassName}，Win32 错误码 {err}）——放弃 WebView2，回退 curl");
            _available = false;
            return;
        }
        // 按验证窗口尺寸创建（默认不可见；WebView2 内容大小随宿主窗口而定，验证时只需 MoveWindow+Show）
        _hwnd = CreateWindowEx(0, _wndClassName, "wv2", WS_OVERLAPPEDWINDOW,
            0, 0, CaptchaWidth, CaptchaHeight, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            var err = GetLastError();
            _lastError = $"CreateWindowEx 失败（Win32 错误码 {err}）";
            _log.Error($"[wv2] 创建抓取窗口失败（Win32 错误码 {err}）——放弃 WebView2，回退 curl");
            _available = false;
        }
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
            _captchaWindowVisible = false; // 用户操作期结束，空闲自退恢复计时
            TouchPumpActivity();
            try { OnCaptchaWindowClosed?.Invoke(); } catch { /* UI 未挂载则忽略 */ }
            return IntPtr.Zero;
        }
        else if (m == WM_SIZE)
        {
            // 用户拖动改变验证窗口大小：同步 WebView2 显示区域（0x0005；lParam 低16位=客户区宽、高16位=高）
            var cw = (int)(l.ToInt64() & 0xFFFF);
            var ch = (int)((l.ToInt64() >> 16) & 0xFFFF);
            if (_ctl != null && cw > 0 && ch > 0)
            {
                try { _ctl.Bounds = new System.Drawing.Rectangle(0, 0, cw, ch); } catch { /* 忽略 */ }
            }
            return IntPtr.Zero;
        }
        return DefWindowProc(h, m, w, l);
    }

    private void Pump()
    {
        // 铁律：线程与控制器同生共死——循环**没有**绝对 deadline。
        // 历史事故：曾有 300 秒上限，用户正在过 CF 验证时线程到期静默退出 → Windows 销毁
        // 线程创建的窗口 → WebView2 进程组清零 → 全程无日志（2026-10-13 16:16:55 实测）。
        // 退出只认三条路：① Dispose 的 WM_QUIT；② 空闲 reaper 的 WM_QUIT；③ 窗口消息到达时
        // 顺带做的空闲检查（PumpShouldReap）。GetMessage 阻塞期间线程天然存活，不耗 CPU。
        TouchPumpActivity();
        while (!_disposed)
        {
            if (GetMessage(out var m, IntPtr.Zero, 0, 0) <= 0) break; // WM_QUIT 返回 0
            TranslateMessage(ref m);
            DispatchMessage(ref m);
            if (PumpShouldReap(DateTime.UtcNow, LastPumpActivity, _captchaWindowVisible, PumpIdleExitLimit))
                break;
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
        _watchdog?.Dispose();
        _reaper?.Dispose();
        try { _ctl?.Close(); } catch { /* 已释放/未创建 */ }
        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                // WM_QUIT 让阻塞在 GetMessage 的消息泵立刻返回（0）退出，
                // 不必干等 Pump 的 300 秒自然到期（热重载时 STA 线程早退一分钟是一分钟）。
                PostMessage(_hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
        catch { /* 忽略 */ }
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