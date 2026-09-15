using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using FFXIVPenumbraHanhua.Services;

namespace FFXIVPenumbraHanhua.Windows;

/// <summary> 汉化流程窗口：① 提取英文 → ② 预翻译 → ③ AI 翻译 → ④ 汇总已翻译内容 → ⑤ 翻译写入MOD。 </summary>
public class TranslatePipelineWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly ExtractService _extract;
    private readonly AiTranslateService _ai;
    private readonly ImportService _import;
    private readonly SumupService _sumup;
    private readonly DictionaryService _dict;
    private readonly AppLog _log;

    private bool _skipMarked = true;
    private string _result = "";
    private Task<int>? _task;
    private CancellationTokenSource? _cts;
    private string _taskStatus = "";
    private bool _stopRequested; // 已请求停止（避免重复点击反复改写状态文字）

    public TranslatePipelineWindow(Plugin plugin) : base("汉化流程###HanhuaPipeline")
    {
        Size = new Vector2(640, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        _plugin = plugin;
        _extract = plugin.Extract;
        _ai = plugin.AiTranslate;
        _import = plugin.Import;
        _sumup = plugin.Sumup;
        _dict = plugin.Dict;
        _log = plugin.AppLog;
    }

    public void Dispose() => _cts?.Cancel(); // 插件卸载时中断进行中的 AI 翻译

    public override void Draw()
    {
        var cfg = _plugin.Configuration;
        var transDir = cfg.TranslationPath;
        var modRoot = _plugin.Penumbra.GetModRoot();
        var notFound = string.IsNullOrEmpty(modRoot) || !Directory.Exists(modRoot);

        ImGui.TextWrapped("流程：① 提取英文 → ② 预翻译（词典预填）→ ③ AI 翻译 → ④ 汇总已翻译内容（编入词典）→ ⑤ 翻译写入MOD。");
        ImGui.Spacing();
        Ui.Hint($"翻译目录：{transDir}（{(Directory.Exists(transDir) ? "存在" : "不存在，提取时会自动创建")}）");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── ① 提取英文 ──
        ImGui.TextWrapped("① 提取英文（只处理主窗口勾选的模组，未勾选可用下方「全选」一键勾选）");
        ImGui.Spacing();
        ImGui.Checkbox("跳过已标记「已翻译」的模组", ref _skipMarked);
        ImGui.Spacing();
        if (ImGui.Button("提取英文"))
        {
            if (notFound)
            {
                _result = "无法获取 Penumbra 模组根目录";
            }
            else
            {
                var mods = _plugin.MainWindow.SelectedMods;
                var n = _extract.ExtractPerMod(mods, _skipMarked, transDir, modRoot ?? "");
                _result = _extract.LastResult;
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"每个模组单独生成 翻译目录\\<模组名>_未翻译.json\n" +
                             $"键格式：模组目录/文件||字段||原文（含翻译规则段）\n" +
                             $"文件名用简洁模组名，键内仍带完整目录，⑤ 写回不受影响");
        }
        Ui.SameLineIfFits(Ui.ButtonWidth("汇总提取"));
        if (ImGui.Button("汇总提取"))
        {
            if (notFound)
            {
                _result = "无法获取 Penumbra 模组根目录";
            }
            else
            {
                var mods = _plugin.MainWindow.SelectedMods;
                var n = _extract.Extract(mods, _skipMarked, transDir, modRoot ?? "");
                _result = _extract.LastResult;
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"所有模组合并生成 {Path.Combine(transDir, "全部模组_未翻译.json")}\n适合整批交给外部 AI 翻译");
        }
        Ui.SameLineIfFits(Ui.ButtonWidth("全选") + ImGui.GetFrameHeight());
        // 全选开关（与主窗口列表头、⑤区一致）：首点=全选当前列表，再点=全部取消
        // ##step1：同一窗口内与⑤区「全选」区分 ImGui ID（标签相同会 ID 撞车，点击全被先声明者截走）
        var allSelTop = _plugin.MainWindow.AllVisibleSelected;
        if (ImGui.Checkbox("全选##step1", ref allSelTop))
        {
            _plugin.MainWindow.SetAllVisibleSelection(allSelTop);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("勾选=全选主窗口列表（按当前「已翻译」筛选：默认即全部未翻译模组）\n再点=全部取消勾选");
        }
        Ui.SameLineIfFits(ImGui.CalcTextSize($"已选 {_plugin.MainWindow.SelectedMods.Count} 个").X);
        Ui.Hint($"已选 {_plugin.MainWindow.SelectedMods.Count} 个");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── ② 预翻译 ──
        ImGui.TextWrapped("② 预翻译（词典预填：我的翻译/个性翻译/wiki/AI知识库 能翻的自动填上）");
        ImGui.Spacing();
        if (ImGui.Button("预翻译"))
        {
            var files = ListUntranslatedFiles(transDir);
            if (files.Count == 0)
            {
                _result = "未找到 _未翻译.json，请先执行 ① 提取英文";
            }
            else
            {
                var hit = 0;
                var done = 0;
                foreach (var path in files)
                {
                    var n = Prefill(path, modRoot ?? "");
                    if (n >= 0)
                    {
                        hit += n;
                        done++;
                    }
                }
                _result = done == 0 ? "预翻译失败：文件解析错误" : $"预翻译完成：{done} 个文件，命中 {hit} 项（仍为英文的交给 ③ AI 翻译）";
            }
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── ③ AI 翻译 ──
        ImGui.TextWrapped("③ AI 翻译（把仍未翻译的项交给所选供应商）");
        ImGui.Spacing();
        var providerName = AiTranslateService.CurrentProviderName(cfg);
        Ui.Hint($"供应商：{providerName}（{AiTranslateService.ResolveEndpoint(cfg).Model}）");
        if (string.IsNullOrWhiteSpace(AiTranslateService.GetApiKey(cfg)))
        {
            Ui.SameLineIfFits(ImGui.CalcTextSize("　⚠ 未填写 API Key").X);
            Ui.Hint("　⚠ 未填写 API Key");
        }
        ImGui.Spacing();

        if (_task != null && !_task.IsCompleted)
        {
            ImGui.TextWrapped(_taskStatus);
            ImGui.Spacing();
            Ui.PushDanger();
            if (ImGui.Button("停止翻译", new Vector2(120f * ImGuiHelpers.GlobalScale, 0)) && !_stopRequested)
            {
                _stopRequested = true;
                _cts?.Cancel();
                _taskStatus = "正在停止…（已中断当前请求；已翻完的部分会保留并写盘）";
                _log.Info("AI 翻译：已请求停止，正在中断当前请求");
            }
            Ui.PopDanger();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("停止：不再发送新批次，正在请求中的那批也会被立即中断。\n" +
                                 "⚠ 已翻完的批次会保留并写盘（那部分额度已消耗，不浪费）。");
            }
            Ui.Hint("翻译进行中…（可切到其他窗口，完成后回来查看）");
        }
        else
        {
            if (ImGui.Button("AI 翻译"))
            {
                // 未配 Key 时直接拦下并指路（避免白跑一趟才失败）
                if (string.IsNullOrWhiteSpace(AiTranslateService.GetApiKey(cfg)))
                {
                    _result = "未填写 API Key：请到「AI 设置」配置，或改用「复制翻译提示词」交给外部 AI（零成本）";
                }
                else
                {
                    var files = ListUntranslatedFiles(transDir);
                    if (files.Count == 0)
                    {
                        _result = "未找到 _未翻译.json，请先执行 ① 提取英文";
                    }
                    else
                    {
                        _result = "";
                        _stopRequested = false; // 新一轮任务：复位停止标记
                        _taskStatus = "AI 翻译进行中…";
                        _cts = new CancellationTokenSource();
                        var token = _cts.Token;
                        _task = Task.Run(async () =>
                        {
                            var total = 0;
                            foreach (var input in files)
                            {
                                var output = Path.ChangeExtension(input, null) + "_已翻译.json";
                                _taskStatus = $"AI 翻译中：{Path.GetFileName(input)}…";
                                total += await _ai.TranslateAsync(input, output, cfg, token);
                                if (token.IsCancellationRequested) break;
                            }
                            return total;
                        });
                    }
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"翻译翻译目录下所有 _未翻译.json\n分别写出对应的 _已翻译.json（外部 AI 翻好的文件也按此命名即可被 ④ 汇总）");
            }
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── ④ 汇总已翻译内容 ──
        ImGui.TextWrapped("④ 汇总已翻译内容（把 _已翻译.json 的译文编入 我的翻译.json，已有译文不覆盖）");
        ImGui.Spacing();
        if (ImGui.Button("汇总已翻译内容"))
        {
            var files = Directory.Exists(transDir)
                ? Directory.GetFiles(transDir, "*_已翻译.json", SearchOption.TopDirectoryOnly).ToList()
                : new List<string>();
            if (files.Count == 0)
            {
                _result = "未找到 _已翻译.json，请先执行 ③ AI 翻译（或把外部 AI 翻好的文件命名为 <模组名>_已翻译.json）";
            }
            else
            {
                var total = 0;
                foreach (var f in files)
                {
                    total += _sumup.Sumup(f, cfg.DictionaryPath);
                }
                _result = $"汇总完成：{files.Count} 个文件，新增 {total} 条 → 我的翻译.json";
                if (total > 0) _plugin.ReloadDictionary();
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("沉淀翻译到 我的翻译.json：同一个词下次直接命中，不必重复翻译");
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── ⑤ 翻译写入MOD ──
        ImGui.TextWrapped("⑤ 翻译写入MOD（读取词典译文：我的翻译/个性翻译/wiki/AI知识库，写回勾选的模组文件并重载）");
        ImGui.Spacing();
        if (ImGui.Button("词典翻译写入MOD（覆写旧译法）"))
        {
            if (notFound)
            {
                _result = "无法获取 Penumbra 模组根目录";
            }
            else
            {
                var mods = _plugin.MainWindow.SelectedMods;
                if (mods.Count == 0)
                {
                    _result = "未勾选任何模组，请点右侧「全选」或到主窗口勾选";
                }
                else
                {
                    var n = _import.ApplyDictionary(modRoot ?? "", _dict, mods, overwrite: true);
                    _result = _import.LastResult;
                }
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("直接把词典里的译文应用到模组选项（组名/选项名/描述）并重载，无需 _已翻译.json。\n" +
                             "用外部 AI 翻译时：先执行 ④ 汇总（把译文编入 我的翻译.json）再点这里写回。\n" +
                             "已翻译条目将按英文快照重查词典，可覆写旧译法；写回前自动备份（zip）");
        }
        Ui.SameLineIfFits(Ui.ButtonWidth("全选") + ImGui.GetFrameHeight());
        // 全选：勾选主窗口列表中全部模组（按当前「已翻译」筛选），与写入按钮同行便于直接开工
        var allSel = _plugin.MainWindow.AllVisibleSelected;
        if (ImGui.Checkbox("全选##step5", ref allSel))
        {
            _plugin.MainWindow.SetAllVisibleSelection(allSel);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("勾选主窗口列表中的全部模组（按当前「已翻译」筛选：默认即全部未翻译模组）");
        }
        Ui.SameLineIfFits(ImGui.CalcTextSize($"已选 {_plugin.MainWindow.SelectedMods.Count} 个").X);
        Ui.Hint($"已选 {_plugin.MainWindow.SelectedMods.Count} 个");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 流程结果日志区：带边框统一风格
        Plugin.ResultBox("##PipelineResult", _result, "操作结果将显示在这里（如：提取完成 N 个模组…）");

        // 轮询 AI 任务完成
        if (_task != null && _task.IsCompleted)
        {
            try
            {
                // 任务异常（如翻译目录不可写）时 LastResult 是旧值，须显式提示
                _result = _task.IsFaulted
                    ? "AI 翻译异常：" + (_task.Exception?.GetBaseException().Message ?? "未知错误")
                    : _ai.LastResult;
            }
            finally
            {
                _task = null;
                _taskStatus = "";
                _cts?.Dispose();
                _cts = null;
            }
        }
    }

    /// <summary> 列出翻译目录下所有 _未翻译.json（含按模组文件与 全部模组_未翻译.json）。 </summary>
    private static List<string> ListUntranslatedFiles(string transDir)
        => Directory.Exists(transDir)
            ? Directory.GetFiles(transDir, "*_未翻译.json", SearchOption.TopDirectoryOnly).ToList()
            : new List<string>();

    /// <summary> 词典预填：委托 ExtractService.PrefillFile（与一键汉化共用同一实现）。返回命中数。 </summary>
    private int Prefill(string path, string modRoot)
    {
        var n = _extract.PrefillFile(path);
        if (n < 0) _result = "预翻译失败：文件解析错误";
        return n;
    }
}
