# 多机器人账户消息管道

## 目标

多个 QQ/LLBot 账户只是 Hime 的传输入口和出口。AI、语音、表情包、LiteDB、指令模块和消息队列仍然只有一份，不为每个账户复制一套业务服务。

```text
账户 A（Milky） ─┐
账户 B（Milky） ─┼─> 适配器 -> 唯一事件去重 -> 有界分区队列 -> 中间件 -> 指令/AI
账户 C（Milky） ─┘                                      |
                                                       └-> 原 ReplyChannel 返回
```

每条消息是一次唯一工作，不是广播主题。`Command Bus` 继续执行单处理器；`Event Bus` 仅用于日志、指标等旁路通知。

## 配置

连接清单位于 `config/accounts.json`：

```json
{
  "BotAccounts": {
    "Connections": [
      {
        "Id": "primary",
        "DisplayName": "小黄瓜",
        "Enabled": true,
        "IsPrimary": true,
        "SelfId": 1928076256,
        "Host": "127.0.0.1",
        "Port": 3010,
        "AccessToken": "",
        "OneBotApiBaseUrl": "http://127.0.0.1:3000"
      }
    ]
  }
}
```

- `Id`：连接稳定标识，必须唯一；不是 QQ 号。
- `DisplayName`：仅用于日志辨认。
- `Enabled`：是否在本次启动时连接。
- `IsPrimary`：主动发言使用的账户；被动回复不读取它。
- `SelfId`：该机器人 QQ 号，用于把 OneBot 合并转发和音乐卡片路由到正确账户。
- `Host`、`Port`、`AccessToken`：对应 LLBot 的 Milky 服务配置。
- `OneBotApiBaseUrl`：该账户对应的 OneBot V11 HTTP 地址；留空时回退 `integrations.json` 的全局地址。

添加账户时复制一项并修改 `Id` 和连接参数。未配置任何启用项时会回退到旧版的 `127.0.0.1:3010`，保证单账户升级兼容。

## 路由规则

1. 被动消息从哪个账户收到，就把该账户的 `ReplyChannel` 附在消息上。
2. 相同 QQ 事件跨账户只进入业务管道一次。
3. 消息明确提及某个机器人时，其他机器人账户不能抢占该消息。
4. 私聊的连续消息合并以“账户 + 用户”为键，不会跨账户混合。
5. 主动消息没有原始回复通道，因此由 `IsPrimary` 账户发送。
6. Sora 只负责协议事件；业务指令由 `MessageCommandRouter` 在统一管道内单次执行。

## 公共资产

以下服务继续由依赖注入容器以单例形式共享：AI 客户端、人物关系、聊天记录、图片与表情目录、语音引擎、GsCore、点歌、运行诊断和 LiteDB。增加账户不会重复加载模型，也不会按账户复制数据库。
