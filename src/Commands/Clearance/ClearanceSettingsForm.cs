using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace JarviTools.Commands.Clearance
{
    /// <summary>标高下拉项（与 Revit 解耦）。</summary>
    public class LevelChoice
    {
        public string Name { get; set; }
        public double ElevationM { get; set; }
        public override string ToString()
        {
            return Name + "  (" + ElevationM.ToString("0.000") + "m)";
        }
    }

    public enum ScopeMode { WholeLevel, PickRectangle, CurrentSelection }

    internal class ClearanceSettingsForm : Form
    {
        private readonly RadioButton _rbLevel = new RadioButton { Text = "整个楼层", Checked = true, AutoSize = true };
        private readonly RadioButton _rbRect = new RadioButton { Text = "框选矩形区域（点\"开始分析\"后去视图里框选）", AutoSize = true };
        private readonly RadioButton _rbSel = new RadioButton { Text = "仅当前已选构件", AutoSize = true };
        private readonly ComboBox _cbPrimary = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        private readonly ComboBox _cbCompare = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        private readonly NumericUpDown _offset = new NumericUpDown
        {
            Minimum = -1000, Maximum = 1000, Increment = 10, DecimalPlaces = 0, Width = 80
        };
        private readonly CheckedListBox _cats = new CheckedListBox
        {
            MultiColumn = true, ColumnWidth = 120, CheckOnClick = true, IntegralHeight = false
        };
        private readonly DataGridView _bands = new DataGridView
        {
            AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false
        };
        private readonly CheckBox _links = new CheckBox { Text = "包含链接模型（梁板常在结构链接里）", AutoSize = true };
        private readonly CheckBox _delOld = new CheckBox { Text = "运行前删除旧结果视图（仅无用户标注/上图视图）", AutoSize = true };
        private readonly CheckBox _riser = new CheckBox { Text = "排除竖直立管/竖管（推荐，避免贴地假净高）", AutoSize = true };
        private readonly List<CategoryOption> _catOptions = CategoryOption.All();

        public ScopeMode Scope
        {
            get
            {
                if (_rbRect.Checked) return ScopeMode.PickRectangle;
                if (_rbSel.Checked) return ScopeMode.CurrentSelection;
                return ScopeMode.WholeLevel;
            }
        }
        public LevelChoice PrimaryLevel { get { return (LevelChoice)_cbPrimary.SelectedItem; } }
        public LevelChoice CompareLevel { get { return _cbCompare.SelectedItem as LevelChoice; } } // "（无）"时为 null

        public ClearanceSettingsForm(ClearanceSettings s, List<LevelChoice> levels,
                                     string defaultPrimaryName, int preselectedCount)
        {
            Text = "净高分析 — 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new System.Drawing.Size(560, 672);

            // ---- 范围 ----
            var gbScope = new GroupBox { Text = "分析范围", Bounds = new System.Drawing.Rectangle(12, 10, 536, 78) };
            _rbLevel.Location = new System.Drawing.Point(14, 22);
            _rbRect.Location = new System.Drawing.Point(120, 22);
            _rbSel.Location = new System.Drawing.Point(14, 48);
            _rbSel.Enabled = preselectedCount > 0;
            _rbSel.Text = "仅当前已选构件" + (preselectedCount > 0 ? "（" + preselectedCount + " 个）" : "（未预选）");
            gbScope.Controls.Add(_rbLevel);
            gbScope.Controls.Add(_rbRect);
            gbScope.Controls.Add(_rbSel);

            // ---- 基准 ----
            var gbDatum = new GroupBox { Text = "标高基准", Bounds = new System.Drawing.Rectangle(12, 94, 536, 108) };
            var lb1 = new Label { Text = "主基准标高:", Location = new System.Drawing.Point(14, 26), AutoSize = true };
            _cbPrimary.Location = new System.Drawing.Point(110, 22);
            var lb2 = new Label { Text = "偏移(mm):", Location = new System.Drawing.Point(360, 26), AutoSize = true };
            _offset.Location = new System.Drawing.Point(440, 22);
            var lb3 = new Label { Text = "对比基准标高:", Location = new System.Drawing.Point(14, 62), AutoSize = true };
            _cbCompare.Location = new System.Drawing.Point(110, 58);
            var lb4 = new Label { Text = "(对比列不加偏移)", Location = new System.Drawing.Point(360, 62), AutoSize = true };
            gbDatum.Controls.Add(lb1);
            gbDatum.Controls.Add(_cbPrimary);
            gbDatum.Controls.Add(lb2);
            gbDatum.Controls.Add(_offset);
            gbDatum.Controls.Add(lb3);
            gbDatum.Controls.Add(_cbCompare);
            gbDatum.Controls.Add(lb4);

            // ---- 类别 ----
            var gbCats = new GroupBox { Text = "参与分析的类别", Bounds = new System.Drawing.Rectangle(12, 208, 536, 168) };
            _cats.Bounds = new System.Drawing.Rectangle(12, 20, 512, 138);
            gbCats.Controls.Add(_cats);

            // ---- 颜色分级 ----
            var gbBands = new GroupBox
            {
                Text = "颜色分级（下限留空 = 低于以上全部档位的兜底档）",
                Bounds = new System.Drawing.Rectangle(12, 382, 536, 178)
            };
            _bands.Bounds = new System.Drawing.Rectangle(12, 22, 430, 144);
            var colMin = new DataGridViewTextBoxColumn { HeaderText = "净高下限 (m)", FillWeight = 60 };
            var colColor = new DataGridViewButtonColumn { HeaderText = "颜色（点击修改）", FillWeight = 40 };
            _bands.Columns.Add(colMin);
            _bands.Columns.Add(colColor);
            _bands.CellClick += OnBandCellClick;
            var btnAdd = new Button { Text = "＋", Bounds = new System.Drawing.Rectangle(452, 22, 72, 30) };
            var btnDel = new Button { Text = "－", Bounds = new System.Drawing.Rectangle(452, 58, 72, 30) };
            btnAdd.Click += (o, e) => AddBandRow("", System.Drawing.Color.Gray);
            btnDel.Click += (o, e) =>
            {
                if (_bands.CurrentRow != null && _bands.Rows.Count > 1)
                    _bands.Rows.Remove(_bands.CurrentRow);
            };
            gbBands.Controls.Add(_bands);
            gbBands.Controls.Add(btnAdd);
            gbBands.Controls.Add(btnDel);

            // ---- 选项 + 按钮 ----
            _links.Location = new System.Drawing.Point(16, 568);
            _delOld.Location = new System.Drawing.Point(300, 568);
            _riser.Location = new System.Drawing.Point(16, 594);
            var ok = new Button
            {
                Text = "开始分析", DialogResult = DialogResult.OK,
                Bounds = new System.Drawing.Rectangle(368, 630, 84, 30)
            };
            var cancel = new Button
            {
                Text = "取消", DialogResult = DialogResult.Cancel,
                Bounds = new System.Drawing.Rectangle(462, 630, 84, 30)
            };

            Controls.Add(gbScope);
            Controls.Add(gbDatum);
            Controls.Add(gbCats);
            Controls.Add(gbBands);
            Controls.Add(_links);
            Controls.Add(_delOld);
            Controls.Add(_riser);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            // ---- 回填 ----
            foreach (LevelChoice lv in levels) _cbPrimary.Items.Add(lv);
            _cbCompare.Items.Add("（无）");
            foreach (LevelChoice lv in levels) _cbCompare.Items.Add(lv);

            LevelChoice primary = levels.FirstOrDefault(l => l.Name == s.PrimaryLevelName)
                               ?? levels.FirstOrDefault(l => l.Name == defaultPrimaryName)
                               ?? levels.FirstOrDefault();
            _cbPrimary.SelectedItem = primary;
            LevelChoice compare = levels.FirstOrDefault(l => l.Name == s.CompareLevelName);
            if (compare != null) _cbCompare.SelectedItem = compare;
            else _cbCompare.SelectedIndex = 0;

            decimal offsetVal = (decimal)s.OffsetMm;
            if (offsetVal < _offset.Minimum) offsetVal = _offset.Minimum;
            if (offsetVal > _offset.Maximum) offsetVal = _offset.Maximum;
            _offset.Value = offsetVal;
            _links.Checked = s.IncludeLinks;
            _delOld.Checked = s.DeleteOldViews;
            _riser.Checked = s.ExcludeRisers;

            HashSet<string> enabled = (s.EnabledCategories != null && s.EnabledCategories.Count > 0)
                ? new HashSet<string>(s.EnabledCategories)
                : null;
            foreach (CategoryOption c in _catOptions)
            {
                bool on = enabled != null ? enabled.Contains(c.BicName) : c.DefaultOn;
                _cats.Items.Add(c.Display, on);
            }

            List<ColorBand> bands = (s.Bands != null && s.Bands.Count > 0)
                ? s.Bands
                : ClearanceSettings.DefaultBands();
            foreach (ColorBand b in bands.OrderByDescending(b2 => b2.MinM))
                AddBandRow(b.MinM <= ColorBand.BOTTOM ? "" : b.MinM.ToString("0.0#"),
                           System.Drawing.Color.FromArgb(b.R, b.G, b.B));
        }

        private void AddBandRow(string minText, System.Drawing.Color color)
        {
            int i = _bands.Rows.Add(minText, "");
            _bands.Rows[i].Cells[1].Style.BackColor = color;
            _bands.Rows[i].Cells[1].Style.SelectionBackColor = color;
        }

        private void OnBandCellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 1) return;
            using (var dlg = new ColorDialog
            {
                FullOpen = true,
                Color = _bands.Rows[e.RowIndex].Cells[1].Style.BackColor
            })
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    _bands.Rows[e.RowIndex].Cells[1].Style.BackColor = dlg.Color;
                    _bands.Rows[e.RowIndex].Cells[1].Style.SelectionBackColor = dlg.Color;
                }
            }
        }

        private bool ValidateBands(out List<ColorBand> bands)
        {
            bands = new List<ColorBand>();
            int emptyCount = 0;
            foreach (DataGridViewRow row in _bands.Rows)
            {
                string txt = (row.Cells[0].Value ?? "").ToString().Trim();
                var c = row.Cells[1].Style.BackColor;
                if (txt.Length == 0)
                {
                    emptyCount++;
                    bands.Add(new ColorBand { MinM = ColorBand.BOTTOM, R = c.R, G = c.G, B = c.B });
                }
                else
                {
                    double v;
                    if (!double.TryParse(txt, out v)) return false;
                    bands.Add(new ColorBand { MinM = v, R = c.R, G = c.G, B = c.B });
                }
            }
            if (bands.Count == 0 || emptyCount != 1) return false;
            bands = bands.OrderByDescending(b => b.MinM).ToList();
            return true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK)
            {
                List<ColorBand> bands;
                if (!ValidateBands(out bands))
                {
                    MessageBox.Show(
                        "颜色分级无效：下限必须是数字，且必须有且仅有一行下限留空（兜底档）。",
                        "净高分析", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                    return;
                }
                if (PrimaryLevel == null)
                {
                    MessageBox.Show("请选择主基准标高。", "净高分析",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                    return;
                }
                bool anyCat = false;
                for (int i = 0; i < _catOptions.Count; i++)
                    if (_cats.GetItemChecked(i)) { anyCat = true; break; }
                if (!anyCat)
                {
                    MessageBox.Show("请至少勾选一个分析类别。", "净高分析",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                    return;
                }
            }
            base.OnFormClosing(e);
        }

        /// <summary>把界面值写回设置对象（仅在 OK 后调用）。</summary>
        public void Apply(ClearanceSettings s)
        {
            List<ColorBand> bands;
            ValidateBands(out bands);
            s.Bands = bands;
            s.PrimaryLevelName = PrimaryLevel != null ? PrimaryLevel.Name : null;
            s.CompareLevelName = CompareLevel != null ? CompareLevel.Name : null;
            s.OffsetMm = (double)_offset.Value;
            s.IncludeLinks = _links.Checked;
            s.DeleteOldViews = _delOld.Checked;
            s.ExcludeRisers = _riser.Checked;
            s.EnabledCategories = new List<string>();
            for (int i = 0; i < _catOptions.Count; i++)
                if (_cats.GetItemChecked(i))
                    s.EnabledCategories.Add(_catOptions[i].BicName);
        }
    }
}
