using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace FFXIVPenumbraHanhua.Windows;

/// <summary> 开发功能窗口：实验性开关与调试按钮（新用户无需关注）。 </summary>
public class DevWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private string _result = "";

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
            ImGui.SetTooltip("还原备份后，立即自动重跑未翻译模组的完整汉化流程。\n相当于「还原即重置，自动补回译文」。默认关。");

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

        // 手动兜底：校验并清除失效标记（启动 / 各流程入口已自动跑，这里给开发测试一个即时入口）
        Ui.SameLineIfFits(Ui.ButtonWidth("校验失效标记"));
        if (ImGui.Button("校验失效标记"))
        {
            var healed = _plugin.Mark.PruneStaleMarks(_plugin.Penumbra.Mods, _plugin.Dict, _plugin.ModFiles);
            _result = healed.Count > 0
                ? $"已清除 {healed.Count} 个失效标记：{string.Join("、", healed.Take(8))}{(healed.Count > 8 ? " 等" : "")}"
                : "未发现失效标记（所有标记与内容一致）";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("扫描全部模组：有「已翻译」标记但内容已还原成英文（词典有译文却仍是英文）的，清除其标记、回到未翻译列表。");

        ImGui.Spacing();
        Plugin.ResultBox("##DevResult", _result, "校验结果将显示在这里");
    }
}
