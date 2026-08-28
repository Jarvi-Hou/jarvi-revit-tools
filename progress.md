# 编译进度 — 2026-07-07 (第七批：机电检查面板)

## 状态：编译通过 ✅（0 errors）

### 新增 Ribbon 面板「机电检查」（2 个命令）
- **设备检查剖面** `EquipmentSectionCommand`：批量为机械/电气设备生成检查剖面。
  族朝向识别长短边 → 剖切线垂直长边过中心（看短边立面）→ 左右3m/上下2m/深度2m可配置 →
  自动命名"设备检查-类型-ID" → TransactionGroup 整批一次撤销。
- **净高分析** `ClearanceAnalysisCommand`：构件级净高检查。
  真实 Solid 面三角化取最低点（非包围盒）→ 净高 = 最低点 − 基准标高（主/对比双基准）→
  复制专用视图逐构件着色 + TextNote 图例 + 结果清单（排序/双击定位/CSV 导出）。
  支持整层/框选/当前选择三种范围，链接模型进清单（Revit 限制：链接构件不着色）。

### 新增文件
- `src/Commands/Common/`：JsonSettingsStore、GeometryUtil
- `src/Commands/EquipmentSection/`：SectionSettings、SectionSettingsForm、EquipmentSectionCommand
- `src/Commands/Clearance/`：ClearanceSettings、ClearanceSettingsForm、GridLocator、ProgressForm、
  ClearanceCalculator、ClearanceResultForm、ClearanceAnalysisCommand
- `Resources/icons/`：section_icon.png、clearance_icon.png（脚本生成）
- csproj 新增 `System.Drawing` 引用

### 匿名测试模型验证修复（客户与项目标识已移除）
- **剖面朝向反转**：原 BasisZ 取正向导致看设备背面。改 BasisZ=-outDir、剖切面 Min.Z=0，
  实测 11/11 台朝向正确（观察者站风管侧看向设备）
- **净高假最低值**：竖直立管贴地下端被当最低点。加 ExcludeRisers（默认开）过滤 <15° 近竖直
  线性风/水管，实测剔除 50 段。落地设备（卧式AHU等）是真实低点，不过滤
- **轴网定位**：机电宿主常只有零星轴网，主力在结构链接。GridLocator 改为读所有已载入链接轴网
- **execute_csharp**：移除并行会话加的 /langversion:7.3（内置 CodeDom 不支持，脚本全挂）

### 当日追加（用户反馈后）
- 剖面定位逻辑改为**风口连接件锚定**（VRV 族长短边对调也不受影响），无连接件退回几何兜底
- 新增第三个按钮 **设备三维检查** `Equipment3DViewCommand`：每台设备一个三维视图，
  沿风管连通网络追踪到末端风口，剖面框包住设备+风管，包裹距离可配（0=贴紧）
- 多代理审查修复 6 个问题（详见 git log 3c35b04），含 Transform.Identity 引用比较陷阱、
  ProjectElevation 基准、成对标高 Z 带压缩、DoEvents 重入防护等

### 编译中修复的问题
1. `Application.DoEvents` 与 `JarviTools.Application` 撞名 → 全限定 `System.Windows.Forms.Application.DoEvents()`

---

# 编译进度 — 2026-05-21 (第六批)

## 状态：编译通过 ✅（0 errors, 0 warnings）

### 总工具数：81（原有 73 + 新增 8）

---

## Phase 17 — MEP 查询（5 个只读）✅
- list_mep_elements ✓
- get_duct_parameters ✓
- get_pipe_parameters ✓
- get_element_connectivity ✓
- get_mep_system_info ✓

## Phase 18 — 碰撞检测（3 个只读）✅
- run_clash_detection ✓ (bbox/solid 双模式)
- get_clash_report ✓ (分级报告)
- highlight_clash ✓ (UI 高亮选中)

## 辅助类
- SolidHelper.cs ✓ (静态 GetMainSolid，供 P1 共享)

---

## 编译中修复的问题
1. `Duct` 类需要 `Autodesk.Revit.DB.Mechanical` 命名空间
2. `Pipe` 类需要 `Autodesk.Revit.DB.Plumbing` 命名空间
3. `MechanicalSystem`/`PipingSystem`/`ElectricalSystem` 分别需要 Mechanical/Plumbing/Electrical 命名空间
4. C# 7.3 中 `JObject` 和 `JValue` 不能出现在同一三元表达式两端 → 需要 `(JToken)` 显式转换

---

## Smoke 测试计划
启动 Revit + MCP Server 后：

### O 组
- `list_mep_elements` → 查看 MEP 元素分布（可能为空）
- 如有 duct: `get_duct_parameters ductId=?`
- 如有 pipe: `get_pipe_parameters pipeId=?`
- `get_element_connectivity elementId=?`（传有 connector 的元素）
- `get_mep_system_info` → 查看系统列表

### P 组
- `run_clash_detection categoryA=OST_Walls categoryB=OST_Columns mode=bbox`
- 拿 P1 输出的 clashes 喂 `get_clash_report`
- 拿 clash 的两个 id 喂 `highlight_clash`
