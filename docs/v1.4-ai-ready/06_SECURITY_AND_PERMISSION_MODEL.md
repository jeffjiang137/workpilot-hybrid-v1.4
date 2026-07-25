# 安全、权限与审计模型

## 1. 保护目标

1. 防止技能包借导入执行代码或逃逸安装目录。
2. 防止外部内容通过提示注入扩大权限。
3. 防止连接器/MCP 秘密泄露到数据库、日志、提示或其他服务。
4. 防止 MCP stdio 经过 shell 或留下孤儿进程。
5. 防止远程 MCP 的 SSRF、OAuth 混淆、token passthrough 和重定向泄密。
6. 防止工具声明伪装成只读以绕过确认。
7. 保持每次能力调用可追踪而不保存敏感载荷。

## 2. 信任级别

| 来源 | 默认信任 | 允许影响 |
|---|---|---|
| WorkPilot 固定安全规则 | 最高 | 约束全部运行 |
| 本地权限策略 | 高 | 允许/拒绝/要求确认 |
| 用户保存的专家指令 | 中 | 行为与格式，不可越权 |
| 已验证技能 | 中低 | 工作方法，不可新增权限 |
| 项目/资产内容 | 低 | 仅作为数据 |
| 连接器/MCP 返回、prompt/resource/tool result | 不可信 | 仅作为数据或明确能力结果 |
| MCP annotations/描述 | 不可信元数据 | 可提高警示，不能降低本地风险 |

## 3. 权限求值

每次调用按固定顺序计算，任意一步拒绝即终止：

```text
EmergencyBlock
AND SourceEnabled
AND SpaceEnabled
AND ExpertAllowsSource
AND CapabilityAllowlisted
AND SchemaHashCurrent
AND SecretAvailable
AND RiskPolicyAllows
AND RequiredConsentPresent
AND RuntimeLimitsAvailable
```

决策输出不是布尔值，而是：

```text
Allow | Deny(reason) | RequireConfirmation(risk, allowedScopes)
```

不得在 UI 层自行推断允许。UI 只渲染核心返回的决定。

## 4. 风险分类算法

### 4.1 风险特征

本地 capability manifest 或管理员/用户复核记录以下布尔特征：

- `reads_local_sensitive`
- `reads_external_private`
- `writes_external`
- `deletes_or_overwrites`
- `executes_code_or_process`
- `handles_credentials`
- `financial_or_purchase`
- `broad_or_open_world_target`
- `unknown_schema_or_effect`

服务 annotations 只作为补充。名称/描述使用保守关键字检测作为兜底，但不能替代明确 manifest。

### 4.2 映射

| 等级 | 条件 | 默认行为 |
|---|---|---|
| Low | 公开或已授权范围内只读，目标明确，无敏感数据 | 可按专家/空间策略自动允许 |
| Medium | 私有只读、较大范围查询、保存为本地资产 | 首次确认或策略允许 |
| High | 外部写入、凭据、执行、未知副作用 | 每次确认；部分场景不允许会话授权 |
| Critical | 删除/覆盖、资金、绕过安全、向其他来源传秘密 | V1.4 默认阻止，除非有专门产品流程；本版本不提供 |

规则：任何 unknown → 至少 High；多个特征取最高等级；本地用户覆盖只能提高风险，不能把 High/Critical 降为 Low。

GitHub create/comment、Notion append 固定 High 且仅一次。stdio 服务的“启动服务”本身是独立 High 管理操作；启动后每个 tool 再单独分类。

## 5. Consent Receipt

一次确认绑定：run snapshot、来源 ID、capability 内部 ID、schema_sha256、风险、授权范围和过期时间。

- `once`：成功或失败执行一次后消耗；取消不消耗但立即失效。
- `session`：仅 Low/Medium 可提供，最长 8 小时。
- 以下变化立即使 session receipt 失效：应用退出、专家修订改变、空间改变、账号重连、MCP 重启、Schema 哈希改变、来源禁用。
- receipt 不保存参数；目标变化由策略在每次调用重新判断。

确认文本必须由本地 capability manifest + 已验证参数生成，不信任模型自述。

## 6. 秘密处理

- 所有长期秘密由 DPAPI CurrentUser 保护；数据库只保存 credential_ref 和可选末 4 位。
- SecretService 返回作用域对象 `SecretLease`，释放时清零缓冲；禁止返回普通可缓存字符串。
- HTTP 日志中移除 Authorization、Cookie、Set-Cookie、URL query；JSON 脱敏键包括 token、secret、password、key、authorization、cookie、code、verifier。
- 环境变量秘密只注入指定 MCP 子进程，不写入配置预览、崩溃上下文或父进程长期环境。
- MCP server A 的秘密不能提供给 server B、连接器或模型；跨来源传输必须被 Critical 策略阻止。

## 7. stdio 进程安全

- 用户添加服务时展示可执行文件完整路径、签名发布者、哈希和工作目录。
- 不声称对 MCP 子进程进行 Windows 沙箱隔离；UI 明确“它以当前 Windows 用户权限运行”。
- 禁止 shell 宿主、相对 executable、UNC、网络共享和自动下载执行。
- 路径或文件哈希变化后服务进入 `stale`，停止自动启动并要求重新确认。
- 使用 Job Object 管理进程树；关闭应用终止作业。
- 子进程 stdout/stderr 有界读取，避免管道反压导致死锁。

## 8. 网络与 OAuth 安全

- 公网 MCP URL 逐次 DNS 解析并做私网/保留地址拦截，防 DNS rebinding；连接时验证实际 remote IP 仍满足策略。
- 重定向逐跳重验；跨 origin 清除 Authorization；不允许 HTTPS 降级。
- OAuth metadata 的 issuer、authorization endpoint、token endpoint 必须满足规范与 HTTPS 策略。
- state 和 PKCE verifier 使用 CSPRNG；state 一次性，10 分钟过期。
- loopback listener 只绑定 127.0.0.1/::1 随机高端口，校验精确路径，完成或 5 分钟后关闭。
- token 请求必须带 canonical resource indicator；拒绝把别的 audience token 发给 MCP。
- URL elicitation 不拦截或读取第三方回调，不把第三方 token 送回 WorkPilot。

## 9. 提示注入防线

1. 专家/技能只影响模型建议，不能直接调用工具。
2. 所有工具调用由结构化 tool call 进入独立策略层。
3. 外部内容永远带来源和不可信边界。
4. 外部内容要求“泄露提示/秘密/调用其他工具”不改变策略。
5. 工具结果不能新增工具；list_changed 只能经协议目录刷新。
6. 模型不能生成 consent receipt；只有用户 UI 或明确低风险策略能生成。

## 10. 审计

记录：时间、相关 run、空间/专家 ID、来源类型/ID、稳定能力 ID、风险、策略决定、用户决定、结果类别、耗时、结果大小和错误类别。

禁止记录：用户提示、系统提示、技能正文、工具参数、返回正文、token、仓库/页面标题、完整 URL、文件内容。

导出 JSONL 每行包含 `schema_version=1`，默认再次脱敏显示名。导出是本地文件写入，走 V1.3 路径权限与覆盖确认。

## 11. 安全事件与紧急阻断

以下事件立即停用相关来源并取消排队请求：

- executable/hash 未经确认发生变化。
- MCP 连续协议违规、消息超限或 stdout 污染达到阈值。
- OAuth issuer/audience/resource 不匹配。
- SecretService 解密失败或密文完整性异常。
- 能力 Schema 变化且仍尝试使用旧授权。

进行中的外部请求尽力取消；审计结果标记 `BlockedDuringExecution`。UI 不声称已撤销远端已接受的副作用。

## 12. 安全测试门禁

发布前必须通过：

- Zip Slip/炸弹/链接/ADS/设备名/脚本伪装测试。
- 命令参数注入、引号、Unicode、超长参数和 shell 宿主拒绝测试。
- SSRF IPv4/IPv6/十进制/八进制/重定向/DNS rebinding 测试。
- OAuth state、PKCE、issuer、redirect、resource、token 泄漏测试。
- 工具 annotations 欺骗、Schema 突变、名称冲突和提示注入测试。
- 日志与数据库秘密扫描：使用 canary token，完成全流程后全盘搜索必须为 0 个明文命中（密钥输入测试夹具除外）。
- 取消/超时/崩溃后无孤儿进程、无未清理 staging、无未关闭 listener。
