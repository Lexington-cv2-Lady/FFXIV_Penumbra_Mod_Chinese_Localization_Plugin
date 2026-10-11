using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    // B 方案（零依赖）：撞 Cloudflare 验证时，用系统浏览器过点后把 cf_clearance cookie 粘回，自动续跑。
    private string? _pendingCaptchaUrl;   // 待过点的验证 URL
    private string _captchaCookieInput = "";

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
        Ui.Hint("注意：该功能可能因灰机 wiki 的风控而暂时失效。");
        Ui.Hint("请求已内置节流（约 0.7 秒/次）；若日志提示被 CDN 风控拦截，请间隔数十分钟或换网络后再试，插件会自动从上次断点续抓。");
        ImGui.Spacing();
        var captchaOn = cfg.WikiCaptchaVerify;
        if (ImGui.Checkbox("启用 wiki 验证接管（撞验证时用浏览器过点并粘贴 cookie，自动续跑）##WikiCaptcha", ref captchaOn))
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
                // -4 = 遇到 Cloudflare 验证且开启接管 → 自动复制验证链接，准备浏览器过点+粘 cookie
                if (n == -4 && cfg.WikiCaptchaVerify && _wiki.PendingCaptchaUrl != null)
                {
                    _pendingCaptchaUrl = _wiki.PendingCaptchaUrl;
                    ImGui.SetClipboardText(_pendingCaptchaUrl);
                    _result += (_result.Length > 0 ? "\n" : "")
                               + "[提示] 检测到 Cloudflare 验证：已把验证链接复制到剪贴板。请用浏览器过点后，把 cf_clearance cookie 粘到窗口下方，插件将自动从断点继续。";
                }
            }
            finally
            {
                _task = null;
                _cts?.Dispose();
                _cts = null;
            }
        }

        DrawCaptchaPanel(dictOk ? dictDir : null);
    }

    /// <summary> 验证面板（B 方案）：打开/复制验证链接 + cf_clearance cookie 回填，收到后自动续跑。 </summary>
    private void DrawCaptchaPanel(string? dictDir)
    {
        if (_pendingCaptchaUrl == null) return;

        ImGui.Separator();
        ImGui.TextWrapped("撞到 Cloudflare 验证。请用浏览器打开下方链接完成验证，然后把 cf_clearance cookie 粘到下面，插件会自动从断点继续：");
        if (ImGui.Button("用浏览器打开验证页##OpenUrl"))
        {
            OpenUrl(_pendingCaptchaUrl);
        }
        ImGui.SameLine();
        if (ImGui.Button("复制验证链接##CopyUrl"))
        {
            ImGui.SetClipboardText(_pendingCaptchaUrl);
            _result += (_result.Length > 0 ? "\n" : "") + "[提示] 已复制验证链接到剪贴板。";
        }
        Ui.Hint("验证链接：");
        ImGui.TextWrapped(_pendingCaptchaUrl);
        Ui.Hint("在验证页完成验证后，按 F12 → Application（应用）→ Cookies → 找到 cf_clearance → 复制其值，粘贴到下面：");
        ImGui.InputTextWithHint("##cfck", "粘贴 cf_clearance cookie 值（或含 cf_clearance= 的整段 Cookie）", ref _captchaCookieInput, 8192);
        ImGui.SameLine();
        if (ImGui.Button("确定并继续##CfOk"))
        {
            var val = CaptchaCookieStore.ExtractCfClearance(_captchaCookieInput);
            if (val != null && dictDir != null)
            {
                _wiki.SetCaptchaCookie(val, CaptchaTtlMinutes);
                _pendingCaptchaUrl = null;
                _captchaCookieInput = "";
                _result += (_result.Length > 0 ? "\n" : "") + "[完成] 已接收 cookie，自动从断点继续提取…";
                StartExtract(dictDir);
            }
            else
            {
                _result += (_result.Length > 0 ? "\n" : "") + "[错误] 未能识别 cookie 值，请粘贴 cf_clearance 的值。";
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("放弃验证##CfCancel"))
        {
            _pendingCaptchaUrl = null;
            _captchaCookieInput = "";
            _result += (_result.Length > 0 ? "\n" : "") + "[提示] 已放弃验证。";
        }
        ImGui.Spacing();
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            /* 打开失败可忽略：仍可手动复制链接 */
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
