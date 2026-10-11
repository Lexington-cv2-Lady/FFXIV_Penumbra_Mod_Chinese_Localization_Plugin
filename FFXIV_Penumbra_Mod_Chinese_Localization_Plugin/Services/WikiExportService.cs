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
/// </summary>
public sealed class WikiExportService
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

    /// <summary> 可选的 WebView2 抓取器（真实浏览器指纹取数，绕开 CDN 对脚本的风控）；null=只用 curl。 </summary>
    private readonly WebView2WikiFetcher? _wv2;

    /// <summary> 抓取用 UA（curl 与 HttpClient 共用）。 </summary>
    private const string UserAgentValue = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) FFXIVPenumbraHanhua";

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

    public WikiExportService(AppLog log, bool enableWebView2 = false)
    {
        _log = log;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentValue);
        _http.Timeout = TimeSpan.FromSeconds(30);
        if (enableWebView2)
        {
            _wv2 = new WebView2WikiFetcher();
            _wv2.Start(); // 独立线程异步初始化；未就绪/失败自动回退 curl 路径
        }
    }

    /// <summary>
    /// 取数据页正文（含节流与退避）。
    /// 灰机 wiki 的 CDN 是 Cloudflare。早期它主要按 TLS 握手指纹（JA3/JA4）打分，.NET 的 SChannel 指纹会被拦，
    /// 故仍优先用系统自带 curl.exe（指纹更接近浏览器）。
    /// 但 2026-10 实测：**风控已升级到「速率/行为」维度**——短时间内密集请求会把出口 IP 拉黑（curl 指纹同样不放行），
    /// 且冷却长达数十分钟到数小时。因此本方法统一做两件事：
    /// ① 请求节流：相邻请求之间至少间隔 <see cref="RequestIntervalMs"/> 毫秒；
    /// ② 失败退避：网络失败按 10s/30s 退避重试，重试耗尽才放弃。
    /// 命中 Cloudflare 挑战页时**不重试**（反复试只会延长封禁），交由调用方全局中止。
    /// </summary>
    /// <returns>(正文, 是否被风控拦截)。被拦时正文为 null 且第二项为 true。</returns>
    private async Task<(string? Body, bool Blocked)> FetchAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (ct.IsCancellationRequested) return (null, false);
            if (!await ThrottleAsync(ct).ConfigureAwait(false)) return (null, false);

        // 取数优先级：WebView2（真实浏览器指纹，CDN 放行）→ curl → HttpClient。
        // WebView2 未就绪/失败即回退，故「WebView2 不可用」只会退回现状，不会让功能更差。
        var body = await FetchViaWebView2(url, ct).ConfigureAwait(false)
                   ?? await TryCurlAsync(url, ct).ConfigureAwait(false)
                   ?? await TryHttpAsync(url, ct).ConfigureAwait(false);

        if (body != null)
        {
            // 拿到内容但是 Cloudflare 挑战页：确认被风控，立刻上报，不再重试
            if (CloudflareChallengeDetector.IsChallenge(body))
                return (null, true);
            return (body, false);
        }

            if (ct.IsCancellationRequested) return (null, false);
            if (attempt >= MaxRetries) return (null, false);
            if (!await DelayAsync(attempt == 0 ? RetryDelayFirstMs : RetryDelayNextMs, ct).ConfigureAwait(false))
                return (null, false);
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

    /// <summary> WebView2（真实浏览器指纹）取数；未启用/未就绪/失败均返回 null，交回 curl 路径。 </summary>
    private Task<string?> FetchViaWebView2(string url, CancellationToken ct)
        => _wv2 != null && _wv2.IsAvailable ? _wv2.FetchAsync(url, ct) : Task.FromResult<string?>(null);

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
    //    种族.json（2026-10-11 司令官截图指出）。改本表务必同步核对词典与 wiki 侧。
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
                var api = "https://cdn.huijiwiki.com/ff14/api.php?action=query&generator=allpages&format=json&utf8=1" +
                          "&gaplimit=500&prop=revisions&rvprop=content&rvslots=main&gapnamespace=3500&gapprefix=" +
                          Uri.EscapeDataString(prefix);
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

    /// <summary> 唯一入库口：校验、噪音/黑名单拒绝、去重、多义统计。 </summary>
    private static void AddTerm(JsonObject terms, string enRaw, string zhRaw, ref int added, ref int hitExisting,
        HashSet<string> blacklist, ref int rejected, ref int noise, ref int multi, List<string> multiList)
    {
        var e = enRaw.Trim();
        var z = zhRaw.Trim();
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
