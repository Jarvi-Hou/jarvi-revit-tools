using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 创建新的 3D 视图（等轴或透视）。
    /// Transaction 包裹。
    /// </summary>
    public class Create3DViewTool : IRevitTool
    {
        public string Name => "create_3d_view";
        public string Description =>
            "创建新的 3D 视图。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["name"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional name for the new 3D view."
                },
                ["isPerspective"] = new JObject
                {
                    ["type"] = "boolean",
                    ["description"] = "If true, create a perspective view. Default false (isometric).",
                    ["default"] = false
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            // 解析参数
            string desiredName = null;
            var nameToken = input?["name"];
            if (nameToken != null && nameToken.Type != JTokenType.Null)
                desiredName = (string)nameToken;

            bool isPerspective = false;
            var perspToken = input?["isPerspective"];
            if (perspToken != null && perspToken.Type != JTokenType.Null)
                isPerspective = (bool)perspToken;

            // 找 3D ViewFamilyType
            var vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);

            if (vft == null)
                throw new InvalidOperationException("No 3D ViewFamilyType found in the project.");

            View3D newView;
            using (var tx = new Transaction(doc, "Create 3D view"))
            {
                tx.Start();
                try
                {
                    newView = isPerspective
                        ? View3D.CreatePerspective(doc, vft.Id)
                        : View3D.CreateIsometric(doc, vft.Id);

                    // 处理命名
                    if (!string.IsNullOrEmpty(desiredName))
                    {
                        string finalName = desiredName;
                        int suffix = 2;
                        while (ViewNameExists(doc, finalName))
                            finalName = desiredName + " (" + (suffix++) + ")";
                        newView.Name = finalName;
                    }

                    JarviTools.Core.TransactionSafety.Commit(tx, "Create 3D view");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw new InvalidOperationException(
                        "Failed to create 3D view: " + ex.Message, ex);
                }
            }

            return new JObject
            {
                ["viewId"] = newView.Id.Value,
                ["viewName"] = newView.Name,
                ["isPerspective"] = isPerspective
            };
        }

        private static bool ViewNameExists(Document doc, string name)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Any(v => string.Equals(v.Name, name, StringComparison.Ordinal));
        }
    }
}
