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
    /// <summary> 验证 cookie 保守有效期（分钟）。cf_clearance 实际可存更久，过期后再过点即可。 </summary>
    private const int CaptchaTtlMinutes = 60;

    private readonly Plugin _plugin;
    private readonly WikiExportService _wiki;

    private readonly bool[] _catChecked;
    private bool _perCat = true;          // true=分类提取（默认），false=汇总提取
    private string _result = "";
    private Task<int>? _task;
    private CancellationTokenSource? _cts;
    private WikiCaptchaWindow? _captcha;  // Cloudflare 验证窗口（实验功能；主线程轮询）

    public WikiExportWindow(Plugin plugin) : base("Wiki 提取###HanhuaWiki")
    {
        Size = new Vector2(1280, 500); // 默认放宽：勾选行单行排到「种族（Race）」不被裁
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        _plugin = plugin;
        _wiki = plugin.Wiki;
        _catChecked = new bool[WikiExportService.Categories.Count];
        // 默认全勾选（列表已精简为灰机 wiki 实测有数据的分类）
        for (var i = 0; i < _catChecked.Length; i++) _catChecked[i] = true;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _captcha?.Dispose();
        _captcha = null;
    }

    public override void Draw()
    {
        var cfg = _plugin.Configuration;
        var dictDir = cfg.DictionaryPath;
        var dictOk = !string.IsNullOrWhiteSpace(dictDir) && System.IO.Directory.Exists(dictDir);

        ImGui.TextWrapped("从灰机 wiki（cdn.huijiwiki.com/ff14）抓取 FFXIV 官方中/英名，写入 词典目录\\wiki_术语对照\\。");
        Ui.Hint("注意：该功能可能因灰机 wiki 的风控而暂时失效。");
        Ui.Hint("请求已内置节流（约 0.7 秒/次）；若日志提示被 CDN 风控拦截，请间隔数十分钟或换网络后再试，插件会自动从上次断点续抓。");
        ImGui.Spacing();
        // 实验开关：默认关。开启后遇 Cloudflare 验证会弹独立 WebView2 窗口让用户过点，插件自动接管 cookie 续跑。
        var captchaOn = cfg.WikiCaptchaVerify;
        if (ImGui.Checkbox("启用 wiki 验证接管（遇 Cloudflare 验证弹独立窗口；实验功能，需游戏内实测）##WikiCaptcha", ref captchaOn))
        {
            cfg.WikiCaptchaVerify = captchaOn;
            cfg.Save();
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
            // 单行排列：窗口默认已放宽到能完整显示到「种族（Race）」
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
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("抓取全部页面可能需要几分钟；增量更新：已存在的词条不会被覆盖；黑名单与内置噪音词不入库");
        }

        ImGui.Spacing();

        // 日志结果区：带边框统一风格；高度自适应（矮窗口收缩，最高 200）。默认自动跟随底部，
        // 但用户手动上滑看历史时不抢滚动条（ResultBox 内部按是否贴底判定）。
        var boxH = Math.Min(200f, Math.Max(100f, ImGui.GetContentRegionAvail().Y));
        Plugin.ResultBox("##WikiResult", _result, "提取日志将显示在这里（进度 / 新增 / 命中已有 / 拒绝数…）", boxH, true);

        // 轮询任务完成
        if (_task != null && _task.IsCompleted)
        {
            try
            {
                var n = _task.Result;
                if (n != 0) _plugin.ReloadDictionary(); // 取消(-2)时也可能已写入部分词条，需重载
                if (n >= 0 && _result.Length == 0) _result = _wiki.LastResult;
                // -4 = 遇到 Cloudflare 验证且开启接管 → 弹独立 WebView2 窗口让用户过点
                if (n == -4 && cfg.WikiCaptchaVerify && _wiki.PendingCaptchaUrl != null && _captcha == null)
                {
                    _captcha = new WikiCaptchaWindow(_plugin.AppLog);
                    _captcha.Start(_wiki.PendingCaptchaUrl!);
                    _result += (_result.Length > 0 ? "\n" : "") + "[提示] 已弹出 wiki 验证窗口：请在窗口内完成 Cloudflare 验证，插件将自动接管 cookie 并继续提取。";
                }
            }
            finally
            {
                _task = null;
                _cts?.Dispose();
                _cts = null;
            }
        }

        PollCaptcha(dictOk ? dictDir : null);
    }

    /// <summary>
    /// 主线程轮询 Cloudflare 验证窗口：拿到 cookie 则注入并自动从断点续跑；
    /// 失败/用户取消则关闭并回退（不阻断其它功能）。
    /// </summary>
    private void PollCaptcha(string? dictDir)
    {
        if (_captcha == null) return;

        if (_captcha.HasCookie)
        {
            var ck = _captcha.TakeCookie();
            _captcha.Dispose();
            _captcha = null;
            if (!string.IsNullOrEmpty(ck) && dictDir != null)
            {
                _wiki.SetCaptchaCookie(ck!, CaptchaTtlMinutes);
                _result += (_result.Length > 0 ? "\n" : "") + "[完成] 已获取 Cloudflare 验证 cookie，自动继续提取…";
                StartExtract(dictDir); // 从已保存的断点续跑
            }
            return;
        }

        if (_captcha.Failed || _captcha.ClosedByUser || !_captcha.IsActive)
        {
            var failed = _captcha.Failed;
            _captcha.Dispose();
            _captcha = null;
            _result += (_result.Length > 0 ? "\n" : "")
                       + (failed
                           ? "[提示] 验证窗口不可用（WebView2 初始化失败），已回退。可关闭「wiki 验证接管」或稍后重试。"
                           : "[提示] 已取消 Cloudflare 验证。");
        }
    }

    /// <summary> 按当前勾选启动提取（供「开始提取」与验证后续跑复用）。 </summary>
    private void StartExtract(string dictDir)
    {
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
            // 汇总提取：固定抓取有数据的分类（Race 为固定词表，提取时自动并入）
            prefixes.AddRange(new[] { "Item/", "Action/", "Quest/", "Achievement/", "Emote/" });
        }

        _result = "";
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
