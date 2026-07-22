# yangyang-BOT（Hime）

Hime 是一个基于 .NET 10、Sora 与 Milky/LLBot 的 QQ 智能机器人项目，主要包含：

- 秧秧人格与关系轨迹对话
- 群聊、私聊、主动发言和上下文记忆
- 动态情绪标签与表情包检索
- IndexTTS2 等语音服务接入
- 点歌、鸣潮 GsCore 与 OpenCode 工具集成
- 事件分发、命令路由、中间件和后台服务

## 运行

1. 安装 .NET 10 SDK。
2. 在 `Hime/appsettings.Local.json` 中配置本机密钥和覆盖项；该文件不会提交到 Git。
3. 按配置启动需要的本机外部服务，例如 LLBot、IndexTTS2、OpenCode 或 GsCore。
4. 执行：

   ```powershell
   dotnet run --project .\Hime\Hime.csproj
   ```

## 仓库范围

本仓库保存 Hime 自研源码、配置结构、人格资料和测试代码。模型权重、Python 运行时、第三方完整项目、聊天数据库、日志、自动收集图片、表情包和 API 密钥不会上传；请按本机配置自行准备这些资源。

