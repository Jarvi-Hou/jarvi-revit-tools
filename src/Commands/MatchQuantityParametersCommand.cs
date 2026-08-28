using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JarviTools.Core;
using JarviTools.Mcp.Server;

namespace JarviTools.Commands
{
    /// <summary>
    /// 命令：一键匹配计量参数
    /// 1. 向项目添加三个共享参数（专业名称、分包类型、是否导出）
    /// 2. 根据BuiltInCategory自动给当前视图可见构件赋值
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class MatchQuantityParametersCommand : IExternalCommand
    {
        // 类别映射表：BuiltInCategory -> 专业/分包
        // 注意：每个Key唯一，OST_Floors 统一归建筑楼板
        private static readonly Dictionary<BuiltInCategory, MajorSubcontractorPair> CategoryMapping =
            new Dictionary<BuiltInCategory, MajorSubcontractorPair>
        {
            // 建筑专业
            { BuiltInCategory.OST_Walls,                new MajorSubcontractorPair(Constants.MAJOR_ARCHITECTURE, "墙体") },
            { BuiltInCategory.OST_CurtainWallPanels,    new MajorSubcontractorPair(Constants.MAJOR_ARCHITECTURE, "墙体") },
            { BuiltInCategory.OST_CurtainWallMullions,  new MajorSubcontractorPair(Constants.MAJOR_ARCHITECTURE, "墙体") },
            { BuiltInCategory.OST_Doors,                new MajorSubcontractorPair(Constants.MAJOR_ARCHITECTURE, "门窗") },
            { BuiltInCategory.OST_Windows,              new MajorSubcontractorPair(Constants.MAJOR_ARCHITECTURE, "门窗") },
            { BuiltInCategory.OST_Floors,               new MajorSubcontractorPair(Constants.MAJOR_ARCHITECTURE, "楼板") },
            { BuiltInCategory.OST_Roofs,                new MajorSubcontractorPair(Constants.MAJOR_ARCHITECTURE, "屋顶") },
            { BuiltInCategory.OST_Gutter,               new MajorSubcontractorPair(Constants.MAJOR_ARCHITECTURE, "屋顶") },
            { BuiltInCategory.OST_Stairs,               new MajorSubcontractorPair(Constants.MAJOR_ARCHITECTURE, "楼梯") },
            { BuiltInCategory.OST_StairsRailing,        new MajorSubcontractorPair(Constants.MAJOR_ARCHITECTURE, "栏杆") },
            { BuiltInCategory.OST_Ceilings,             new MajorSubcontractorPair(Constants.MAJOR_ARCHITECTURE, "天花板") },
            { BuiltInCategory.OST_Columns,              new MajorSubcontractorPair(Constants.MAJOR_ARCHITECTURE, "柱") },

            // 结构专业
            { BuiltInCategory.OST_StructuralColumns,    new MajorSubcontractorPair(Constants.MAJOR_STRUCTURE, "结构柱") },
            { BuiltInCategory.OST_StructuralFraming,    new MajorSubcontractorPair(Constants.MAJOR_STRUCTURE, "结构框架") },
            { BuiltInCategory.OST_StructuralFoundation, new MajorSubcontractorPair(Constants.MAJOR_STRUCTURE, "基础") },

            // 机电专业
            { BuiltInCategory.OST_PipeCurves,           new MajorSubcontractorPair(Constants.MAJOR_MEP, "管道") },
            { BuiltInCategory.OST_PipeFitting,          new MajorSubcontractorPair(Constants.MAJOR_MEP, "管道配件") },
            { BuiltInCategory.OST_PipeAccessory,        new MajorSubcontractorPair(Constants.MAJOR_MEP, "管道附件") },
            { BuiltInCategory.OST_DuctCurves,           new MajorSubcontractorPair(Constants.MAJOR_MEP, "风管") },
            { BuiltInCategory.OST_DuctFitting,          new MajorSubcontractorPair(Constants.MAJOR_MEP, "风管配件") },
            { BuiltInCategory.OST_DuctAccessory,        new MajorSubcontractorPair(Constants.MAJOR_MEP, "风管附件") },
            { BuiltInCategory.OST_CableTray,            new MajorSubcontractorPair(Constants.MAJOR_MEP, "电缆桥架") },
            { BuiltInCategory.OST_Conduit,              new MajorSubcontractorPair(Constants.MAJOR_MEP, "线管") },
            { BuiltInCategory.OST_MechanicalEquipment,  new MajorSubcontractorPair(Constants.MAJOR_MEP, "机械设备") },
            { BuiltInCategory.OST_ElectricalEquipment,  new MajorSubcontractorPair(Constants.MAJOR_MEP, "电气设备") },
            { BuiltInCategory.OST_PlumbingFixtures,     new MajorSubcontractorPair(Constants.MAJOR_MEP, "卫生器具") },

            // 装饰专业
            { BuiltInCategory.OST_Furniture,            new MajorSubcontractorPair(Constants.MAJOR_INTERIOR, "家具") },
            { BuiltInCategory.OST_FurnitureSystems,     new MajorSubcontractorPair(Constants.MAJOR_INTERIOR, "家具系统") },
            { BuiltInCategory.OST_Casework,             new MajorSubcontractorPair(Constants.MAJOR_INTERIOR, "橱柜") },
            { BuiltInCategory.OST_SpecialityEquipment,  new MajorSubcontractorPair(Constants.MAJOR_INTERIOR, "专用设备") },

            // 场地专业
            { BuiltInCategory.OST_Topography,           new MajorSubcontractorPair(Constants.MAJOR_SITE, "地形") },
            { BuiltInCategory.OST_Parking,              new MajorSubcontractorPair(Constants.MAJOR_SITE, "停车位") },
            { BuiltInCategory.OST_Site,                 new MajorSubcontractorPair(Constants.MAJOR_SITE, "场地") },
            { BuiltInCategory.OST_Roads,                new MajorSubcontractorPair(Constants.MAJOR_SITE, "道路") },
        };

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show(Constants.PLUGIN_NAME, "请先打开一个Revit项目文件。");
                return Result.Cancelled;
            }

            try
            {
                Document doc = uidoc.Document;
                View activeView = doc.ActiveView;

                // 1. 必须在3D视图中运行（uidoc.ActiveView/doc.ActiveView 在某些上下文可能为 null）
                if (activeView == null)
                {
                    TaskDialog.Show("提示", "没有活动视图，请先打开一个 3D 视图。");
                    return Result.Cancelled;
                }
                if (!(activeView is View3D))
                {
                    TaskDialog.Show("提示", "请在3D视图中运行此命令！\n\n当前视图类型：" + activeView.ViewType.ToString());
                    return Result.Cancelled;
                }

                // 2. 验证共享参数文件存在
                // 文件存放在 Resources 子文件夹（与DLL同级的 Resources 目录）
                string sharedParamFile = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    "Resources",
                    Constants.SHARED_PARAM_FILE
                );

                if (!File.Exists(sharedParamFile))
                {
                    TaskDialog.Show("错误", "共享参数文件不存在！\n\n期望路径：\n" + sharedParamFile
                        + "\n\n请确认 Resources 文件夹已复制到 DLL 所在目录。");
                    return Result.Failed;
                }

                // 参数绑定和参数赋值必须作为一个原子操作提交，避免只绑定不赋值的半成品状态。
                using (TransactionGroup group = new TransactionGroup(doc, "添加并匹配计量参数"))
                {
                    if (group.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException("无法启动计量参数事务组。");

                    try
                    {
                        using (Transaction transBind = new Transaction(doc, "添加共享参数绑定"))
                        {
                            if (transBind.Start() != TransactionStatus.Started)
                                throw new InvalidOperationException("无法启动共享参数绑定事务。");
                            AddSharedParameters(doc, sharedParamFile);
                            if (transBind.Commit() != TransactionStatus.Committed)
                                throw new InvalidOperationException("共享参数绑定未提交成功。");
                        }

                        int matchedCount = MatchParameters(doc, activeView);
                        if (group.Assimilate() != TransactionStatus.Committed)
                            throw new InvalidOperationException("计量参数事务组未提交成功。");

                        TaskDialog.Show("完成", string.Format(
                            "参数匹配完成！\n\n已匹配构件：{0} 个\n未匹配构件已标记为\"{1}\"，可使用\"筛选未匹配构件\"命令查找。",
                            matchedCount, Constants.VALUE_UNMATCHED));
                    }
                    catch
                    {
                        if (group.HasStarted()) group.RollBack();
                        throw;
                    }
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.Error("MatchQuantityParametersCommand failed", ex);
                TaskDialog.Show("错误", "执行失败：\n" + ex.Message);
                return Result.Failed;
            }
        }

        /// <summary>
        /// 向项目添加三个共享参数（调用前需开启事务）
        /// 执行完成后恢复原共享参数文件路径，不干扰用户
        /// </summary>
        private void AddSharedParameters(Document doc, string sharedParamFile)
        {
            string originalPath = doc.Application.SharedParametersFilename;
            try
            {
                doc.Application.SharedParametersFilename = sharedParamFile;
                DefinitionFile defFile = doc.Application.OpenSharedParameterFile();
                if (defFile == null)
                    throw new Exception("无法打开共享参数文件：" + sharedParamFile);

                // 注意：DefinitionGroups.get_Item 找不到时抛 ArgumentException 而非返回 null，
                // 所以 ?? 永远走不到。改成显式遍历查找。
                DefinitionGroup group = null;
                foreach (DefinitionGroup g in defFile.Groups)
                {
                    if (g.Name == Constants.PARAM_GROUP_NAME) { group = g; break; }
                }
                if (group == null)
                    group = defFile.Groups.Create(Constants.PARAM_GROUP_NAME);

                AddParameterToProject(doc, group, Constants.PARAM_MAJOR_NAME,    Constants.GUID_MAJOR,         defFile);
                AddParameterToProject(doc, group, Constants.PARAM_SUBCONTRACTOR, Constants.GUID_SUBCONTRACTOR,  defFile);
                AddParameterToProject(doc, group, Constants.PARAM_SHOULD_EXPORT, Constants.GUID_SHOULD_EXPORT,  defFile);
            }
            finally
            {
                // 无论是否出错，都恢复原来的路径
                doc.Application.SharedParametersFilename = originalPath;
            }
        }

        /// <summary>
        /// 将单个共享参数绑定到项目所有模型类别（如已绑定则跳过）
        /// </summary>

        private void AddParameterToProject(Document doc, DefinitionGroup group, string paramName, Guid guid, DefinitionFile defFile)
        {
            // 先在整个共享参数文件的所有组里查找该GUID
            Definition definition = null;
            foreach (DefinitionGroup g in defFile.Groups)
            {
                foreach (Definition def in g.Definitions)
                {
                    ExternalDefinition extDef = def as ExternalDefinition;
                    if (extDef != null && extDef.GUID == guid)
                    {
                        definition = def;
                        break;
                    }
                }
                if (definition != null) break;
            }

            // 没找到才创建
            if (definition == null)
            {
                definition = group.Definitions.Create(
                    new ExternalDefinitionCreationOptions(paramName, SpecTypeId.String.Text) { GUID = guid });
            }

            // 检查是否已绑定 —— 用 GUID 而非 Name 比对。
            // 同名但不同 GUID 的参数（用户其它插件已经创建过）不该被当成"已绑定"，
            // 否则后续 LookupParameter(paramName) 会拿到错的参数，赋值看似成功实则写错地方。
            BindingMap bindingMap = doc.ParameterBindings;
            DefinitionBindingMapIterator iter = bindingMap.ForwardIterator();
            while (iter.MoveNext())
            {
                ExternalDefinition boundExt = iter.Key as ExternalDefinition;
                if (boundExt != null && boundExt.GUID == guid) return;
            }

            // 绑定到所有模型类别
            CategorySet categories = doc.Application.Create.NewCategorySet();
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat.CategoryType == CategoryType.Model && cat.AllowsBoundParameters)
                    categories.Insert(cat);
            }
            if (!bindingMap.Insert(definition, new InstanceBinding(categories), GroupTypeId.IdentityData))
                throw new InvalidOperationException("无法绑定共享参数：" + paramName);
        }

        /// <summary>
        /// 遍历当前视图可见构件，根据类别映射表赋值参数
        /// </summary>
        private int MatchParameters(Document doc, View activeView)
        {
            int matchedCount = 0;

            IList<Element> visibleElements;
            using (FilteredElementCollector collector = new FilteredElementCollector(doc, activeView.Id))
            {
                visibleElements = collector.WhereElementIsNotElementType().ToElements();
            }

            using (Transaction trans = new Transaction(doc, "匹配计量参数"))
            {
                trans.Start();

                foreach (Element elem in visibleElements)
                {
                    if (elem.Category == null) continue;

                    Parameter paramMajor  = elem.get_Parameter(Constants.GUID_MAJOR);
                    Parameter paramSub    = elem.get_Parameter(Constants.GUID_SUBCONTRACTOR);
                    Parameter paramExport = elem.get_Parameter(Constants.GUID_SHOULD_EXPORT);

                    if (paramMajor == null || paramSub == null || paramExport == null) continue;
                    if (paramMajor.IsReadOnly || paramSub.IsReadOnly || paramExport.IsReadOnly) continue;

                    // Revit 2024: 直接使用 Category.BuiltInCategory，避免 ElementId.Value cast 的精度风险
                    BuiltInCategory bic = elem.Category.BuiltInCategory;

                    if (CategoryMapping.ContainsKey(bic))
                    {
                        MajorSubcontractorPair pair = CategoryMapping[bic];
                        if (!paramMajor.Set(pair.MajorName) ||
                            !paramSub.Set(pair.SubcontractorType) ||
                            !paramExport.Set(Constants.VALUE_YES))
                            throw new InvalidOperationException("图元 " + elem.Id.Value + " 的计量参数写入失败。");
                        matchedCount++;
                    }
                    else
                    {
                        if (!paramMajor.Set(Constants.VALUE_UNMATCHED) ||
                            !paramSub.Set(Constants.VALUE_UNMATCHED) ||
                            !paramExport.Set(Constants.VALUE_YES))
                            throw new InvalidOperationException("图元 " + elem.Id.Value + " 的未匹配标记写入失败。");
                    }
                }

                if (trans.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("计量参数赋值未提交成功。");
            }

            return matchedCount;
        }

        private class MajorSubcontractorPair
        {
            public string MajorName { get; private set; }
            public string SubcontractorType { get; private set; }
            public MajorSubcontractorPair(string major, string sub) { MajorName = major; SubcontractorType = sub; }
        }
    }
}
