using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace FFXIVPenumbraHanhua.Windows;

/// <summary> 目录和词典管理窗口：词典/翻译目录设置、备份份数，以及词典加载状态与各来源词条统计。 </summary>
public class DictionaryWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly FileDialogManager _fileDialog = new();
    private string _openMsg = "";
    private string _saveMsg = "";

    public DictionaryWindow(Plugin plugin)
        : base("目录和词典管理###HanhuaDict")
    {
        Size = new Vector2(680, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        this.plugin = plugin;
    }

    public void Dispose()
    {
        // 本版 Dalamud 的 FileDialogManager 未实现 IDisposable/Dispose（上游新版才有），暂无可释放接口；
        // 插件卸载时程序集整体卸载，此处保底：后续上游提供清理接口时在此调用。
    }

    public override void Draw()
    {
        var configuration = plugin.Configuration;
        var dict = plugin.Dict;

        // ═══ 目录设置 ═══
        ImGui.TextUnformatted("目录设置");
        ImGui.Spacing();

        DrawDirectoryRow("词典目录（我的翻译 / 个性翻译 / wiki 术语 / AI知识库）：", "Dict", "选择词典目录",
            "弹出文件夹选择框，选择词典目录",
            () => configuration.DictionaryPath, v => configuration.DictionaryPath = v);

        ImGui.Spacing();

        DrawDirectoryRow("翻译目录（提取英文 / AI 翻译的输入输出目录）：", "Trans", "选择翻译目录",
            "弹出文件夹选择框，选择翻译目录",
            () => configuration.TranslationPath, v => configuration.TranslationPath = v);

        ImGui.Spacing();
        Ui.Hint($"当前状态：{(Directory.Exists(configuration.TranslationPath) ? "翻译目录存在" : "翻译目录不存在（保存后创建生效）")}");

        ImGui.Spacing();

        // 备份保留份数
        ImGui.TextUnformatted("备份保留份数（写回前自动备份，轮转保留）：");
        var backupCount = configuration.BackupCount;
        var intW = 160f * ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(intW);
        if (ImGui.InputInt("##BackupCount", ref backupCount, 1, 5))
        {
            configuration.BackupCount = Math.Clamp(backupCount, 1, 20);
            plugin.ModFiles.MaxBackups = configuration.BackupCount;
        }
        ImGui.SameLine();
        ImGui.TextDisabled("（1 - 20）");

        ImGui.Spacing();

        // 保存 / 状态提示
        if (ImGui.Button("保存设置", new Vector2(100 * ImGuiHelpers.GlobalScale, 0)))
        {
            configuration.Save();
            plugin.MigrateDirectories();
            plugin.Snapshot.EnsureRoot();
            plugin.ReloadDictionary(); // 目录可能已变更：立即重载词典，避免旧内存词典不生效
            plugin.AppLog.Info($"[配置] 已保存设置：词典 {configuration.DictionaryPath} / 翻译 {configuration.TranslationPath} / 备份份数 {configuration.BackupCount}");
            _saveMsg = "设置已保存，词典已重载";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("保存目录与备份份数；词典/翻译目录变更后自动迁移相关资产并重载词典");
        }
        if (_saveMsg.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.55f, 0.9f, 0.55f, 1f), "" + _saveMsg);
        }
        if (_openMsg.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.2f, 1f), _openMsg);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ═══ 词典加载状态 ═══
        ImGui.TextUnformatted("词典加载状态");
        ImGui.Spacing();
        ImGui.TextWrapped(dict.Status);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ═══ 各来源词条统计 ═══
        ImGui.TextUnformatted("各来源词条统计");
        ImGui.Spacing();
        var statRoot = configuration.DictionaryPath;
        DrawStatRow("我的翻译.json（mods + terms）", Path.Combine(statRoot, "我的翻译.json"), dict.MyCount);
        DrawStatRow("个性翻译.json（词级覆盖层）", Path.Combine(statRoot, "个性翻译.json"), dict.CustomCount);
        DrawStatRow("wiki_术语对照\\ 分类文件夹", Path.Combine(statRoot, "wiki_术语对照"), dict.WikiCount);
        DrawStatRow("AI知识库\\AI知识库.json（只读底料）", Path.Combine(statRoot, "AI知识库", "AI知识库.json"), dict.AiCount);
        DrawStatRow("单词黑名单.json（保留英文专名）", Path.Combine(statRoot, "单词黑名单.json"), dict.BlacklistCount);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 词典结构说明
        Ui.Hint("词典目录结构：\n  · 我的翻译.json —— 主词典（mods 双层 + terms 数组），优先命中\n  · 个性翻译.json —— 词级覆盖层，覆盖 我的翻译\n  · 单词黑名单.json —— 命中词保留英文，不翻译\n  · wiki_术语对照\\ —— 分类术语只读兜底（不覆盖用户词条）\n      · wiki_术语对照_黑名单.json —— 剔除污染词\n  · AI知识库\\AI知识库.json —— 只读底料，优先级最低");

        // 文件选择对话框（浏览文件夹用）
        _fileDialog.Draw();
    }

    /// <summary> 一行目录配置：标签独占一行，下一行 输入框 + 打开/浏览/粘贴 三按钮，保证各窗口排版一致。 </summary>
    private void DrawDirectoryRow(string label, string idPrefix, string dialogTitle, string browseHint,
        Func<string> get, Action<string> set)
    {
        ImGui.TextWrapped(label);
        var path = get();
        var btnW = 56f * ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - btnW * 3 - 24f * ImGuiHelpers.GlobalScale));
        if (ImGui.InputText($"##{idPrefix}Path", ref path, 512)) set(path);
        ImGui.SameLine();
        if (ImGui.Button($"打开##{idPrefix}Open", new Vector2(btnW, 0))) OpenFolder(get());
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("用资源管理器打开该目录");
        ImGui.SameLine();
        if (ImGui.Button($"浏览##{idPrefix}Browse", new Vector2(btnW, 0)))
        {
            _fileDialog.OpenFolderDialog(dialogTitle, (ok, p) =>
            {
                if (ok && !string.IsNullOrWhiteSpace(p)) set(p.Trim());
            });
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(browseHint);
        ImGui.SameLine();
        if (ImGui.Button($"粘贴##{idPrefix}Paste", new Vector2(btnW, 0)))
        {
            var clip = ImGui.GetClipboardText();
            if (!string.IsNullOrWhiteSpace(clip)) set(clip.Trim());
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("读取剪贴板中的路径填入（无需 Ctrl+V）");
    }

    private void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (Directory.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _openMsg = "打开目录失败：" + ex.Message;
            }
        }
        else
        {
            _openMsg = "目录不存在，无法打开：" + path;
        }
    }

    private void DrawStatRow(string name, string path, int count)
    {
        var w = ImGui.GetContentRegionAvail().X;
        var btnW = 56f * ImGuiHelpers.GlobalScale;
        var countW = 80f * ImGuiHelpers.GlobalScale;
        var isDir = Directory.Exists(path);
        var exists = isDir || File.Exists(path);

        // 最左：标准「打开」按钮（与其他按钮风格一致）
        if (exists)
        {
            if (ImGui.Button("打开", new Vector2(btnW, 0)))
            {
                OpenPath(path);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip((isDir ? "打开文件夹" : "用系统默认程序打开") + "：" + path);
            }
        }
        else
        {
            ImGui.InvisibleButton("##openMissing", new Vector2(btnW, 0)); // 占位保持对齐
        }

        // 文件名（超长截断省略号，悬停显示完整路径）
        ImGui.SameLine();
        var nameW = Math.Max(40f, w - btnW - countW - 20f);
        var text = name;
        if (exists && ImGui.CalcTextSize(text).X > nameW)
        {
            while (text.Length > 2 && ImGui.CalcTextSize(text + "…").X > nameW)
            {
                text = text[..^1];
            }
            text += "…";
        }
        ImGui.TextUnformatted(text);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip((exists ? (isDir ? "文件夹：" : "文件：") : "文件不存在：") + path);
        }

        // 最右：条数
        ImGui.SameLine(w - countW);
        ImGui.TextColored(new Vector4(0.8f, 0.9f, 1f, 1f), count.ToString("N0") + " 条");
    }

    /// <summary> 用系统默认程序打开文件 / 资源管理器打开文件夹（失败给出提示，与 OpenFolder 行为一致）。 </summary>
    private void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            _openMsg = "打开失败：" + ex.Message;
        }
    }
}
