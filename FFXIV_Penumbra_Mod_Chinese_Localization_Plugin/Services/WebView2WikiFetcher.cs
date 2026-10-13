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
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
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
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
    }

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

    private string _wndClassName = NewWindowClassName(); // 非 readonly：重启时换新 Guid，防与未退完的旧线程自撞（1410）

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

    /// <summary> 当前窗口类名（每实例唯一；重启时更换——供测试锁「重启必须换类名」）。 </summary>
    internal string WindowClassName => _wndClassName;

    /// <summary> 验证窗口是否正显示（司令官操作中）：供 service 判断「是否正处于人工验证中」以屏蔽 curl 回退。 </summary>
    internal bool CaptchaWindowVisible => _captchaWindowVisible;

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
        ResetForRestart(); // 关键：换类名防 1410 自撞 + 清死窗口/控制器引用 + 清就绪态（否则「就绪」等待被旧值瞬间骗过）
        Start();
        var deadline = DateTime.UtcNow.AddSeconds(20); // 初始化实测约 2 秒，给足余量
        while (!_disposed && DateTime.UtcNow < deadline)
        {
            if (_available && _ctl != null) { _log.Info("[wv2] 重启后就绪"); return; }
            Thread.Sleep(200);
        }
        _log.Warn("[wv2] 重启后 20 秒仍未就绪，本次提取回退 curl");
    }

    /// <summary>
    /// 重启前状态重置（幂等；2026-10-13 16:56 实测教训——不重置的懒重启三连炸）：
    /// ① 类名必须换新 Guid：旧线程被 WM_QUIT 收走后窗口/类名的释放与退出有窗口期，
    ///    复用旧类名会 RegisterClassEx 自撞（1410，日志实录）；
    /// ② <c>_hwnd</c>/<c>_ctl</c> 必须清：旧窗口随旧线程退出被 Windows 销毁，旧控制器随之失效——
    ///    不清则「重启后就绪」被旧 <c>_available/_ctl</c> 值瞬间骗过（假就绪），
    ///    且后续会拿死窗口句柄创建控制器（COMException 无效的窗口句柄 0x80070578）。
    /// </summary>
    internal void ResetForRestart()
    {
        _wndClassName = NewWindowClassName();
        _ctl = null;
        _hwnd = IntPtr.Zero;
        _available = false;
        _captchaPending = false;
        _captchaWindowVisible = false;
        _lastError = null;
        LastPumpActivity = DateTime.UtcNow;
    }

    /// <summary> 空闲回收：超过 <see cref="PumpIdleExitLimit"/> 无业务投递且验证窗口不可见 → WM_QUIT 自退。 </summary>
    private void ReaperTick()
    {
        if (_disposed || _captchaWindowVisible) return;
        if (!PumpShouldReap(DateTime.UtcNow, LastPumpActivity, _captchaWindowVisible, PumpIdleExitLimit)) return;
        _reaper?.Dispose();
        _reaper = null;
        if (_thread?.IsAlive != true) return;
        _log.Info("[wv2] 消息泵空闲超过 10 分钟，STA 线程自退（先关闭控制器以释放 WebView2 进程组；下次提取自动重启，cookie 在持久 profile 内不丢失）");
        MarkUnavailable("消息泵空闲自退（下次提取自动重启）");
        _captchaPending = false;
        // ★ 顺序不能反：先关控制器（在泵线程上），再发退出信号。
        // 旧代码只发 WM_QUIT —— 线程退了、窗口也销毁了，但 msedgewebview2 进程组**并不跟着退**
        // （2026-10-14 实测残留 7 个孤儿进程长期占住 profile），日志里那句「进程组已释放」是错的。
        CloseControllerOnPumpThread("空闲自退");
        // 再发退出信号（此刻窗口句柄还有效），最后清引用：
        // 旧窗口随线程退出被 Windows 销毁、旧控制器随之失效——清引用可防懒重启的
        // 「就绪等待」被这些死值瞬间骗过（假就绪），也便于 GC 回收。
        if (_hwnd != IntPtr.Zero) { try { PostMessage(_hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { /* 已退 */ } }
        _ctl = null;
        _hwnd = IntPtr.Zero;
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
                        // ★ 诊断（2026-10-14）：本分支把**三种成因混为一谈**——raw 为 null（脚本无返回/
                        //   调用超时）、raw 为 "null"（Promise 未兑现）、raw 是非字符串 JSON 令牌。
                        //   日志却一律写「跨源被拒」，长期把排查方向带偏（真身多半是 renderer 里 JS 没执行）。
                        //   故先把原始返回值原样打出来，一眼可辨。
                        _log.Warn("[wv2] fetch 原始返回值诊断：raw="
                                  + (raw == null ? "(null)" : raw.Length == 0 ? "(空串)" : Truncate(raw, 200)));
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
                // 弹到「鼠标所在显示器」的工作区中央：多屏用户（游戏全屏主屏时，按主屏尺寸
                // 算的居中会落到副屏）压根看不见它——2026-10-13 17:04 实录「弹出来不明显」。
                var (mx, my) = CenterOnMouseMonitor(CaptchaWidth, CaptchaHeight);
                MoveWindow(_hwnd, mx, my, CaptchaWidth, CaptchaHeight, true);
                ShowWindow(_hwnd, SW_SHOW);
                SyncWebViewBounds();
                // WebView2 已知坑（2026-10-13 联网搜到的多案例实测，与 18:36 白窗症状完全吻合）：
                // 在隐藏宿主窗口上初始化的控制器，显示时渲染层可能「卡住」不重绘——白屏，
                // 直到鼠标移动等外部事件才唤醒（WPF 案例：第二次及以后显示必白，第一次正常）。
                // 解法（该案例博主原话「百分百有效」）：抢焦点 + 轻微改变尺寸再恢复，强制重绘。
                SetFocus(_hwnd);
                MoveWindow(_hwnd, mx, my, CaptchaWidth + 1, CaptchaHeight, true);
                MoveWindow(_hwnd, mx, my, CaptchaWidth, CaptchaHeight, true);
                SyncWebViewBounds();
                // FFXIV 是无边框全屏：普通 ShowWindow 不会把窗口带到前台，用户既看不见也点不着
                // （turnstile 交互要求窗口可接收输入）——三连：置前 + 临时置顶 + 抢焦点。
                // SetForegroundWindow 在本进程（ffxiv）内调用不受系统前台锁限制；
                // 置顶随窗口隐藏（WM_CLOSE → SW_HIDE）自然失效，不常驻挡游戏画面。
                BringWindowToTop(_hwnd);
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                SetForegroundWindow(_hwnd);
                SetFocus(_hwnd);
                // WebView2 控制器是在「隐藏」宿主窗口上初始化的，不会自动合成画面；
                // 显示后必须按宿主客户区**显式重设 Bounds** 才会渲染
                // （2026-10-13 实测：只 MoveWindow+ShowWindow 会是整片空白，页面加载了但不合成）。
                SyncWebViewBounds();
                _captchaPending = false; // 窗口已弹出，进入「用户操作中」；关闭时再清一次并通知 UI 续跑
                _ctl!.CoreWebView2.Navigate(url);
                // 监听：一旦导航到非挑战内容，视为验证通过。
                // 先减后加——18:01 实录：每次弹窗都重复 += 从不 -=，导航完成日志越叠越多（3/4/6…）
                _ctl.CoreWebView2.NavigationCompleted -= OnCaptchaNavCompleted;
                _ctl.CoreWebView2.NavigationCompleted += OnCaptchaNavCompleted;
            }
            catch (Exception ex)
            {
                _log.Error($"[wv2] 显示验证窗口失败：{ex.GetType().Name} {Truncate(ex.Message, 120)}");
            }
        });
        WakePump();
    }

    /// <summary>
    /// 在指定工作区内居中放置窗口的左上角坐标（纯函数，可单测）。
    /// 坐标为负是合法的——副屏在主屏左侧时工作区左缘为负（如 -1920）。
    /// 窗口比工作区大时贴工作区左上角（Max(0,…)），绝不弹到屏幕外。
    /// </summary>
    internal static (int X, int Y) CenterInWorkArea(int workLeft, int workTop, int workRight, int workBottom, int w, int h)
    {
        var x = workLeft + Math.Max(0, (workRight - workLeft - w) / 2);
        var y = workTop + Math.Max(0, (workBottom - workTop - h) / 2);
        return (x, y);
    }

    /// <summary> 取鼠标所在显示器的工作区（排除任务栏），返回验证窗口的居中左上角；取显示器失败时回退主屏。 </summary>
    private static (int X, int Y) CenterOnMouseMonitor(int w, int h)
    {
        if (GetCursorPos(out var pt))
        {
            var mon = MonitorFromPoint(pt, MONITOR_DEFAULTTOPRIMARY);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
                return CenterInWorkArea(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom, w, h);
        }
        return CenterInWorkArea(0, 0, GetSystemMetrics(0), GetSystemMetrics(1), w, h);
    }

    /// <summary>
    /// 手动隐藏验证窗口（UI 逃生门）：白窗/窗口被全屏游戏挡住点不到 X 时，
    /// 司令官可从插件界面点「隐藏验证窗口」把它收掉。注意：隐藏**不**触发
    /// <see cref="OnCaptchaWindowClosed"/>——「收起暂存」与「用户点 X 真正关闭（触发关窗续跑）」
    /// 是两种意图，共用同一信号会让「隐藏」后立即被自动续跑重新弹回（2026-10-13 18:53 实录
    /// 「隐藏功能无效」）。隐藏后 WebView2 与持久 profile/cookie 仍在，可随时「重新打开验证窗口」。
    /// </summary>
    public void HideCaptchaWindow()
    {
        if (_disposed) return;
        // 收起「用户操作中」态（空闲 reaper 恢复计时）；同时清 _captchaPending 表示「等待」暂止——
        // 用户稍后重新打开/重新提取会再次按需触发验证。
        _captchaWindowVisible = false;
        _captchaPending = false;
        TouchPumpActivity();
        PumpQueue.Enqueue(() =>
        {
            try { if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE); } catch { /* 已没 */ }
        });
        WakePump();
        // 关键：隐藏**不**调用 OnCaptchaWindowClosed——否则会被 UI 的「关窗自动续跑」当成真实关闭，
        // 立刻重新弹出验证窗口，隐藏形同无效。
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
        // 验证窗口正显示着（用户还没处理完）→ 不重复通知、不重复弹窗：
        // 2026-10-13 18:01 实录「弹窗风暴」——白窗无法完成验证 → 用户反复关窗 → 续跑 →
        // 再撞墙 → 再弹窗，每秒一轮无限循环，日志被刷爆、导航完成事件重复订阅越叠越多。
        if (_captchaPending || _captchaWindowVisible) return;
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

    /// <summary>
    /// 解析用户粘贴的 cf_clearance cookie 值（纯函数，可单测）。
    /// 接受三种形态：① 纯值（直接当作值）；② <c>cf_clearance=VALUE</c>；③ 带引号的值。
    /// 解析出的值已去首尾空白/引号；空则 false。
    /// </summary>
    internal static bool TryParseCfClearance(string? raw, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw!.Trim();
        if (s.StartsWith("cf_clearance=", StringComparison.OrdinalIgnoreCase))
            s = s.Substring("cf_clearance=".Length).Trim();
        if (s.Length >= 2 && s.StartsWith("\"") && s.EndsWith("\""))
            s = s.Substring(1, s.Length - 2).Trim();
        if (s.Length == 0) return false;
        value = s;
        return true;
    }

    /// <summary>
    /// 手动导入 cf_clearance cookie（B 路径：绕开游戏内验证窗）。
    /// 在司令官自己的浏览器里过完 Cloudflare 后，把 cf_clearance 的值粘贴进来，本方法把它写入
    /// WebView2 持久 profile 的 CookieManager——之后游戏内抓取自动带上，不再弹可见验证窗（自然无白窗）。
    /// <para>关键：cookie 注入发生在 WebView2 核心层（浏览器进程），<b>不依赖渲染进程绘制</b>，
    /// 故即使游戏内验证窗白屏（渲染被拦），本导入仍可成功。</para>
    /// 立即返回状态串（注入成功/失败/未就绪），由 UI 显示。
    /// </summary>
    public Task<string> ImportCfClearanceAsync(string raw)
    {
        if (!TryParseCfClearance(raw, out var value))
            return Task.FromResult("无法解析 cf_clearance：请粘贴 cookie 的值（形如 cf_clearance=xxxx 或直接粘贴 xxxx）。");
        EnsureThread(); // 空闲自退后的懒重启（线程死才重起）
        if (_disposed) return Task.FromResult("WebView2 抓取器已释放（插件正在卸载？）。");
        // 等就绪（最多 20s，同 Fetch 的就绪等待）
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!_disposed && DateTime.UtcNow < deadline)
        {
            if (_available && _ctl != null) break;
            Thread.Sleep(200);
        }
        if (_disposed || !_available || _ctl == null)
            return Task.FromResult("WebView2 未就绪，无法注入 cookie（将回退 curl）；请稍后重试或重载插件后再导入。");
        using var done = new ManualResetEventSlim(false);
        string status = "未知错误";
        PumpQueue.Enqueue(() =>
        {
            try
            {
                var cookie = _ctl!.CoreWebView2.CookieManager.CreateCookie("cf_clearance", value, ".huijiwiki.com", "/");
                cookie.IsSecure = true;
                cookie.IsHttpOnly = true;
                // SameSite 不显式设置：本 SDK 版本未公开同名枚举（实测 CoreWebView2CookieSameSite 编译不过），
                // 保留默认的 Lax 即可——我们的请求是同站（huijiwiki.com → cdn.huijiwiki.com），Lax 下 cookie 正常携带。
                cookie.Expires = DateTime.UtcNow.AddDays(30); // Expires 是 DateTime（非 DateTimeOffset，实测）
                _ctl.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
                _log.Info("[wv2] 已手动导入 cf_clearance cookie 到持久 profile（domain=.huijiwiki.com，30 天有效）");
                status = "已导入 cf_clearance（domain=.huijiwiki.com，30 天有效），已写入持久 profile。之后游戏内抓取将自动复用，不再弹验证窗；若仍提示验证，请先点一次「开始提取」触发同源导航。";
            }
            catch (Exception ex)
            {
                _log.Error($"[wv2] 导入 cf_clearance 失败：{ex.GetType().Name} {Truncate(ex.Message, 150)}");
                status = $"导入失败：{ex.GetType().Name} {Truncate(ex.Message, 120)}";
            }
            finally { done.Set(); }
        });
        WakePump();
        done.Wait(TimeSpan.FromSeconds(15));
        return Task.FromResult(status);
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
    /// <summary>
    /// 构造 WebView2 启动参数（纯函数，便于单测）。
    /// 默认强制软件渲染（--disable-gpu --disable-gpu-compositing）绕开游戏内 GPU 合成冲突；
    /// <paramref name="emergency"/>=true（环境变量 PMH_WV2_NO_SANDBOX=1）时追加
    /// --no-sandbox --disable-features=RendererCodeIntegrity，尝试绕过火绒对渲染进程的代码完整性钩子
    /// （2026-10-14 选项 A 应急兜底；会放松 Chromium 沙箱，仅排障用，验证后应撤除环境变量）。
    /// </summary>
    internal static string BuildWebView2Args(bool emergency)
    {
        // 基线＝软件渲染（绕开游戏内 GPU 合成冲突）**＋ 防渲染冻结**。
        // 后者 2026-10-14 新增，源于这样一组实测：游戏内 renderer 进程活着（CPU 0.3s）、
        // 导航 IsSuccess=True，但 ExecuteScriptAsync 恒返回 null —— JS 根本没执行。
        // 这正是「窗口被判为不可见/后台 → Chromium 冻结渲染与脚本」的典型形态，四参数逐条对症：
        //   --disable-features=CalculateNativeWinOcclusion  不做原生窗口遮挡计算（游戏全屏时我们的窗口极易被判为被遮挡）
        //   --disable-backgrounding-occluded-windows        被遮挡也不后台化
        //   --disable-renderer-backgrounding                renderer 不进后台节流
        //   --disable-background-timer-throttling           后台计时器不降频
        var baseline = "--disable-gpu --disable-gpu-compositing"
                     + " --disable-features=CalculateNativeWinOcclusion"
                     + " --disable-backgrounding-occluded-windows"
                     + " --disable-renderer-backgrounding"
                     + " --disable-background-timer-throttling";
        if (!emergency) return baseline;
        // 应急（排障用）：并入同一条 --disable-features（Chromium 只认最后一个，不能写两条），再加 --no-sandbox
        return baseline.Replace("--disable-features=CalculateNativeWinOcclusion",
                                "--disable-features=CalculateNativeWinOcclusion,RendererCodeIntegrity",
                                StringComparison.Ordinal)
               + " --no-sandbox";
    }

    private async Task InitWebView2Async()
    {
        try
        {
            if (_hwnd == IntPtr.Zero)
            {
                // 宿主窗口没建成（注册/建窗失败，或重启时被重置）：绝不能拿零/死句柄
                // 创建控制器——2026-10-13 16:56 实录 COMException「无效的窗口句柄 (0x80070578)」
                _lastError = "宿主窗口不可用（窗口创建失败或已销毁）";
                _available = false;
                _log.Error("[wv2] 宿主窗口不可用，放弃 WebView2 初始化，回退 curl");
                return;
            }
            // 持久 profile 目录（插件配置目录内，随插件存续）
            Directory.CreateDirectory(_profileDir);
            _log.Info("[wv2] 使用持久 profile：" + _profileDir);

            var options = WebView2RuntimeProbe.TryGetRuntimeExecutableFolder();
            _log.Info("[wv2] 正在创建 WebView2 环境…"
                      + (options != null ? "（显式指定 runtime：" + options + "）" : "（自动探测 runtime）"));

            // 2026-10-13 18:36 白窗事故 + 联网多案例实证：WebView2 嵌在游戏（DX 全屏）内时，
            // GPU 合成常与游戏冲突——渲染进程活着、导航也成功，但画面全白。
            // 解法：强制软件渲染（--disable-gpu --disable-gpu-compositing）。
            // 2026-10-14 选项 A 应急兜底：本机实测火绒(HipsDaemon)注入 ffxiv_dx11.exe 进程树、
            // 在游戏内静默阻止 WebView2 渲染进程绘制（renderer 不崩、导航成功、但画面全白，无 ProcessFailed）。
            // 环境变量 PMH_WV2_NO_SANDBOX=1 时追加 --no-sandbox --disable-features=RendererCodeIntegrity，
            // 尝试绕过火绒对渲染进程的代码完整性钩子。默认关闭：该组合会放松 Chromium 沙箱，仅排障用。
            var emergency = Environment.GetEnvironmentVariable("PMH_WV2_NO_SANDBOX");
            var emergencyOn = !string.IsNullOrWhiteSpace(emergency)
                && (emergency == "1" || emergency.Equals("true", StringComparison.OrdinalIgnoreCase));
            if (emergencyOn)
                _log.Warn("[wv2] 应急渲染模式已启用（PMH_WV2_NO_SANDBOX）：追加 --no-sandbox --disable-features=RendererCodeIntegrity，"
                          + "Chromium 沙箱已放松，仅用于验证火绒是否拦截渲染；验证后请删除该环境变量恢复默认。");
            var envOpts = new CoreWebView2EnvironmentOptions(BuildWebView2Args(emergencyOn));
            CoreWebView2Environment env;
            try
            {
                // 2026-10-14 实测：持久 profile 被残留的 msedgewebview2 进程占用时，CreateAsync
                // **永不返回**（既不成功也不抛错），插件就这样一直卡在「初始化中」——
                // 加超时把它变成可诊断的明确失败，而不是无限期的哑火。
                var createTask = options != null
                    ? CoreWebView2Environment.CreateAsync(options, _profileDir, envOpts)
                    : CoreWebView2Environment.CreateAsync(null, _profileDir, envOpts); // 无显式 runtime：自动探测
                env = await createTask.WaitAsync(TimeSpan.FromSeconds(40));
            }
            catch (TimeoutException)
            {
                _lastError = "创建 WebView2 环境超时（40s）：持久 profile 多半被残留的 msedgewebview2 进程占用";
                _available = false;
                _log.Error("[wv2] " + _lastError + "——请结束残留的 msedgewebview2 进程后重试；本次提取回退 curl");
                return;
            }

            _ctl = await env.CreateCoreWebView2ControllerAsync(_hwnd);
            _log.Info("[wv2] 控制器已绑定，停在 about:blank…");

            var blank = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ctl.CoreWebView2.NavigationCompleted += (_, a) => blank.TrySetResult(a.IsSuccess);
            _ctl.CoreWebView2.Navigate("about:blank");
            try { await blank.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch { _log.Warn("[wv2] about:blank 就绪等待超时（仍尝试 fetch）"); }

            // 渲染进程崩溃/被杀监听（白窗取证）：2026-10-13 18:01 实录验证窗口整片白、
            // 进程组看着健康——不接这个事件永远不知道 renderer 是不是死了。
            // 崩溃后自愈：关掉死控制器、就地重建（窗口与持久 profile 都在，cookie 不丢）。
            _ctl.CoreWebView2.ProcessFailed += (_, a) =>
            {
                _log.Error($"[wv2] 渲染进程失败：{a.ProcessFailedKind}（{a.Reason}）——验证内容可能空白，尝试就地重建控制器");
                PumpQueue.Enqueue(() =>
                {
                    try
                    {
                        _ctl?.Close();
                        _ctl = null;
                        _available = false;
                        _ = InitWebView2Async(); // 窗口/类名/profile 俱在，直接重建
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"[wv2] 重建控制器失败：{ex.GetType().Name} {Truncate(ex.Message, 100)}");
                    }
                });
                WakePump();
            };

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
        // ★ 关控制器必须走泵线程（详见 CloseControllerOnPumpThread 注释）。旧写法在本线程直接
        //   _ctl?.Close() 属跨线程调用，被 WebView2 拒绝后异常又被 catch 吞掉，进程组从不退出。
        CloseControllerOnPumpThread("插件卸载");
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

    /// <summary>
    /// 在 **WebView2 的 STA 泵线程** 上关闭控制器并等待完成。
    /// <para>
    /// 2026-10-14 实测定位到的白窗根因：<c>CoreWebView2Controller.Close()</c> 有线程亲和性，
    /// 必须在创建它的那个 STA 泵线程上调用。旧代码在卸载线程直接 <c>_ctl?.Close()</c>，
    /// 跨线程调用被拒绝、异常又被 <c>catch</c> 吞掉，于是 <c>msedgewebview2</c> 进程组**从不退出**。
    /// 实测残留 7 个孤儿进程（启动于 2:44，游戏早已退出），长期占住持久 profile：
    /// 后续实例 <c>CreateAsync</c> 静默挂起、renderer 不干活 → 游戏内白窗、
    /// <c>ExecuteScriptAsync</c> 返回 null（被 ClassifyRaw 判成「跨源被拒」，即日志里那串报错的真身）。
    /// </para>
    /// 卸载与空闲自退都**必须先调它**再退线程——顺序反了就又是孤儿进程。
    /// </summary>
    private void CloseControllerOnPumpThread(string reason)
    {
        if (_hwnd == IntPtr.Zero || _thread?.IsAlive != true)
        {
            _ctl = null; // 线程已死/无窗口：无从投递，只能清引用（进程组此时已随线程窗口销毁）
            return;
        }
        using var closed = new ManualResetEventSlim(false);
        PumpQueue.Enqueue(() =>
        {
            try
            {
                if (_ctl != null)
                {
                    _ctl.Close();
                    _log.Info($"[wv2] 控制器已关闭（{reason}）——WebView2 进程组将正常退出，不再残留孤儿进程");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"[wv2] 关闭控制器异常（{reason}）：{ex.GetType().Name} {Truncate(ex.Message, 120)}");
            }
            finally
            {
                _ctl = null;
                _available = false;
                closed.Set();
            }
        });
        WakePump();
        if (!closed.Wait(TimeSpan.FromSeconds(8)))
            _log.Warn($"[wv2] 关闭控制器等待超时（{reason}）——进程组可能仍会残留，可手动结束 msedgewebview2 进程");
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