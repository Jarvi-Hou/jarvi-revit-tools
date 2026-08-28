using System;
using System.Reflection;
using System.IO;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using JarviTools.Commands.Plenum;
using JarviTools.Commands.MaintenanceReachability;
using JarviTools.Core;
using JarviTools.Mcp;
using JarviTools.Mcp.Server;

namespace JarviTools
{
    /// <summary>
    /// OpenRevit Tools - 主应用程序类
    /// IExternalApplication 不需要 [Transaction] 和 [Regeneration] Attribute
    /// </summary>
    public class Application : IExternalApplication
    {
        // 图标所在目录：DLL同级的 Resources\icons\ 文件夹
        private static readonly string IconFolder = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
            "Resources", "icons"
        );

        /// <summary>
        /// 加载图标。图标缺失时静默返回null，不影响插件加载
        /// </summary>
        private static BitmapImage LoadImage(string iconFileName)
        {
            try
            {
                string path = Path.Combine(IconFolder, iconFileName);
                if (!File.Exists(path)) return null;

                BitmapImage img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(path);
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze(); // 允许跨WPF线程访问
                return img;
            }
            catch { return null; }
        }

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                string tabName = Constants.TAB_NAME;
                EnsureRibbonTab(application, tabName);

                // Experimental compatibility commands remain registered so local
                // hot-load clients can invoke them, but they are not part of the
                // stable OpenRevit Ribbon surface.
                CreateExportPanel(application, tabName);
                CreateSchedulePanel(application, tabName);
                CreateParameterPanel(application, tabName);
                CreateMepCheckPanel(application, tabName);

                // MCP 子系统：初始化 + 加 ribbon 面板。失败时不阻塞其它面板。
                try
                {
                    McpHost.Initialize();
                    string autoStart = Environment.GetEnvironmentVariable("OPENREVIT_MCP_AUTOSTART");
                    if ((string.Equals(autoStart, "1", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(autoStart, "true", StringComparison.OrdinalIgnoreCase)) &&
                        (McpHost.Server == null || !McpHost.Server.IsRunning))
                    {
                        McpHost.Server = new HttpServer(7800);
                        McpHost.Server.Start();
                    }
                    CreateMcpPanel(application, tabName);
                }
                catch (Exception mcpEx)
                {
                    TaskDialog.Show(Constants.PLUGIN_NAME + " — MCP 初始化失败",
                        "MCP 面板未能加载，其它功能正常：\n" + mcpEx.Message);
                }

                application.ControlledApplication.DocumentOpened += OnDocumentOpened;
                application.ControlledApplication.DocumentClosed += OnDocumentClosed;
                application.ControlledApplication.DocumentChanged += MaintenanceDocumentChangeTracker.OnDocumentChanged;
                application.ViewActivated += OnViewActivated;

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Constants.PLUGIN_NAME + " 加载错误", "创建功能区失败：" + ex.Message);
                return Result.Failed;
            }
        }

        private static void EnsureRibbonTab(UIControlledApplication application, string tabName)
        {
            try
            {
                application.CreateRibbonTab(tabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                if (application.GetRibbonPanels(tabName) == null) throw;
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                if (application.GetRibbonPanels(tabName) == null) throw;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
                application.ControlledApplication.DocumentClosed -= OnDocumentClosed;
                application.ControlledApplication.DocumentChanged -= MaintenanceDocumentChangeTracker.OnDocumentChanged;
                application.ViewActivated -= OnViewActivated;
                MaintenanceDocumentChangeTracker.Clear();
                PlenumAnalysisStore.Clear(null);
                JarviTools.Mcp.Tools.MaintenanceAnalysisStore.Clear(null);
                JarviTools.Mcp.Tools.MaintenanceHandReachStore.Clear(null);
            }
            catch { /* ignore */ }
            try { McpHost.Shutdown(); } catch { /* ignore */ }
            return Result.Succeeded;
        }

        private static void OnDocumentOpened(object sender, DocumentOpenedEventArgs args)
        {
            PlenumAnalysisStore.Clear(null);
            JarviTools.Mcp.Tools.MaintenanceAnalysisStore.Clear(null);
            JarviTools.Mcp.Tools.MaintenanceHandReachStore.Clear(null);
            var doc = args == null ? null : args.Document;
            MaintenanceDocumentChangeTracker.TrackOpened(doc);
            McpHost.CaptureActiveContext(doc, doc == null ? null : doc.ActiveView);
        }

        private static void OnDocumentClosed(object sender, DocumentClosedEventArgs args)
        {
            PlenumAnalysisStore.Clear(null);
            JarviTools.Mcp.Tools.MaintenanceAnalysisStore.Clear(null);
            JarviTools.Mcp.Tools.MaintenanceHandReachStore.Clear(null);
            McpHost.ClearActiveContext();
        }

        private static void OnViewActivated(object sender, ViewActivatedEventArgs args)
        {
            var view = args == null ? null : args.CurrentActiveView;
            McpHost.CaptureActiveContext(view == null ? null : view.Document, view);
        }

        private void CreateExportPanel(UIControlledApplication application, string tabName)
        {
            RibbonPanel panel = application.CreateRibbonPanel(tabName, "构件导出");
            string dll = Assembly.GetExecutingAssembly().Location;

            var iconMatch = LoadImage("match_icon.png");
            panel.AddItem(new PushButtonData("MatchQuantityParameters", "一键匹配\n计量参数", dll,
                "JarviTools.Commands.MatchQuantityParametersCommand")
            {
                ToolTip = "自动添加工程量统计参数并按类别匹配",
                LongDescription = "添加共享参数（专业名称、分包类型、是否导出），并根据Revit类别自动匹配参数值。",
                Image = iconMatch, LargeImage = iconMatch
            });

            var iconFilter = LoadImage("filter_icon.png");
            panel.AddItem(new PushButtonData("FilterUnmatchedElements", "筛选未匹配\n成功构件", dll,
                "JarviTools.Commands.FilterUnmatchedElementsCommand")
            {
                ToolTip = "临时隔离未匹配成功的构件",
                LongDescription = "筛选专业名称或分包类型为\"未匹配成功\"的构件，临时隔离显示便于手动修改。",
                Image = iconFilter, LargeImage = iconFilter
            });

            var iconExport = LoadImage("export_icon.png");
            panel.AddItem(new PushButtonData("ExportVisibleElements", "导出可见\n构件", dll,
                "JarviTools.Commands.ExportVisibleElementsCommand")
            {
                ToolTip = "将当前3D视图中的可见构件导出到Excel",
                LongDescription = "根据专业和分包分组，导出为Excel多工作表文件（每个分包一个工作表）。",
                Image = iconExport, LargeImage = iconExport
            });

            panel.Visible = false;
        }

        private void CreateSchedulePanel(UIControlledApplication application, string tabName)
        {
            RibbonPanel panel = application.CreateRibbonPanel(tabName, "明细表导出");
            string dll = Assembly.GetExecutingAssembly().Location;

            var iconSchedule = LoadImage("schedule_icon.png");
            panel.AddItem(new PushButtonData("ExportAllSchedules", "导出所有\n类别明细表", dll,
                "JarviTools.Commands.ExportAllSchedulesCommand")
            {
                ToolTip = "一键导出项目中所有类别的明细表",
                LongDescription = "收集所有模型类别图元数据，导出为Excel多工作表文件，首页为汇总。",
                Image = iconSchedule, LargeImage = iconSchedule
            });

            panel.Visible = false;
        }

        private void CreateParameterPanel(UIControlledApplication application, string tabName)
        {
            RibbonPanel panel = application.CreateRibbonPanel(tabName, "参数管理");
            string dll = Assembly.GetExecutingAssembly().Location;

            var iconParam = LoadImage("param_manager_icon.png");
            panel.AddItem(new PushButtonData("ParameterManager", "参数\n管理器", dll,
                "JarviTools.Commands.ParameterManagerCommand")
            {
                ToolTip = "查看项目中工程量参数使用情况",
                LongDescription = "统计已匹配、未匹配、未添加参数的构件数量。",
                Image = iconParam, LargeImage = iconParam
            });

            panel.Visible = false;
        }

        private void CreateMepCheckPanel(UIControlledApplication application, string tabName)
        {
            RibbonPanel panel = application.CreateRibbonPanel(tabName, "机电检查");
            string dll = Assembly.GetExecutingAssembly().Location;

            var iconSection = LoadImage("section_icon.png");
            panel.AddItem(new PushButtonData("EquipmentSection", "设备检查\n剖面", dll,
                "JarviTools.Commands.EquipmentSection.EquipmentSectionCommand")
            {
                ToolTip = "为选中的设备批量生成检查剖面",
                LongDescription = "优先按风管连接件识别气流轴，为每台设备生成纵向检查剖面；" +
                                  "无连接件时再按设备几何兜底。范围可配置、自动命名，整批可一次撤销。",
                Image = iconSection, LargeImage = iconSection
            });

            var iconEquip3d = LoadImage("equip3d_icon.png");
            panel.AddItem(new PushButtonData("Equipment3DView", "设备三维\n检查", dll,
                "JarviTools.Commands.EquipmentSection.Equipment3DViewCommand")
            {
                ToolTip = "为选中的设备批量生成三维检查视图（剖面框包住设备+整段风管）",
                LongDescription = "从设备风口沿风管追踪到末端风口，每台设备生成一个三维视图，" +
                                  "剖面框自动包住设备与风管，包裹距离可配置（0=贴紧）。",
                Image = iconEquip3d, LargeImage = iconEquip3d
            });

            var iconClearance = LoadImage("clearance_icon.png");
            panel.AddItem(new PushButtonData("ClearanceAnalysis", "净高\n分析", dll,
                "JarviTools.Commands.Clearance.ClearanceAnalysisCommand")
            {
                ToolTip = "构件级净高分析：着色视图 + 结果清单",
                LongDescription = "按构件真实几何最低点计算净高（支持建筑/结构双标高基准），" +
                                  "生成着色检查视图与可排序、可定位、可导出的结果清单。",
                Image = iconClearance, LargeImage = iconClearance
            });

            var iconPlenum = LoadImage("plenum_icon.png");
            panel.AddItem(new PushButtonData("PlenumSpaceField", "负空间\n分析", dll,
                "JarviTools.Commands.Plenum.PlenumSpaceFieldCommand")
            {
                ToolTip = "吊顶到结构之间的三维负空间场",
                LongDescription = "对当前三维视图中的单块吊顶自适应取样，" +
                                  "纳入已加载结构和机电链接，生成可清除的分级三维空间色块。",
                Image = iconPlenum, LargeImage = iconPlenum
            });

            var iconMaintenance = LoadImage("maintenance_reachability_icon.png");
            panel.AddItem(new PushButtonData("MaintenanceReachability", "AI维修可达\n入口", dll,
                "JarviTools.Commands.MaintenanceReachability.MaintenanceReachabilityCommand")
            {
                ToolTip = "启动顶级 AI + MCP + 负空间分析协同工作流",
                LongDescription = "本按钮是维修可达的 AI 引导入口，不会独立产生正式判定。" +
                                  "它会检查 MCP 与负空间准备状态，并将正式任务交给 " +
                                  "Codex 5.6 Sol（极高推理）通过 Revit MCP 完成。",
                Image = iconMaintenance, LargeImage = iconMaintenance
            });

            panel.Visible = false;
        }

        private void CreateMcpPanel(UIControlledApplication application, string tabName)
        {
            RibbonPanel panel = application.CreateRibbonPanel(tabName, "MCP AI 服务器");
            string dll = Assembly.GetExecutingAssembly().Location;

            var iconStart  = LoadImage("mcp_start_icon.png");
            var iconStop   = LoadImage("mcp_stop_icon.png");
            var iconStatus = LoadImage("mcp_status_icon.png");

            panel.AddItem(new PushButtonData("MCP_Start", "启动\nMCP", dll,
                "JarviTools.Mcp.Commands.StartServerCommand")
            {
                ToolTip = "在 127.0.0.1:7800 启动 MCP HTTP server",
                LongDescription = "让受信任的 Codex 或其他 MCP 客户端连接当前 Revit 实例；写操作仍需遵守确认与安全边界。",
                Image = iconStart, LargeImage = iconStart
            });

            panel.AddItem(new PushButtonData("MCP_Stop", "停止\nMCP", dll,
                "JarviTools.Mcp.Commands.StopServerCommand")
            {
                ToolTip = "停止 MCP HTTP server",
                Image = iconStop, LargeImage = iconStop
            });

            panel.AddSeparator();

            panel.AddItem(new PushButtonData("MCP_Status", "状态\n+工具", dll,
                "JarviTools.Mcp.Commands.StatusCommand")
            {
                ToolTip = "查看 MCP server 状态和已注册的工具列表",
                LongDescription = "显示当前 MCP 服务器是否在运行，以及全部已注册工具的清单。",
                Image = iconStatus, LargeImage = iconStatus
            });
        }
    }
}
