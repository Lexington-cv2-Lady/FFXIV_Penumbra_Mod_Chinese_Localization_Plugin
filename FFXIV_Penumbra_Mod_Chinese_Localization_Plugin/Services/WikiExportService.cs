using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FFXIVPenumbraHanhua.Services;

/// <summary>
/// Wiki 词条提取：从灰机 wiki（cdn.huijiwiki.com/ff14/api.php）抓取 FFXIV 官方中/英名，
/// 按分类写入 词典目录\wiki_术语对照\&lt;分类&gt;.json（分类提取）或 汇总.json（汇总提取）。
/// 语义与独立版一致：增量更新（已有词条不覆盖）、黑名单过滤、噪音词拒绝、Race 固定词表并入、空分类跳过。
/// <para>
/// ★ 取数架构（2026-10-14 定型，此前走过一条已废弃的死路，勿再回头）：
/// 取数只有两条腿——① <b>外部抓取器</b>（游戏外独立进程 + 真实浏览器引擎，第一优先级）；
/// ② <b>curl / HttpClient</b>（兜底，多数情况下会被 Cloudflare 拦）。
/// <b>「游戏内嵌 WebView2」已整体移除</b>：实测它在游戏里 <c>ExecuteScriptAsync</c> 恒返回 <c>{}</c>
/// （renderer 进程活着、导航成功，但脚本压根不执行），控制器重建直接报 0x80070270；
/// 且它还会在取数失败时弹一个<b>永远白屏</b>的验证窗口。那条链一行不剩，见 2026-10-14 日志。
/// </para>
/// </summary>
public sealed class WikiExportService : IDisposable
{
    /// <summary> 可选分类（Display 用于界面与中文文件名；Prefix 带尾斜杠用于 Data namespace）。 </summary>
    public sealed record WikiCategory(string Display, string Prefix);

    /// <summary>
    /// 可选分类（仅保留灰机 wiki 实测有中英对照数据页的分类；其余分类无数据页或格式无法解析，导出恒为 0 条，已去掉）。
    /// Race 为固定词表（不抓取，勾选时并入种族/子种族官方词条）。
    /// </summary>
    public static readonly IReadOnlyList<WikiCategory> Categories = new List<WikiCategory>
    {
        new("物品（Item）", "Item/"),
        new("技能/动作（Action）", "Action/"),
        new("任务（Quest）", "Quest/"),
        new("成就（Achievement）", "Achievement/"),
        new("情感动作（Emote）", "Emote/"),
        new("种族（Race）", "Race/")
    };

    private readonly AppLog _log;
    private readonly HttpClient _http;

    /// <summary> 抓取用 UA（curl 与 HttpClient 共用）。 </summary>
    private const string UserAgentValue = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) FFXIVPenumbraHanhua";

    /// <summary>
    /// 插件实际抓取的 API 端点（不含 <c>gapprefix</c>）。UI 的「在浏览器中打开」按钮也用它，
    /// 保证两处永远一致——2026-10-14 教训：按钮里写了个**不带任何参数**的裸 api.php，
    /// MediaWiki 于是返回「API 帮助页」，看到的永远是个正常网页，既拿不到 JSON
    /// 也无从判断 CF 到底拦没拦（「又绕回来了」的真因）。
    /// </summary>
    public const string ApiEndpoint = "https://cdn.huijiwiki.com/ff14/api.php?action=query&generator=allpages&format=json&utf8=1"
        + "&gaplimit=500&prop=revisions&rvprop=content&rvslots=main&gapnamespace=3500";

    /// <summary> 相邻请求最小间隔（毫秒）。灰机 wiki 走 Cloudflare，密集请求会被拉黑 IP，故主动放慢。 </summary>
    private const int RequestIntervalMs = 700;

    /// <summary> 单次请求的网络失败重试次数（不含首次）；命中 Cloudflare 挑战页不重试。 </summary>
    private const int MaxRetries = 2;

    /// <summary> 续抓进度文件名（写在 词典目录\wiki_术语对照\ 下）。 </summary>
    private const string ProgressFileName = "_抓取进度.json";

    /// <summary> 续抓进度有效期（小时）：超时则丢弃、改从头抓取，避免长期跳过断点之前的页面。 </summary>
    private const int ProgressValidHours = 12;

    /// <summary> 首次失败退避（毫秒）。 </summary>
    private const int RetryDelayFirstMs = 10_000;

    /// <summary> 后续失败退避（毫秒）。 </summary>
    private const int RetryDelayNextMs = 30_000;

    /// <summary> 节流状态锁。 </summary>
    private readonly object _gate = new();

    /// <summary> 下次允许发起请求的时间（UTC）。 </summary>
    private DateTime _nextRequestAt;

    public string LastResult { get; private set; } = "";

    /// <summary>
    /// 插件 DLL 所在目录（由 <see cref="Plugin"/> 从 Dalamud 的 <c>PluginInterface.AssemblyLocation</c> 传入）。
    /// 定位随插件发布的 <c>wv2_fetch_helper.exe</c> **必须靠它**：Dalamud 常以字节流加载插件，
    /// 此时 <c>Assembly.Location</c> 为空字符串，而 <c>AppContext.BaseDirectory</c> 指向游戏目录——
    /// 两个候选都找不到 exe（2026-10-14 05:35 实测「未找到外部抓取器」正是这么来的）。
    /// </summary>
    private static string? s_pluginDir;

    /// <summary> 抓取器（wv2_fetch_helper.exe）的独立 profile 目录，由 <see cref="Plugin"/> 经 Dalamud 的
    /// <c>GetPluginConfigDirectory()</c> 传入——随启动器自适应，不硬编码启动器名。 </summary>
    private static string? s_fetchProfileDir;

    /// <summary>
    /// 抓取器初始化。
    /// </summary>
    /// <param name="log">日志。</param>
    /// <param name="pluginDir">
    /// 插件 DLL 所在目录（由 <see cref="Plugin"/> 从 Dalamud 的 <c>PluginInterface.AssemblyLocation</c> 传入）。
    /// 定位随插件发布的 <c>wv2_fetch_helper.exe</c> **必须靠它**：Dalamud 常以字节流加载插件，
    /// 此时 <c>Assembly.Location</c> 为空字符串，而 <c>AppContext.BaseDirectory</c> 指向游戏目录——
    /// 两个候选都找不到 exe（2026-10-14 05:35 实测「未找到外部抓取器」正是这么来的）。
    /// </param>
    public WikiExportService(AppLog log, string? pluginDir = null, string? pluginConfigDir = null)
    {
        _log = log;
        if (!string.IsNullOrEmpty(pluginDir)) s_pluginDir = pluginDir;
        // 抓取器的独立 profile 目录。必须由 Dalamud 的 GetPluginConfigDirectory() 传进来：
        // 它会随启动器（XIVLauncherCN / XIVLauncher）自适应；写死启动器名会让国际版用户的
        // profile 落到与插件自身不一致的目录下。
        if (!string.IsNullOrWhiteSpace(pluginConfigDir))
            s_fetchProfileDir = Path.Combine(pluginConfigDir, "WebView2ProfileFetch");
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentValue);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// 释放抓取资源（插件卸载/热重载时由 <see cref="Plugin"/> 调用）。
    /// </summary>
    public void Dispose()
    {
        try { _http?.Dispose(); } catch { /* 卸载路径不因清理异常而中断 */ }
    }

    /// <summary>
    /// 取数据页正文（含节流与退避）。
    /// 灰机 wiki 的 CDN 是 Cloudflare。早期它主要按 TLS 握手指纹（JA3/JA4）打分，.NET 的 SChannel 指纹会被拦，
    /// 故仍保留 curl.exe（指纹更接近浏览器）作兜底。
    /// 但 2026-10 实测：**风控已升级到「速率/行为」维度**——短时间内密集请求会把出口 IP 拉黑（curl 指纹同样不放行），
    /// 且冷却长达数十分钟到数小时。因此本方法统一做两件事：
    /// ① 请求节流：相邻请求之间至少间隔 <see cref="RequestIntervalMs"/> 毫秒；
    /// ② 失败退避：网络失败按 10s/30s 退避重试，重试耗尽才放弃。
    /// 命中 Cloudflare 挑战页时**不重试**（反复试只会延长封禁），交由调用方全局中止。
    /// <para>
    /// ★ 2026-10-14 架构定型：取数＝**外部抓取器（游戏外独立进程 + 真实浏览器引擎）→ curl → HttpClient**。
    /// 「游戏内嵌 WebView2」已整体移除——它在游戏里脚本压根不执行（<c>ExecuteScriptAsync</c> 恒返回 <c>{}</c>），
    /// 而且一旦回退到它，就会弹一个<b>永远白屏</b>的验证窗口（2026-10-14 13:12 实录：取消提取 → 回退内嵌 → raw={{}} → 弹白窗）。
    /// 故此处**不再有任何"需要人工验证"分支**：取不到就是取不到，如实上报，绝不弹窗。
    /// </para>
    /// </summary>
    /// <returns>(正文, 是否被风控拦截)。被拦时正文为 null 且第二项为 true。</returns>
    private async Task<(string? Body, bool Blocked)> FetchAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            // ★ 取消必须第一时间短路：旧代码取消后仍继续往下走，被"取不到正文"的分支当成
            //   「需要人工过 CF 验证」，于是弹出一个白窗——用户明明按了取消，却跳出个验证窗。
            if (ct.IsCancellationRequested) return (null, false);
            if (!await ThrottleAsync(ct).ConfigureAwait(false)) return (null, false);

            var body = await FetchViaExternalHelperAsync(url, ct).ConfigureAwait(false)
                       ?? await TryCurlAsync(url, ct).ConfigureAwait(false)
                       ?? await TryHttpAsync(url, ct).ConfigureAwait(false);

            if (body != null)
            {
                // 拿到内容但是 Cloudflare 挑战页：确认被风控，立刻上报，不再重试
                if (CloudflareChallengeDetector.IsChallenge(body)) return (null, true);
                return (body, false);
            }

            if (ct.IsCancellationRequested) return (null, false);
            if (attempt >= MaxRetries) return (null, false);
            if (!await DelayAsync(attempt == 0 ? RetryDelayFirstMs : RetryDelayNextMs, ct).ConfigureAwait(false))
                return (null, false);
        }
    }

    private static bool _helperLogged;

    /// <summary>
    /// 在给定目录里定位可用的外部抓取器 exe；找不到或不完整时返回 null。
    /// <para>
    /// ★ 2026-10-14 第二轮教训：**exe 存在 ≠ 可启动**。.NET 的 exe 只是 apphost（原生启动器），
    /// 它转头就去加载同名托管 dll；只部署了 exe 时会以 exit=0x8000809A 失败，
    /// stderr 明写「The application to execute does not exist: '…wv2_fetch_helper.dll'」。
    /// 因此这里**必须连带校验托管 dll 在场**，否则又会像上一版那样「找到了却还是跑不起来」。
    /// </para>
    /// 布局优先级：独立子目录 <c>wv2_fetch_helper\</c>（自成一格，推荐）→ 插件根目录平铺（旧布局）。
    /// </summary>
    internal static string? LocateHelperIn(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        try
        {
            var sub = Path.Combine(dir, "wv2_fetch_helper");
            if (File.Exists(Path.Combine(sub, "wv2_fetch_helper.exe"))
                && File.Exists(Path.Combine(sub, "wv2_fetch_helper.dll")))
                return Path.Combine(sub, "wv2_fetch_helper.exe");

            if (File.Exists(Path.Combine(dir, "wv2_fetch_helper.exe"))
                && File.Exists(Path.Combine(dir, "wv2_fetch_helper.dll")))
                return Path.Combine(dir, "wv2_fetch_helper.exe");
        }
        catch { /* 路径非法，视作未部署 */ }
        return null;
    }

    /// <summary>
    /// 目录里**摆着 exe 却凑不齐一套**（缺同名托管 dll）——部署不完整，apphost 必然启动失败，用于日志定性。
    /// 只在本目录定位不到可用抓取器（<see cref="LocateHelperIn"/> 返回 null）时才可能为真，
    /// 好把「部署漏文件」与「压根没部署」区分开，避免排查方向被带偏。
    /// </summary>
    internal static bool HelperDeployIncomplete(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        if (LocateHelperIn(dir) != null) return false; // 已能定位＝部署完整，谈不上残缺
        try
        {
            return File.Exists(Path.Combine(dir, "wv2_fetch_helper", "wv2_fetch_helper.exe"))
                   || File.Exists(Path.Combine(dir, "wv2_fetch_helper.exe"));
        }
        catch { return false; }
    }

    /// <summary>
    /// 外部抓取器 exe 的随插件路径；未随插件发布时为 null（静默回退，不影响既有链路）。
    /// 2026-10-14 教训：① 只认 `Assembly.Location` 一个目录不够——Dalamud 的装配位置未必等于
    /// 部署目录，首版因此「exe 明明在 devPlugins、却从没被调用过」，且因无日志而查不出来；
    /// ② 找到 exe 也不算数，还得有同名托管 dll，否则 apphost 起不来。
    /// 故改为多候选依次探测 + 完整性校验。
    /// </summary>
    internal static string? ExternalHelperPath
    {
        get
        {
            foreach (var d in HelperSearchDirs())
            {
                var p = LocateHelperIn(d);
                if (p != null) return p;
            }
            return null;
        }
    }

    /// <summary>
    /// 抓取器的候选目录（顺序即优先级）。Dalamud 常以**字节流**加载插件，此时 `Assembly.Location`
    /// 为空、`AppContext.BaseDirectory` 是游戏目录，只有 Dalamud 给的 `AssemblyLocation` 才指向真实
    /// 部署目录——故三个都要试，不许只认一个（首版就栽在这）。
    /// </summary>
    internal static List<string> HelperSearchDirs()
    {
        var cands = new List<string>();
        try
        {
            var d1 = Path.GetDirectoryName(typeof(WikiExportService).Assembly.Location);
            if (!string.IsNullOrEmpty(d1)) cands.Add(d1);
        }
        catch { /* 忽略 */ }
        try { cands.Add(AppContext.BaseDirectory); } catch { /* 忽略 */ }
        if (!string.IsNullOrEmpty(s_pluginDir)) cands.Add(s_pluginDir); // Dalamud 给的真实插件目录（最可靠）
        return cands;
    }

    /// <summary>
    /// 用**游戏外独立进程**的真实浏览器引擎取一个 URL 的正文（2026-10-14 新增，取数第一优先级）。
    /// <para>
    /// 立论：游戏内嵌 WebView2 的脚本不执行（实测 raw={}），游戏外同引擎 0.2s 可取回真实 JSON；
    /// 独立进程跑浏览器正是 Browsingway 的做法（renderer 独立进程 + 离屏），故不受全屏/G-SYNC 影响。
    /// </para>
    /// 不可用（exe 未随插件发布 / 超时 / 失败）一律返回 null，由调用方回退既有链路——绝不比现状更差。
    /// </summary>
    private async Task<string?> FetchViaExternalHelperAsync(string url, CancellationToken ct)
    {
        var exe = ExternalHelperPath;
        if (exe == null)
        {
            // 首版在这里是「静默 return null」，结果 exe 明明在 devPlugins 却从没被调用、日志也查不出——必须留痕。
            if (!_helperLogged)
            {
                _helperLogged = true;
                var incomplete = HelperSearchDirs().Exists(HelperDeployIncomplete);
                _log.Warn(incomplete
                    ? "[wv2] 外部抓取器部署不完整（只有 wv2_fetch_helper.exe，缺同名托管 dll / runtimeconfig.json）——"
                      + "apphost 无法启动，本页改走 curl/HttpClient 兜底"
                    : "[wv2] 未找到外部抓取器 wv2_fetch_helper.exe（未随插件发布）——本页改走 curl/HttpClient 兜底");
            }
            return null;
        }
        if (!_helperLogged)
        {
            _helperLogged = true;
            _log.Info("[wv2] 外部抓取器（游戏外独立进程）已启用：" + exe);
        }

        var outFile = Path.Combine(Path.GetTempPath(), "wv2out_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--url=" + url);
            psi.ArgumentList.Add("--outfile=" + outFile);
            // 显式传入 profile 目录：让国际版启动器（XIVLauncher）用户的 profile 也落在插件自己的配置目录下，
            // 而不是靠 helper 里那个写死 XIVLauncherCN 的兜底路径。
            if (!string.IsNullOrEmpty(s_fetchProfileDir)) psi.ArgumentList.Add("--profile=" + s_fetchProfileDir);
            using var p = Process.Start(psi);
            if (p == null) return null;

            // 必须**异步读走** stdout/stderr：重定向后不读，缓冲区满会把子进程活活憋死（经典陷阱）。
            var readOut = p.StandardOutput.ReadToEndAsync();
            var readErr = p.StandardError.ReadToEndAsync();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(120)); // 单次上限：启动开销 + 导航 45s + 解析 5s
            try { await p.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { /* 已退出 */ }
                // 区分「真超时」与「被调用方取消」（插件卸载 / 用户中止）——否则日志一律写超时，
                // 会把「插件正好在卸载」误读成「抓取器跑了 120s 还没完」（2026-10-14 06:06:50 实录）。
                _log.Warn(ct.IsCancellationRequested
                    ? "[wv2] 外部抓取器被调用方取消（插件卸载/中止）——本页不再取数"
                    : "[wv2] 外部抓取器超时（120s）——本页改走 curl/HttpClient 兜底");
                return null;
            }

            var errText = await readErr.ConfigureAwait(false);
            if (p.ExitCode != 0 || !File.Exists(outFile))
            {
                // 0x8000809A（-2147450726）＝ apphost 找不到托管应用；配套 stderr「does not exist: '….dll'」。
                // 见到这句就等于「部署漏文件」，不必再往 Cloudflare / 指纹方向查。
                var hint = errText.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "；提示：exe 只是 .NET 原生启动器，必须连同同名 .dll 与 .runtimeconfig.json 一起部署"
                    : "";
                _log.Warn($"[wv2] 外部抓取器未取得结果（exit={p.ExitCode}，outFile 存在={File.Exists(outFile)}）"
                          + (errText.Length > 0 ? "；stderr=" + errText.Substring(0, Math.Min(200, errText.Length)) : "")
                          + hint
                          + "——本页改走 curl/HttpClient 兜底");
                return null;
            }
            var text = await File.ReadAllTextAsync(outFile, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            _log.Warn($"[wv2] 外部抓取器失败（回退内嵌/curl）：{ex.GetType().Name} {ex.Message}");
            return null;
        }
        finally
        {
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { /* 临时文件清理失败无妨 */ }
        }
    }

    /// <summary> 可取消等待；被取消返回 false（不抛异常，交由调用方按 ct 判定）。 </summary>
    private static async Task<bool> DelayAsync(int ms, CancellationToken ct)
    {
        if (ms <= 0) return true;
        try
        {
            await Task.Delay(ms, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary> HttpClient 兜底取数（curl 缺失/失败时）。 </summary>
    private async Task<string?> TryHttpAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            // 与原 GetStringAsync 语义一致：仅成功响应返回正文；非成功（含 403 挑战页）视为失败，
            // 交由 curl 路径/退避重试处理
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary> 请求节流：保证相邻请求间隔不小于 <see cref="RequestIntervalMs"/>；被取消返回 false。 </summary>
    private async Task<bool> ThrottleAsync(CancellationToken ct)
    {
        var wait = 0;
        lock (_gate)
        {
            wait = NextDelayMs(_nextRequestAt, DateTime.UtcNow);
            _nextRequestAt = DateTime.UtcNow.AddMilliseconds(wait + RequestIntervalMs);
        }
        return wait <= 0 || await DelayAsync(wait, ct).ConfigureAwait(false);
    }

    /// <summary> 距离下次可请求还需等待的毫秒数（纯函数，便于单测）。 </summary>
    internal static int NextDelayMs(DateTime nextAllowedAt, DateTime now)
        => nextAllowedAt > now ? (int)Math.Ceiling((nextAllowedAt - now).TotalMilliseconds) : 0;

    /// <summary> 用系统自带 curl.exe 抓取（返回正文；curl 缺失/失败返回 null）。 </summary>
    private static async Task<string?> TryCurlAsync(string url, CancellationToken ct)
    {
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo("curl.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // curl 输出为 UTF-8；不显式指定则 .NET 默认按系统 ANSI 码页（中文系统 GBK）解码，
                // 会把多字节中文错解成乱码并撑坏 JSON 结构（"':' is invalid after a value"）。
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add("--max-time");
            psi.ArgumentList.Add("30");
            psi.ArgumentList.Add("-A");
            psi.ArgumentList.Add(UserAgentValue);
            psi.ArgumentList.Add(url);
            proc = Process.Start(psi);
            if (proc == null) return null;
            var outTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errTask = proc.StandardError.ReadToEndAsync(ct); // 并发读，避免 stderr 写满导致死锁
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await outTask.ConfigureAwait(false);
            _ = await errTask.ConfigureAwait(false);
            if (proc.ExitCode != 0) return null;
            return string.IsNullOrEmpty(stdout) ? null : stdout;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { proc?.Dispose(); } catch { /* 释放进程句柄失败可忽略 */ }
        }
    }

    /// <summary> 动作/答语类噪音词条：百科语义与选项语义冲突，拒绝入库。 </summary>
    private static readonly HashSet<string> NoiseTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "no", "on", "off", "true", "false", "ok", "okay",
        "play", "quit", "stop", "start", "pause", "retry", "exit",
        "cancel", "undo", "redo", "reset", "skip"
    };

    /// <summary> 固定词表：玩家种族（含子种族）官方中文名/英文名。 </summary>
    // ⚠ 本表是硬编码的官方译名，**不来自 wiki 抓取**（故不受 CDN 风控影响）。
    //    Wildwood 官方译名为「森林之民」（2026-09-23 经灰机种族页与国服更新笔记核实，
    //    并同日更正过词典三处）；早前此处误写「森岭之民」，wiki 抓取时会把错字一并写入
    //    种族.json（2026-10-11 用户截图指出）。改本表务必同步核对词典与 wiki 侧。
    internal static readonly (string En, string Zh)[] RaceTerms =
    {
        ("Hyur", "人族"), ("Elezen", "精灵族"), ("Lalafell", "拉拉菲尔族"), ("Miqo'te", "猫魅族"),
        ("Roegadyn", "鲁加族"), ("Au Ra", "敖龙族"), ("AuRa", "敖龙族"), ("Hrothgar", "硌狮族"), ("Viera", "维埃拉族"),
        ("Midlander", "中原之民"), ("Highlander", "高地之民"), ("Wildwood", "森林之民"), ("Duskwight", "黑影之民"),
        ("Plainsfolk", "平原之民"), ("Dunesfolk", "沙漠之民"), ("Sunseeker", "逐日之民"), ("Moonkeeper", "护月之民"),
        ("Sea Wolf", "北洋之民"), ("Hellsguard", "红焰之民"), ("Raen", "晨曦之民"), ("Xaela", "暮晖之民"),
        ("Helions", "掠日之民"), ("Lost", "迷失之民"), ("Veena", "密林之民"), ("Rava", "山林之民"),
        ("Hume", "尘族"), ("Elf", "茕灵族"), ("Dwarf", "矮人族"), ("Mystel", "猫秘族"),
        ("Galdjent", "迦震族"), ("Drahn", "朵龙族"), ("Ronso", "隆索族"), ("Viis", "维斯族")
    };

    /// <summary>
    /// 执行提取。perCat=true 按勾选分类各写一个 json；false 合并写入 汇总.json。
    /// log 用于逐条日志回传；ct 可取消（已抓取部分仍保存）。
    /// 请求带节流（<see cref="RequestIntervalMs"/>）与失败退避；命中 Cloudflare 挑战页立即全局中止（-3），
    /// 并保留续抓进度（<see cref="ProgressValidHours"/> 小时内下次运行从断点续抓）。
    /// <para>
    /// 取数＝外部抓取器（游戏外独立进程）→ curl → HttpClient。<b>不再有「需要人工过 CF 验证」分支</b>：
    /// 2026-10-14 移除内嵌 WebView2 后，取不到就是取不到，如实上报并保存断点，绝不弹验证窗口。
    /// </para>
    /// 返回：新增术语条数；-1=参数/异常，-2=已取消，-3=被风控拦截中止。
    /// </summary>
    public async Task<int> ExportAsync(IReadOnlyList<string> prefixes, bool perCat, string dictionaryDir,
        Action<string>? log = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dictionaryDir))
        {
            LastResult = "[错误] 未设置词典目录";
            return -1;
        }

        // 用户黑名单（行式文本，大小写不敏感）
        var catDir = Path.Combine(dictionaryDir, "wiki_术语对照");
        Directory.CreateDirectory(catDir);
        var blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        blacklist.UnionWith(TextListFile.Load(Path.Combine(catDir, "wiki_术语对照_黑名单.json")));

        // 续抓进度：上次被风控拦截/取消而中断时保留的断点（有效期见 ProgressValidHours）。
        // 目的：续跑只补抓未抓过的页，而不是从头重打几百个请求再次触发 IP 冷却。
        var progress = LoadProgress(catDir);
        if (progress.Count > 0)
            log?.Invoke($"[提示] 检测到上次未完成的抓取进度，将从断点继续（{ProgressValidHours} 小时内有效）");

        var rejected = 0;   // 黑名单拒绝
        var noise = 0;      // 噪音词拒绝
        var added = 0;
        var hitExisting = 0;
        var pages = 0;
        var multi = 0;
        var multiList = new List<string>();

        // perCat=false 时单一汇总 terms
        JsonObject summary = new();
        var catResults = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var p in prefixes)
        {
            var t = p.TrimEnd('/');
            catResults[t] = new JsonObject();
        }

        var cancelled = false;     // 取消时跳出抓取循环，已抓取部分仍在下方写回保存
        var blockedGlobal = false; // 命中 Cloudflare 风控时全局中止，已抓取部分同样写回保存

        try
        {
            foreach (var prefix in prefixes)
            {
                var type = prefix.TrimEnd('/');
                var disp = CategoryFileNameZh(DisplayOf(type));
                var cur = catResults[type];
                var api = ApiEndpoint + "&gapprefix=" + Uri.EscapeDataString(prefix);
                var gapCont = "";
                var rvCont = "";
                if (progress.TryGetValue(type, out var savedCont))
                {
                    gapCont = savedCont.Gap;
                    rvCont = savedCont.Rv;
                    log?.Invoke($"[提示] {disp} 从上次断点继续（gapcontinue={gapCont}）");
                }
                var done = false;
                var catPages = 0;
                var catLogged = 0; // 已记录进度时的页数（按累计值每 100 页记一次，与单次响应批量大小无关）
                log?.Invoke($"[提示] 开始抓取 {disp} ...");

                while (!done)
                {
                    if (ct.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                    var url = api;
                    if (gapCont.Length > 0) url += "&gapcontinue=" + Uri.EscapeDataString(gapCont);
                    if (rvCont.Length > 0) url += "&rvcontinue=" + Uri.EscapeDataString(rvCont);

                    var (resp, blocked) = await FetchAsync(url, ct).ConfigureAwait(false);
                    if (blocked)
                    {
                        // 命中 Cloudflare 挑战页：立即全局中止，后续分类不再尝试（继续打只会延长 IP 冷却）
                        blockedGlobal = true;
                        break;
                    }
                    if (resp == null)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }
                        log?.Invoke($"[错误] 获取数据页失败（{disp}）：网络请求失败（curl 与内置 HttpClient 均未取到数据）");
                        break;
                    }

                    JsonObject? j;
                    try
                    {
                        j = JsonNode.Parse(resp) as JsonObject;
                    }
                    catch
                    {
                        j = null;
                    }
                    if (j == null)
                    {
                        var snip = (resp.Length > 200 ? resp[..200] : resp).Replace("\n", " ").Replace("\r", " ");
                        log?.Invoke($"[错误] 数据页解析失败（{disp}）：非 JSON 响应，开头：{snip}");
                        break;
                    }
                    if (j.ContainsKey("error"))
                    {
                        log?.Invoke($"[错误] API: {j["error"]?["info"]}");
                        break;
                    }

                    if (j["query"]?["pages"] is JsonObject pagesObj)
                    {
                        foreach (var kv in pagesObj)
                        {
                            if (kv.Value is not JsonObject p || p["revisions"] is not JsonArray revs || revs.Count == 0) continue;
                            if (revs[0] is not JsonObject r0) continue;
                            string? content = null;
                            if (r0["slots"]?["main"] is JsonObject main && main["*"] != null)
                                content = main["*"]!.ToString();
                            else if (r0["*"] != null)
                                content = r0["*"]!.ToString();
                            if (string.IsNullOrEmpty(content)) continue;
                            pages++;
                            catPages++;
                            ParseDataPage(content, cur, ref added, ref hitExisting, blacklist,
                                ref rejected, ref noise, ref multi, multiList);
                        }
                    }

                    // 分页：优先 rvcontinue 拿完当前批 revisions，再 gapcontinue 进下一批 allpages
                    var hasCont = false;
                    if (j["continue"] is JsonObject c)
                    {
                        if (c["rvcontinue"] != null)
                        {
                            rvCont = c["rvcontinue"]!.ToString();
                            hasCont = true;
                        }
                        else
                        {
                            rvCont = "";
                            if (c["gapcontinue"] != null)
                            {
                                gapCont = c["gapcontinue"]!.ToString();
                                hasCont = true;
                            }
                            else gapCont = "";
                        }
                    }
                    if (!hasCont)
                    {
                        gapCont = "";
                        rvCont = "";
                        done = true;
                    }

                    // 续抓点：本批已解析完毕才落盘；抓完的分类移除断点（下次从头抓，可拾取新增页）
                    if (done) progress.Remove(type);
                    else progress[type] = (gapCont, rvCont);
                    SaveProgress(catDir, progress);

                    if (catPages - catLogged >= 100)
                    {
                        catLogged = catPages;
                        log?.Invoke($"[进度] {disp} 已处理 {catPages} 页，新增 {added} 条（命中已有 {hitExisting}，跳过）");
                    }
                }

                if (blockedGlobal)
                {
                    log?.Invoke($"[错误] {disp} 被 CDN 风控拦截（Cloudflare 验证页，非数据），已中止全部分类抓取。" +
                                "已抓取部分仍会保存；请间隔数十分钟或切换网络/代理后重试，切勿连续重试。");
                    break;
                }
            }

            // 未被取消、未被拦截：全部分类均已抓完，清掉续抓进度（下次为完整抓取，可拾取新增页）
            if (!cancelled && !blockedGlobal)
                SaveProgress(catDir, progress);

            // 汇总模式：合并全部分类
            if (!perCat)
            {
                foreach (var t in catResults)
                {
                    if (t.Value.Count == 0) continue;
                    foreach (var kv in t.Value)
                    {
                        if (summary[kv.Key] == null)
                            summary[kv.Key] = kv.Value?.DeepClone();
                        else
                            hitExisting++;
                    }
                }
            }

            // 固定种族词表：分类模式并入 Race（勾选时），汇总模式并入 summary
            if (perCat)
            {
                if (catResults.TryGetValue("Race", out var raceCat))
                    AddRaceTerms(raceCat, ref added, ref hitExisting, blacklist, ref rejected, ref noise);
                else
                    log?.Invoke("[提示] 未勾选种族分类，固定种族/子种族词表未并入");
            }
            else
            {
                AddRaceTerms(summary, ref added, ref hitExisting, blacklist, ref rejected, ref noise);
            }

            // 写回
            if (perCat)
            {
                var catFiles = 0;
                var catEmpty = 0;
                foreach (var kv in catResults)
                {
                    if (kv.Value.Count == 0)
                    {
                        catEmpty++;
                        continue;
                    }
                    var outObj = new JsonObject { ["terms"] = kv.Value.DeepClone() };
                    var fileName = CategoryFileNameZh(DisplayOf(kv.Key)) + ".json";
                    File.WriteAllText(Path.Combine(catDir, fileName),
                        outObj.ToJsonString(JsonFile.Indented), Encoding.UTF8);
                    catFiles++;
                }
                if (catEmpty > 0)
                    log?.Invoke($"[提示] 跳过 {catEmpty} 个抓取结果为 0 条的分类（灰机 wiki 无对应数据页，不生成空文件）");
                CleanupEmptyCatFiles(catDir);
                log?.Invoke($"[完成] wiki 分类术语已写入 词典目录\\wiki_术语对照\\（{catFiles} 个分类文件，独立只读底料，读取时自动生效）");
            }
            else if (summary.Count == 0)
            {
                // 全部失败/刚开始就取消：空结果不落盘，避免覆盖掉之前积累的 汇总.json
                log?.Invoke("[提示] 本次未抓到任何术语，保留原有 汇总.json 不覆盖");
            }
            else
            {
                var outObj = new JsonObject { ["terms"] = summary.DeepClone() };
                File.WriteAllText(Path.Combine(catDir, "wiki汇总.json"),
                    outObj.ToJsonString(JsonFile.Indented), Encoding.UTF8);
                log?.Invoke("[完成] wiki 术语词典已更新（wiki_术语对照\\wiki汇总.json，独立只读底料，读取时自动生效）");
            }

            if (noise > 0)
                log?.Invoke($"[提示] 已拒绝 {noise} 条动作/答语类噪音词条（Yes/No/On/Off/Play/Quit 等基础词，不入库避免污染词块翻译）");
            if (rejected > 0)
                log?.Invoke($"[提示] 已拒绝 {rejected} 条用户黑名单词条（wiki_术语对照_黑名单.json，直接不入库）");
            if (multi > 0)
            {
                var mm = $"[提示] 一词多义 {multi} 处（同一英文在不同分类出现不同中文，汇总提取保留先到者；需要各自保留请用分类提取）：";
                mm += string.Join("；", multiList.Take(10));
                if (multiList.Count > 10) mm += " 等";
                log?.Invoke(mm);
            }
            log?.Invoke($"[提示] 诊断：共解析 {pages} 个数据页；本次新增术语 {added} 条，命中已有词条 {hitExisting} 条（跳过）");
            if (cancelled)
            {
                LastResult = "Wiki 提取已取消（已抓取部分已保存）";
                _log.Info(LastResult);
                return -2;
            }
            if (blockedGlobal)
            {
                LastResult = "Wiki 提取被 CDN 风控拦截已中止（已抓取部分已保存；请间隔数十分钟或换网络后重试）";
                _log.Info(LastResult);
                return -3;
            }

            LastResult = $"Wiki 提取完成：新增 {added} 条，命中已有 {hitExisting} 条，用时 {DateTime.Now:HH:mm:ss}";
            _log.Info(LastResult);
            return added;
        }
        catch (Exception ex)
        {
            LastResult = "[错误] Wiki 提取异常：" + ex.Message;
            _log.Info(LastResult);
            return -1;
        }
    }

    /// <summary>
    /// 读取续抓进度：上次中断时保存的各分类分页断点（gapcontinue/rvcontinue）。
    /// 不存在、解析失败、超过 <see cref="ProgressValidHours"/> 小时均返回空字典（改从头抓取）。
    /// </summary>
    internal static Dictionary<string, (string Gap, string Rv)> LoadProgress(string catDir)
    {
        var res = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        var path = Path.Combine(catDir, ProgressFileName);
        if (!File.Exists(path)) return res;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return res;
            var savedAt = root["savedAt"]?.ToString();
            if (!string.IsNullOrEmpty(savedAt) &&
                DateTime.TryParse(savedAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var saved) &&
                (DateTime.UtcNow - saved).TotalHours > ProgressValidHours)
                return res; // 过期：丢弃，避免长期跳过断点之前的页面
            if (root["items"] is JsonObject items)
            {
                foreach (var kv in items)
                {
                    if (kv.Value is not JsonObject o) continue;
                    res[kv.Key] = (o["gapcontinue"]?.ToString() ?? "", o["rvcontinue"]?.ToString() ?? "");
                }
            }
        }
        catch
        {
            res.Clear();
        }
        return res;
    }

    /// <summary> 写入续抓进度；map 为空则删除进度文件。失败不影响主流程（仅失去续跑能力）。 </summary>
    internal static void SaveProgress(string catDir, Dictionary<string, (string Gap, string Rv)> map)
    {
        var path = Path.Combine(catDir, ProgressFileName);
        try
        {
            if (map.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            var items = new JsonObject();
            foreach (var kv in map)
                items[kv.Key] = new JsonObject
                {
                    ["gapcontinue"] = kv.Value.Gap,
                    ["rvcontinue"] = kv.Value.Rv
                };
            var root = new JsonObject
            {
                ["savedAt"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["items"] = items
            };
            // 原子写（.tmp → File.Move）：续抓进度虽属派生状态，但直写被中断会留半截 JSON，
            // 复用 JsonFile.WriteAtomic 与项目内其它词典/快照写入口径一致（审查方 2026-10-10 观察项）。
            JsonFile.WriteAtomic(path, root.ToJsonString(JsonFile.Indented));
        }
        catch
        {
            /* 进度保存失败可忽略 */
        }
    }

    /// <summary> 解析 Data 数据页：取 中文名/cn + 英文名/en，两者齐全且不同时入库。 </summary>
    private static void ParseDataPage(string content, JsonObject terms, ref int added, ref int hitExisting,
        HashSet<string> blacklist, ref int rejected, ref int noise, ref int multi, List<string> multiList)
    {
        JsonObject? o;
        try
        {
            o = JsonNode.Parse(content) as JsonObject;
        }
        catch
        {
            return;
        }
        if (o == null) return;
        var zh = GetStr(o, "中文名") ?? GetStr(o, "cn") ?? "";
        var en = GetStr(o, "英文名") ?? GetStr(o, "en") ?? "";
        if (!string.IsNullOrEmpty(zh) && !string.IsNullOrEmpty(en) && zh != en)
            AddTerm(terms, en, zh, ref added, ref hitExisting, blacklist, ref rejected, ref noise, ref multi, multiList);
    }

    private static string? GetStr(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static void AddRaceTerms(JsonObject terms, ref int added, ref int hitExisting,
        HashSet<string> blacklist, ref int rejected, ref int noise)
    {
        var multi = 0;
        var multiList = new List<string>();
        foreach (var r in RaceTerms)
            AddTerm(terms, r.En, r.Zh, ref added, ref hitExisting, blacklist, ref rejected, ref noise, ref multi, multiList);
    }

    /// <summary>
    /// 清洗 wiki 侧取到的中文名（2026-10-14 新增，教训来自用户报障）。
    /// <para>
    /// 灰机 wiki 的命名习惯是「&lt;前缀&gt;:&lt;英文曲名&gt; -&lt;中文译名&gt;-」——用连字符把中文译名括起来当编者注。
    /// 物品「Answers Orchestrion Roll」的中文名因此是
    /// <c>管弦乐琴乐谱:Answers\u3000-生死答问-</c>，而国服本地化译名其实只有「生死答问」
    /// （wiki「Endwalker/Answers - Piano」页自述）。原样入库会让游戏内选项出现中英混杂。
    /// </para>
    /// 两步：① 全角空格 U+3000 一律折叠成普通空格；② 把「&lt;英文曲名&gt; -&lt;中文译名&gt;-」
    /// 整段替换成「&lt;中文译名&gt;」——**保留中文、剥掉英文曲名与编者连字符**。
    /// 反向做法（只剥连字符留下英文）是错的，会把译名洗成纯英文，比不洗更糟。
    /// </para>
    /// 安全边界：正则要求**英文曲名在前**，所以「A级精英杀手4」这类等级/型号标识、
    /// 「生死答问 - 回响」这类本身就用连字符的中文主副标题，都不会被误伤。
    /// </summary>
    internal static string CleanWikiZh(string zh)
    {
        if (string.IsNullOrEmpty(zh)) return zh;

        // ★ URL 守卫：含 "://" 的字符串一律原样放行，不做任何清洗。
        //   否则形如 "https://x.com/a-中文-b" 会被下方规则当成「英文-中文-」削掉一段，把链接洗坏。
        if (zh.Contains("://", StringComparison.Ordinal)) return zh;

        var s = zh.Replace('\u3000', ' ').Trim();

        // 「英文曲名 -中文译名-」→「中文译名」。英文段允许字母数字与空格撇号，中文段不许再含连字符。
        var cleaned = System.Text.RegularExpressions.Regex.Replace(s,
            "[A-Za-z][A-Za-z0-9 ':\\-]*\\s*[-\\u2010\\u2011\\u2012\\u2013\\u2014]\\s*([^\\x00-\\x7F\\-\\u2010\\u2011\\u2012\\u2013\\u2014]+)\\s*[-\\u2010\\u2011\\u2012\\u2013\\u2014]",
            "$1");
        return cleaned.Trim();
    }

    /// <summary> 唯一入库口：校验、噪音/黑名单拒绝、去重、多义统计。 </summary>
    private static void AddTerm(JsonObject terms, string enRaw, string zhRaw, ref int added, ref int hitExisting,
        HashSet<string> blacklist, ref int rejected, ref int noise, ref int multi, List<string> multiList)
    {
        var e = enRaw.Trim();
        var z = CleanWikiZh(zhRaw.Trim());
        if (e.Length == 0 || z.Length == 0 || e.Length > 80 || z.Length > 120) return;
        if (!IsValidEnglish(e) || !IsValidChinese(z)) return;
        if (e == z) return;
        if (NoiseTerms.Contains(e))
        {
            noise++;
            return;
        }
        if (blacklist.Contains(e))
        {
            rejected++;
            return;
        }
        if (terms[e] != null)
        {
            var old = terms[e]!.ToString();
            if (old != z)
            {
                multi++;
                if (multiList.Count < 50) multiList.Add($"{e}：{old} / {z}");
            }
            hitExisting++;
            return;
        }
        terms[e] = z;
        added++;
    }

    /// <summary> 英文侧校验：不含中文、含字母、无 wiki/HTML 标记。 </summary>
    private static bool IsValidEnglish(string s)
    {
        if (ContainsChinese(s)) return false;
        if (!s.Any(char.IsLetter)) return false;
        return !s.Contains("[[") && !s.Contains("]]") && !s.Contains("{{") && !s.Contains("}}") &&
               !s.Contains('<') && !s.Contains('>');
    }

    /// <summary> 中文侧校验：必须含中文、无 wiki/HTML 标记与等号。 </summary>
    private static bool IsValidChinese(string s)
    {
        if (!ContainsChinese(s)) return false;
        return !s.Contains("[[") && !s.Contains("]]") && !s.Contains("{{") && !s.Contains("}}") &&
               !s.Contains('<') && !s.Contains('>') && !s.Contains('=');
    }

    private static bool ContainsChinese(string s)
    {
        foreach (var c in s)
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
        return false;
    }

    /// <summary> 分类显示名 -> 中文文件名（「物品（Item）」->「物品」，清洗非法字符）。 </summary>
    private static string CategoryFileNameZh(string display)
    {
        var p = display.IndexOf('（');
        if (p < 0) p = display.IndexOf('(');
        var name = p >= 0 ? display[..p] : display;
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    private static string DisplayOf(string type)
        => Categories.FirstOrDefault(c => c.Prefix.TrimEnd('/') == type)?.Display ?? type;

    /// <summary> 清理历史空分类文件（terms 为空的 .json，黑名单文件不动）。 </summary>
    private static void CleanupEmptyCatFiles(string catDir)
    {
        foreach (var f in Directory.GetFiles(catDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(f) == "wiki_术语对照_黑名单.json") continue;
            if (Path.GetFileName(f) == "wiki汇总.json") continue;
            if (Path.GetFileName(f) == ProgressFileName) continue; // 续抓进度不是术语文件，不参与空文件清理
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(f)) as JsonObject;
                if (root != null && root["terms"] is JsonObject t && t.Count == 0)
                    File.Delete(f);
            }
            catch
            {
                /* 解析失败不删 */
            }
        }
    }
}
