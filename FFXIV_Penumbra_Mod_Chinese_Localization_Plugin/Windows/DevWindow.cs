using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace FFXIVPenumbraHanhua.Windows;

/// <summary> 开发功能窗口：实验性开关与调试按钮（新用户无需关注）。 </summary>
public class DevWindow : Window, IDisposable
{
    private readonly Plugin _plugin;

    public DevWindow(Plugin plugin) : base("开发功能###HanhuaDev")
    {
        _plugin = plugin;
        Size = new Vector2(520, 260);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 200),
            MaximumSize = new Vector2(900, 700)
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = _plugin.Configuration;

        // 开关：恢复备份后自动重跑
        var onRestore = cfg.AutoHanhuaAfterRestore;
        if (ImGui.Checkbox("恢复备份后自动重跑汉化", ref onRestore) && onRestore != cfg.AutoHanhuaAfterRestore)
        {
            cfg.AutoHanhuaAfterRestore = onRestore;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("还原备份后，立即自动重跑未翻译模组的完整汉化流程。\n相当于「还原=重置，自动补回译文」。默认关。");

        ImGui.Separator();
        ImGui.TextDisabled("调试按钮（正常使用自动完成）");

        if (ImGui.Button("刷新模组列表"))
            _plugin.Penumbra.Refresh();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("强制从 Penumbra 重新拉取模组列表。");

        Ui.SameLineIfFits(Ui.ButtonWidth("重载词典"));
        if (ImGui.Button("重载词典"))
            _plugin.ReloadDictionary();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("重新从词典目录加载我的翻译/个性翻译/wiki/AI知识库。");
    }
}
