using System;
using System.Collections.Generic;

namespace JarviTools.Core
{
    /// <summary>
    /// 常量定义类 - OpenRevit Tools
    /// 整合所有插件的常量定义
    /// </summary>
    public static class Constants
    {
        // ==================== 插件信息 ====================
        public const string PLUGIN_NAME = "OpenRevit Tools";
        public const string PLUGIN_VERSION = "0.5.0";
        public const string TAB_NAME = "OpenRevit";
        public const string VENDOR_ID = "ORVT";
        public const string VENDOR_DESCRIPTION = "OpenRevit Tools - Open-source Revit productivity toolkit";
        
        // ==================== 面板名称 ====================
        public const string PANEL_EXPORT = "构件导出";
        public const string PANEL_SCHEDULE = "明细表导出";
        public const string PANEL_PARAMETER = "参数管理";
        
        // ==================== 共享参数名称 ====================
        public const string PARAM_MAJOR_NAME = "专业名称";
        public const string PARAM_SUBCONTRACTOR = "分包类型";
        public const string PARAM_SHOULD_EXPORT = "是否导出";

        // ==================== 共享参数 GUID ====================
        public static readonly Guid GUID_MAJOR = new Guid("8f3e9c2a-4b5d-4e6f-9a7b-1c2d3e4f5001");
        public static readonly Guid GUID_SUBCONTRACTOR = new Guid("8f3e9c2a-4b5d-4e6f-9a7b-1c2d3e4f5002");
        public static readonly Guid GUID_SHOULD_EXPORT = new Guid("8f3e9c2a-4b5d-4e6f-9a7b-1c2d3e4f5003");

        // ==================== 参数值 ====================
        public const string VALUE_UNMATCHED = "未匹配成功";
        public const string VALUE_YES = "是";
        public const string VALUE_NO = "否";

        // ==================== 专业名称 ====================
        public const string MAJOR_ARCHITECTURE = "建筑";
        public const string MAJOR_STRUCTURE = "结构";
        public const string MAJOR_MEP = "机电";
        public const string MAJOR_INTERIOR = "装饰";
        public const string MAJOR_SITE = "场地";

        // ==================== 专业代码 ====================
        public const string CODE_AR = "AR";
        public const string CODE_ST = "ST";
        public const string CODE_MEP = "MEP";
        public const string CODE_IN = "IN";
        public const string CODE_SI = "SI";
        public const string CODE_UNMATCHED = "XX";

        // ==================== 文件名相关 ====================
        public const string SHARED_PARAM_FILE = "工程量统计共享参数.txt";
        public const string FILE_PREFIX_SCHEDULE = "项目明细表导出";
        public const string FILE_PREFIX_ELEMENTS = "可见构件导出";
        public const string FILE_EXTENSION = ".xlsx";
        public const string TIMESTAMP_FORMAT = "yyyyMMdd_HHmmss";

        // ==================== Excel工作表名称 ====================
        public const string SHEET_SUMMARY = "汇总";
        public const string SHEET_EXCLUDED = "已排除构件";
        public const string SHEET_UNMATCHED = "未匹配成功";
        public const int MAX_SHEET_NAME_LENGTH = 31;
        
        // ==================== 列标题 ====================
        public const string HEADER_INDEX = "序号";
        public const string HEADER_ELEMENT_ID = "图元ID";
        public const string HEADER_CATEGORY = "类别";
        public const string HEADER_FAMILY = "族名称";
        public const string HEADER_TYPE = "类型名称";
        public const string HEADER_LEVEL = "标高";

        // ==================== 参数组名称 ====================
        public const string PARAM_GROUP_NAME = "标识数据";
        
        // ==================== 消息文本 ====================
        public const string MSG_NO_CATEGORIES = "项目中没有找到任何类别。";
        public const string MSG_EXPORT_SUCCESS = "导出成功！";
        public const string MSG_EXPORT_FAILED = "导出失败：";
        public const string MSG_FILE_SAVED = "文件已保存至：";
        public const string MSG_PROCESSING = "正在处理类别：";
        public const string MSG_TOTAL_CATEGORIES = "共处理类别数：";
        public const string MSG_TOTAL_ELEMENTS = "共导出图元数：";

        // ==================== 错误消息 ====================
        public const string ERROR_FILE_OPEN = "文件保存失败！\n\n请检查是否已打开同名的 Excel 文件。\n请关闭文件后重试。";
        public const string ERROR_PERMISSION = "没有权限保存文件！";
        public const string ERROR_UNKNOWN = "保存文件时发生错误：";

        // ==================== 参数管理器报告模板 ====================
        // 占位顺序：{0}=总数  {1}=已添加参数  {2}=已匹配  {3}=未匹配  {4}=未添加参数
        public const string PARAM_MANAGER_REPORT_TEMPLATE =
            "项目参数使用情况统计\n\n" +
            "模型图元总数：{0} 个\n" +
            "已添加参数：{1} 个\n" +
            "  ✓ 已匹配：{2} 个\n" +
            "  ✗ 未匹配：{3} 个\n" +
            "未添加参数：{4} 个\n\n" +
            "提示：请使用\"构件导出\"面板中的命令来添加和匹配参数。";
        
        // ==================== 默认值 ====================
        public const string DEFAULT_VALUE = "-";
        public const string VALUE_EMPTY = "";
        
        // ==================== 单位转换系数 ====================
        public const double FEET_TO_METERS = 0.3048;
        
        // ==================== 工程量参数及其单位 ====================
        // 当前以中文 Revit 为主，只保留中文 key。如未来支持英文 Revit，可在 ElementDataHelper
        // 里做一次"参数名 → 中文标准名"的归一化映射，再来这里查表。
        public static readonly Dictionary<string, string> QuantityParametersWithUnits = new Dictionary<string, string>
        {
            // 长度类参数
            { "长度", "m" },
            { "宽度", "m" },
            { "高度", "m" },
            { "厚度", "m" },
            { "直径", "m" },
            { "半径", "m" },

            // 面积类参数
            { "面积", "m²" },
            { "净面积", "m²" },
            { "表面积", "m²" },

            // 体积类参数
            { "体积", "m³" },
            { "净体积", "m³" }
        };
        
        // ==================== 专业代码映射 ====================
        // 命名风格说明：常量值用 SCREAMING_SNAKE_CASE（如 MAJOR_ARCHITECTURE），
        // 字典/集合等"对象"用 PascalCase（如 MajorCodeMapping、MajorPriority、QuantityParametersWithUnits）。
        // 与外部 Tools 的 Constants.MajorCodeMapping / MajorPriority 引用保持一致。
        public static readonly Dictionary<string, string> MajorCodeMapping = new Dictionary<string, string>
        {
            { MAJOR_ARCHITECTURE, CODE_AR },
            { MAJOR_STRUCTURE, CODE_ST },
            { MAJOR_MEP, CODE_MEP },
            { MAJOR_INTERIOR, CODE_IN },
            { MAJOR_SITE, CODE_SI },
            { VALUE_UNMATCHED, CODE_UNMATCHED }
        };

        // ==================== 专业排序优先级 ====================
        public static readonly Dictionary<string, int> MajorPriority = new Dictionary<string, int>
        {
            { CODE_AR, 1 },
            { CODE_ST, 2 },
            { CODE_MEP, 3 },
            { CODE_IN, 4 },
            { CODE_SI, 5 },
            { CODE_UNMATCHED, 99 }
        };
    }
}
