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
        Ui.Hint($"文件 {files.Count} 个 / 选项组 {totalGroups} 个 / 选项 {totalOptions} 项　（绿色=已中文，橙色=未翻译；描述悬停可看）");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        using (var list = ImRaii.Child("##FileListScroll", new Vector2(0, -1), false))
        {
            if (list.Success)
            {
                foreach (var f in files)
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
                        ImGui.TextDisabled($"组 {f.Groups.Count} / 选项 {CountOptions(f)}");
                        ImGui.Indent();
                        foreach (var g in f.Groups)
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
                        ImGui.TextDisabled($"组 {f.Groups.Count} / 选项 {CountOptions(f)}");
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
