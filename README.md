# OpenRevit Tools

OpenRevit Tools 是面向 Revit 2024 的开源 MCP 基础设施，让 Codex 等顶级 AI 通过受控工具与
当前 Revit 模型协作。稳定功能区保持最小化，业务工作流通过 MCP 客户端组合和验证。

当前版本：`0.5.0`。运行环境：Windows、Autodesk Revit 2024、.NET Framework 4.8。

> 重要：AI/MCP 可以修改当前 Revit 模型。使用写入工具前请保存或同步模型，并确认作用范围。

## 功能

### Revit 功能区

- **启动 MCP**：启动只监听本机的 Revit MCP 服务。
- **停止 MCP**：停止本机会话服务。
- **状态 + 工具**：查看运行状态与当前动态注册的工具清单。

其它命令实现作为实验兼容能力保留，但默认不显示在稳定功能区中；它们不构成稳定产品承诺。

### MCP

Revit 启动时动态发布实际工具清单；当前默认构建包含约 100 个查询、检查、视图、参数、几何与导出工具。
不要在文档或客户端中复制一份易过期的静态工具表，以 `tools/list` 为准。

大结果查询 `list_mep_elements`、`get_room_boundaries` 和 `find_untagged_rooms` 使用统一的 `limit`/`offset` 分页：`limit` 默认 100、最大 1000，结果按 ElementId 稳定排序，并返回 `total`、`returned`、`truncated` 和 `nextOffset`。

维修可达协作具有专用工具，不依赖任意 C#：

- `analyze_maintenance_reachability`
- `analyze_maintenance_route_candidates`
- `get_maintenance_reachability_summary`
- `get_maintenance_route_candidates`
- `approve_maintenance_reachability`
- `show_maintenance_reachability`
- `clear_maintenance_reachability`
- `sync_maintenance_ledger_bridge`

400×400 伸手检修专用工具（默认仅侧墙；人员门检查后可显式改为天花；data-only 默认，视图按需生成）：

- `analyze_maintenance_hand_reach_candidates`
- `get_maintenance_hand_reach_summary`
- `get_maintenance_hand_reach_candidates`
- `approve_maintenance_hand_reach`
- `show_maintenance_hand_reach`
- `clear_maintenance_hand_reach`

`execute_csharp` 默认不注册。只有在 Revit 启动前显式设置
`OPENREVIT_ENABLE_FULL_TRUST_CSHARP=1` 才开放；它拥有与 Revit 相同的完全信任权限，详见 [SECURITY.md](SECURITY.md)。

交互式 `run_command` 也默认关闭；只有 Revit 前台有人完成点选时才设置
`OPENREVIT_ENABLE_INTERACTIVE_COMMANDS=1`。返回“pending_user_interaction”不等于命令已经完成。

门宽工具是**风险初筛**，不是 ADA 或其他规范合规证明。名义宽度、洞口宽度与实际净开口不是同一个量。

## 维修可达的产品分工

按钮是能力入口，MCP 是桥梁，AI 是负责理解项目语义和组织证据的决策层：

1. 负空间算法提供可追溯的几何证据；
2. 专用维修 MCP 工具生成候选入口、梯子、通行路径、设备检修区和阻挡来源；
3. 顶级 AI（Codex 或 DeepSeek 等）结合天花注释分组、设备检修侧、侧墙/天花入口策略和现场条件审查候选；
4. 专业/施工人员确认黄色或受限方案；
5. Revit 实例参数作为事实源，通过台账桥接生成用户表、Codex 证据表和哈希清单。

这套流程给出“候选方案与证据”，不替代法规审查、厂家维修要求、梯具安全和现场确认。
候选保留范围、分页字段和“选择排序不等于汇报排序”的边界见
[维修可达候选审计契约](docs/maintenance-candidate-audit.md)。

## 构建

前置条件：

- Windows 10/11
- Autodesk Revit 2024（仓库不分发 Revit API）
- .NET Framework 4.8 Developer Pack
- Visual Studio 2022 或 Build Tools 2022
- .NET SDK 8（用于桥梁和纯逻辑测试）

```powershell
git clone https://github.com/Jarvi-Hou/jarvi-revit-tools.git
cd jarvi-revit-tools
.\scripts\build.ps1 -Configuration Release
.\scripts\test.ps1
.\tests\run-maintenance-ledger-tests.ps1
.\scripts\package-release.ps1 -Build
```

如果 Revit 安装在非标准目录：

```powershell
.\scripts\build.ps1 -RevitInstallDir 'D:\Path\To\Revit 2024'
```

也可设置环境变量 `REVIT_2024_INSTALL_DIR`。项目文件不包含任何开发机绝对路径。

## 安装与卸载

从源码安装时，先关闭 Revit，再执行：

```powershell
.\scripts\install.ps1 -Configuration Release -Build
```

使用公开二进制包时，先解压完整 ZIP、关闭 Revit，再在解压目录执行（不需要源码、Visual Studio 或 `-Build`）：

```powershell
.\scripts\install.ps1
```

同一个安装脚本会自动识别源码目录或二进制包的 `Plugin/`、`Bridge/`、`Resources/` 结构，把插件安装到当前 Windows 用户的 Revit 2024 Addins 目录，把 MCP Bridge 安装到 `%LOCALAPPDATA%\OpenRevit Tools\Bridge`，并根据实际 DLL 位置生成可直接加载的 `.addin` 清单。源码调试时可显式加 `-IncludeSymbols`；公开包不包含 PDB。

公开二进制包只允许使用 `package-release.ps1` 的白名单 staging；不要压缩工作目录，因为其中可能存在被 Git 忽略的客户模型、日志或旧构建产物。发布包包含可独立运行的安装/卸载脚本，并生成逐文件 SHA-256 清单 `manifest.sha256.json`。

卸载：

```powershell
.\scripts\uninstall.ps1
```

卸载不会删除项目模型或模型里已经生成的成果。

## 连接 MCP 客户端

构建后的 stdio 桥梁位于 `bridge/RevitMcpBridge/bin/Release/net48/RevitMcpBridge.exe`；安装脚本会复制到：

```text
%LOCALAPPDATA%\OpenRevit Tools\Bridge\RevitMcpBridge.exe
```

任意支持 stdio MCP 的客户端都可把该 EXE 配为 server command。使用步骤：

1. 打开 Revit 2024 与目标模型；
2. 点击 **OpenRevit → 启动 MCP**；
3. 启动/重载 MCP 客户端连接；
4. 先调用 `get_model_info` 确认当前文档和视图，再执行写入。

HTTP 仅监听 `127.0.0.1:7800`。每次启动自动生成 Windows 当前用户加密的随机令牌，桥梁自动读取；无须手抄密钥，也不接受远程 URL。

模型与链接的完整路径默认隐藏。`get_model_info`、`analyze_plenum_space` 和
`get_plenum_analysis_summary` 只有在明确传入 `includePath=true` 时才返回完整模型路径；
`get_link_status` 对应使用 `includePaths=true`。默认响应会返回空路径和 `pathIncluded=false`。

## 开发与质量

- 架构说明：[docs/architecture.md](docs/architecture.md)
- 台账同步协议：[docs/maintenance-ledger-sync.md](docs/maintenance-ledger-sync.md)
- 安全边界：[SECURITY.md](SECURITY.md)
- 贡献指南：[CONTRIBUTING.md](CONTRIBUTING.md)
- 第三方许可：[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)

CI 可在没有 Revit 的 GitHub Runner 上运行纯路径、Excel/CSV 与桥梁测试；完整插件编译和 Revit API 冒烟仍需装有合法 Revit 2024 的 Windows runner/工作站。

## 已知限制

- Revit 2024 专用；其他版本尚未建立多目标编译。
- 链接模型必须已加载，且其几何可被 Revit API 读取。
- Mesh、无 Solid、坐标变换或 Boolean 失败会保守标为 Unknown/待核查，而不是假定可用。
- `list_mep_elements`、`get_room_boundaries` 和 `find_untagged_rooms` 已建立统一分页契约；其他尚未分页的清单型 MCP 工具仍应优先按视图、类别、楼层或明确 ElementId 缩小范围。族文件体积测量会把最多 100 个可编辑族临时保存为 RFA 副本后测量，默认只测 50 个，Top N 只代表该批次。
- 碰撞报告的 critical/major/minor 只是基于精确相交体积的可配置启发式分组；包围框结果与缺少体积证据的结果始终标为 unverified，不是轻微碰撞结论。
- 维修可达的 700/600 mm 人体包络、900 mm 操作区等是本产品的工程判定规则，不是法定尺寸；规则应随项目台账一同交付。
- 公开仓库从经过隐私检查的单根提交建立；内部开发历史不属于公开发行内容。

## 许可

MIT License。见 [LICENSE](LICENSE)。Autodesk Revit/Revit API 不属于本仓库许可范围。
