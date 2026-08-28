using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace JarviTools.Commands.Clearance
{
    /// <summary>
    /// 净高结果清单：按净高升序、色块标档位、双击定位构件、导出 CSV（Excel 可开）。
    /// 定位动作通过回调交给命令层执行（窗口不碰 Revit API）。
    /// </summary>
    internal class ClearanceResultForm : Form
    {
        private readonly DataGridView _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        private readonly List<ClearanceResult> _results;
        private readonly List<ColorBand> _bands;
        private readonly Action<ClearanceResult> _zoomTo;
        private readonly bool _hasCompare;

        public ClearanceResultForm(List<ClearanceResult> results, List<ColorBand> bands,
                                   string summary, Action<ClearanceResult> zoomTo)
        {
            _results = results;
            _bands = bands;
            _zoomTo = zoomTo;
            _hasCompare = results.Any(r => !double.IsNaN(r.ClearCompareM));

            Text = "净高分析 — 结果（" + results.Count + " 个构件）";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new System.Drawing.Size(980, 560);

            var top = new Label
            {
                Text = summary + "    （双击行 → 视图定位到该构件）",
                Dock = DockStyle.Top,
                Height = 34,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0)
            };

            var bottom = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Bottom,
                Height = 42,
                Padding = new Padding(6)
            };
            var btnClose = new Button { Text = "关闭", Width = 90 };
            var btnCsv = new Button { Text = "导出 CSV (Excel)", Width = 130 };
            btnClose.Click += (o, e) => Close();
            btnCsv.Click += (o, e) => ExportCsv();
            bottom.Controls.Add(btnClose);
            bottom.Controls.Add(btnCsv);

            BuildGrid();
            Controls.Add(_grid);
            Controls.Add(top);
            Controls.Add(bottom);
        }

        private void BuildGrid()
        {
            _grid.Columns.Add(MakeCol("档", 32, false));
            _grid.Columns.Add(MakeCol("类别", 80, true));
            _grid.Columns.Add(MakeCol("族/类型", 190, true));
            _grid.Columns.Add(MakeCol("系统", 120, true));
            _grid.Columns.Add(MakeCol("元素ID", 78, true));
            _grid.Columns.Add(MakeCol("底标高(m)", 78, true));
            _grid.Columns.Add(MakeCol("净高-主(m)", 82, true));
            if (_hasCompare) _grid.Columns.Add(MakeCol("净高-对比(m)", 88, true));
            _grid.Columns.Add(MakeCol("轴网位置", 90, true));
            _grid.Columns.Add(MakeCol("来源", 110, true));

            foreach (ClearanceResult r in _results)
            {
                var cells = new List<object>
                {
                    "", r.Category, r.TypeLabel, r.SystemName, r.Id.Value,
                    Math.Round(r.BottomAbsM, 3), Math.Round(r.ClearPrimaryM, 3)
                };
                if (_hasCompare)
                    cells.Add(double.IsNaN(r.ClearCompareM) ? (object)"" : Math.Round(r.ClearCompareM, 3));
                cells.Add(r.GridLabel);
                cells.Add(r.Source);

                int i = _grid.Rows.Add(cells.ToArray());
                _grid.Rows[i].Tag = r;
                var color = BandColor(r.ClearPrimaryM);
                _grid.Rows[i].Cells[0].Style.BackColor = color;
                _grid.Rows[i].Cells[0].Style.SelectionBackColor = color;
            }

            _grid.CellDoubleClick += (o, e) =>
            {
                if (e.RowIndex < 0) return;
                var r = _grid.Rows[e.RowIndex].Tag as ClearanceResult;
                if (r != null && _zoomTo != null)
                {
                    try { _zoomTo(r); }
                    catch (Exception ex)
                    {
                        MessageBox.Show("定位失败：" + ex.Message, "净高分析",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            };
        }

        private static DataGridViewTextBoxColumn MakeCol(string header, int width, bool sortable)
        {
            return new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                FillWeight = width,
                SortMode = sortable
                    ? DataGridViewColumnSortMode.Automatic
                    : DataGridViewColumnSortMode.NotSortable
            };
        }

        private System.Drawing.Color BandColor(double clearM)
        {
            foreach (ColorBand b in _bands)               // _bands 已按 MinM 降序
                if (clearM >= b.MinM)
                    return System.Drawing.Color.FromArgb(b.R, b.G, b.B);
            ColorBand last = _bands[_bands.Count - 1];
            return System.Drawing.Color.FromArgb(last.R, last.G, last.B);
        }

        private void ExportCsv()
        {
            using (var dlg = new SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = "净高分析-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".csv"
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                var sb = new StringBuilder();
                sb.Append("序号,来源,类别,族/类型,系统,元素ID,底标高(m),净高-主基准(m)");
                if (_hasCompare) sb.Append(",净高-对比基准(m)");
                sb.AppendLine(",轴网位置");
                int idx = 1;
                foreach (ClearanceResult r in _results)
                {
                    sb.Append(idx++).Append(',')
                      .Append(Csv(r.Source)).Append(',')
                      .Append(Csv(r.Category)).Append(',')
                      .Append(Csv(r.TypeLabel)).Append(',')
                      .Append(Csv(r.SystemName)).Append(',')
                      .Append(r.Id.Value).Append(',')
                      .Append(r.BottomAbsM.ToString("0.000")).Append(',')
                      .Append(r.ClearPrimaryM.ToString("0.000"));
                    if (_hasCompare)
                        sb.Append(',').Append(double.IsNaN(r.ClearCompareM) ? "" : r.ClearCompareM.ToString("0.000"));
                    sb.Append(',').Append(Csv(r.GridLabel)).AppendLine();
                }
                try
                {
                    File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); // 带 BOM，Excel 中文不乱码
                    MessageBox.Show("已导出：\n" + dlg.FileName, "净高分析",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("导出失败：" + ex.Message, "净高分析",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
