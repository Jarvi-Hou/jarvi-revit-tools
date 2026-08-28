# 维修可达台账同步桥接

## 为什么不在插件里直接改 Excel

Revit 模型与它的 8 个 `CODEX_`实例参数是事实源；项目 XLSX 可以有自己的版式、公式、人工确认列与其他项目特有内容。插件如果直接操作 XLSX，会引入易碎的 Excel 依赖，也容易破坏用户格式。

因此正式链路是：

1. Revit 插件读取两类正式 DirectShape：`ApplicationId=JarviTools.MaintenanceReachability.v1`（墙门完整维修）与 `ApplicationId=JarviTools.MaintenanceHandReach.v1`（天花 400×400 伸手检修）；两者都写入同一份台账，以 `ApplicationDataId` 区分证据来源。
2. MCP 工具 `sync_maintenance_ledger_bridge` 输出稳定 JSON/CSV 快照。
3. AI 先校验 manifest 中的模型指纹、行数与 SHA-256，再把快照映射到项目 XLSX 的“用户台账”和“CODEX 证据”子表。
4. AI 更新时按稳定行键 upsert，保留 XLSX 中的人工确认、备注、公式与格式。

## MCP 调用

工具：`sync_maintenance_ledger_bridge`

输入：

- `outputDirectory`：必填，已存在的绝对文件夹。
- `filePrefix`：可选，默认 `maintenance-ledger`。必须是单一文件名，不能带路径。
- `expectedModelFingerprint`：可选安全锁。与当前 Revit 模型不一致时拒绝写文件，防止串项目。
- `dryRun`：可选。为 `true` 时只返回统计和将要生成的路径。

输出文件：

- `<prefix>.user.csv`：普通用户级数据，一个“设备维修区”一行，并带出 AI 复核人、复核说明、时间和分析证据指纹。
- `<prefix>.codex.csv`：完整证据，每个 CODEX 所有的 DirectShape 一行，包含 8 个参数、DirectShape ID/UniqueId/ApplicationDataId、包围框与内部复核追溯信息。
- `<prefix>.manifest.json`：该次快照的提交标记，含生成 UTC 时间、模型指纹、路径哈希、行数、文件哈希、参数合约和警告。

AI 必须把 manifest 当作最后提交标记：两个 CSV 的 SHA-256 或行数与 manifest 不一致时，不得更新 XLSX。

## 完整的 Revit 参数合约

1. `CODEX_构件名称`
2. `CODEX_天花分组`
3. `CODEX_入口组`
4. `CODEX_构件角色`
5. `CODEX_维修对象`
6. `CODEX_维修结论`
7. `CODEX_判断说明`
8. `CODEX_专业备注`

其中 `CODEX_维修结论` 和 `CODEX_专业备注` 是 Revit 中可编辑的专业确认字段。重新同步不得用 AI 猜测覆盖这两个字段。

## 人工数据保护与幂等性

- CSV 是每次完整快照替换，不会追加出重复行。
- 行键优先使用 DirectShape `ApplicationDataId`。如果缺失，本次会降级为图元 ID 并在 manifest 报警。
- `ApplicationDataId` 包含当次分析证据指纹；只有相关源图元版本未变时，上次的黄色人工结论才可能继承。
- 桥接用户 CSV 中的 `台账人工确认` 和 `台账人工备注` 在下次生成时按行键保留。
- 如果旧 CSV 无法解析、缺少人工列，或同一行键有冲突的人工值，同步会停止，不会覆盖旧文件。
- 每个输出文件先写同目录临时文件，再原子替换；manifest 最后写入。

## AI 更新 XLSX 的最小规则

1. 先确认 manifest 的 `model.fingerprint` 属于目标项目。
2. 重新计算两个 CSV 哈希，必须与 manifest 一致。
3. “用户台账”按 `行键` upsert，不按当前 Excel 行号匹配。
4. “CODEX 证据”按 `证据行键` 重建当前快照，旧证据如需留档应转入历史页，不得混在当前快照。
5. 保留 XLSX 中非 Revit 事实源的人工列、公式、数据验证与格式。
6. 更新完成后，把 manifest 的 `generatedAtUtc`、`modelFingerprint` 与 `snapshotHashSha256` 记入 CODEX 证据页。

## 当前边界

- 桥接导出 Revit 的 8 个用户参数，以及插件写入 DirectShape Extensible Storage 的 AI 复核追溯信息。链接阻挡图元的更深系统和几何证据仍需 AI 通过 MCP 核查并补入 XLSX 的 CODEX 证据页。
- 修改项目 XLSX 是 AI 层有意义的外部写操作，不由这个 Revit 只读工具隐式完成。
- 模型指纹用于防止串项目；证据快照哈希用于判断维修结果是否变化，两者不可互相替代。

## 验证

纯 CSV 测试（不需启动 Revit）：

```powershell
.\tests\run-maintenance-ledger-tests.ps1
```

测试覆盖中文、逗号、引号、跨行备注、UTF-8 BOM、快照替换不累加旧行，以及非法 CSV 拒绝。
