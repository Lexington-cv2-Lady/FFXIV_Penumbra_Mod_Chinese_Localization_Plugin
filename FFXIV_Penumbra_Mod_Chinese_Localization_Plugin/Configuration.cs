using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace FFXIVPenumbraHanhua;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    /// <summary> 词典目录（默认留空：首次使用时由主窗口引导配置）。 </summary>
    public string DictionaryPath { get; set; } = "";

    /// <summary> 翻译目录（AI 翻译管线的输入输出；默认留空，首次使用时由主窗口引导配置）。 </summary>
    public string TranslationPath { get; set; } = "";

    /// <summary> 是否自动刷新模组列表。 </summary>
    public bool AutoRefresh { get; set; } = true;

    /// <summary> 写回前备份轮转保留份数。 </summary>
    public int BackupCount { get; set; } = 5;

    // ── 全自动汉化（默认关：需用户显式开启；运行中静默，进度在主窗口状态栏）──
    /// <summary> 插件启动后 ~10 秒自动扫一遍未翻译模组并跑全自动汉化（提取→词典预填→AI→汇总→写回；需已配 API Key，未配则静默跳过）。 </summary>
    public bool AutoHanhuaOnStart { get; set; }

    /// <summary> Penumbra 新模组加入后自动跑全自动汉化（需已配 API Key；未配则静默跳过）。 </summary>
    public bool AutoHanhuaOnNewMod { get; set; }

    /// <summary> 【开发功能】恢复备份后自动重跑未翻译模组的汉化（还原=重置，立即补回译文）。 </summary>
    public bool AutoHanhuaAfterRestore { get; set; }

    // ── AI 翻译设置 ──
    /// <summary> 已选 AI 供应商（OpenAI 兼容端点预置表的下标；-1 = 手工自定义模式）。 </summary>
    public int AiProvider { get; set; }

    /// <summary> 当前选中的服务商名（内置名或自定义服务商名；空 = 回退使用 AiProvider 下标，兼容旧配置）。 </summary>
    public string AiProviderName { get; set; } = "";

    /// <summary> 用户自定义供应商列表（自定义置顶显示，可在 AI 设置中增删改）。 </summary>
    public List<CustomProvider> CustomProviders { get; set; } = new();

    /// <summary> 自定义 API 地址（覆盖供应商预设；空 = 用预设）。 </summary>
    public string AiBaseUrl { get; set; } = "";

    /// <summary> API Key（旧版单一保存字段，兼容迁移用；新版按服务商保存在 AiApiKeys）。 </summary>
    public string AiApiKey { get; set; } = "";

    /// <summary> 按服务商分别保存的 API Key（键：供应商名 / 「自定义」）。 </summary>
    public Dictionary<string, string> AiApiKeys { get; set; } = new();

    /// <summary> 模型名（空 = 用供应商预设）。 </summary>
    public string AiModel { get; set; } = "";

    /// <summary> 采样温度。 </summary>
    public float AiTemperature { get; set; } = 0.2f;

    /// <summary> 单次请求最大条数（超过字符上限也会自动拆批）。 </summary>
    public int AiBatchSize { get; set; } = 80;

    /// <summary> 关闭深度思考（仅对支持关闭的模型生效，如 DeepSeek V4 / GLM / Qwen3）。 </summary>
    public bool AiDisableThinking { get; set; }

    /// <summary> 联网搜索（仅通义/百炼 OpenAI 兼容端支持，其他平台自动失效）。 </summary>
    public bool AiWebSearch { get; set; }

    /// <summary> 报错日志导出目录（空 = 插件数据目录，即 pluginConfigs\<ID>\）。 </summary>
    public string LogExportPath { get; set; } = "";

    // ── 网络代理（访问海外 AI 服务商时需要；国内服务商无需）──
    /// <summary> AI 翻译是否走自定义代理。 </summary>
    public bool UseProxy { get; set; }

    /// <summary> 代理地址，如 http://127.0.0.1:7890（Clash / v2ray 等本地代理 HTTP 端口）。 </summary>
    public string ProxyAddress { get; set; } = "";

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

/// <summary> 用户自定义 AI 供应商。 </summary>
[Serializable]
public class CustomProvider
{
    /// <summary> 供应商名称（用于下拉显示与 Key 分存键）。 </summary>
    public string Name { get; set; } = "";

    /// <summary> API 地址（OpenAI 兼容 /chat/completions 端点）。 </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary> 默认模型名。 </summary>
    public string DefaultModel { get; set; } = "";
}
