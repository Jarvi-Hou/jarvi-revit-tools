using System.Collections.Generic;

namespace JarviTools.Commands.Clearance
{
    /// <summary>一档颜色分级。MinM = 净高下限（米，含）；最低档用 BOTTOM 兜底。</summary>
    public class ColorBand
    {
        public const double BOTTOM = -9999;

        public double MinM { get; set; }
        public int R { get; set; }
        public int G { get; set; }
        public int B { get; set; }
    }

    /// <summary>净高分析设置（JSON 持久化）。标高按名字记，换项目对不上时回落到当前视图标高。</summary>
    public class ClearanceSettings
    {
        public string PrimaryLevelName { get; set; }
        public string CompareLevelName { get; set; }          // null/空 = 不用对比基准
        public double OffsetMm { get; set; }                  // 主基准完成面偏移
        public bool IncludeLinks { get; set; } = true;
        public bool DeleteOldViews { get; set; } = false;
        public bool ExcludeRisers { get; set; } = true;      // 排除竖直立管/竖管（避免贴地下端造成假最低净高）
        public List<string> EnabledCategories { get; set; }   // BuiltInCategory 枚举名
        public List<ColorBand> Bands { get; set; }

        public static List<ColorBand> DefaultBands()
        {
            return new List<ColorBand>
            {
                new ColorBand { MinM = 2.8, R = 0,   G = 176, B = 80  }, // 绿
                new ColorBand { MinM = 2.6, R = 255, G = 214, B = 0   }, // 黄
                new ColorBand { MinM = 2.4, R = 255, G = 140, B = 0   }, // 橙
                new ColorBand { MinM = ColorBand.BOTTOM, R = 230, G = 30, B = 30 }, // 红
            };
        }
    }

    /// <summary>净高分析支持的类别清单（显示名 + 枚举名 + 默认勾选）。</summary>
    public class CategoryOption
    {
        public string Display { get; set; }
        public string BicName { get; set; }
        public bool DefaultOn { get; set; }

        public static List<CategoryOption> All()
        {
            return new List<CategoryOption>
            {
                new CategoryOption { Display = "风管",         BicName = "OST_DuctCurves",          DefaultOn = true },
                new CategoryOption { Display = "风管管件",     BicName = "OST_DuctFitting",         DefaultOn = true },
                new CategoryOption { Display = "风管附件",     BicName = "OST_DuctAccessory",       DefaultOn = true },
                new CategoryOption { Display = "软风管",       BicName = "OST_FlexDuctCurves",      DefaultOn = true },
                new CategoryOption { Display = "风管保温",     BicName = "OST_DuctInsulations",     DefaultOn = true },
                new CategoryOption { Display = "水管",         BicName = "OST_PipeCurves",          DefaultOn = true },
                new CategoryOption { Display = "管件",         BicName = "OST_PipeFitting",         DefaultOn = true },
                new CategoryOption { Display = "管路附件",     BicName = "OST_PipeAccessory",       DefaultOn = true },
                new CategoryOption { Display = "软管",         BicName = "OST_FlexPipeCurves",      DefaultOn = true },
                new CategoryOption { Display = "水管保温",     BicName = "OST_PipeInsulations",     DefaultOn = true },
                new CategoryOption { Display = "电缆桥架",     BicName = "OST_CableTray",           DefaultOn = true },
                new CategoryOption { Display = "桥架配件",     BicName = "OST_CableTrayFitting",    DefaultOn = true },
                new CategoryOption { Display = "线管",         BicName = "OST_Conduit",             DefaultOn = true },
                new CategoryOption { Display = "线管配件",     BicName = "OST_ConduitFitting",      DefaultOn = true },
                new CategoryOption { Display = "机械设备",     BicName = "OST_MechanicalEquipment", DefaultOn = true },
                new CategoryOption { Display = "电气设备",     BicName = "OST_ElectricalEquipment", DefaultOn = true },
                new CategoryOption { Display = "喷头",         BicName = "OST_Sprinklers",          DefaultOn = false },
                new CategoryOption { Display = "梁(结构框架)", BicName = "OST_StructuralFraming",   DefaultOn = true },
                new CategoryOption { Display = "楼板",         BicName = "OST_Floors",              DefaultOn = true },
            };
        }
    }
}
