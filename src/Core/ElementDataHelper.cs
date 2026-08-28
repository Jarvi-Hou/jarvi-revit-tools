using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using JarviTools.Mcp.Server;

namespace JarviTools.Core
{
    public static class ElementDataHelper
    {
        public static string GetFamilyName(Element elem)
        {
            try
            {
                if (elem is FamilyInstance)
                {
                    FamilyInstance fi = (FamilyInstance)elem;
                    if (fi.Symbol != null && fi.Symbol.Family != null)
                        return fi.Symbol.Family.Name ?? Constants.DEFAULT_VALUE;
                }
                ElementId typeId = elem.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    ElementType elemType = elem.Document.GetElement(typeId) as ElementType;
                    if (elemType != null)
                        return elemType.FamilyName ?? Constants.DEFAULT_VALUE;
                }
                return Constants.DEFAULT_VALUE;
            }
            catch (Exception ex)
            {
                Logger.Warn("GetFamilyName failed: " + ex.Message);
                return Constants.DEFAULT_VALUE;
            }
        }

        public static string GetTypeName(Element elem)
        {
            try
            {
                if (elem is FamilyInstance)
                {
                    FamilyInstance fi = (FamilyInstance)elem;
                    if (fi.Symbol != null)
                        return fi.Symbol.Name ?? Constants.DEFAULT_VALUE;
                }
                ElementId typeId = elem.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    ElementType elemType = elem.Document.GetElement(typeId) as ElementType;
                    if (elemType != null)
                        return elemType.Name ?? Constants.DEFAULT_VALUE;
                }
                return Constants.DEFAULT_VALUE;
            }
            catch (Exception ex)
            {
                Logger.Warn("GetTypeName failed: " + ex.Message);
                return Constants.DEFAULT_VALUE;
            }
        }

        public static string GetLevelName(Element elem)
        {
            try
            {
                if (elem.LevelId != null && elem.LevelId != ElementId.InvalidElementId)
                {
                    Level level = elem.Document.GetElement(elem.LevelId) as Level;
                    if (level != null) return level.Name;
                }
                string[] paramNames = new string[] { "参照标高", "标高", "Level", "Reference Level" };
                foreach (string pn in paramNames)
                {
                    Parameter p = elem.LookupParameter(pn);
                    if (p != null && p.HasValue && p.StorageType == StorageType.ElementId)
                    {
                        ElementId levelId = p.AsElementId();
                        if (levelId != null && levelId != ElementId.InvalidElementId)
                        {
                            Level level = elem.Document.GetElement(levelId) as Level;
                            if (level != null) return level.Name;
                        }
                    }
                }
                return Constants.DEFAULT_VALUE;
            }
            catch (Exception ex)
            {
                Logger.Warn("GetLevelName failed: " + ex.Message);
                return Constants.DEFAULT_VALUE;
            }
        }

        public static string GetParameterValue(Parameter param)
        {
            if (param == null || !param.HasValue) return Constants.DEFAULT_VALUE;
            try
            {
                switch (param.StorageType)
                {
                    case StorageType.Double:
                        double v = param.AsDouble();
                        string metricUnit = GetMetricUnit(param.Definition == null ? null : param.Definition.GetDataType());
                        if (metricUnit != null)
                        {
                            double converted = ConvertFromInternalUnits(v, metricUnit);
                            // 用机器级 epsilon 区分"真零"和"小数值"，
                            // 之前用 0.0001 一刀切会把 0.05 m 的小构件显示成 "-" 而丢数据。
                            return Math.Abs(converted) < 1e-9
                                ? "0.000"
                                : converted.ToString("F3", CultureInfo.InvariantCulture);
                        }
                        string formatted = param.AsValueString();
                        return string.IsNullOrWhiteSpace(formatted)
                            ? v.ToString("G17", CultureInfo.InvariantCulture)
                            : formatted;
                    case StorageType.Integer:
                        return param.AsInteger().ToString();
                    case StorageType.String:
                        if (!param.HasValue) return Constants.DEFAULT_VALUE;
                        string s = param.AsString();
                        return string.IsNullOrEmpty(s) ? Constants.DEFAULT_VALUE : s;
                    case StorageType.ElementId:
                        ElementId eid = param.AsElementId();
                        if (eid == null || eid == ElementId.InvalidElementId)
                            return Constants.DEFAULT_VALUE;
                        // param.Element / param.Element.Document 在某些极端情况下可能为 null
                        if (param.Element == null || param.Element.Document == null)
                            return Constants.DEFAULT_VALUE;
                        try
                        {
                            Element refElem = param.Element.Document.GetElement(eid);
                            return (refElem != null) ? refElem.Name : eid.Value.ToString();
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn("GetParameterValue (ElementId resolve) failed: " + ex.Message);
                            return eid.Value.ToString();
                        }
                    default:
                        return Constants.DEFAULT_VALUE;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("GetParameterValue failed: " + ex.Message);
                return Constants.DEFAULT_VALUE;
            }
        }

        /// <summary>
        /// 单位换算：英尺 → 目标单位。优先用 Revit 2024 标准 API (UnitTypeId)，
        /// 失败兜底回旧的常量乘法。
        /// </summary>
        private static string GetMetricUnit(ForgeTypeId dataType)
        {
            if (dataType == null) return null;
            if (dataType.Equals(SpecTypeId.Length)) return "m";
            if (dataType.Equals(SpecTypeId.Area)) return "m²";
            if (dataType.Equals(SpecTypeId.Volume)) return "m³";
            return null;
        }

        private static string GetExportParameterName(Parameter parameter)
        {
            if (parameter == null || parameter.Definition == null) return string.Empty;
            string name = parameter.Definition.Name ?? string.Empty;
            string unit = GetMetricUnit(parameter.Definition.GetDataType());
            return unit == null ? name : name + " (" + unit + ")";
        }

        public static double ConvertFromInternalUnits(double v, string unit)
        {
            try
            {
                if (unit == "m")  return UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Meters);
                if (unit == "m²") return UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.SquareMeters);
                if (unit == "m³") return UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.CubicMeters);
            }
            catch (Exception ex)
            {
                Logger.Warn("ConvertFromInternalUnits via UnitUtils failed, fallback: " + ex.Message);
            }
            // Fallback：旧的常量法
            if (unit == "m")  return v * Constants.FEET_TO_METERS;
            if (unit == "m²") return v * Constants.FEET_TO_METERS * Constants.FEET_TO_METERS;
            if (unit == "m³") return v * Constants.FEET_TO_METERS * Constants.FEET_TO_METERS * Constants.FEET_TO_METERS;
            return v;
        }

        /// <summary>
        /// 三处口径统一："专业名称"或"分包类型"参数缺失/空/等于 VALUE_UNMATCHED 都算未匹配。
        /// 供 FilterUnmatchedElementsCommand、ParameterManagerCommand 共用。
        /// (MCP 端的 FilterUnmatchedElementsTool 仍保留自己的版本，待后续单独统一。)
        /// </summary>
        public static bool IsUnmatched(Element elem)
        {
            if (elem == null) return false;
            var pMajor = elem.get_Parameter(Constants.GUID_MAJOR);
            var pSub   = elem.get_Parameter(Constants.GUID_SUBCONTRACTOR);
            if (pMajor == null || pSub == null) return true;  // 没绑定参数 = 未匹配
            string vMajor = (pMajor.HasValue && pMajor.StorageType == StorageType.String) ? pMajor.AsString() : null;
            string vSub   = (pSub.HasValue   && pSub.StorageType   == StorageType.String) ? pSub.AsString()   : null;
            return string.IsNullOrEmpty(vMajor) || string.IsNullOrEmpty(vSub)
                || vMajor == Constants.VALUE_UNMATCHED || vSub == Constants.VALUE_UNMATCHED;
        }

        public static ElementData ExtractElementData(Element elem)
        {
            ElementData data = new ElementData();
            data.ElementId    = elem.Id.Value.ToString();
            data.Category     = (elem.Category != null) ? elem.Category.Name : Constants.DEFAULT_VALUE;
            data.FamilyName   = GetFamilyName(elem);
            data.TypeName     = GetTypeName(elem);
            data.Level        = GetLevelName(elem);

            Parameter pm = elem.get_Parameter(Constants.GUID_MAJOR);
            Parameter ps = elem.get_Parameter(Constants.GUID_SUBCONTRACTOR);
            Parameter pe = elem.get_Parameter(Constants.GUID_SHOULD_EXPORT);

            // HasValue 校验放在 AsString 之前，避免空参数被读成 ""
            data.MajorName     = (pm != null && pm.HasValue && pm.AsString() != null) ? pm.AsString() : Constants.VALUE_UNMATCHED;
            data.Subcontractor = (ps != null && ps.HasValue && ps.AsString() != null) ? ps.AsString() : Constants.VALUE_UNMATCHED;
            data.ShouldExport  = (pe != null && pe.HasValue && pe.AsString() != null) ? pe.AsString() : Constants.VALUE_YES;

            foreach (Parameter param in elem.Parameters)
            {
                if (param.Definition == null) continue;
                string paramName = GetExportParameterName(param);
                if (string.IsNullOrEmpty(paramName)) continue;
                if (!data.Parameters.ContainsKey(paramName))
                    data.Parameters[paramName] = GetParameterValue(param);
            }
            return data;
        }
    }
}
