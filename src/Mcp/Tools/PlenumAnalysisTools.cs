using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JarviTools.Commands.Plenum;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    public class AnalyzePlenumSpaceTool : IRevitTool
    {
        public string Name => "analyze_plenum_space";
        public string Description =>
            "对当前三维视图中的单块吊顶计算吊顶连通负空间场，纳入已加载宿主/链接结构与机电实体，可生成三维色块。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["ceilingElementId"] = Number("可选：宿主装饰模型中的吊顶 ElementId。未填时使用唯一选中/唯一可见吊顶。"),
                ["baseCellMm"] = Number("基础分析单元，默认 200 mm。"),
                ["featureCellMm"] = Number("机电特征附近加密单元，默认 40 mm。"),
                ["featureSpacingMm"] = Number("沿管线特征的探针间距，默认 20 mm。"),
                ["searchHeightMm"] = Number("无三维剖面框时的搜索高度，默认 3000 mm。"),
                ["maxCells"] = Number("最大单元数，默认 25000；超大或高密度吊顶可显式调整质量参数后重试。"),
                ["includePath"] = new JObject
                {
                    ["type"] = "boolean",
                    ["description"] = "是否在摘要中返回完整模型路径，默认 false。路径可能包含客户或工作站信息。",
                    ["default"] = false
                },
                ["show"] = new JObject
                {
                    ["type"] = "boolean",
                    ["description"] = "是否在原分析三维视图生成可清除的模型级 DirectShape 色块，默认 false。",
                    ["default"] = false
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null || uiapp.ActiveUIDocument == null)
                throw new InvalidOperationException("Revit 没有活动文档。");
            if (!(uiapp.ActiveUIDocument.Document.ActiveView is View3D))
                throw new InvalidOperationException("V1 请在目标三维视图中运行。");

            input = input ?? new JObject();
            var config = new PlenumAnalysisConfig();
            ApplyDouble(input, "baseCellMm", x => config.BaseCellMm = x);
            ApplyDouble(input, "featureCellMm", x => config.FeatureCellMm = x);
            ApplyDouble(input, "featureSpacingMm", x => config.FeatureSpacingMm = x);
            ApplyDouble(input, "searchHeightMm", x => config.SearchHeightMm = x);
            ApplyInt(input, "maxCells", x => config.MaxCells = x);
            config.ShowVisualization = ReadBool(input, "show", false);

            long? ceilingId = null;
            JToken idToken = input["ceilingElementId"];
            if (idToken != null && idToken.Type != JTokenType.Null)
                ceilingId = idToken.Value<long>();

            Element ceiling = PlenumAnalysisService.ResolveCeiling(uiapp.ActiveUIDocument, ceilingId);
            PlenumAnalysisResult result = PlenumAnalysisService.Analyze(uiapp, ceiling, config);
            PlenumAnalysisStore.Set(uiapp.ActiveUIDocument.Document, result);
            if (config.ShowVisualization) PlenumVisualizationService.Show(uiapp, result);
            return result.ToSummaryJson(ReadBool(input, "includePath", false));
        }

        private static JObject Number(string description)
        {
            return new JObject { ["type"] = "number", ["description"] = description };
        }

        private static void ApplyDouble(JObject input, string name, Action<double> setter)
        {
            JToken token = input[name];
            if (token != null && token.Type != JTokenType.Null) setter(token.Value<double>());
        }

        private static void ApplyInt(JObject input, string name, Action<int> setter)
        {
            JToken token = input[name];
            if (token != null && token.Type != JTokenType.Null) setter(checked(token.Value<int>()));
        }

        private static bool ReadBool(JObject input, string name, bool fallback)
        {
            JToken token = input[name];
            return token == null || token.Type == JTokenType.Null ? fallback : token.Value<bool>();
        }
    }

    public class GetPlenumAnalysisSummaryTool : IRevitTool
    {
        public string Name => "get_plenum_analysis_summary";
        public string Description =>
            "返回最近一次负空间分析的净空分布、Unknown、主要阻挡构件和可追溯性统计。完整模型路径默认隐藏。";
        public JObject InputSchema => AnalysisIdSchema(true);

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            PlenumAnalysisResult result = RequireLast(uiapp, ReadExpectedAnalysisId(input));
            bool includePath = input != null && ((bool?)input["includePath"]).GetValueOrDefault();
            return result.ToSummaryJson(includePath);
        }

        internal static JObject EmptySchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject(),
                ["additionalProperties"] = false
            };
        }

        internal static JObject AnalysisIdSchema(bool includePathOption = false)
        {
            var properties = new JObject
            {
                ["analysisId"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "可选：期望查询的 analysisId，用于防止误读后续重算快照。"
                }
            };
            if (includePathOption)
            {
                properties["includePath"] = new JObject
                {
                    ["type"] = "boolean",
                    ["description"] = "是否返回完整模型路径，默认 false。路径可能包含客户或工作站信息。",
                    ["default"] = false
                };
            }

            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false
            };
        }

        internal static PlenumAnalysisResult RequireLast(UIApplication uiapp, string expectedAnalysisId)
        {
            if (uiapp == null || uiapp.ActiveUIDocument == null)
                throw new InvalidOperationException("Revit 没有活动文档。");
            Document doc = uiapp.ActiveUIDocument.Document;
            PlenumAnalysisResult result = PlenumAnalysisStore.Get(doc);
            if (result == null)
                throw new InvalidOperationException("当前进程还没有负空间分析结果，请先调用 analyze_plenum_space。");
            if (!string.IsNullOrWhiteSpace(expectedAnalysisId)
                && !string.Equals(result.AnalysisId, expectedAnalysisId, StringComparison.Ordinal))
                throw new InvalidOperationException("当前最新 analysisId 与请求不同，请先重新读取 summary。");
            return result;
        }

        internal static string ReadExpectedAnalysisId(JObject input)
        {
            JToken token = input == null ? null : input["analysisId"];
            return token == null || token.Type == JTokenType.Null ? null : token.Value<string>();
        }
    }

    public class QueryPlenumRegionsTool : IRevitTool
    {
        public string Name => "query_plenum_regions";
        public string Description =>
            "查询负空间单元：可按吊顶连通净空上/下限筛选，或只查 Unknown；返回坐标、足迹、探针剖面、MixedAtLeaf、分辨率与阻挡来源。MixedAtLeaf 不得当作均质自由体块。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["maxHeightMm"] = Num("吊顶连通净空上限。"),
                ["minHeightMm"] = Num("吊顶连通净空下限。"),
                ["analysisId"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "可选：期望查询的 analysisId。"
                },
                ["unknownOnly"] = new JObject
                {
                    ["type"] = "boolean",
                    ["description"] = "只返回 Unknown 单元，默认 false。",
                    ["default"] = false
                },
                ["limit"] = new JObject
                {
                    ["type"] = "integer",
                    ["description"] = "最多返回的单元数，1–1000，默认 100。",
                    ["default"] = 100
                },
                ["offset"] = new JObject
                {
                    ["type"] = "integer",
                    ["description"] = "分页偏移，默认 0。",
                    ["default"] = 0
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            input = input ?? new JObject();
            PlenumAnalysisResult result = GetPlenumAnalysisSummaryTool.RequireLast(
                uiapp, GetPlenumAnalysisSummaryTool.ReadExpectedAnalysisId(input));
            double? max = OptionalDouble(input, "maxHeightMm");
            double? min = OptionalDouble(input, "minHeightMm");
            bool unknownOnly = OptionalBool(input, "unknownOnly", false);
            int limit = input["limit"] == null ? 100 : input["limit"].Value<int>();
            int offset = input["offset"] == null ? 0 : input["offset"].Value<int>();
            if (limit < 1 || limit > 1000)
                throw new ArgumentOutOfRangeException("limit", "limit 必须在 1–1000 之间。");
            if (offset < 0)
                throw new ArgumentOutOfRangeException("offset", "offset 不能小于 0。");
            if (min.HasValue && max.HasValue && min.Value > max.Value)
                throw new ArgumentException("minHeightMm 不能大于 maxHeightMm。");
            return result.Query(max, min, unknownOnly, offset, limit);
        }

        private static JObject Num(string description)
        {
            return new JObject { ["type"] = "number", ["description"] = description };
        }

        private static double? OptionalDouble(JObject input, string name)
        {
            JToken token = input[name];
            return token == null || token.Type == JTokenType.Null ? (double?)null : token.Value<double>();
        }

        private static bool OptionalBool(JObject input, string name, bool fallback)
        {
            JToken token = input[name];
            return token == null || token.Type == JTokenType.Null ? fallback : token.Value<bool>();
        }
    }

    public class ShowPlenumAnalysisTool : IRevitTool
    {
        public string Name => "show_plenum_analysis";
        public string Description => "将最近一次负空间分析重新显示为当前三维视图中的分级透明色块。";
        public JObject InputSchema => GetPlenumAnalysisSummaryTool.AnalysisIdSchema();

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            PlenumAnalysisResult result = GetPlenumAnalysisSummaryTool.RequireLast(
                uiapp, GetPlenumAnalysisSummaryTool.ReadExpectedAnalysisId(input));
            PlenumVisualizationStats stats = PlenumVisualizationService.Show(uiapp, result);
            return new JObject
            {
                ["analysisId"] = result.AnalysisId,
                ["createdElementCount"] = stats.CreatedElementCount,
                ["renderedCellCount"] = stats.RenderedCellCount,
                ["renderedFreeSegmentCount"] = stats.RenderedFreeSegmentCount,
                ["skippedBoundaryCellCount"] = stats.SkippedBoundaryCellCount,
                ["failedGeometryCellCount"] = stats.FailedGeometryCellCount,
                ["deletedPreviousElementCount"] = stats.DeletedPreviousElementCount,
                ["targetViewId"] = stats.TargetViewId,
                ["targetViewName"] = stats.TargetViewName,
                ["modelWriteStatement"] = "DirectShape 已写入宿主模型，可撤销或用 clear_plenum_analysis 删除。"
            };
        }
    }

    public class ClearPlenumAnalysisTool : IRevitTool
    {
        public string Name => "clear_plenum_analysis";
        public string Description => "仅删除 OpenRevit Tools 生成的负空间 DirectShape，可选同时清空内存分析结果。";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["clearStoredAnalysis"] = new JObject
                {
                    ["type"] = "boolean",
                    ["description"] = "是否同时清空最近一次分析，默认 true。",
                    ["default"] = true
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            input = input ?? new JObject();
            bool clearStored = input["clearStoredAnalysis"] == null
                ? true
                : input["clearStoredAnalysis"].Value<bool>();
            int deleted = PlenumVisualizationService.Clear(uiapp, clearStored);
            return new JObject
            {
                ["deletedDirectShapeCount"] = deleted,
                ["storedAnalysisCleared"] = clearStored
            };
        }
    }
}
