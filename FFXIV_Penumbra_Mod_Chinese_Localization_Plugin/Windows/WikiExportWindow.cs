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

    public void Dispose() => _cts?.Cancel();

    public override void Draw()
    {
        var cfg = _plugin.Configuration;
        var dictDir = cfg.DictionaryPath;
        var dictOk = !string.IsNullOrWhiteSpace(dictDir) && System.IO.Directory.Exists(dictDir);

        ImGui.TextWrapped("从灰机 wiki（cdn.huijiwiki.com/ff14）抓取 FFXIV 官方中/英名，写入 词典目录\\wiki_术语对照\\。");
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
                    var prefixes = new List<string>();
                    if (_perCat)
                    {
                        for (var i = 0; i < _catChecked.Length; i++)
                            if (_catChecked[i]) prefixes.Add(WikiExportService.Categories[i].Prefix);
                        if (prefixes.Count == 0)
                        {
                            _result = "请至少勾选一个分类";
                            goto skipStart;
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
            skipStart:;
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("抓取全部页面可能需要几分钟；增量更新：已存在的词条不会被覆盖；黑名单与内置噪音词不入库");
        }

        ImGui.Spacing();

        // 日志结果区：带边框统一风格；高度自适应（矮窗口收缩，最高 200）
        var boxH = Math.Min(200f, Math.Max(100f, ImGui.GetContentRegionAvail().Y));
        Plugin.ResultBox("##WikiResult", _result, "提取日志将显示在这里（进度 / 新增 / 命中已有 / 拒绝数…）", boxH);

        // 轮询任务完成
        if (_task != null && _task.IsCompleted)
        {
            try
            {
                var n = _task.Result;
                if (n != 0) _plugin.ReloadDictionary(); // 取消(-2)时也可能已写入部分词条，需重载
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
}
