using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 创建剖面视图。通过 origin、direction、depth、width 定义剖切范围。
    /// 复杂度较高，是第四批中最复杂的工具。
    /// </summary>
    public class CreateSectionTool : IRevitTool
    {
        public string Name => "create_section";
        public string Description =>
            "创建新的剖面视图。使用右手坐标系。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["origin"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "number" },
                    ["minItems"] = 3,
                    ["maxItems"] = 3,
                    ["description"] = "Section line origin [x, y, z] in meters."
                },
                ["direction"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "number" },
                    ["minItems"] = 2,
                    ["maxItems"] = 2,
                    ["description"] = "Section direction [dx, dy] (2D, will be normalized)."
                },
                ["depth_m"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Section cut depth in meters (how far into the model)."
                },
                ["width_m"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Section width in meters (horizontal extents perpendicular to direction)."
                },
                ["name"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional name for the new section view."
                }
            },
            ["required"] = new JArray { "origin", "direction", "depth_m", "width_m" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            // 找 Section ViewFamilyType
            var vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.Section);
            if (vft == null)
                throw new InvalidOperationException("No Section ViewFamilyType found in the project.");

            // 解析 origin
            var originArr = (JArray)input["origin"];
            double ox = (double)originArr[0];
            double oy = (double)originArr[1];
            double oz = (double)originArr[2];

            // 解析 direction
            var dirArr = (JArray)input["direction"];
            double dx = (double)dirArr[0];
            double dy = (double)dirArr[1];
            var dirVec = new XYZ(dx, dy, 0).Normalize();

            // 规范化确保非零向量
            if (dirVec.GetLength() < 0.001)
                throw new ArgumentException("Direction vector is too close to zero.");

            double depthM = (double)input["depth_m"];
            double widthM = (double)input["width_m"];

            // 可选名称
            string desiredName = null;
            var nameToken = input["name"];
            if (nameToken != null && nameToken.Type != JTokenType.Null)
                desiredName = (string)nameToken;

            // 英尺单位
            double originXFt = ox / 0.3048;
            double originYFt = oy / 0.3048;
            double originZFt = oz / 0.3048;
            double depthFt = depthM / 0.3048;
            double widthFt = widthM / 0.3048;
            double heightFt = 3.0 / 0.3048; // 默认高度 3 米

            // 构造 Transform
            var upVec = XYZ.BasisZ;
            var rightVec = upVec.CrossProduct(dirVec).Normalize();

            var transform = Transform.Identity;
            transform.Origin = new XYZ(originXFt, originYFt, originZFt);
            transform.BasisX = rightVec;
            transform.BasisY = upVec;
            transform.BasisZ = dirVec;

            var bb = new BoundingBoxXYZ
            {
                Transform = transform,
                Min = new XYZ(-widthFt / 2, -heightFt / 2, 0),
                Max = new XYZ(widthFt / 2, heightFt / 2, depthFt)
            };

            ViewSection section;
            using (var tx = new Transaction(doc, "Create section view"))
            {
                tx.Start();
                try
                {
                    section = ViewSection.CreateSection(doc, vft.Id, bb);

                    if (!string.IsNullOrEmpty(desiredName))
                    {
                        string finalName = desiredName;
                        int suffix = 2;
                        while (ViewNameExists(doc, finalName))
                            finalName = desiredName + " (" + (suffix++) + ")";
                        section.Name = finalName;
                    }

                    JarviTools.Core.TransactionSafety.Commit(tx, "Create section");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    string inner = ex.InnerException?.Message;
                    string detail = string.IsNullOrEmpty(ex.Message)
                        ? ("ViewSection.CreateSection failed. " + (inner ?? "no details"))
                        : (ex.Message + (inner != null ? " | inner: " + inner : ""));
                    throw new InvalidOperationException(
                        "Failed to create section view: " + detail, ex);
                }
            }

            return new JObject
            {
                ["viewId"] = section.Id.Value,
                ["viewName"] = section.Name,
                ["sectionBox"] = new JObject
                {
                    ["origin"] = new JObject { ["x"] = ox, ["y"] = oy, ["z"] = oz },
                    ["direction"] = new JObject { ["dx"] = Math.Round(dirVec.X, 6), ["dy"] = Math.Round(dirVec.Y, 6) },
                    ["depth_m"] = depthM,
                    ["width_m"] = widthM
                }
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
