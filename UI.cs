using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

namespace RightPanel
{
    internal static class Theme
    {
        internal static readonly Color Canvas = Color.FromArgb(22, 24, 28);
        internal static readonly Color Surface = Color.FromArgb(33, 37, 43);
        internal static readonly Color Line = Color.FromArgb(64, 71, 79);
        internal static readonly Color Ink = Color.FromArgb(237, 241, 243);
        internal static readonly Color Muted = Color.FromArgb(169, 180, 191);
        internal static readonly Color Mint = Color.FromArgb(130, 224, 194);

        internal static Button Button(string caption, bool primary)
        {
            Button b = new Button();
            b.Text = caption;
            b.Dock = DockStyle.Fill;
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = primary ? Mint : Surface;
            b.ForeColor = primary ? Canvas : Ink;
            b.FlatAppearance.BorderColor = primary ? Mint : Line;
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            return b;
        }

        internal static Label Label(string value, Color color, float size)
        {
            return new Label { Text = value, ForeColor = color, Font = new Font("Segoe UI", size),
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        }
    }

    // The preview represents the monitor's active timing in physical pixels.
    // All mode requests use the integer values; interpolation is visual only.
    internal sealed class MonitorPreview : Control
    {
        internal const int PanelWidth = 2560, PanelHeight = 1080;
        internal Rectangle RegionPixels = new Rectangle(679, 0, 1881, 1080);
        internal event Action<Rectangle> Edited;
        private RectangleF panel;
        private Rectangle initial;
        private Point origin;
        private int grab;
        private const int LeftEdge = 1, RightEdge = 2, TopEdge = 4, BottomEdge = 8, MoveArea = 16;
        private readonly Timer fade = new Timer { Interval = 20 };
        private float highlight;
        private bool hovering;

        internal MonitorPreview()
        {
            DoubleBuffered = true;
            Dock = DockStyle.Fill;
            BackColor = Theme.Surface;
            AccessibleName = "Схема рабочей области монитора";
            TabStop = false;
            SetStyle(ControlStyles.ResizeRedraw, true);
            fade.Tick += delegate
            {
                float dest = hovering ? 1f : 0f;
                highlight += (dest - highlight) * 0.35f;
                if (Math.Abs(dest - highlight) < .02f) { highlight = dest; fade.Stop(); }
                Invalidate();
            };
        }

        private RectangleF ShownArea()
        {
            float scale = panel.Width / PanelWidth;
            return new RectangleF(panel.Left + RegionPixels.X * scale, panel.Top + RegionPixels.Y * scale,
                RegionPixels.Width * scale, RegionPixels.Height * scale);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float dpi = g.DpiX / 96f;
            float inset = 14 * dpi;
            float scale = Math.Max(.001f, Math.Min((Width - 2*inset) / PanelWidth, (Height - 2*inset) / PanelHeight));
            float w = PanelWidth * scale, h = PanelHeight * scale;
            panel = new RectangleF((Width-w)/2, (Height-h)/2, w, h);
            using (var hatch = new HatchBrush(HatchStyle.ForwardDiagonal, Color.FromArgb(85, 67, 63), Color.FromArgb(49, 42, 42)))
                g.FillRectangle(hatch, panel);
            RectangleF actual = ShownArea();
            RectangleF visible = RectangleF.Intersect(panel, actual);
            using (var bg = new SolidBrush(Color.FromArgb(35, 63, 59))) g.FillRectangle(bg, visible);
            using (var line = new Pen(Theme.Line, dpi)) g.DrawRectangle(line, panel.X, panel.Y, panel.Width, panel.Height);
            if (visible.Width <= 0 || visible.Height <= 0) return;
            using (var pen = new Pen(Theme.Mint, (1.2f + highlight) * dpi))
                g.DrawRectangle(pen, visible.X, visible.Y, visible.Width, visible.Height);
            string size = RegionPixels.Width + " × " + RegionPixels.Height;
            using (Font title = new Font("Segoe UI", 13, FontStyle.Bold))
            {
                SizeF measure = g.MeasureString(size, title);
                if (measure.Width + 6 < visible.Width && measure.Height + 4 < visible.Height)
                    using (var brush = new SolidBrush(Theme.Ink))
                        g.DrawString(size, title, brush, visible.X + (visible.Width-measure.Width)/2,
                            visible.Y + (visible.Height-measure.Height)/2);
            }
            float midX = visible.Left + visible.Width/2, midY = visible.Top + visible.Height/2;
            float dot = (2.5f + highlight) * dpi;
            using (var b = new SolidBrush(Theme.Mint))
                foreach (PointF p in new [] {new PointF(visible.Left,midY),new PointF(visible.Right,midY),
                    new PointF(midX,visible.Top),new PointF(midX,visible.Bottom)})
                    g.FillEllipse(b,p.X-dot,p.Y-dot,dot*2,dot*2);
        }

        private int Hit(Point pt)
        {
            RectangleF a = ShownArea();
            float margin = Math.Max(5, panel.Width * .014f);
            RectangleF bounds = a;
            bounds.Inflate(margin,margin);
            if (!bounds.Contains(pt)) return 0;
            int sides = 0;
            if (Math.Abs(pt.X-a.Left) < margin) sides |= LeftEdge;
            else if (Math.Abs(pt.X-a.Right) < margin) sides |= RightEdge;
            if (Math.Abs(pt.Y-a.Top) < margin) sides |= TopEdge;
            else if (Math.Abs(pt.Y-a.Bottom) < margin) sides |= BottomEdge;
            return sides == 0 ? MoveArea : sides;
        }

        private static Cursor CursorFor(int hit)
        {
            if (hit == MoveArea) return Cursors.SizeAll;
            if (hit == LeftEdge || hit == RightEdge) return Cursors.SizeWE;
            if (hit == TopEdge || hit == BottomEdge) return Cursors.SizeNS;
            if (hit == (LeftEdge | TopEdge) || hit == (RightEdge | BottomEdge)) return Cursors.SizeNWSE;
            return hit == 0 ? Cursors.Default : Cursors.SizeNESW;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || panel.Width <= 0) return;
            grab = Hit(e.Location);
            if (grab == 0) return;
            initial = RegionPixels;
            origin = e.Location;
            Capture = true;
        }

        private static int Clamp(int v, int lo, int hi) { return Math.Min(hi, Math.Max(lo, v)); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = grab == 0 ? Hit(e.Location) : grab;
            Cursor = CursorFor(hit);
            if (hovering != (hit != 0)) { hovering = hit != 0; fade.Start(); }
            if (grab == 0 || panel.Width <= 0) return;
            int dx = (int)Math.Round((e.X-origin.X) * PanelWidth/panel.Width);
            int dy = (int)Math.Round((e.Y-origin.Y) * PanelHeight/panel.Height);
            int l = initial.Left, t = initial.Top, r = Math.Min(PanelWidth,initial.Right), b = Math.Min(PanelHeight,initial.Bottom);
            if (grab == MoveArea)
            {
                dx = Clamp(dx,-l,PanelWidth-r);
                dy = Clamp(dy,-t,PanelHeight-b);
                l += dx; r += dx; t += dy; b += dy;
            }
            else
            {
                if ((grab & LeftEdge) != 0) l = Clamp(l+dx,0,r-1);
                if ((grab & RightEdge) != 0) r = Clamp(r+dx,l+1,PanelWidth);
                if ((grab & TopEdge) != 0) t = Clamp(t+dy,0,b-1);
                if ((grab & BottomEdge) != 0) b = Clamp(b+dy,t+1,PanelHeight);
            }
            if (Edited != null) Edited(Rectangle.FromLTRB(l,t,r,b));
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Capture = false; grab = 0; }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) grab = 0; }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; fade.Start(); }
        protected override void Dispose(bool disposing) { if (disposing) fade.Dispose(); base.Dispose(disposing); }
    }

    internal sealed class MainWindow : Form
    {
        private const int EmergencyId = 0x5241;
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "RightPanel";
        [DllImport("user32.dll", SetLastError=true)]
        private static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd,int id);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd,int key,ref int value,int size);
        private readonly NumericUpDown[] numbers = new NumericUpDown[5];
        private readonly ComboBox profiles = new ComboBox();
        private readonly Dictionary<string,int[]> saved = new Dictionary<string,int[]>(StringComparer.OrdinalIgnoreCase);
        private readonly MonitorPreview preview = new MonitorPreview();
        private readonly CheckBox startup = new CheckBox();
        private readonly Label actual = Theme.Label("",Theme.Ink,9f);
        private readonly Label note = Theme.Label("",Theme.Muted,9f);
        private readonly Label hotkey = Theme.Label("Восстановление   Ctrl + Alt + F11",Theme.Muted,9f);
        private readonly NotifyIcon icon = new NotifyIcon();
        private readonly Icon panelIcon = CreatePanelIcon();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolTip tips = new ToolTip();
        private readonly Button test = Theme.Button("Проверить режим",true);
        private readonly Button restore = Theme.Button("Восстановить",false);
        private readonly Button diag = Theme.Button("Диагностика",false);
        private readonly Button save = Theme.Button("Сохранить",false);
        private readonly Timer tick = new Timer { Interval = 350 };
        private bool startupSync, uiSync, busy, pending, registered, quitting, refreshing, refreshAgain;
        private bool canConfirm;
        private string notice = "";
        private readonly string profileFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RightPanel","profiles.xml");

        internal MainWindow(bool tray)
        {
            Text = "RightPanel";
            Icon = panelIcon;
            BackColor = Theme.Canvas;
            ForeColor = Theme.Ink;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96,96);
            Font = new Font("Segoe UI",10);
            ClientSize = new Size(602,627);
            MinimumSize = new Size(550,555);
            StartPosition = FormStartPosition.Manual;
            var screen = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(Math.Max(screen.Left,screen.Left + (screen.Width-Width)*2/3),
                Math.Max(screen.Top,screen.Top + (screen.Height-Height)/2));
            LayoutControls();
            LoadProfiles();
            startupSync = true;
            try { using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) startup.Checked = key != null && key.GetValue(RunName) != null; }
            catch (Exception ex) { Notice(ex.Message); }
            startupSync = false;
            icon.Text = "RightPanel";
            icon.Icon = panelIcon;
            menu.Items.Add("Открыть RightPanel",null,delegate { Reveal(); });
            menu.Items.Add("Восстановить исходный режим",null,delegate { Emergency(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход",null,delegate { quitting = true; Close(); });
            icon.ContextMenuStrip = menu;
            icon.DoubleClick += delegate { Reveal(); };
            icon.Visible = true;
            Shown += delegate
            {
                if (tray) BeginInvoke(new Action(Hide));
                RefreshActual();
                Poll();
                tick.Start();
            };
            Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            };
            tick.Tick += delegate { Poll(); };
        }

        private void LayoutControls()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18,9,18,12),
                Margin = Padding.Empty, ColumnCount = 1, RowCount = 12 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach (float height in new float[] {33,52,30,-1,22,25,36,39,39,38,34,32})
                layout.RowStyles.Add(height < 0 ? new RowStyle(SizeType.Percent,100) : new RowStyle(SizeType.Absolute,height));
            Controls.Add(layout);
            var heading = Theme.Label("RightPanel",Theme.Ink,17);
            heading.Font = new Font("Segoe UI Semibold",17);
            layout.Controls.Add(heading,0,0);
            actual.AccessibleName = "Фактические параметры активного дисплея";
            actual.Text = "Чтение параметров дисплея…";
            layout.Controls.Add(actual,0,1);
            var areaTitle = Theme.Label("Рабочая область",Theme.Ink,11);
            areaTitle.Font = new Font("Segoe UI Semibold",11);
            layout.Controls.Add(areaTitle,0,2);
            layout.Controls.Add(preview,0,3);
            layout.Controls.Add(Theme.Label("Матрица 2560 × 1080   ·   Перетащите границу или введите координаты.",Theme.Muted,9),0,4);
            TableLayoutPanel labels = Columns(5);
            TableLayoutPanel fields = Columns(5);
            string[] names = {"X, px","Y, px","Ширина, px","Высота, px","Частота, Гц"};
            int[] minima = {0,0,1,1,1}, maxima = {2559,1079,2560,1080,1000};
            for (int i=0;i<5;i++)
            {
                labels.Controls.Add(Theme.Label(names[i],Theme.Muted,9),i,0);
                var n = new NumericUpDown { Minimum = minima[i], Maximum = maxima[i], Dock = DockStyle.Fill,
                    AccessibleName = names[i], TextAlign = HorizontalAlignment.Left, ForeColor = Theme.Ink,
                    BackColor = Theme.Surface, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI",11) };
                numbers[i] = n;
                fields.Controls.Add(n,i,0);
                n.ValueChanged += delegate { UpdatePreview(); };
            }
            layout.Controls.Add(labels,0,5);
            layout.Controls.Add(fields,0,6);
            preview.Edited += delegate(Rectangle rect) { SetValues(new[]{rect.X,rect.Y,rect.Width,rect.Height,(int)numbers[4].Value}); };
            var row = new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,90));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,118));
            row.Controls.Add(Theme.Label("Профиль",Theme.Muted,9),0,0);
            profiles.DropDownStyle = ComboBoxStyle.DropDown;
            profiles.BackColor = Theme.Surface; profiles.ForeColor = Theme.Ink;
            profiles.Dock = DockStyle.Fill; profiles.AccessibleName = "Название профиля";
            profiles.Margin = new Padding(3,6,6,3); profiles.MaxLength = 100;
            profiles.SelectedIndexChanged += delegate {
                int[] v; if (profiles.SelectedItem != null && saved.TryGetValue(profiles.SelectedItem.ToString(),out v)) SetValues(v);
            };
            row.Controls.Add(profiles,1,0); row.Controls.Add(save,2,0);
            layout.Controls.Add(row,0,7);
            var actions = Columns(3);
            actions.Controls.Add(test,0,0); actions.Controls.Add(restore,1,0); actions.Controls.Add(diag,2,0);
            layout.Controls.Add(actions,0,8);
            layout.Controls.Add(note,0,9);
            startup.Text = "Запускать в трее при входе в Windows";
            startup.ForeColor = Theme.Ink; startup.Dock = DockStyle.Fill; startup.AutoSize = true;
            startup.CheckedChanged += delegate { SetStartup(); };
            layout.Controls.Add(startup,0,10);
            layout.Controls.Add(hotkey,0,11);
            tips.SetToolTip(restore,"Вернуть сохранённые исходные параметры монитора");
            tips.SetToolTip(save,"Сохранить текущие значения под именем из поля «Профиль»");
            tips.SetToolTip(test,"После проверки подтвердите изображение за 15 секунд");
            save.Click += delegate { SaveProfile(); };
            diag.Click += async delegate { await Report(); };
            restore.Click += delegate { Emergency(); };
            test.Click += async delegate
            {
                if (canConfirm) { await Command(Engine.Confirm,"Пробный режим подтверждён."); return; }
                if (!ValidBounds()) { Notice("Рабочая область выходит за границы матрицы."); return; }
                int[] v = Values();
                await Command(delegate { Engine.StartTrial(v[0],v[1],v[2],v[3],v[4]); },"Проверка отправлена драйверу.");
            };
            Notice("Пробный режим требует подтверждения в течение 15 секунд.");
        }

        private static TableLayoutPanel Columns(int count)
        {
            var p = new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=count,RowCount=1,Margin=Padding.Empty };
            for (int i=0;i<count;i++) p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100f/count));
            return p;
        }

        private static Icon CreatePanelIcon()
        {
            using (var bitmap = new Bitmap(32,32))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                using (var bg = new SolidBrush(Theme.Canvas)) g.FillRectangle(bg,1,5,30,22);
                using (var edge = new Pen(Theme.Muted,2)) g.DrawRectangle(edge,2,6,28,20);
                using (var zone = new SolidBrush(Theme.Mint)) g.FillRectangle(zone,12,9,15,14);
                IntPtr handle = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        private int[] Values() { return Array.ConvertAll(numbers,delegate(NumericUpDown n){return (int)n.Value;}); }
        private void SetValues(int[] values)
        {
            uiSync = true;
            try { for(int i=0;i<numbers.Length;i++) numbers[i].Value=values[i]; }
            finally { uiSync = false; }
            UpdatePreview();
        }
        private bool ValidBounds()
        {
            int[] v = Values();
            return v[0]+v[2]<=MonitorPreview.PanelWidth && v[1]+v[3]<=MonitorPreview.PanelHeight;
        }
        private void UpdatePreview()
        {
            if (uiSync) return;
            int[] v = Values();
            preview.RegionPixels = new Rectangle(v[0],v[1],v[2],v[3]);
            preview.Invalidate();
            RefreshButtons();
        }
        private void Notice(string s) { notice = s; note.Text = s; tips.SetToolTip(note,s); }
        private void Reveal() { Show(); WindowState=FormWindowState.Normal; Activate(); }

        private void RefreshButtons()
        {
            test.Text = canConfirm ? "Подтвердить" : "Проверить режим";
            test.Enabled = !busy && (canConfirm || (!pending && ValidBounds()));
            restore.Enabled = !busy;
            diag.Enabled = !busy;
            foreach (var n in numbers) n.Enabled = !pending && !busy;
            preview.Enabled = profiles.Enabled = save.Enabled = !pending && !busy;
        }

        private async void RefreshActual()
        {
            if (refreshing) { refreshAgain = true; return; }
            refreshing = true;
            try
            {
                do {
                    refreshAgain = false;
                    string s = await Task.Run(new Func<string>(Engine.Status));
                    if (IsDisposed) return;
                    actual.Text = s;
                    tips.SetToolTip(actual,s);
                } while (refreshAgain);
            }
            catch(Exception ex) { if (!IsDisposed) actual.Text = ex.Message; }
            finally { refreshing = false; }
        }

        private void Poll()
        {
            try {
                string state = Engine.TrialStatus() ?? "";
                bool oldPending = pending;
                canConfirm = Engine.CanConfirm();
                pending = state.Length > 0;
                string message = pending ? state : notice;
                if (note.Text != message) { note.Text = message; tips.SetToolTip(note,message); }
                if (pending != oldPending) RefreshActual();
                RefreshButtons();
            } catch (Exception ex) { canConfirm=false; pending=false; Notice(ex.Message); RefreshButtons(); }
        }

        private async Task Command(Action operation,string success)
        {
            if (busy) return;
            busy = true;
            RefreshButtons();
            try { await Task.Run(operation); if (!IsDisposed) Notice(success); }
            catch(Exception ex) { if (!IsDisposed) { Notice(ex.Message); ShowError(ex.Message); } }
            finally { busy = false; if (!IsDisposed) { Poll(); RefreshActual(); } }
        }

        private bool restoring;
        private async void Emergency()
        {
            if (restoring) return;
            restoring = true;
            try
            {
                if (!busy) { await Command(Engine.Restore,"Запрошено восстановление исходного режима."); }
                else { await Task.Run(new Action(Engine.Restore)); if (!IsDisposed) RefreshActual(); }
            } catch(Exception ex) { if (!IsDisposed) ShowError(ex.Message); }
            finally { restoring=false; }
        }

        private void ShowError(string s)
        {
            if (!Visible) { icon.ShowBalloonTip(5000,Text,s,ToolTipIcon.Warning); return; }
            MessageBox.Show(this,s,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }

        private async Task Report()
        {
            if (busy) return;
            busy = true; RefreshButtons();
            try {
                string s = await Task.Run(new Func<string>(Engine.Diagnose));
                if (IsDisposed) return;
                using (var f = new Form { Text="Диагностика RightPanel",StartPosition=FormStartPosition.CenterParent,
                    BackColor=Theme.Canvas,ForeColor=Theme.Ink,Size=new Size(680,460),MinimizeBox=false,Padding=new Padding(12) })
                using (var box = new TextBox { Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,WordWrap=false,
                    ScrollBars=ScrollBars.Both,BackColor=Theme.Surface,ForeColor=Theme.Ink,Font=new Font("Consolas",9.5f),
                    BorderStyle=BorderStyle.FixedSingle,Text=s.Replace("\r\n","\n").Replace("\n",Environment.NewLine) })
                {
                    var copy=Theme.Button("Скопировать",false);
                    copy.Dock=DockStyle.Bottom; copy.Height=36;
                    copy.Click += delegate { try { Clipboard.SetText(box.Text); copy.Text="Скопировано"; } catch(Exception ex) { ShowError(ex.Message); } };
                    f.Controls.Add(box); f.Controls.Add(copy); f.ShowDialog(this);
                }
            } catch(Exception ex) { if (!IsDisposed) ShowError(ex.Message); }
            finally { busy=false; if (!IsDisposed) Poll(); }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = 1;
            DwmSetWindowAttribute(Handle,20,ref dark,sizeof(int));
            registered = RegisterHotKey(Handle,EmergencyId,0x4003,(uint)Keys.F11);
            if (!registered) {
                hotkey.Text="Ctrl + Alt + F11 занята. Восстановление доступно в меню трея.";
                hotkey.ForeColor=Color.SandyBrown;
            }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == EmergencyId) { Emergency(); return; }
            base.WndProc(ref m);
            if (m.Msg == 0x007e && !IsDisposed && IsHandleCreated)
                BeginInvoke(new Action(RefreshActual));
        }
        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (registered) { UnregisterHotKey(Handle,EmergencyId); registered=false; }
            base.OnHandleDestroyed(e);
        }

        private void SetStartup()
        {
            if (startupSync) return;
            try { using(var key=Registry.CurrentUser.CreateSubKey(RunKey)) {
                if (startup.Checked) key.SetValue(RunName,"\""+Application.ExecutablePath+"\" --tray");
                else key.DeleteValue(RunName,false);
            } }
            catch(Exception ex) {
                startupSync = true; startup.Checked=!startup.Checked; startupSync=false; ShowError(ex.Message);
            }
        }

        private void LoadProfiles()
        {
            string name="Titan Army · 1881";
            int initialHz = 200; try { initialHz = (int)Math.Round(Ccd.Read()[0].Hz); } catch { }
            saved[name]=new[]{679,0,1881,1080,initialHz};
            saved["Titan Army · 1880"]=new[]{680,0,1880,1080,initialHz};
            try {
                if (File.Exists(profileFile)) {
                    var doc=new XmlDocument { XmlResolver=null };
                    using(var r=XmlReader.Create(profileFile,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,
                        MaxCharactersInDocument=512000,XmlResolver=null })) doc.Load(r);
                    foreach(XmlElement el in doc.SelectNodes("/profiles/p")) {
                        int[] v = new int[5];
                        string[] fields=el.GetAttribute("values").Split(',');
                        if(fields.Length!=5) continue;
                        bool valid=true;
                        for(int i=0;i<5;i++) {
                            if(!Int32.TryParse(fields[i],out v[i]) || v[i]<numbers[i].Minimum || v[i]>numbers[i].Maximum) valid=false;
                        }
                        string key=el.GetAttribute("name").Trim();
                        if(valid && key.Length>0 && key.Length<=100 && v[0]+v[2]<=2560 && v[1]+v[3]<=1080)
                            saved[key]=v;
                    }
                    var selected=doc.DocumentElement.GetAttribute("selected");
                    if(saved.ContainsKey(selected)) name=selected;
                }
            } catch(Exception ex) { Notice("Ошибка чтения профилей. "+ex.Message); }
            foreach(var key in saved.Keys) profiles.Items.Add(key);
            profiles.SelectedItem=name;
        }
        private void SaveProfile()
        {
            string name=profiles.Text.Trim();
            if(name.Length==0 || !ValidBounds()) { Notice("Введите имя профиля и допустимые координаты."); return; }
            string tmp=null;
            try {
                var next=new Dictionary<string,int[]>(saved,StringComparer.OrdinalIgnoreCase);
                next[name]=Values();
                Directory.CreateDirectory(Path.GetDirectoryName(profileFile));
                tmp=profileFile+"."+Guid.NewGuid().ToString("N")+".tmp";
                using(var writer=XmlWriter.Create(tmp,new XmlWriterSettings { Indent=true })) {
                    writer.WriteStartElement("profiles"); writer.WriteAttributeString("selected",name);
                    foreach(var pair in next) {
                        writer.WriteStartElement("p"); writer.WriteAttributeString("name",pair.Key);
                        writer.WriteAttributeString("values",String.Join(",",pair.Value)); writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                if(File.Exists(profileFile)) File.Replace(tmp,profileFile,null); else File.Move(tmp,profileFile);
                if(!saved.ContainsKey(name)) profiles.Items.Add(name);
                saved[name]=next[name];
                Notice("Профиль «"+name+"» сохранён.");
            } catch(Exception ex) { ShowError(ex.Message); }
            finally { if(tmp!=null && File.Exists(tmp)) try { File.Delete(tmp); } catch(IOException) {} }
        }

        protected override void Dispose(bool disposing)
        {
            if(disposing) {
                tick.Dispose(); tips.Dispose(); icon.Visible=false; icon.Dispose(); menu.Dispose(); panelIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

