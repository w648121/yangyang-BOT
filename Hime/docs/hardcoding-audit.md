# Hime 业务硬编码审计

审计日期：2026-07-27
审计范围：`*.cs`、`config/*.json`，排除 `bin/`、`obj/`、运行数据、测试夹具和媒体目录。

## 判断标准

“动态化”不是把每个字符串和数字都塞进配置文件。Hime 按以下边界处理：

- 账号、群、外部地址、模型、资源路径、业务触发词和内容别名：必须来自配置、数据库或数据目录。
- OneBot 动作名、JSON 字段、数据库集合名、枚举、正则语法骨架：属于协议或数据结构，保持代码常量。
- 安全禁词、模型输出标签顺序、并发和输入绝对上限：保持不可被普通配置删除的安全边界。
- 配置类中的默认值：只作为缺失配置时的向后兼容兜底；当前实际值仍由 `config/*.json` 覆盖。

## 本次已消除的业务硬编码

| 原位置 | 原问题 | 处理 |
|---|---|---|
| `OneBotForwardMessageSender` | 缺少事件 `SelfId` 时固定使用 QQ `1928076256` | 按“事件账户 → 主账户 → 首个有效账户”动态解析；仍无有效账号时停止发送并记录警告，避免串号 |
| `MusicCommand` | 保留了一套未被调用的旧 QQ 卡片代码及固定 `appid=100495085` | 删除死代码；实际点歌继续走已使用的 OneBot `music/163` 消息段 |
| `TargetedInteractionOptions` | 旧定向用户回复与现有焦点路由重复，且默认绑死旧群、旧用户与人格路径 | 删除服务、配置、DI 注册、语音前缀与专用场景枚举；定向关系统一由 `ConversationFocusResolver` 判断 |
| `JobIntentDetector` | 提醒、闹钟自然语言词写死在类中 | 移入 `Jobs.IntentMarkers` 和 `Jobs.CommandPrefixes`；使用 `IOptionsMonitor`，修改配置后可热更新 |
| `JobEditIntentParser` | 修改提醒的触发词写死在类中 | 移入 `Jobs.EditMarkers`；使用 `IOptionsMonitor`，修改配置后可热更新 |
| `JobTimeParser` | 机器人别名、闹钟同义词、提醒正文清理词和事件名清理词写死 | 移入 `Jobs.AssistantAliases`、`InputNormalizationReplacements`、`ContentRemovalMarkers`、`TemporalRemovalMarkers`、`EventNameNoiseWords` 和 `EventNameSuffixes`；中文时间语法骨架保留为稳定解析器 |
| `TemporalAnchorService` | 事件相对时间过滤词和 `work_end_time -> 下班` 别名写死 | 统一读取 `Jobs` 配置和结构化记忆别名；“下班前/午休前/活动前”等事件锚点可继续扩展 |
| `PersonaStateService` | 结构化记忆默认别名写死在 `DefaultAliases` | 移入 `PersonaState.FactAliases`，支持精确键和 `code_*` 这类通配前缀 |
| `PersonaStateService` | 显式事实捕获规则、用户/群事实类型白名单写死在代码中 | 移入 `PersonaState.FactCaptureRules`、`AllowedUserFactKinds` 和 `AllowedGroupFactKinds`；仅当配置为空时启用兼容兜底规则 |
| `ConversationRouter` | 时间、技术、复杂问题和复杂情绪词写死 | 移入 `ModelRouting` 四组词表；路由通过 `IOptionsMonitor` 热更新 |
| `RelationshipTrajectoryService` | 关系推断类型白名单写死 | 移入 `RelationshipTrajectory.AllowedInferenceKinds`，后续新增关系事件类型无需改 C# |
| `EmotionalPragmaticsPlanner` | 支持/默认情绪、允许情绪和媒体意图标签写死 | 移入 `EmotionalPragmatics` 配置；运行时可调整策略，不再固定成少量死标签 |
| `SetuIntentInterpreter` | 已知标签、停用词和语义图片触发词写死 | 移入 `Setu` 配置并热更新；内置非 R18 安全词仍不可被配置移除 |
| `SetuApiService` | Lolicon 标签别名写死在 URI 构造逻辑中 | 移入 `Setu.TagAliases`；先按标签请求，标签无返回后再降级 keyword 的策略保持不变 |
| `PersonaPlotKnowledgeService` | 剧情错字别名、问题信号、弱检索词以及秧秧/玄翎/漂泊者/鸣潮身份词写死 | 移入 `Personas` 的别名、问题信号、弱词和 `PlotIdentityMarkers`；换人格或更新剧情无需重新编译 |
| `RecentVisualContextStore` | “这张图/上张图”等追图词写死 | 移入 `RecentVisualContext.ReferenceMarkers` |
| `StickerLabelVocabulary` | 基础情绪、细粒度语义、别名和否定关系编译在静态 C# 表中 | 移入 `config/sticker-labels.json`；运行时快照热更新，新增基础情绪或别名无需重新编译 |
| `AnimeStickerTagger` | 二次元表情识别后的情绪/意图映射写死 | 移入 `AnimeTagger.EmotionTagMap` 和 `IntentTagMap`；模型输出语义和表情情绪可独立调参 |
| `ConversationRouting` | 事实/技术模式的旧策略明确压制角色扮演，并覆盖人设格式 | 移入 `ResponsePolicies`；准确性只约束事实主张，不再关闭当前秧秧人格 |
| `ConversationStyleService` | 对话行为选择词和默认动作写死在方法中 | 移入 `ConversationStyle.DialogueMoveRules` 与默认动作列表，按顺序热更新 |
| `HimeBotService` | 显式 `~ai`、私聊等待与消息分发保留两套历史入口 | 拆为 `ExplicitAiRequestService` 和统一消息协调入口，删除旧 Dispatcher 命令与示例指令 |
| `PersonaComplianceService` | 泛化拒绝和重复客服腔可能通过分数阈值，不触发自然化 | 由配置正则和相似度阈值识别；只对明确命中的坏回复进行一次有界改写 |
| `PersonaRuntimeProfileService` | 最终人格锁在 C# 中重复写死秧秧身份、旧人格名称和固定表达策略 | 人格事实迁入 `Personas.RuntimeIdentityRules`，输出边界迁入 `Personas.RuntimeGuardRules`，运行时热更新 |
| `VoiceSynthesisService` | IndexTTS2 情绪别名与八维向量写成 `if/else` | 迁入 `VoiceSynthesis.IndexTts.EmotionVectors` 和 `FallbackEmotionVector`；代码只执行有序匹配与向量校验 |
| `VoiceCommand` | 情绪对比测试固定使用某个 voice id | 移入 `VoiceSynthesis.EmotionComparisonVoice`；换测试基准无需改命令代码 |
| `RuntimeStatusService` | 本机依赖服务端口写死 | 移入 `RuntimeDiagnostics.DependencyPorts`；状态面板扫描端口由配置驱动 |
| `ChatService` | 摘要关键词、敏感/指令词、长期记忆召回词和去噪词写死 | 移入 `ChatHistory.KeyTopicMarkers`、`SensitiveOrInstructionMarkers`、`RecallMarkers`、`SearchNoiseMarkers`，并加入 `MemoryIndexSchemaVersion` |
| `ConversationContextAssembler` | 正文按话题隔离后，近期助手回复仍可能从其他并行话题进入去重列表 | 正文证据和 `RecentAssistantReplies` 统一使用相同 `TopicId`，并增加并行话题回归测试 |
| `HelpCommand` / `HelpMenuRenderer` | 帮助菜单副标题固定写“秧秧智能终端” | 由 `Personas.DisplayName` 注入当前人格显示名；换人格不会残留旧标题 |
| `AiCommand` / 旧提示修补 | 依靠 `prompt.Contains(...)` 临时给情绪、客服腔或关系请求打补丁，容易越写越死 | 新增 `SocialIntentAnalyzer` + `config/social-intelligence.json`；社交意图、姿态、回复目标和避坑词由配置热更新 |
| `SocialTurnCoordinator` | 模型只收到“用户提问 -> AI 回答”的扁平任务，缺少社交动作、关系边界和人工反馈样例 | 新增 `SocialTurnStrategyService`，在生成前注入动态社交策略，并从 `reply_learning_examples` 检索管理员标注的好/坏回复样例 |

## 已经是动态来源，不需要重复改造

- QQ/LLBot 多账户：`config/accounts.json`
- 管理员：`config/ai.json`
- 群响应开关：LiteDB，由 `/响应`、`/停止` 修改
- AI/OpenCode/视觉/语音/GsCore/点歌/图片 API 地址与模型：各职责配置文件
- 主动发言、私聊、自然接话和关系轨迹：`config/conversation.json`
- 表情图片及单图多情绪数据：表情目录、LiteDB/JSON 数据
- 表情情绪词汇、别名、基础情绪与兜底：`config/sticker-labels.json`
- 社交意图、自然度避坑、人格边界和回复目标：`config/social-intelligence.json`
- 管理员人工标注的好/坏回复样例：LiteDB `reply_learning_examples`，由 `/学习 好`、`/学习 坏` 写入
- 秧秧剧情与语料：`data/personas/*.jsonl`

## 有意保留为稳定代码约束

| 类型 | 示例 | 不配置化原因 |
|---|---|---|
| 协议 | `send_group_msg`、`send_group_forward_msg`、OneBot 字段名 | 改动后不再符合协议，配置化只会延迟暴露错误 |
| 数据结构 | LiteDB 集合名、枚举值、关系事件种类 | 运行中改变会造成旧数据不可读或语义漂移 |
| 解析语法骨架 | 日期/周期正则、4 位任务编号 | 这是兼容接口；改变位数需要同时迁移既有任务和帮助文本 |
| 模型输出契约 | FER+/ONNX 标签顺序 | 必须与模型输出张量索引完全一致 |
| 安全边界 | R18/NSFW 基础禁词、输入大小和超远日期上限 | 不允许通过普通配置意外关闭 |
| 兼容默认值 | `127.0.0.1`、常用本机端口 | 实际部署值均已在配置中；默认值保证旧配置缺项时仍能启动 |

`Tests/Hime.IntelligenceChecks/Program.cs` 中的固定输入、QQ 测试号、日期和期望文本属于回归夹具，
不会进入运行时消息链路。测试数据必须固定，才能在重构后发现行为漂移；把这些值改成随机业务规则
反而会让测试失去判断能力。运行时语义词表、人格规则、情绪向量和账号配置不从该文件读取。

## 长期记忆词表的动态化边界

`ChatService` 的摘要关键词、敏感信息词、长期记忆召回词和检索去噪词已经迁入 `ChatHistory` 配置，并加入 `MemoryIndexSchemaVersion`。这意味着普通补词、删词和调整召回噪声词不需要重新编译。

需要注意的是：这些词会影响新写入记忆的 `SearchTerms` 和重要度计算。若只是小幅补充同义词，直接改配置即可；若大幅改动词表含义或版本号，应执行一次长期记忆索引重建，避免“旧记忆按旧规则索引、新记忆按新规则索引”导致召回不一致。

建议的重建流程：

1. 提升 `MemoryIndexSchemaVersion`；
2. 后台批量重建长期记忆 `SearchTerms`；
3. 重建期间双版本兼容读取；
4. 完成后原子切换版本。

因此当前不是死代码，而是“配置热更新 + 必要时重建索引”的动态化设计。

## 回归要求

- 没有任何真实 QQ、群号或旧群人格路径残留在业务源码中。
- 配置缺项时沿用原行为；现有配置加载后的行为不变。
- 自定义提醒词、路由词、图片标签和剧情别名无需重新编译即可生效。
- 新增表情情绪、细粒度语义或别名无需重新编译；配置无效时拒绝替换当前有效快照。
- 事实与技术回答仍保持当前人格，只限制未经验证的事实主张。
- 多账户合并转发从收到消息的账户原路发送，不再回退到固定 QQ。
- 全量构建、智能检查、Job 回归和启动健康检查全部通过后才允许上线。
