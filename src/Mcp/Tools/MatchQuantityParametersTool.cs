using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using JarviTools.Core;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// MCP wrapper around MatchQuantityParametersCommand.
    /// 1) Ensures the 3 shared parameters (专业名称/分包类型/是否导出) are bound to model categories.
    /// 2) Assigns values to visible elements of the active 3D view based on a category->major/sub map.
    /// No TaskDialog / SaveFileDialog. Shared-param file path is fixed to <DllDir>\Resources\工程量统计共享参数.txt.
    /// </summary>
    public class MatchQuantityParametersTool : IRevitTool
    {
        public string Name => "match_quantity_parameters";

        public string Description =>
            "将 3 个工程量统计共享参数绑定到所有模型类别，并在活动 3D 视图中按 BuiltInCategory 设置值。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false
        };

        // Category -> (Major, Subcontractor) mapping. Same content as MatchQuantityParametersCommand.
        private static readonly Dictionary<BuiltInCategory, MajorSubPair> CategoryMapping =
            new Dictionary<BuiltInCategory, MajorSubPair>
        {
            { BuiltInCategory.OST_Walls,                new MajorSubPair(Constants.MAJOR_ARCHITECTURE, "墙体") },
            { BuiltInCategory.OST_CurtainWallPanels,    new MajorSubPair(Constants.MAJOR_ARCHITECTURE, "墙体") },
            { BuiltInCategory.OST_CurtainWallMullions,  new MajorSubPair(Constants.MAJOR_ARCHITECTURE, "墙体") },
            { BuiltInCategory.OST_Doors,                new MajorSubPair(Constants.MAJOR_ARCHITECTURE, "门窗") },
            { BuiltInCategory.OST_Windows,              new MajorSubPair(Constants.MAJOR_ARCHITECTURE, "门窗") },
            { BuiltInCategory.OST_Floors,               new MajorSubPair(Constants.MAJOR_ARCHITECTURE, "楼板") },
            { BuiltInCategory.OST_Roofs,                new MajorSubPair(Constants.MAJOR_ARCHITECTURE, "屋顶") },
            { BuiltInCategory.OST_Gutter,               new MajorSubPair(Constants.MAJOR_ARCHITECTURE, "屋顶") },
            { BuiltInCategory.OST_Stairs,               new MajorSubPair(Constants.MAJOR_ARCHITECTURE, "楼梯") },
            { BuiltInCategory.OST_StairsRailing,        new MajorSubPair(Constants.MAJOR_ARCHITECTURE, "栏杆") },
            { BuiltInCategory.OST_Ceilings,             new MajorSubPair(Constants.MAJOR_ARCHITECTURE, "天花板") },
            { BuiltInCategory.OST_Columns,              new MajorSubPair(Constants.MAJOR_ARCHITECTURE, "柱") },

            { BuiltInCategory.OST_StructuralColumns,    new MajorSubPair(Constants.MAJOR_STRUCTURE, "结构柱") },
            { BuiltInCategory.OST_StructuralFraming,    new MajorSubPair(Constants.MAJOR_STRUCTURE, "结构框架") },
            { BuiltInCategory.OST_StructuralFoundation, new MajorSubPair(Constants.MAJOR_STRUCTURE, "基础") },

            { BuiltInCategory.OST_PipeCurves,           new MajorSubPair(Constants.MAJOR_MEP, "管道") },
            { BuiltInCategory.OST_PipeFitting,          new MajorSubPair(Constants.MAJOR_MEP, "管道配件") },
            { BuiltInCategory.OST_PipeAccessory,        new MajorSubPair(Constants.MAJOR_MEP, "管道附件") },
            { BuiltInCategory.OST_DuctCurves,           new MajorSubPair(Constants.MAJOR_MEP, "风管") },
            { BuiltInCategory.OST_DuctFitting,          new MajorSubPair(Constants.MAJOR_MEP, "风管配件") },
            { BuiltInCategory.OST_DuctAccessory,        new MajorSubPair(Constants.MAJOR_MEP, "风管附件") },
            { BuiltInCategory.OST_CableTray,            new MajorSubPair(Constants.MAJOR_MEP, "电缆桥架") },
            { BuiltInCategory.OST_Conduit,              new MajorSubPair(Constants.MAJOR_MEP, "线管") },
            { BuiltInCategory.OST_MechanicalEquipment,  new MajorSubPair(Constants.MAJOR_MEP, "机械设备") },
            { BuiltInCategory.OST_ElectricalEquipment,  new MajorSubPair(Constants.MAJOR_MEP, "电气设备") },
            { BuiltInCategory.OST_PlumbingFixtures,     new MajorSubPair(Constants.MAJOR_MEP, "卫生器具") },

            { BuiltInCategory.OST_Furniture,            new MajorSubPair(Constants.MAJOR_INTERIOR, "家具") },
            { BuiltInCategory.OST_FurnitureSystems,     new MajorSubPair(Constants.MAJOR_INTERIOR, "家具系统") },
            { BuiltInCategory.OST_Casework,             new MajorSubPair(Constants.MAJOR_INTERIOR, "橱柜") },
            { BuiltInCategory.OST_SpecialityEquipment,  new MajorSubPair(Constants.MAJOR_INTERIOR, "专用设备") },

            { BuiltInCategory.OST_Topography,           new MajorSubPair(Constants.MAJOR_SITE, "地形") },
            { BuiltInCategory.OST_Parking,              new MajorSubPair(Constants.MAJOR_SITE, "停车位") },
            { BuiltInCategory.OST_Site,                 new MajorSubPair(Constants.MAJOR_SITE, "场地") },
            { BuiltInCategory.OST_Roads,                new MajorSubPair(Constants.MAJOR_SITE, "道路") },
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc   = uidoc.Document       ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            var activeView = doc.ActiveView ?? throw new InvalidOperationException("No active view.");
            if (!(activeView is View3D))
                throw new InvalidOperationException("请在 3D 视图中使用 (current view type: " + activeView.ViewType + ").");

            // Locate shared-param file: <DllDir>\Resources\工程量统计共享参数.txt
            string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string sharedParamFile = Path.Combine(dllDir, "Resources", Constants.SHARED_PARAM_FILE);
            if (!File.Exists(sharedParamFile))
                throw new FileNotFoundException("共享参数文件不存在：" + sharedParamFile, sharedParamFile);

            int sharedParamsAdded;
            int matchedCount = 0;
            int skippedCount = 0;
            var processedCategories = new HashSet<string>(StringComparer.Ordinal);

            // Single Transaction wrapping both binding and value-assignment.
            using (var tx = new Transaction(doc, "match_quantity_parameters"))
            {
                if (tx.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Could not start the quantity-parameter transaction.");
                try
                {
                    sharedParamsAdded = EnsureSharedParametersBound(doc, sharedParamFile);

                    // Collect visible elements of the active 3D view.
                    IList<Element> visibleElements;
                    using (var collector = new FilteredElementCollector(doc, activeView.Id))
                    {
                        visibleElements = collector.WhereElementIsNotElementType().ToElements();
                    }

                    foreach (var elem in visibleElements)
                    {
                        if (elem.Category == null) { skippedCount++; continue; }

                        var paramMajor  = elem.get_Parameter(Constants.GUID_MAJOR);
                        var paramSub    = elem.get_Parameter(Constants.GUID_SUBCONTRACTOR);
                        var paramExport = elem.get_Parameter(Constants.GUID_SHOULD_EXPORT);

                        if (paramMajor == null || paramSub == null || paramExport == null) { skippedCount++; continue; }
                        if (paramMajor.IsReadOnly || paramSub.IsReadOnly || paramExport.IsReadOnly) { skippedCount++; continue; }

                        BuiltInCategory bic = elem.Category.BuiltInCategory;
                        processedCategories.Add(elem.Category.Name);

                        if (CategoryMapping.TryGetValue(bic, out var pair))
                        {
                            if (!paramMajor.Set(pair.Major) ||
                                !paramSub.Set(pair.Sub) ||
                                !paramExport.Set(Constants.VALUE_YES))
                                throw new InvalidOperationException(
                                    "Could not write quantity parameters on element " + elem.Id.Value + ".");
                            matchedCount++;
                        }
                        else
                        {
                            if (!paramMajor.Set(Constants.VALUE_UNMATCHED) ||
                                !paramSub.Set(Constants.VALUE_UNMATCHED) ||
                                !paramExport.Set(Constants.VALUE_YES))
                                throw new InvalidOperationException(
                                    "Could not write unmatched quantity markers on element " + elem.Id.Value + ".");
                        }
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit did not commit the quantity-parameter transaction.");
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }

            var catArr = new JArray();
            foreach (var c in processedCategories.OrderBy(s => s)) catArr.Add(c);

            return new JObject
            {
                ["shared_params_added"]  = sharedParamsAdded,
                ["matched_count"]        = matchedCount,
                ["skipped_count"]        = skippedCount,
                ["categories_processed"] = catArr,
                ["shared_param_file"]    = sharedParamFile
            };
        }

        // ---- helpers ------------------------------------------------------

        /// <summary>
        /// Ensure the 3 shared params exist in the file and are bound to all model categories.
        /// Returns how many fresh bindings were added this call (0..3).
        /// </summary>
        private static int EnsureSharedParametersBound(Document doc, string sharedParamFile)
        {
            string originalPath = doc.Application.SharedParametersFilename;
            int added = 0;
            try
            {
                doc.Application.SharedParametersFilename = sharedParamFile;
                var defFile = doc.Application.OpenSharedParameterFile();
                if (defFile == null)
                    throw new InvalidOperationException("无法打开共享参数文件：" + sharedParamFile);

                DefinitionGroup group = null;
                foreach (DefinitionGroup g in defFile.Groups)
                {
                    if (g.Name == Constants.PARAM_GROUP_NAME) { group = g; break; }
                }
                if (group == null)
                    group = defFile.Groups.Create(Constants.PARAM_GROUP_NAME);

                if (BindParameter(doc, group, Constants.PARAM_MAJOR_NAME,    Constants.GUID_MAJOR,         defFile)) added++;
                if (BindParameter(doc, group, Constants.PARAM_SUBCONTRACTOR, Constants.GUID_SUBCONTRACTOR, defFile)) added++;
                if (BindParameter(doc, group, Constants.PARAM_SHOULD_EXPORT, Constants.GUID_SHOULD_EXPORT, defFile)) added++;
            }
            finally
            {
                doc.Application.SharedParametersFilename = originalPath;
            }
            return added;
        }

        /// <summary>
        /// Bind one shared parameter (by GUID) to all bindable model categories.
        /// Returns true if a new binding was inserted, false if already bound.
        /// </summary>
        private static bool BindParameter(Document doc, DefinitionGroup group, string paramName, Guid guid, DefinitionFile defFile)
        {
            // Look for an existing ExternalDefinition with this GUID anywhere in the file.
            Definition definition = null;
            foreach (DefinitionGroup g in defFile.Groups)
            {
                foreach (Definition def in g.Definitions)
                {
                    var extDef = def as ExternalDefinition;
                    if (extDef != null && extDef.GUID == guid) { definition = def; break; }
                }
                if (definition != null) break;
            }
            if (definition == null)
            {
                definition = group.Definitions.Create(
                    new ExternalDefinitionCreationOptions(paramName, SpecTypeId.String.Text) { GUID = guid });
            }

            // Already bound by GUID? Skip.
            BindingMap bindingMap = doc.ParameterBindings;
            DefinitionBindingMapIterator iter = bindingMap.ForwardIterator();
            while (iter.MoveNext())
            {
                var boundExt = iter.Key as ExternalDefinition;
                if (boundExt != null && boundExt.GUID == guid) return false;
            }

            var categories = doc.Application.Create.NewCategorySet();
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat.CategoryType == CategoryType.Model && cat.AllowsBoundParameters)
                    categories.Insert(cat);
            }
            if (!bindingMap.Insert(definition, new InstanceBinding(categories), GroupTypeId.IdentityData))
                throw new InvalidOperationException("Could not bind shared parameter '" + paramName + "'.");
            return true;
        }

        private class MajorSubPair
        {
            public string Major { get; }
            public string Sub   { get; }
            public MajorSubPair(string major, string sub) { Major = major; Sub = sub; }
        }
    }
}
