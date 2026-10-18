# 题库包与 AI 创作流程

题库是离线 JSON 数据。导入题库不会执行代码、下载解释器、启用管理目标或改写已有学习历史和娱乐许可。代码题只有在用户进入对应题目并明确运行时，才交给独立运行器；只导入、预览、查看参考解答均不运行。

## 在应用中使用

1. 在“API 与扩展”页导出 AI 提示词。提示词包含 v1 JSON Schema、已有主题及题目目录（ID、题名、题型、模式、语言、复习关系）。它不读取学习历史、答题草稿、个人代码、软件路径、管理配置或 API 密钥。
2. 把提示词交给你选择的 AI，补充想学的主题、水平、题数与预计时长。应用内在线生成功能也需要用户明确触发；在线服务设置和题库导入是两步。
3. 将返回的 JSON 粘贴到应用，或选择本地 JSON 包。点预览，先检查内容、答案、提示和引用来源。AI 输出与原创声明均未经软件核实，须人工审阅；公开可访问不等于可以转载进题库。
4. 在预览中逐题确认新增、跳过或更新，最后点确认导入。相同 ID 的内容更新会明确显示；相似题默认跳过，需要你主动选择新增。
5. 导入成功后，主题下拉列表会出现新主题，题目参与原有推荐/随机选择和通过/复习进度。普通状态刷新不切走正在回答的题。

可先用程序目录中的 `content/examples/java-basics.json`、`english-basics.json`、`chinese-reading.json` 试用。它们分别提供一道 Java 编程题、两道英语场景题、两道语文阅读题。示例是原创内容，均不默认导入。Java 需要另外明确安装并信任插件和选择本地 JDK；导入包不会自动安装运行器。

## v1 精确范本

```json
{
  "schemaVersion": 1,
  "packId": "my-dns-notes",
  "name": "我的 DNS 复习",
  "version": "1.0",
  "description": "原创 DNS 基础小练习",
  "author": "你的署名",
  "topics": [
    { "id": "network", "name": "计算机网络基础", "description": "网络基础练习" }
  ],
  "exercises": [
    {
      "id": "my-dns-role-v1",
      "topicId": "network",
      "mode": "standard",
      "kind": "scenario",
      "title": "域名解析先做什么",
      "scenario": "应用需要先查询域名对应的地址，再尝试连接目标服务。请指出这个查询系统的简称。",
      "takeaway": "域名查询和连接目标服务是两个不同环节。",
      "sourceType": "original",
      "sourceLabel": "自编场景与练习，来源声明待人工核实",
      "sourceUrl": "",
      "estimatedMinutes": 2,
      "parts": [
        {
          "id": "service",
          "kind": "fill",
          "prompt": "填写三个英文字母。",
          "acceptedAnswers": ["DNS"],
          "hints": ["Domain Name System 的首字母。"],
          "explanation": "DNS 指域名系统，应用可通过它查询地址等记录。"
        }
      ]
    }
  ]
}
```

填空答案采用 Unicode 兼容归一化、首尾去空白、忽略大小写后精确匹配，不用关键词包含判断。选择题使用 `kind="choice"`、至少两个 `options` 及从 0 开始的 `correctIndex`，同时提供 `hints` 和 `explanation`。原题模式可以是 `standard` 或 `light`，二者完成后获得同等许可。

编程题使用 `kind="code"` 和 `coding`，不添加场景小问。`languageId` 默认 `python`；其他值须有已明确安装并信任的对应语言插件。`coding` 包含 `functionName`、`starterCode`、`referenceCode`、`hints`、`cases`；可增加 `solutionExplanation`、`complexity`、`alternativeCode` 和 `alternativeExplanation`。每个用例包含 `name`、`argumentsJson`（位置参数的 JSON 数组字符串）、`expectedJson`（JSON 值字符串）、`isExample`。至少一个示例和一个非示例检查。

Java 示例使用 `languageId="java"`，`public class Solution` 内的静态方法。`coding.parameterTypes` 和 `coding.returnType` 使用插件支持的 `int`、`string`、`int[]`、`string[]`、`bool`。只有一个字符串数组参数时，`argumentsJson` 例如 `[["ready","","ok"]]`；这整个数组仍作为 JSON 字符串存放。空数组不能可靠推断元素类型，推荐显式填写类型。完整可导入范本见 `content/examples/java-basics.json`。

短复习题设置 `reviewOfId` 为对应原题 ID，要求同主题、`mode="light"`、`kind="scenario"`、预计 1–2 分钟。引用的原题可以在同一包或已经安装的目录里；最终选择跳过原题导致引用缺失时，整个导入被拒绝。监控仍要求原题实际通过后才接受其复习。复习记录不会把原题变成已通过。

## 稳定 ID、去重与更新

- `packId`、主题 `id`、题目 `id`、小问 `id` 使用小写 ASCII，格式 `[a-z0-9][a-z0-9._-]{0,79}`。主题 `all` 为应用保留值。ID 只作为标识，不参与文件路径拼接。
- 题目 `topicId` 必须在包的 `topics` 中声明。已有主题 ID 必须沿用原名称；显示名可以是中文。新主题无需修改程序。保留已有题目 ID，才能继续识别历史通过记录；改变题目身份请创建新 ID。
- `new`：新 ID、无明显相似，默认新增。
- `identical`：同 ID 的规范化完整内容一致，仅跳过。
- `update`：同 ID 内容改变，默认跳过，可明确更新。更新保留 ID、主题、题型、语言和复习关系；内容身份变化请用新 ID。
- `similar`：不同 ID 但同主题同题型，题名归一化相同或场景字符二元组相似度达到 0.82，默认跳过。只是一项启发式提醒，不能识别所有语义重复；仍需人工判断。

预览会固定目录版本。其他导入改变目录后，旧预览不能继续写入，需要重新预览。用户逐行选定后，导入再次验证所有结构和复习关系。更新或跨包替换相同题目 ID 作为单个原子目录事务，不改变历史记录或许可。

## 大小与结构限制

- 每次提交一个 v1 包，最多 48 KiB UTF-8、30 道题、20 个主题。命名管道整条请求另有 256 KiB 上限，JSON 字符串转义也会占空间；过大时请拆成小包。
- 已安装包最多 30 个，合并目录最多 300 道题，外部内容总序列化大小最多 512 KiB。
- 题干最多 14,000 字符；每题最多 20 个场景小问；每问最多 12 个选项、30 个可接受填空答案、10 条提示。代码模板/参考实现各最多 20,000 字符；每道编程题 2–40 个用例，每例位置参数最多 10 个。具体字段约束由校验器执行，导出的 Schema 配合提示词说明使用。
- JSON 最大嵌套 24 层；用例 JSON 最大 12 层。拒绝未知字段、重复键、错误类型、空必要字段、重复 ID、路径式 ID、非法选项索引与无效测试 JSON。
- `sourceType="original"` 可以留空 `sourceUrl`，但必须提供非空原创 `sourceLabel`；`reference` 要求 HTTPS 来源，不允许 `file:`、`javascript:`、HTTP 或带用户名密码的 URL。URL 只是内容元数据，不会因导入而自动访问。
- 格式检查不替代内容准确性、答案正确性、引用许可或代码可信度审阅。题库数据不允许定义安装脚本、启动命令、文件路径或任意插件执行入口。

## 保存和恢复

监控是唯一题库写入者。外部包统一保存在当前数据目录的 `packs/installed.json`，通过临时文件、磁盘刷新和原子替换提交，已有有效文件保留 `installed.json.bak`。导入前先校验候选目录，保存成功后才替换运行中的快照；写入失败保留旧目录。

读取损坏主文件时可以使用经过验证的备份，并在状态中给出警告；原始损坏文件保留，后续写入前另存 `.corrupt` 证据。主文件和备份均损坏时使用内置题目，拒绝静默覆盖已有损坏数据。学习配置、会话、历史和草稿存放在其他文件中，不因题库导入而重建。

代码任务未收尾时不能确认导入，避免在一份测试执行过程中替换对应题目。请先等待完成或明确取消，再提交导入。预览和导出提示词不受此限制。

## IPC 与本地验证

- `export_pack_prompt` → `PromptText`。
- `preview_pack` + `PackJson` → `PackPreview`：包 ID、名称、`CatalogRevision`、逐行状态和默认动作。
- `import_pack` + 同份 `PackJson` + 每行 `PackChoices[exerciseId]="add|skip|update"` + `ExpectedCatalogRevision`。成功后返回合并后的 `Exercises`、`Topics`、`ContentPacks`；不会自动切题。
- `status` 同时返回 `ContentWarnings`，供 UI 展示读盘恢复/错误。

`tests/EntLimit.Content.Tests` 运行离线格式/事务/恢复/隐私检查。追加 `--monitor <EntLimit.Monitor.exe>` 在随机数据目录和独立实例管道下验证真实预览、导入、判题、许可、拒绝、更新和重启；仅停止自身测试监控，不使用生产数据。Windows 上该回归需允许创建本用户桌面监控和命名管道；受限沙箱可能使连接超时。
