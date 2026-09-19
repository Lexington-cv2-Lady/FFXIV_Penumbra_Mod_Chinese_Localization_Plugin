# FFXIV_penumbra的模组汉化插件

> 把 Penumbra 模组的英文选项与描述批量汉化为简体中文并写回，游戏内即时生效。

卫月（Dalamud）插件。读取 Penumbra 模组的选项与描述，用本地词典与 AI 批量汉化为简体中文，写回 `meta.json` / `group_*.json` 后自动触发 Penumbra 重载，游戏内即时生效。纯本地翻译，无遥测。

**AI 编写声明**：本插件代码由 AI 辅助编写，作者（Lexington-cv2-Lady）负责需求设计、逐项测试与验收，对应卫月官方 [AI Usage Policy](https://dalamud.dev/plugin-publishing/ai-policy) 的 Copilot 级 AI 使用。插件不采集任何遥测或用户数据，所有翻译仅在本地完成。

---

## 📦 安装

> [!IMPORTANT]
> **把下面的仓库地址添加进卫月，然后在插件安装器中搜索「模组汉化」安装。**

**① 插件清单库（推荐）**——作者的插件总库，以后新增插件会自动出现在列表里，无需改配置

```
https://raw.githubusercontent.com/Lexington-cv2-Lady/Lexington_CV-2_Repository/main/plugin_repo.json
```

```
https://cdn.jsdmirror.com/gh/Lexington-cv2-Lady/Lexington_CV-2_Repository@main/plugin_repo.json
```

（第一个是主地址，第二个是镜像；两行任选其一即可）

**② 兼容旧地址**——仅添加过旧地址的老用户继续使用，效果与 ① 相同

```
https://raw.githubusercontent.com/Lexington-cv2-Lady/FFXIV_Penumbra_Mod_Chinese_Localization_Plugin/main/plugin_repo.json
```

**添加步骤**

1. 打开卫月设置 → **「实验性」** → **「自定义插件仓库」**
2. 粘贴上面任一地址，点 **「+」** 添加
3. 打开**插件安装器**，在「自定义插件仓库」分类中搜索 **「模组汉化」** 并安装

> [!TIP]
> **用外部 AI 翻译？** 把插件导出的 `_未翻译.json` 连同这份 [translation_rules.json](Data/translation_rules.json) 一起发给 AI（规则内容[点此直接复制](https://raw.githubusercontent.com/Lexington-cv2-Lady/FFXIV_Penumbra_Mod_Chinese_Localization_Plugin/main/Data/translation_rules.json)），翻好的文件改名为 `xxx_已翻译.json` 放回翻译目录即可。

<details>
<summary><b>手动安装（不通过自定义仓库）</b></summary>

1. 从 [Releases](https://github.com/Lexington-cv2-Lady/FFXIV_Penumbra_Mod_Chinese_Localization_Plugin/releases) 下载最新版 zip（见 Releases 页面）
2. 解压到 `XIVLauncherCN\plugins\FFXIV_Penumbra_Mod_Chinese_Localization_Plugin\`
3. 重启游戏或重新加载卫月

</details>

---

## ✨ 功能

- 读取 Penumbra 模组选项与描述（`meta.json` / `group_*.json`，兼容新旧双格式）
- 词典汉化：我的翻译 / 个性翻译 / wiki 术语对照 / AI 知识库 / 单词黑名单
- AI 翻译：内置多家国内主流服务商（按平台自动拆批与输出上限），也支持导出后交给外部 AI 翻译再汇总
- 汉化前自动备份（zip 按份数轮转），可一键还原且保留 Penumbra 的选项状态
- 纯中文输出、黑名单保留英文专名、「已翻译」标记自动跳过
- 列表支持鼠标拖框多选，实时日志可落盘并一键打开

## 🚀 使用

- **打开主窗口**：聊天框输入 **`/pmh`**，或通过插件安装器点击齿轮图标。
- **汉化流程**：① AI 设置（配 Key）→ ② 主动扫描英文 → ③ 词典预填 → ④ AI 翻译 → ⑤ 汇总写回
- 主窗口可对单个模组「翻译并写入」、手动编辑中英文并保存、创建/删除「已翻译」标记、查漏补缺。

## 📂 数据目录

| 目录 | 用途 |
|---|---|
| 词典目录（默认自动创建在插件安装目录内，可改） | 我的翻译 / 个性翻译 / 单词黑名单 / wiki_术语对照 / AI知识库 |
| 翻译目录（默认自动创建在插件安装目录内，可改） | AI 翻译管线的 `_未翻译.json` / `_已翻译.json` |
| 模组根目录（Penumbra 数据目录，插件自动获取） | 存放 Penumbra 模组的目录 |

> 安装即用：两个目录首次启动时自动创建，无需手动设置；后续可在「目录和词典管理」中修改（自动迁移已有数据）。

## 📄 协议

本项目采用 **AGPL-3.0** 开源协议——你可以自由使用、修改和分发，但修改后的作品必须同样以 AGPL-3.0 开源。

## 🛠 构建与打包

```powershell
$env:DALAMUD_HOME = "$env:APPDATA\XIVLauncherCN\addon\Hooks\dev"
dotnet build "FFXIV_Penumbra_Mod_Chinese_Localization_Plugin\FFXIV_Penumbra_Mod_Chinese_Localization_Plugin.csproj" -c Debug
```

打包：`dotnet build -c Release` 生成 dll + manifest，手动打 zip（dll + json + images/icon.png）传到 Releases。
