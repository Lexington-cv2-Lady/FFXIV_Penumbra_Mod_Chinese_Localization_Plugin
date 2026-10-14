namespace FFXIVPenumbraHanhua;

/// <summary>
/// ImGui 控件 ID 集中生成处。
/// <para>
/// 为什么要有这个类：ImGui 拿**标签文本**当 ID 源，同一窗口内两个标签相同的控件会被当成同一个——
/// 点击全部错位到先绘制的那个。2026-10-14 实录：「目录和词典管理」的「各来源词条统计」有 5 行，
/// 每行一个光秃秃的 <c>"打开"</c>，结果只有第一行（我的翻译.json）点得动，其余四个毫无反应。
/// </para>
/// 规矩：**同窗口会出现多次的按钮，标签必须带 `##唯一后缀`**（`##` 之后的部分不显示、只参与 ID 计算）。
/// 同文件的 <c>DrawDirectoryRow</c> 一直带着 `##{idPrefix}Open`，只是统计行漏了，故把规则收口到此处统一生成。
/// </para>
/// 放在不依赖 Dalamud 的静态类里，是为了让单测能直接验证 ID 唯一性——
/// 挂在 <c>Window</c> 子类上会迫使测试工程引用 Dalamud（CS0012）。
/// </summary>
internal static class UiIds
{
    /// <summary> 「各来源词条统计」每行的「打开」按钮 ID。入参为该行的显示名（五条互不相同）。 </summary>
    internal static string StatOpen(string name)
        => "打开##stat_" + (string.IsNullOrWhiteSpace(name) ? "unknown" : name.Trim());
}
