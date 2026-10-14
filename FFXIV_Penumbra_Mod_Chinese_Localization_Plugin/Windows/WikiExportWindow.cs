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
    private bool _followLog = true;       // 提取日志「跟随最新」；取消勾选后可自由翻阅历史，滚动条不再被拽回

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
        Ui.Hint("注意：该功能可能因灰机 wiki 的风控而暂时失效（CDN 拦截脚本客户端）。");
        Ui.Hint("请求已内置节流（约 0.7 秒/次）；被风控拦截时请间隔数十分钟或换网络后再试，插件会自动从上次断点续抓。");
        ImGui.Spacing();

        // 2026-10-14 实测结论（已固化，勿再改回去）：
        // Cloudflare 按 TLS 指纹识别客户端——**真实浏览器引擎直连灰机 API 是通的**（游戏外独立进程抓取器，
        // 0.2 秒拿到词条 JSON），被拦的只有 curl/HttpClient 这类脚本客户端。
        // 因此本插件不再在游戏内嵌 WebView2（实测脚本压根不执行，还会弹白屏验证窗），
        // 取数改走「游戏外独立进程抓取器」；真浏览器里可以直接打开下面的端点自查是否畅通。
        if (ImGui.Button("在浏览器中打开抓取端点##CfOpenBrowser"))
        {
            // 必须开「带全部参数的真实抓取端点」：裸 api.php 只会返回 MediaWiki 的 API 帮助页，
            // 根本看不出 CF 拦没拦（2026-10-14 实测「打开的是正常网站，又绕回来了」的真因）。
            OpenUrl(WikiExportService.ApiEndpoint);
            _result += (_result.Length > 0 ? "\n" : "")
                       + "[提示] 已用默认浏览器打开插件实际抓取的端点：返回词条 JSON 即畅通；"
                       + "若出现「请稍候…」说明当前出口 IP 被 CDN 拦了，请间隔数十分钟或换网络后再试。";
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

        // 日志结果区（取消「跟随最新」即可自由翻阅，滚动条不再被拽到底部）
        ImGui.Checkbox("跟随最新", ref _followLog);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("勾选时只在已停在底部才自动滚动；取消勾选后完全不自动滚动，可自由翻看历史");
        var boxH = Math.Min(200f, Math.Max(100f, ImGui.GetContentRegionAvail().Y));
        Plugin.ResultBox("##WikiResult", _result, "提取日志将显示在这里（进度 / 新增 / 命中已有 / 拒绝数…）", boxH, _followLog);

        // 轮询任务完成
        if (_task != null && _task.IsCompleted)
        {
            try
            {
                var n = _task.Result;
                if (n != 0) _plugin.ReloadDictionary();
                if (n >= 0 && _result.Length == 0) _result = _wiki.LastResult;
            }
            finally
            {
                _task = null;
                _cts?.Dispose();
                _cts = null;
            }
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

    /// <summary> 按当前勾选启动提取。 </summary>
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
