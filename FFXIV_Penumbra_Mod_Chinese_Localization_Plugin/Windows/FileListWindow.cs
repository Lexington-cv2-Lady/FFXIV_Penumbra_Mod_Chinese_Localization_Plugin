using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVPenumbraHanhua.Services;

namespace FFXIVPenumbraHanhua.Windows;

/// <summary> 文件总览：独立窗口列出当前模组全部 meta/group 文件与选项树（只读），
/// 每个文件可一键跳回主窗口编辑——解决 group 文件超多时详情区列表占满窗口的问题。 </summary>
public class FileListWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private string _search = ""; // 搜索：匹配文件名/组名/选项名/描述，未命中的文件与选项隐藏

    public FileListWindow(Plugin plugin) : base("文件总览###HanhuaFileList")
    {
        Size = new Vector2(720, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        _plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var mw = _plugin.MainWindow;
        var mod = mw.FocusedMod;
        if (mod == null)
        {
            Ui.Hint("主窗口未选中模组：先在主窗口点选一个模组，再打开本窗口。");
            return;
        }
        var files = mw.FilesForFocusedMod();
        if (files.Count == 0)
        {
            Ui.Hint("该模组没有可汉化文件（meta.json / group_*.json）。");
            return;
        }

        var totalGroups = files.Sum(f => f.Groups.Count);
        var totalOptions = files.Sum(f => f.Groups.Sum(g => g.Options.Count));
        ImGui.TextWrapped($"模组：{mod.Name}");

        // 搜索框：匹配 文件名 / 组名 / 选项名 / 描述，只显示命中的文件与选项
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##FileSearch", "搜索文件 / 组 / 选项 / 描述…", ref _search, 256);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("不区分大小写；文件名命中显示整文件，组/选项/描述命中只显示命中部分");
        }

        var q = _search.Trim();
        var display = new List<(ModFileInfo File, List<ModGroup> Groups, bool WholeFile)>();
        foreach (var f in files)
        {
            if (q.Length == 0 || f.FileName.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                display.Add((f, f.Groups, true));
                continue;
            }
            var groups = new List<ModGroup>();
            foreach (var g in f.Groups)
            {
                if (g.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(g.Description) && g.Description.Contains(q, StringComparison.OrdinalIgnoreCase)))
                {
                    groups.Add(g);
                    continue;
                }
                var opts = g.Options.Where(o =>
                        o.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrWhiteSpace(o.Description) && o.Description.Contains(q, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (opts.Count == 0) continue;
                var part = new ModGroup { Index = g.Index, Name = g.Name, Description = g.Description };
                part.Options.AddRange(opts);
                groups.Add(part);
            }
            if (groups.Count > 0) display.Add((f, groups, false));
        }

        if (q.Length == 0)
        {
            Ui.Hint($"文件 {files.Count} 个 / 选项组 {totalGroups} 个 / 选项 {totalOptions} 项　（绿色=已中文，橙色=未翻译；描述悬停可看）");
        }
        else
        {
            Ui.Hint($"搜索「{q}」：命中 {display.Count} / {files.Count} 个文件　（绿色=已中文，橙色=未翻译；描述悬停可看）");
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (display.Count == 0)
        {
            Ui.Hint("没有匹配的文件/组/选项。");
            return;
        }

        using (var list = ImRaii.Child("##FileListScroll", new Vector2(0, -1), false))
        {
            if (list.Success)
            {
                foreach (var (f, groups, whole) in display)
                {
                    if (ImGui.Button($"编辑##edit{f.Path}", new Vector2(44f * ImGuiHelpers.GlobalScale, 0)))
                    {
                        mw.FocusFile(f.Path);
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("在主窗口选中该文件并打开编辑");
                    }
                    ImGui.SameLine();
                    if (ImGui.TreeNode($"{(f.IsMeta ? "[新] " : "")}{f.FileName}##tree{f.Path}"))
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled(whole
                            ? $"组 {groups.Count} / 选项 {groups.Sum(g => g.Options.Count)}"
                            : $"命中 {groups.Count} 组 / {groups.Sum(g => g.Options.Count)} 项");
                        ImGui.Indent();
                        foreach (var g in groups)
                        {
                            ImGui.TextColored(new Vector4(0.75f, 0.85f, 1f, 1f),
                                string.IsNullOrEmpty(g.Description) ? $"组：{g.Name}" : $"组：{g.Name}　（{g.Description}）");
                            ImGui.Indent();
                            foreach (var o in g.Options)
                            {
                                var done = _plugin.Dict.ContainsChinese(o.Name);
                                ImGui.TextColored(done ? new Vector4(0.55f, 0.9f, 0.55f, 1f) : new Vector4(1f, 0.75f, 0.4f, 1f),
                                    o.Name.Length > 0 ? o.Name : "（空选项名）");
                                if (!string.IsNullOrWhiteSpace(o.Description) && ImGui.IsItemHovered())
                                {
                                    ImGui.SetTooltip(o.Description);
                                }
                            }
                            ImGui.Unindent();
                        }
                        ImGui.Unindent();
                        ImGui.TreePop();
                    }
                    else
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled(whole
                            ? $"组 {groups.Count} / 选项 {groups.Sum(g => g.Options.Count)}"
                            : $"命中 {groups.Count} 组 / {groups.Sum(g => g.Options.Count)} 项");
                    }
                }
            }
        }
    }

    private static int CountOptions(ModFileInfo f)
    {
        var n = 0;
        foreach (var g in f.Groups) n += g.Options.Count;
        return n;
    }
}
