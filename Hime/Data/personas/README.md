# 秧秧人格资料库

本目录保存普通秧秧与“秧秧·玄翎”的可追溯参考资料。两个时期必须分开检索，避免身份、经历与关系阶段互相污染。

## 文件说明

- `yangyang-lines.jsonl`：当前普通秧秧运行时语气库。已执行规范化文本去重。
- `yangyang-xuanling-lines.jsonl`：玄翎独立语气库；当前不会被普通秧秧自动加载。
- `yangyang-reference-materials.jsonl`：两种形态的角色档案、故事、珍贵之物与共鸣报告。它们用于事实核对，不应直接当作日常回复示例。
- `yangyang-reference-sources.json`：来源、用途、许可提示和隔离规则。
- `yangyang-import-report.json`：本次导入数量、去重结果、场景/情绪分布和文件哈希。

## 去重规则

1. Unicode NFKC 规范化。
2. 忽略空白与标点差异进行精确去重。
3. 长文本进行保守的近似匹配；不自动合并短战斗口号。
4. Wiki 中明确放在删除线内的误发文本不导入。

## 使用边界

- 普通秧秧仍使用 `yangyang-lines.jsonl`。
- 玄翎必须显式选择独立人格后才能使用 `yangyang-xuanling-lines.jsonl`。
- `medium` 置信度表示文字来自对官方视频的社区转写；正式上线为人格示例前应再次人工听校。
- 原始游戏文本和音视频版权归库洛游戏所有；公开数据集与社区 Wiki 的许可和用途限制见来源清单。
