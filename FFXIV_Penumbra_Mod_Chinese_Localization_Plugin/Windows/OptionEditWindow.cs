using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace FFXIVPenumbraHanhua.Windows;

/// <summary>
/// 选项编辑独立二级窗口：承载原主详情区的组名/选项中英文编辑表单。
/// 编辑缓冲（_editBufs/_editFileKey/_showAllOptions）在此窗口内部维护；
/// 保存调 MainWindow.SaveEdits(file, mod, bufs)，放弃清缓冲并 ReloadSelectedFile。
/// </summary>
public class OptionEditWindow : Window, IDisposable
{
    private readonly Plugin _plugin;

    // 编辑缓冲（随文件切换重置）
    private readonly Dictionary<string, string> _editBufs = new();
    private string _editFileKey = "";
    private bool _showAllOptions;

    public OptionEditWindow(Plugin plugin) : base("选项编辑###HanhuaOptionEdit")
    {
        Size = new Vector2(720, 600);
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
        var file = mw.FocusedFile;

        if (mod == null || file == null)
        {
            Ui.Hint("请先在主窗口左侧选择一个模组与文件。");
            ImGui.Spacing();
            if (ImGui.Button("关闭")) IsOpen = false;
            return;
        }

        // 切换文件时重置编辑缓冲
        if (_editFileKey != file.Path)
        {
            _editFileKey = file.Path;
            _editBufs.Clear();
            _showAllOptions = false;
        }

        // 原文对照：优先英文快照，无快照回退当前值
        var snapInfo = mw.GetEnglishCached(mod.Directory, file.FileName);
        string OriginalOf(int gIndex, int? oIndex, string current)
        {
            var g = snapInfo?.Groups.FirstOrDefault(x => x.Index == gIndex);
            if (g == null) return current;
            var en = oIndex == null
                ? g.Name
                : g.Options.FirstOrDefault(x => x.Index == oIndex)?.Name ?? current;
            return string.IsNullOrWhiteSpace(en) ? current : en;
        }

        var total = 0;
        foreach (var g in file.Groups) total += g.Options.Count;

        ImGui.TextWrapped($"{mod.Name}　—　{file.FileName}");
        ImGui.TextDisabled($"（{file.Groups.Count} 组 / {total} 项，直接改中英文，点保存写回）");
        ImGui.Separator();
        ImGui.Spacing();

        var rowW = ImGui.GetContentRegionAvail().X;
        var inputW = Math.Max(120f, rowW - 230f * ImGuiHelpers.GlobalScale);
        var shown = 0;
        var limit = _showAllOptions ? int.MaxValue : 30;
        var truncated = false;

        using (var scroll = ImRaii.Child("##OptEditScroll", new Vector2(0, -ImGui.GetFrameHeight() * 3.2f), false))
        {
            if (scroll.Success)
            {
                foreach (var g in file.Groups)
                {
                    if (g.Name.Length > 0 || g.Options.Count > 0)
                    {
                        if (shown >= limit) { truncated = true; break; }
                        var gk = $"{file.Path}|G{g.Index}";
                        if (!_editBufs.TryGetValue(gk, out var gv)) _editBufs[gk] = gv = g.Name;
                        Ui.Hint(string.IsNullOrEmpty(g.Description) ? "组名：" : $"组名（{g.Description}）：");
                        ImGui.SetNextItemWidth(inputW);
                        if (ImGui.InputText($"##g{g.Index}", ref gv, 1024)) _editBufs[gk] = gv;
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("组名（可直接改中英文）\n原文：" + OriginalOf(g.Index, null, g.Name));
                        shown++;
                    }

                    foreach (var o in g.Options)
                    {
                        if (shown >= limit) { truncated = true; break; }
                        var k = $"{file.Path}|{g.Index}|{o.Index}";
                        var origEn = OriginalOf(g.Index, o.Index, o.Name);
                        if (!_editBufs.TryGetValue(k, out var v)) _editBufs[k] = v = o.Name;
                        ImGui.SetNextItemWidth(inputW);
                        if (ImGui.InputText($"##e{shown}", ref v, 1024)) _editBufs[k] = v;
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("输入框内可直接改中英文\n原文：" + origEn);
                        ImGui.SameLine();
                        var orig = origEn.Length > 22 ? origEn.Substring(0, 22) + "…" : origEn;
                        if (orig.Length > 0) ImGui.TextDisabled(orig);
                        shown++;
                    }
                    if (truncated) break;
                }
            }
        }

        if (truncated)
        {
            if (ImGui.Button("显示全部选项")) _showAllOptions = true;
        }
        else if (total > 0)
        {
            ImGui.TextDisabled($"共 {total} 项，已全部显示");
        }

        ImGui.Spacing();
        if (ImGui.Button("保存修改", new Vector2(120f * ImGuiHelpers.GlobalScale, 0)))
        {
            mw.SaveEdits(file, mod, _editBufs);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("先自动备份原文件，再把输入框内容写回模组，随后触发游戏重载");
        ImGui.SameLine();
        if (ImGui.Button("放弃修改", new Vector2(120f * ImGuiHelpers.GlobalScale, 0)))
        {
            _editBufs.Clear();
            mw.ReloadSelectedFile();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("丢弃输入框未保存的修改，重新从文件读取");
        ImGui.SameLine();
        if (ImGui.Button("关闭", new Vector2(80f * ImGuiHelpers.GlobalScale, 0))) IsOpen = false;

        ImGui.Spacing();
        Plugin.ResultBox("##OptEditResult", mw.ResultText, "操作结果将显示在这里…");
    }
}
