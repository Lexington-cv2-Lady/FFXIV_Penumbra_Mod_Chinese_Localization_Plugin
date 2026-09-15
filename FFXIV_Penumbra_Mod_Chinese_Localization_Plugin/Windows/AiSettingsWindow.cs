using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using FFXIVPenumbraHanhua.Services;

namespace FFXIVPenumbraHanhua.Windows;

/// <summary> AI 设置窗口：供应商 / Key / 模型 / 温度 / 批量 / 测试连接。 </summary>
public class AiSettingsWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly AiTranslateService _ai;
    private string _testResult = "";
    private Task<string>? _testTask;
    private bool _showKey; // API Key 明文显示开关

    public AiSettingsWindow(Plugin plugin) : base("AI 设置###HanhuaAi")
    {
        Size = new Vector2(640, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        _plugin = plugin;
        _ai = plugin.AiTranslate;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = _plugin.Configuration;

        ImGui.TextWrapped("配置 OpenAI 兼容接口（智谱 / 通义 / 混元 / 千帆 / OpenRouter / GPT 等），用于「翻译管线 → AI 翻译」。");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 供应商（自定义置顶，国内优先，海外在后；可增删改自定义服务商）
        ImGui.TextWrapped("供应商（自定义置顶；国内优先；可增删改自定义服务商）：");
        Ui.SameLineIfFits(Ui.ButtonWidth("AI 配置列表"));
        if (ImGui.Button("AI 配置列表"))
        {
            _plugin.ToggleAiConfigUi();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("打开 AI 配置列表（三级窗口）：新增 / 删除自定义服务商、查看内置预设、清空预设配置");
        }
        var allProviders = AiTranslateService.GetAllProviders(cfg);
        var currentName = AiTranslateService.CurrentProviderName(cfg);
        var manualMode = cfg.AiProviderName == "" && cfg.AiProvider < 0;
        var shown = manualMode ? "自定义（手工填写 API 地址）" : currentName;
        if (ImGui.BeginCombo("##Provider", shown))
        {
            foreach (var item in allProviders)
            {
                var sel = !manualMode && item.Name == currentName;
                if (ImGui.Selectable(item.Name, sel))
                {
                    cfg.AiProviderName = item.Name;
                    cfg.AiProvider = -2; // 名称优先解析；下标仅兜底
                    cfg.Save(); // 修改即保存
                    _testResult = $"已切换为「{item.Name}」，Key 各服务商独立保存";
                }
                if (sel) ImGui.SetItemDefaultFocus();
            }
            if (ImGui.Selectable("自定义（手工填写 API 地址与模型）", manualMode))
            {
                cfg.AiProviderName = "";
                cfg.AiProvider = -1;
                cfg.Save(); // 修改即保存
                _testResult = "已切换为「自定义」，Key 各服务商独立保存";
            }
            if (manualMode) ImGui.SetItemDefaultFocus();
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
        {
            var tip = "";
            if (manualMode)
            {
                tip = "自定义模式：完全使用下方「API 地址 + 模型」，需自行填写";
            }
            else
            {
                var hit = allProviders.FirstOrDefault(x => x.Name == currentName);
                if (hit.Name != null) tip = hit.Note;
            }
            if (tip.Length > 0) ImGui.SetTooltip(tip);
        }

        ImGui.Spacing();

        // BaseUrl
        ImGui.TextUnformatted("API 地址（留空 = 服务商预设）：");
        var baseUrl = cfg.AiBaseUrl;
        var pasteW = 64f * ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - pasteW - 8f * ImGuiHelpers.GlobalScale));
        if (ImGui.InputText("##AiBaseUrl", ref baseUrl, 512))
        {
            cfg.AiBaseUrl = baseUrl;
            cfg.Save(); // 修改即保存，避免重启丢失
        }
        ImGui.SameLine();
        if (ImGui.Button("粘贴##AiBaseUrlPaste", new Vector2(pasteW, 0)))
        {
            var clip = ImGui.GetClipboardText();
            if (!string.IsNullOrWhiteSpace(clip))
            {
                cfg.AiBaseUrl = clip.Trim();
                cfg.Save();
                _testResult = "已从剪贴板粘贴 API 地址";
            }
            else
            {
                _testResult = "剪贴板为空，粘贴失败";
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("直接读取剪贴板填入，无需 Ctrl+V（避开游戏内焦点问题）");
        }
        Ui.Hint($"当前生效：{AiTranslateService.ResolveEndpoint(cfg).BaseUrl}/chat/completions");

        ImGui.Spacing();

        // API Key（按服务商独立保存；带「显示/隐藏」+「粘贴」按钮：绕过游戏内焦点问题）
        ImGui.TextUnformatted("API Key（仅当前服务商）：");
        var key = AiTranslateService.GetApiKey(cfg);
        var showW = 52f * ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - pasteW - showW - 16f * ImGuiHelpers.GlobalScale));
        var keyFlags = _showKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        if (ImGui.InputText("##AiKey", ref key, 512, keyFlags))
        {
            AiTranslateService.SetApiKey(cfg, key);
            cfg.Save(); // 修改即保存
        }
        ImGui.SameLine();
        if (ImGui.Button(_showKey ? "隐藏" : "显示", new Vector2(showW, 0)))
        {
            _showKey = !_showKey;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(_showKey ? "隐藏 API Key（恢复密文）" : "显示 API Key 明文（确认是否输错）");
        }
        ImGui.SameLine();
        if (ImGui.Button("粘贴", new Vector2(pasteW, 0)))
        {
            var clip = ImGui.GetClipboardText();
            if (!string.IsNullOrWhiteSpace(clip))
            {
                AiTranslateService.SetApiKey(cfg, clip);
                cfg.Save();
                _testResult = "已从剪贴板粘贴 API Key";
            }
            else
            {
                _testResult = "剪贴板为空，粘贴失败";
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("直接读取剪贴板填入，无需 Ctrl+V（避开游戏内焦点问题）");
        }
        if (string.IsNullOrWhiteSpace(AiTranslateService.GetApiKey(cfg)))
        {
            Ui.ColoredWrapped(new Vector4(1f, 0.5f, 0.2f, 1f), "未填写 Key：AI 翻译不可用，可改用外部 AI 翻译（导出 _未翻译.json → 外部翻译 → ④ 汇总 → ⑤ 写回）。");
            Ui.Hint("免费 AI 路线：在「汉化流程」① 导出 _未翻译.json（连同 翻译规则.json）交给外部 AI，翻好改名为 _已翻译.json 放回翻译目录，再点 ④ 汇总 → ⑤ 翻译写入MOD。");
        }

        ImGui.Spacing();

        // 模型
        ImGui.TextUnformatted("模型（留空 = 供应商预设）：");
        var model = cfg.AiModel;
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - pasteW - 8f * ImGuiHelpers.GlobalScale));
        if (ImGui.InputText("##AiModel", ref model, 256))
        {
            cfg.AiModel = model;
            cfg.Save(); // 修改即保存
        }
        ImGui.SameLine();
        if (ImGui.Button("粘贴##AiModelPaste", new Vector2(pasteW, 0)))
        {
            var clip = ImGui.GetClipboardText();
            if (!string.IsNullOrWhiteSpace(clip))
            {
                cfg.AiModel = clip.Trim();
                cfg.Save();
                _testResult = "已从剪贴板粘贴模型名";
            }
            else
            {
                _testResult = "剪贴板为空，粘贴失败";
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("直接读取剪贴板填入，无需 Ctrl+V（避开游戏内焦点问题）");
        }
        Ui.Hint($"当前生效：{AiTranslateService.ResolveEndpoint(cfg).Model}");

        ImGui.Spacing();

        // 温度 + 批量
        var temp = cfg.AiTemperature;
        if (ImGui.SliderFloat("温度（越低越忠实原文）", ref temp, 0f, 1f))
        {
            cfg.AiTemperature = temp;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            cfg.Save(); // 拖动结束才落盘，避免拖动过程每帧写配置文件
        }
        var batch = cfg.AiBatchSize;
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("单批条数", ref batch, 10, 50))
        {
            cfg.AiBatchSize = Math.Clamp(batch, 1, 500);
            cfg.Save(); // 修改即保存
        }
        Ui.SameLineIfFits(ImGui.CalcTextSize("（条目过多自动按平台字符上限拆批）").X);
        Ui.Hint("（条目过多自动按平台字符上限拆批）");

        ImGui.Spacing();

        // 关闭深度思考 + 联网搜索（联网仅通义/百炼支持，其他平台置灰）
        var disableThinking = cfg.AiDisableThinking;
        if (ImGui.Checkbox("关闭深度思考", ref disableThinking))
        {
            cfg.AiDisableThinking = disableThinking;
            cfg.Save(); // 修改即保存
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("对支持关闭思考的模型生效（DeepSeek V4 / GLM-5.2 / Kimi K2.6 / 通义 Qwen3），可加快响应、节省 token");
        }
        ImGui.SameLine();
        var webSupported = AiTranslateService.PlatformSupportsWebSearch(cfg);
        if (!webSupported && cfg.AiWebSearch)
        {
            cfg.AiWebSearch = false; // 平台不支持时自动关闭（值变化才落盘，避免每帧写盘）
            cfg.Save();
        }
        var webSearch = cfg.AiWebSearch;
        ImGui.BeginDisabled(!webSupported);
        if (ImGui.Checkbox("联网搜索", ref webSearch))
        {
            cfg.AiWebSearch = webSearch;
            cfg.Save(); // 修改即保存
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(webSupported
                ? "AI 翻译时参考互联网搜索结果（通义/百炼专用参数 enable_search）"
                : "当前平台（地址）不支持联网搜索：仅通义/百炼（dashscope/aliyuncs）支持");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── 网络代理（访问 OpenAI / Claude / Gemini 等海外服务商时需要）──
        ImGui.TextUnformatted("网络代理（仅海外服务商需要；国内服务商留空即可）：");
        var useProxy = cfg.UseProxy;
        if (ImGui.Checkbox("启用代理", ref useProxy))
        {
            cfg.UseProxy = useProxy;
            cfg.Save();
            AiTranslateService.ApplyProxyConfig(cfg);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("勾选后，AI 翻译请求将通过下方代理地址发出。\n访问智谱 / 通义 / 腾讯 / 百度 / DeepSeek 等国内服务商时无需开启。");
        }
        ImGui.SameLine();
        var proxyAddr = cfg.ProxyAddress;
        ImGui.SetNextItemWidth(Math.Max(160f, ImGui.GetContentRegionAvail().X - 70f * ImGuiHelpers.GlobalScale));
        ImGui.BeginDisabled(!cfg.UseProxy);
        if (ImGui.InputTextWithHint("##ProxyAddr", "http://127.0.0.1:7890", ref proxyAddr, 256))
        {
            cfg.ProxyAddress = proxyAddr;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            cfg.Save();
            AiTranslateService.ApplyProxyConfig(cfg);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("代理地址，如 http://127.0.0.1:7890（Clash / v2ray 等本地代理的 HTTP 端口）。\n修改后回车或点击别处即生效。");
        }
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 测试连接
        if (_testTask != null && !_testTask.IsCompleted)
        {
            ImGui.TextDisabled("测试中…");
        }
        else
        {
            if (ImGui.Button("测试连接"))
            {
                _testResult = "";
                _testTask = Task.Run(() => _ai.TestAsync(cfg));
            }
            ImGui.SameLine();
            if (ImGui.Button("保存设置"))
            {
                cfg.Save();
                _testResult = "设置已保存";
            }
            ImGui.Spacing();
            if (_testTask != null && _testTask.IsCompleted)
            {
                _testResult = _testTask.Result;
                _testTask = null;
            }
            // 测试结果日志区：带边框统一风格
            Plugin.ResultBox("##AiResult", _testResult, "测试结果将显示在这里（如：连接成功…）");
        }
    }
}
