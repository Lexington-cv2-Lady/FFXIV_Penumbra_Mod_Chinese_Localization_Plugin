using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using FFXIVPenumbraHanhua.Services;

namespace FFXIVPenumbraHanhua.Windows;

/// <summary> AI 配置列表（三级窗口）：自定义 / 内置服务商管理 + 清空预设配置。 </summary>
public class AiConfigWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private string _result = "";
    private bool _showCpKey; // 服务商表单内 API Key 明文显示开关

    public AiConfigWindow(Plugin plugin) : base("AI 配置列表###HanhuaAiConfig")
    {
        _plugin = plugin;
        Size = new Vector2(560, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = _plugin.Configuration;
        var pasteW = 64f * ImGuiHelpers.GlobalScale;
        var showW = 52f * ImGuiHelpers.GlobalScale;

        ImGui.TextWrapped("内置服务商为预设（名字/地址/模型不可改，可填 Key）；自定义服务商可完全编辑。Key 均按服务商独立保存，修改即保存。");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var allProviders = AiTranslateService.GetAllProviders(cfg);
        var currentName = AiTranslateService.CurrentProviderName(cfg);
        var manualMode = cfg.AiProviderName == "" && cfg.AiProvider < 0;

        // 按钮行：新增 / 删除 / 清空预设配置
        if (ImGui.Button("新增自定义服务商"))
        {
            var n = 1;
            while (cfg.CustomProviders.Any(cp => cp.Name == $"自定义 {n}")) n++;
            var newName = $"自定义 {n}";
            cfg.CustomProviders.Add(new CustomProvider { Name = newName });
            cfg.AiProviderName = newName;
            cfg.AiProvider = -2;
            cfg.Save();
            _result = $"已新增「{newName}」，请填写 API 地址与默认模型";
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("新增一个自定义 OpenAI 兼容服务商（自动置顶并选中）");
        ImGui.SameLine();
        if (ImGui.Button("删除当前服务商"))
        {
            var cp = cfg.CustomProviders.FirstOrDefault(x => x.Name == currentName);
            if (cp != null)
            {
                cfg.CustomProviders.Remove(cp);
                if (cfg.AiApiKeys != null) cfg.AiApiKeys.Remove(currentName);
                cfg.AiProviderName = "";
                cfg.AiProvider = 0;
                cfg.Save();
                _result = $"已删除自定义服务商「{currentName}」（其 Key 一并删除）";
            }
            else
            {
                _result = "当前选中的不是自定义服务商（内置服务商不可删除）";
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("清空预设配置")) ImGui.OpenPopup("##ClearPresetConfirm");

        var clearConfirm = true;
        if (ImGui.BeginPopupModal("确认清空预设配置？", ref clearConfirm, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("将清除：所有服务商保存的 API Key、API 地址覆盖、模型覆盖（恢复预设默认）。自定义服务商本身不会被删除。");
            ImGui.Spacing();
            if (ImGui.Button("确认清空"))
            {
                if (cfg.AiApiKeys != null) cfg.AiApiKeys.Clear();
                cfg.AiBaseUrl = "";
                cfg.AiModel = "";
                cfg.AiProvider = 0;
                cfg.AiProviderName = "";
                cfg.Save();
                _result = "已清空预设配置（Key / 地址覆盖 / 模型覆盖已恢复默认）";
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("取消")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 服务商列表（自定义置顶 + 内置 12 家；点击选中）
        ImGui.TextUnformatted("服务商列表：");
        var listH = 150f * ImGuiHelpers.GlobalScale;
        if (ImGui.BeginChild("##ProviderList", new Vector2(ImGui.GetContentRegionAvail().X, listH), true))
        {
            foreach (var item in allProviders)
            {
                var isCustom = cfg.CustomProviders != null && cfg.CustomProviders.Any(cp => cp.Name == item.Name);
                var sel = !manualMode && item.Name == currentName;
                if (ImGui.Selectable((isCustom ? "[自定义] " : "") + item.Name, sel))
                {
                    cfg.AiProviderName = item.Name;
                    cfg.AiProvider = -2;
                    cfg.Save(); // 修改即保存
                    _result = $"已选中「{item.Name}」";
                }
                if (sel) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndChild();
        }

        ImGui.Spacing();

        var editing = (cfg.CustomProviders ?? []).FirstOrDefault(x => x.Name == currentName);
        if (editing != null)
        {
            // ── 自定义服务商编辑表单 ──
            var formW = ImGui.GetContentRegionAvail().X;

            ImGui.TextUnformatted("名称：");
            var editName = editing.Name;
            ImGui.SetNextItemWidth(Math.Max(120f, formW - pasteW - 8f * ImGuiHelpers.GlobalScale));
            if (ImGui.InputText("##CpName", ref editName, 128))
            {
                var trimmed = editName.Trim();
                if (trimmed.Length > 0 && (cfg.CustomProviders ?? []).All(x => x == editing || x.Name != trimmed))
                {
                    RenameProvider(cfg, editing, trimmed);
                    cfg.Save(); // 修改即保存
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("粘贴##CpNamePaste", new Vector2(pasteW, 0)))
            {
                var clip = ImGui.GetClipboardText();
                if (!string.IsNullOrWhiteSpace(clip))
                {
                    var trimmed = clip.Trim();
                    if ((cfg.CustomProviders ?? []).All(x => x == editing || x.Name != trimmed))
                    {
                        RenameProvider(cfg, editing, trimmed);
                        cfg.Save();
                        _result = "已从剪贴板粘贴名称";
                    }
                    else _result = "名称与其他服务商重复，未应用";
                }
                else _result = "剪贴板为空，粘贴失败";
            }

        ImGui.TextUnformatted("API 地址（OpenAI 兼容端点）：");
        var editBase = editing.BaseUrl;
        ImGui.SetNextItemWidth(Math.Max(120f, formW - pasteW - 8f * ImGuiHelpers.GlobalScale));
        if (ImGui.InputText("##CpBase", ref editBase, 512))
        {
            editing.BaseUrl = editBase.Trim();
            cfg.Save(); // 修改即保存
        }
        ImGui.SameLine();
        if (ImGui.Button("粘贴##CpBasePaste", new Vector2(pasteW, 0)))
        {
            var clip = ImGui.GetClipboardText();
            if (!string.IsNullOrWhiteSpace(clip))
            {
                editing.BaseUrl = clip.Trim();
                cfg.Save();
                _result = "已从剪贴板粘贴 API 地址";
            }
            else _result = "剪贴板为空，粘贴失败";
        }

        ImGui.TextUnformatted("默认模型：");
        var editModel = editing.DefaultModel;
        ImGui.SetNextItemWidth(Math.Max(120f, formW - pasteW - 8f * ImGuiHelpers.GlobalScale));
        if (ImGui.InputText("##CpModel", ref editModel, 256))
        {
            editing.DefaultModel = editModel.Trim();
            cfg.Save(); // 修改即保存
        }
        ImGui.SameLine();
        if (ImGui.Button("粘贴##CpModelPaste", new Vector2(pasteW, 0)))
        {
            var clip = ImGui.GetClipboardText();
            if (!string.IsNullOrWhiteSpace(clip))
            {
                editing.DefaultModel = clip.Trim();
                cfg.Save();
                _result = "已从剪贴板粘贴模型名";
            }
            else _result = "剪贴板为空，粘贴失败";
        }

        ImGui.TextUnformatted("API Key（仅该服务商）：");
        var cpKey = AiTranslateService.GetApiKey(cfg);
        var cpKeyFlags = _showCpKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        ImGui.SetNextItemWidth(Math.Max(120f, formW - pasteW - showW - 16f * ImGuiHelpers.GlobalScale));
        if (ImGui.InputText("##CpKey", ref cpKey, 512, cpKeyFlags))
        {
            AiTranslateService.SetApiKey(cfg, cpKey);
            cfg.Save(); // 修改即保存
        }
        ImGui.SameLine();
        if (ImGui.Button(_showCpKey ? "隐藏" : "显示", new Vector2(showW, 0)))
        {
            _showCpKey = !_showCpKey;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(_showCpKey ? "隐藏 API Key（恢复密文）" : "显示 API Key 明文（确认是否输错）");
        }
        ImGui.SameLine();
        if (ImGui.Button("粘贴##CpKeyPaste", new Vector2(pasteW, 0)))
        {
            var clip = ImGui.GetClipboardText();
            if (!string.IsNullOrWhiteSpace(clip))
            {
                AiTranslateService.SetApiKey(cfg, clip.Trim());
                cfg.Save();
                _result = "已从剪贴板粘贴 API Key";
            }
            else _result = "剪贴板为空，粘贴失败";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("直接读取剪贴板填入，无需 Ctrl+V（避开游戏内焦点问题）");
        }
        if (string.IsNullOrWhiteSpace(AiTranslateService.GetApiKey(cfg)))
        {
            Ui.ColoredWrapped(new Vector4(1f, 0.5f, 0.2f, 1f), "该服务商未填写 Key：切换后 AI 翻译不可用");
        }

        ImGui.Spacing();
        if (ImGui.Button("保存修改"))
        {
            cfg.Save();
            _result = "自定义服务商已保存";
        }
        }
        else if (!manualMode)
        {
            // ── 内置服务商：只读预设信息 + Key 编辑 ──
            var builtin = AiTranslateService.Providers.FirstOrDefault(p => p.Name == currentName);
            if (builtin.Name != null)
            {
                ImGui.TextUnformatted($"服务商：{builtin.Name}（内置预设）");
                Ui.Hint($"预设地址：{builtin.BaseUrl}/chat/completions");
                Ui.Hint($"预设模型：{builtin.Model}");
                Ui.Hint($"说明：{builtin.Note}");
                ImGui.Spacing();

                ImGui.TextUnformatted("API Key（仅该服务商）：");
                var builtinKey = AiTranslateService.GetApiKey(cfg);
                var bKeyFlags = _showCpKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
                var formW2 = ImGui.GetContentRegionAvail().X;
                ImGui.SetNextItemWidth(Math.Max(120f, formW2 - pasteW - showW - 16f * ImGuiHelpers.GlobalScale));
                if (ImGui.InputText("##BuiltinKey", ref builtinKey, 512, bKeyFlags))
                {
                    AiTranslateService.SetApiKey(cfg, builtinKey);
                    cfg.Save(); // 修改即保存
                }
                ImGui.SameLine();
                if (ImGui.Button(_showCpKey ? "隐藏" : "显示", new Vector2(showW, 0)))
                {
                    _showCpKey = !_showCpKey;
                }
                ImGui.SameLine();
                if (ImGui.Button("粘贴##BuiltinKeyPaste", new Vector2(pasteW, 0)))
                {
                    var clip = ImGui.GetClipboardText();
                    if (!string.IsNullOrWhiteSpace(clip))
                    {
                        AiTranslateService.SetApiKey(cfg, clip.Trim());
                        cfg.Save();
                        _result = "已从剪贴板粘贴 API Key";
                    }
                    else _result = "剪贴板为空，粘贴失败";
                }
                if (string.IsNullOrWhiteSpace(AiTranslateService.GetApiKey(cfg)))
                {
                    Ui.ColoredWrapped(new Vector4(1f, 0.5f, 0.2f, 1f), "该服务商未填写 Key：切换后 AI 翻译不可用");
                }
            }
        }
        else
        {
            Ui.Hint("手工自定义模式：使用 AI 设置上方的「API 地址 + 模型」与 Key 输入框。");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (_result.Length > 0)
        {
            ImGui.TextWrapped(_result);
        }
    }

    /// <summary> 重命名自定义服务商：同步选中名，并把旧名下分存的 API Key 迁移到新名（防 Key 变孤儿）。 </summary>
    private static void RenameProvider(Configuration cfg, CustomProvider cp, string newName)
    {
        var oldName = cp.Name;
        if (oldName == newName) return;
        if (cfg.AiProviderName == oldName) cfg.AiProviderName = newName;
        if (cfg.AiApiKeys != null && cfg.AiApiKeys.TryGetValue(oldName, out var key))
        {
            cfg.AiApiKeys.Remove(oldName);
            cfg.AiApiKeys[newName] = key;
        }
        cp.Name = newName;
    }
}
