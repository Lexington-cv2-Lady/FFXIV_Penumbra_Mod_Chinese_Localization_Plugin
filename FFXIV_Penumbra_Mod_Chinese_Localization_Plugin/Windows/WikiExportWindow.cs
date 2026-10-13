using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using FFXIVPenumbraHanhua.Services;

namespace FFXIVPenumbraHanhua.Windows;

/// <summary> Wiki 提取窗口：从灰机 wiki 抓取官方中/英名，写入 词典目录\wiki_术语对照\。 </summary>
public class WikiExportWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly WikiExportService _wiki;

    private readonly bool[] _catChecked;
    private bool _perCat = true;          // true=分类提取（默认），false=汇总提取
    private string _result = "";
    private Task<int>? _task;
    private CancellationTokenSource? _cts;
    private string? _captchaUrl;   // 待人工过验证的 wiki URL（撞 CF 时由 service 回填）
    private string? _lastCaptchaUrl; // 最近一次待验证的 URL（「重新打开验证窗口」按钮用）
    private bool _captchaWindowClosed;   // 验证窗口已被用户关闭（fetcher 线程置位）
    private bool _awaitingCaptchaResume; // 上一轮因等待人工验证而暂停（-4），关窗后应自动续跑
    private bool _captchaResumeUsed;     // 本轮提取会话的「关窗自动续跑」机会已用掉（一次性，防弹窗风暴）

    public WikiExportWindow(Plugin plugin) : base("Wiki 提取###HanhuaWiki")
    {
        Size = new Vector2(1280, 500);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        _plugin = plugin;
        _wiki = plugin.Wiki;
        // 撞 Cloudflare 验证时：弹 WebView2 验证窗口，请司令官点一次；cookie 存进持久 profile 后自动复用
        _wiki.OnCaptchaNeeded = url => _captchaUrl = url;
        // 验证窗口被关闭：标记置位，Draw 里据此从断点自动续跑
        _wiki.OnCaptchaWindowClosed = () => _captchaWindowClosed = true;
        _catChecked = new bool[WikiExportService.Categories.Count];
        for (var i = 0; i < _catChecked.Length; i++) _catChecked[i] = true;
    }

    public void Dispose() => _cts?.Cancel();

    public override void Draw()
    {
        var cfg = _plugin.Configuration;
        var dictDir = cfg.DictionaryPath;
        var dictOk = !string.IsNullOrWhiteSpace(dictDir) && System.IO.Directory.Exists(dictDir);

        ImGui.TextWrapped("从灰机 wiki（cdn.huijiwiki.com/ff14）抓取 FFXIV 官方中/英名，写入 词典目录\\wiki_术语对照\\。");
        Ui.Hint("注意：该功能可能因灰机 wiki 的风控而暂时失效（CDN 拦截脚本客户端）。");
        Ui.Hint("请求已内置节流（约 0.7 秒/次）；被风控拦截时请间隔数十分钟或换网络后再试，插件会自动从上次断点续抓。");
        ImGui.Spacing();

        // WebView2 直抓开关：默认开（用真实浏览器指纹绕开 CDN 对脚本的风控）；未就绪/失败自动回退 curl。
        var wv2On = cfg.EnableWebView2Fetch;
        if (ImGui.Checkbox("启用 WebView2 抓取（真实浏览器指纹绕开 CDN 风控，需本机已装 WebView2 Runtime；改动后重载插件生效）##Wv2Fetch", ref wv2On))
        {
            cfg.EnableWebView2Fetch = wv2On;
            cfg.Save();
        }
        // WebView2 环境预检：没装则提示 + 官方下载链接
        var wv2Ver = WebView2RuntimeProbe.TryGetInstalledVersion();
        if (wv2Ver == null)
        {
            Ui.Hint("检测到本机未安装 WebView2 Runtime，WebView2 抓取不可用（将回退 curl，多数情况下仍会被 CDN 拦截）。");
            if (ImGui.Button("打开官方 WebView2 下载页##Wv2Dl"))
                OpenUrl(WebView2RuntimeProbe.OfficialDownloadUrl);
        }
        else
        {
            Ui.Hint($"本机 WebView2 Runtime：{wv2Ver}");
        }
        ImGui.Spacing();

        Ui.Hint($"词典目录：{dictDir}（{(dictOk ? "存在" : "不存在，请先在「目录和词典管理」里配置词典目录")}）");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 提取模式
        if (ImGui.RadioButton("分类提取（每个分类一个 json，互不影响）", _perCat)) _perCat = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("wiki汇总（全部合并到 wiki汇总.json）", !_perCat)) _perCat = false;
        ImGui.Spacing();

        // 分类勾选（分类提取模式）
        if (_perCat)
        {
            ImGui.TextUnformatted("勾选要提取的分类（未勾选不抓取）：");
            ImGui.Spacing();
            for (var i = 0; i < WikiExportService.Categories.Count; i++)
            {
                if (i > 0) ImGui.SameLine();
                if (ImGui.Checkbox(WikiExportService.Categories[i].Display, ref _catChecked[i]))
                {
                    /* 直接改字段 */
                }
            }
            ImGui.Spacing();
        }

        ImGui.Separator();
        ImGui.Spacing();

        if (_task != null && !_task.IsCompleted)
        {
            if (ImGui.Button("取消提取"))
            {
                _cts?.Cancel();
            }
        }
        else
        {
            if (ImGui.Button("开始提取"))
            {
                if (!dictOk)
                {
                    _result = "未设置词典目录或目录不存在";
                }
                else
                {
                    StartExtract(dictDir);
                }
            }
            // 等待人工验证期间：逃生按钮与「开始提取」并列（白窗/窗口被全屏游戏挡住时一眼能找到）
            if (_awaitingCaptchaResume)
            {
                ImGui.SameLine();
                if (ImGui.Button("重新打开验证窗口##CapReopenTop"))
                {
                    _captchaWindowClosed = false;
                    _plugin.Wiki.ShowCaptcha(_lastCaptchaUrl ?? "https://cdn.huijiwiki.com/ff14/api.php");
                    _result += "\n[提示] 已重新弹出验证窗口。";
                }
                ImGui.SameLine();
                if (ImGui.Button("隐藏验证窗口##CapHideTop"))
                {
                    _plugin.Wiki.HideCaptchaWindow();
                    _result += "\n[提示] 验证窗口已隐藏（WebView2 与 cookie 保留，可随时重新打开）。";
                }
                ImGui.SameLine();
                if (ImGui.Button("停止等待##CapStopTop"))
                {
                    _awaitingCaptchaResume = false;
                    _captchaWindowClosed = false;
                    _captchaResumeUsed = false;
                    _result += "\n[提示] 已停止自动续跑；重新点「开始提取」即可（断点已保存）。";
                }
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("抓取全部页面可能需要几分钟；增量更新：已存在的词条不会被覆盖；黑名单与内置噪音词不入库");
        }

        ImGui.Spacing();

        // 日志结果区
        var boxH = Math.Min(200f, Math.Max(100f, ImGui.GetContentRegionAvail().Y));
        Plugin.ResultBox("##WikiResult", _result, "提取日志将显示在这里（进度 / 新增 / 命中已有 / 拒绝数…）", boxH, true);

        // 轮询任务完成
        if (_task != null && _task.IsCompleted)
        {
            try
            {
                var n = _task.Result;
                if (n != 0) _plugin.ReloadDictionary();
                if (n >= 0 && _result.Length == 0) _result = _wiki.LastResult;
                if (n == -4)
                {
                    // 需要人工验证：标记待续跑；提示用户在验证窗口完成验证后关窗即自动继续。
                    // 但「关窗续跑」机会**一次性**：2026-10-13 18:01 实录弹窗风暴——
                    // 白窗无法验证 → 用户反复关窗 → 续跑 → 再撞墙 → 再弹窗，每秒一轮无限循环。
                    // 用掉机会后再撞墙，只提示、不再自动续跑/弹窗，由司令官手动决定下一步。
                    _awaitingCaptchaResume = true;
                    _result += (_result.Length > 0 ? "\n" : "")
                        + (_captchaResumeUsed
                            ? "[提示] 验证仍未完成（断点已保存）。请在验证窗口内完成 Cloudflare 验证后点「重新打开验证窗口」或「开始提取」继续；自动续跑已停止，避免反复弹窗。"
                            : "[提示] 已弹出验证窗口：请在窗口内完成 Cloudflare 验证，然后关闭验证窗口，插件会自动从断点继续提取。");
                }
            }
            finally
            {
                _task = null;
                _cts?.Dispose();
                _cts = null;
            }
        }

        // 撞 Cloudflare 验证：弹 WebView2 验证窗口，请司令官点一次；cookie 存进持久 profile 后下次自动复用
        if (_captchaUrl != null)
        {
            var url = _captchaUrl;
            _captchaUrl = null;
            _lastCaptchaUrl = url; // 记住：白窗/关不掉时「重新打开验证窗口」按钮要用
            _plugin.Wiki.ShowCaptcha(url);
            _result += (_result.Length > 0 ? "\n" : "")
                       + "[提示] 检测到 Cloudflare 验证，已弹出验证窗口：请在窗口内完成验证，插件会自动继续提取。";
        }

        // 验证窗口关闭 → 自动从断点续跑（仅当上一轮是因等待人工验证而暂停的 -4，且机会未用掉）
        if (_awaitingCaptchaResume && _captchaWindowClosed && !_captchaResumeUsed && (_task == null || _task.IsCompleted))
        {
            _awaitingCaptchaResume = false;
            _captchaWindowClosed = false;
            StartExtract(dictDir, "[提示] 验证窗口已关闭，从断点继续提取…", isResume: true);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            /* 打开失败可忽略 */
        }
    }

    /// <summary> 按当前勾选启动提取。<paramref name="resumeNote"/> 非空时作为结果区首行（验证后自动续跑用）。 </summary>
    private void StartExtract(string dictDir, string? resumeNote = null, bool isResume = false)
    {
        // 新的一轮：清掉上一轮的验证等待状态。
        // ⚠ _captchaResumeUsed 仅「用户主动开新轮」才重置——续跑（isResume=true）绝不 clears：
        // 2026-10-13 18:36 实录，续跑分支置 true 后进这里被清 false，一次性防护形同虚设，风暴继续。
        _awaitingCaptchaResume = false;
        _captchaWindowClosed = false;
        _captchaResumeUsed = isResume; // 续跑=true（机会已用，再撞墙只提示）；用户新轮=false（重新武装）

        var prefixes = new List<string>();
        if (_perCat)
        {
            for (var i = 0; i < _catChecked.Length; i++)
                if (_catChecked[i]) prefixes.Add(WikiExportService.Categories[i].Prefix);
            if (prefixes.Count == 0)
            {
                _result = "请至少勾选一个分类";
                return;
            }
        }
        else
        {
            prefixes.AddRange(new[] { "Item/", "Action/", "Quest/", "Achievement/", "Emote/" });
        }

        _result = resumeNote ?? "";
        _cts = new CancellationTokenSource();
        _task = Task.Run(async () =>
        {
            var n = await _wiki.ExportAsync(prefixes, _perCat, dictDir,
                msg => _result = _result.Length > 0 ? _result + "\n" + msg : msg,
                _cts.Token);
            return n;
        });
    }
}
