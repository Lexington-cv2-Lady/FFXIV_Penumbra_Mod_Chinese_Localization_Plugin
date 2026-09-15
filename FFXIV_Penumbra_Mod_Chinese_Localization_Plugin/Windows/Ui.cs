using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FFXIVPenumbraHanhua.Windows;

/// <summary> 窗口缩放适配助手：提示文字自动换行、按钮行放不下自动换行，保证缩小窗口不裁字。 </summary>
internal static class Ui
{
    /// <summary> 灰色提示文字（取主题 TextDisabled 色），自动换行。 </summary>
    public static void Hint(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary> 带色文字，自动换行。 </summary>
    public static void ColoredWrapped(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary> 流式同行：剩余宽度够才 SameLine，否则自动落到下一行（防按钮被窗口边缘裁掉）。 </summary>
    public static void SameLineIfFits(float nextWidth)
    {
        if (ImGui.GetContentRegionAvail().X >= nextWidth + ImGui.GetStyle().ItemSpacing.X)
            ImGui.SameLine();
    }

    /// <summary> 高亮按钮配色（橙金色，用于 AI 一键汉化等功能区分）。配对 PopAccent。 </summary>
    public static void PushAccent()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.85f, 0.52f, 0.08f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.98f, 0.62f, 0.12f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.72f, 0.42f, 0.06f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
    }

    /// <summary> 弹出高亮配色。 </summary>
    public static void PopAccent() => ImGui.PopStyleColor(4);

    /// <summary> 危险操作按钮配色（红色，用于覆盖文件的二次确认等）。配对 PopDanger。 </summary>
    public static void PushDanger()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.72f, 0.16f, 0.16f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.88f, 0.24f, 0.24f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.58f, 0.12f, 0.12f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
    }

    /// <summary> 弹出危险操作配色。 </summary>
    public static void PopDanger() => ImGui.PopStyleColor(4);

    /// <summary> 估算文字按钮宽度（含左右内边距），供 SameLineIfFits 使用。 </summary>
    public static float ButtonWidth(string label)
        => ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;
}
