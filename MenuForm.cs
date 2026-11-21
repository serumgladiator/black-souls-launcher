using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BS
{
    sealed class ShellForm : Form
    {
        const int HitClose = 1;
        const int HitMin = 2;
        const int HitRow1 = 3;
        const int HitRow2 = 4;
        const int HitPlay = 5;
        const int HitPath = 6;
        const int HitScan = 7;
        const int HitResL = 8;
        const int HitResR = 9;

        struct ScreenMode
        {
            public int W;
            public int H;
            public string Name;
            public bool Screen;
        }

        static readonly ScreenMode[] Modes = {
            new ScreenMode { W = 640, H = 480, Name = "640x480", Screen = false },
            new ScreenMode { W = 960, H = 720, Name = "960x720", Screen = false },
            new ScreenMode { W = 1280, H = 960, Name = "1280x960", Screen = false },
            new ScreenMode { W = 1600, H = 1200, Name = "1600x1200", Screen = false },
            new ScreenMode { W = 0, H = 0, Name = "Экран", Screen = true }
        };

        static readonly Color Ink = Color.FromArgb(12, 0, 6);
        static readonly Color Label = Color.FromArgb(198, 228, 255);
        static readonly Color Paper = Color.White;

        readonly GameSearch search = new GameSearch();
        readonly StringFormat fmt = new StringFormat(StringFormat.GenericTypographic);

        Bitmap skin;
        Font fontTitle;
        Font fontText;
        Font fontSmall;
        System.Windows.Forms.Timer blinkTimer;

        float scale = 1f;
        int border, topH, divH, botH;
        int sel;
        int mode;
        int hoverCmd;
        int pressed;
        bool blink = true;
        Bitmap plate;
        bool plateDirty = true;
        float loreOffset;
        float loreWidth;
        Rectangle loreRect;
        Bitmap loreBmp;
        System.Windows.Forms.Timer loreTimer;
        string loreText;

        static readonly string[] Lore = {
            "Гримм охотится на сказки, а не спасает их",
            "SEN — это рассудок. От него зависит, какой вы видите Страну чудес",
            "Смерть не стирает путь: души остаются и уходят в уровень",
            "Алиса, Красная Шапочка и Дороти здесь давно сбились со своих историй",
            "Чеширский Кот улыбается даже тогда, когда от него ничего не осталось",
            "Королева Червей по-прежнему кончает споры отрубанием головы",
            "Белый Кролик опаздывает и в этой сказке",
            "Шляпник так и не встал из-за чайного стола",
            "Вторая часть уводит дальше Страны чудес, к другим сказкам",
            "Концовок много, и не каждую стоит называть спасением",
            "Описания предметов часто говорят больше, чем сами герои",
            "Убитый персонаж уже не даст следующего совета",
            "Обе части сделаны на RPG Maker, но устроены как путь за душами",
            "Высокий и низкий SEN показывают разные лица одного места",
            "Жители страны врут охотнее, чем карта",
            "Если тропа кажется доброй, пройдите её ещё раз"
        };

        string path1;
        string path2;
        int shown1;
        int shown2;
        string status = "Ищем...";
        int scanGen;
        volatile bool scanning;
        volatile bool wantDeep;
        volatile bool deepMode;
        Thread scanThread;

        Rectangle rClose, rMin, rRow1, rRow2, rPlay, rPath, rScan, rResL, rResR;

        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        public ShellForm()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            UpdateStyles();
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(158, 18, 28);
            Text = "Black Souls";
            AllowDrop = true;
            KeyPreview = true;
            fmt.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                scale = Math.Max(1f, g.DpiX / 96f);
            LoadSaved();
            LayoutAll();
            blinkTimer = new System.Windows.Forms.Timer();
            blinkTimer.Interval = 460;
            blinkTimer.Tick += delegate
            {
                if (IsDisposed || WindowState == FormWindowState.Minimized) return;
                blink = !blink;
                Touch();
            };
            blinkTimer.Start();
            loreText = string.Join("    *    ", Lore) + "    *    ";
            loreTimer = new System.Windows.Forms.Timer();
            loreTimer.Interval = 40;
            loreTimer.Tick += delegate
            {
                if (IsDisposed || WindowState == FormWindowState.Minimized) return;
                loreOffset -= Math.Max(1f, scale);
                if (loreWidth > 0f && loreOffset <= -loreWidth) loreOffset += loreWidth;
                Invalidate(loreRect);
            };
            loreTimer.Start();
        }

        void Touch()
        {
            plateDirty = true;
            Invalidate();
        }

        int S(int px)
        {
            return (int)Math.Round(px * scale);
        }

        Font Make(int px)
        {
            try { return new Font("Courier New", px, FontStyle.Bold, GraphicsUnit.Pixel); }
            catch { return new Font(FontFamily.GenericMonospace, px, FontStyle.Bold, GraphicsUnit.Pixel); }
        }

        void LayoutAll()
        {
            if (fontTitle != null) fontTitle.Dispose();
            if (fontText != null) fontText.Dispose();
            if (fontSmall != null) fontSmall.Dispose();
            fontTitle = Make(S(20));
            fontText = Make(S(17));
            fontSmall = Make(S(15));

            border = S(16);
            topH = S(172);
            divH = S(12);
            botH = S(108);
            int width = S(520);
            int height = border + topH + divH + botH + border;
            ClientSize = new Size(width, height);

            int innerX = border + S(14);
            int innerW = width - border * 2 - S(28);
            int rowW = innerW;
            int rowH = S(58);
            int row1 = border + S(40);
            rRow1 = new Rectangle(innerX, row1, rowW, rowH);
            rRow2 = new Rectangle(innerX, row1 + rowH + S(6), rowW, rowH);

            int botY = border + topH + divH;
            loreRect = new Rectangle(innerX, botY + S(6), innerW, S(24));
            loreWidth = 0f;
            if (loreBmp != null) { loreBmp.Dispose(); loreBmp = null; }

            int btn = S(22);
            rClose = new Rectangle(width - S(8) - btn, S(6), btn, btn);
            rMin = new Rectangle(rClose.X - S(6) - btn, S(6), btn, btn);

            int resY = botY + S(32);
            rResL = new Rectangle(innerX, resY, S(22), S(22));
            rResR = new Rectangle(innerX + S(148), resY, S(22), S(22));
            int cmdY = botY + S(60);
            rPlay = new Rectangle(innerX, cmdY, S(120), S(26));
            rPath = new Rectangle(innerX + S(132), cmdY, S(80), S(26));
            rScan = new Rectangle(innerX + S(220), cmdY, S(120), S(26));

            if (skin != null) { skin.Dispose(); skin = null; }
            if (plate != null) { plate.Dispose(); plate = null; }
            plateDirty = true;
        }

        void BuildSkin()
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            if (skin != null) skin.Dispose();
            skin = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(skin))
            {
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                FillPattern(g, w, h);
                int divY = border + topH;
                using (var br = new SolidBrush(Color.FromArgb(176, 24, 78)))
                    g.FillRectangle(br, border, divY, w - border * 2, divH);
                using (var br = new SolidBrush(Color.FromArgb(255, 176, 206)))
                {
                    g.FillRectangle(br, border, divY + S(2), w - border * 2, 1);
                    g.FillRectangle(br, border, divY + divH - S(3), w - border * 2, 1);
                }
                using (var br = new SolidBrush(Color.FromArgb(255, 236, 244)))
                    g.FillRectangle(br, border, divY + divH / 2, w - border * 2, 1);
                DrawFrame(g, w, h, border);
                DrawJewels(g, w, h, border);
            }
        }

        void FillPattern(Graphics g, int w, int h)
        {
            int cell = Math.Max(16, S(28));
            using (var br = new SolidBrush(Color.Black))
            {
                for (int y = 0; y < h; y += cell)
                {
                    for (int x = 0; x < w; x += cell)
                    {
                        bool red = ((x / cell) + (y / cell)) % 2 == 0;
                        br.Color = red ? Color.FromArgb(168, 20, 32) : Color.FromArgb(16, 0, 4);
                        g.FillRectangle(br, x, y, cell, cell);
                        int cx = x + cell / 2;
                        int cy = y + cell / 2;
                        int rad = Math.Max(3, cell / 7);
                        if (red) DrawClub(g, br, cx, cy, rad, Color.FromArgb(32, 0, 4));
                        else DrawSpade(g, br, cx, cy, rad, Color.FromArgb(86, 12, 22));
                    }
                }
            }
        }

        static void DrawClub(Graphics g, SolidBrush br, int cx, int cy, int r, Color color)
        {
            br.Color = color;
            g.FillEllipse(br, cx - r * 2, cy - r, r * 2, r * 2);
            g.FillEllipse(br, cx, cy - r, r * 2, r * 2);
            g.FillEllipse(br, cx - r, cy - r * 2, r * 2, r * 2);
            g.FillRectangle(br, cx - Math.Max(1, r / 3), cy, Math.Max(2, r / 2), r * 2);
        }

        static void DrawSpade(Graphics g, SolidBrush br, int cx, int cy, int r, Color color)
        {
            br.Color = color;
            Point[] pts = {
                new Point(cx, cy - r * 2),
                new Point(cx + r * 2, cy + r),
                new Point(cx - r * 2, cy + r)
            };
            g.FillPolygon(br, pts);
            g.FillEllipse(br, cx - r * 2, cy, r * 2, r * 2);
            g.FillEllipse(br, cx, cy, r * 2, r * 2);
            g.FillRectangle(br, cx - Math.Max(1, r / 3), cy + r, Math.Max(2, r / 2), r * 2);
        }

        static void DrawFrame(Graphics g, int w, int h, int t)
        {
            using (var br = new SolidBrush(Color.Black))
            {
                for (int i = 0; i < t; i++)
                {
                    br.Color = FrameColor(i, t);
                    g.FillRectangle(br, i, i, w - i * 2, 1);
                    g.FillRectangle(br, i, h - 1 - i, w - i * 2, 1);
                    g.FillRectangle(br, i, i, 1, h - i * 2);
                    g.FillRectangle(br, w - 1 - i, i, 1, h - i * 2);
                }
            }
        }

        static Color FrameColor(int i, int t)
        {
            if (i == 0 || i == t - 1) return Color.FromArgb(16, 0, 8);
            if (i == 2 || i == t - 3) return Color.FromArgb(255, 186, 214);
            if (i == t / 2) return Color.FromArgb(255, 236, 244);
            if (i % 3 == 0) return Color.FromArgb(214, 36, 96);
            return Color.FromArgb(132, 14, 52);
        }

        static void DrawJewels(Graphics g, int w, int h, int t)
        {
            int mid = Math.Max(2, t / 2);
            int step = Math.Max(10, t);
            using (var br = new SolidBrush(Color.FromArgb(255, 236, 246)))
            {
                for (int x = t + step; x < w - t - 2; x += step)
                {
                    g.FillRectangle(br, x, mid - 1, 2, 2);
                    g.FillRectangle(br, x, h - mid - 1, 2, 2);
                }
                for (int y = t + step; y < h - t - 2; y += step)
                {
                    g.FillRectangle(br, mid - 1, y, 2, 2);
                    g.FillRectangle(br, w - mid - 1, y, 2, 2);
                }
            }
        }

        void EnsurePlate()
        {
            if (skin == null || skin.Width != ClientSize.Width || skin.Height != ClientSize.Height)
                BuildSkin();
            if (plate != null && !plateDirty && plate.Width == ClientSize.Width && plate.Height == ClientSize.Height)
                return;
            if (plate != null) plate.Dispose();
            plate = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(plate))
            {
                g.SmoothingMode = SmoothingMode.None;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.DrawImageUnscaled(skin, 0, 0);
                DrawText(g, "BLACK SOULS", fontTitle, border + S(16), border + S(8), Paper);
                DrawRow(g, rRow1, sel == 0, "BLACK SOULS", path1);
                DrawRow(g, rRow2, sel == 1, "BLACK SOULS II", path2);
                string scanLabel = ScanCaption();
                DrawResolution(g);
                DrawCommand(g, rPlay, "Запуск", hoverCmd == HitPlay);
                DrawCommand(g, rPath, "Путь", hoverCmd == HitPath);
                DrawCommand(g, rScan, scanLabel, hoverCmd == HitScan);
                if (scanning || Missing())
                {
                    string line = status;
                    if (scanning)
                    {
                        int dots = (Environment.TickCount / 380) % 3 + 1;
                        line = status.TrimEnd('.') + new string('.', dots);
                    }
                    int sx = rScan.Right + S(8);
                    int sw = ClientSize.Width - border - S(10) - sx;
                    if (sw > S(40))
                        DrawText(g, Ellipsize(g, line, fontSmall, sw), fontSmall, sx, rPlay.Y + S(2), Label);
                }
                DrawWindowButton(g, rClose, true, hoverCmd == HitClose);
                DrawWindowButton(g, rMin, false, hoverCmd == HitMin);
            }
            plateDirty = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            EnsurePlate();
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.DrawImageUnscaled(plate, 0, 0);
            DrawLore(g);
        }

        void EnsureLore()
        {
            if (loreBmp != null) return;
            int h = Math.Max(8, loreRect.Height);
            using (var probe = new Bitmap(1, 1))
            using (Graphics pg = Graphics.FromImage(probe))
            {
                pg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                loreWidth = pg.MeasureString(loreText, fontSmall, 20000, fmt).Width;
            }
            int w = Math.Max(8, (int)Math.Ceiling(loreWidth) + S(4));
            loreBmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics lg = Graphics.FromImage(loreBmp))
            {
                lg.Clear(Color.Transparent);
                lg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                DrawText(lg, loreText, fontSmall, S(2), S(2), Paper);
            }
            loreWidth = loreBmp.Width;
        }

        void DrawLore(Graphics g)
        {
            if (loreRect.Width < 8) return;
            EnsureLore();
            using (var band = new SolidBrush(Color.FromArgb(210, 8, 0, 4)))
                g.FillRectangle(band, loreRect);
            int bmpW = loreBmp.Width;
            int bmpH = loreBmp.Height;
            int shift = (int)loreOffset % bmpW;
            if (shift > 0) shift -= bmpW;
            int srcX = -shift;
            int visible = loreRect.Width;
            int first = Math.Min(bmpW - srcX, visible);
            g.DrawImage(loreBmp, new Rectangle(loreRect.X, loreRect.Y, first, loreRect.Height), new Rectangle(srcX, 0, first, bmpH), GraphicsUnit.Pixel);
            if (first < visible)
            {
                int rest = visible - first;
                g.DrawImage(loreBmp, new Rectangle(loreRect.X + first, loreRect.Y, rest, loreRect.Height), new Rectangle(0, 0, rest, bmpH), GraphicsUnit.Pixel);
            }
        }

        bool Missing()
        {
            return string.IsNullOrEmpty(path1) || string.IsNullOrEmpty(path2);
        }

        void DrawRow(Graphics g, Rectangle row, bool selected, string name, string exe)
        {
            int textX = row.X + S(22);
            int nameY = row.Y + S(6);
            if (selected)
            {
                var hi = new Rectangle(row.X + S(16), nameY - S(2), S(250), S(20));
                using (var br = new LinearGradientBrush(hi, Color.FromArgb(120, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 0f))
                    g.FillRectangle(br, hi);
                if (blink)
                    DrawArrow(g, row.X + S(2), nameY + S(3));
            }
            DrawText(g, name, fontText, textX, nameY, Paper);
            string where = string.IsNullOrEmpty(exe) ? "Не найдена" : Path.GetDirectoryName(exe);
            int pathW = row.Right - textX - S(4);
            DrawText(g, Ellipsize(g, where, fontSmall, pathW), fontSmall, textX, nameY + S(22), Label);
        }

        void DrawArrow(Graphics g, int x, int y)
        {
            Point[] pts = {
                new Point(x, y),
                new Point(x, y + S(10)),
                new Point(x + S(8), y + S(5))
            };
            using (var br = new SolidBrush(Paper))
                g.FillPolygon(br, pts);
        }

        void DrawResolution(Graphics g)
        {
            string name = Modes[mode].Name;
            DrawText(g, "<", fontText, rResL.X + S(2), rResL.Y, hoverCmd == HitResL ? Paper : Label);
            DrawText(g, name, fontText, rResL.Right + S(8), rResL.Y, Paper);
            DrawText(g, ">", fontText, rResR.X + S(2), rResR.Y, hoverCmd == HitResR ? Paper : Label);
            DrawText(g, "Разрешение", fontSmall, rResR.Right + S(14), rResL.Y + S(2), Label);
        }

        void DrawCommand(Graphics g, Rectangle r, string caption, bool hot)
        {
            if (hot)
            {
                SizeF sz = g.MeasureString(caption, fontText, 400, fmt);
                var hi = new Rectangle(r.X - S(4), r.Y, (int)sz.Width + S(16), r.Height);
                using (var br = new LinearGradientBrush(hi, Color.FromArgb(160, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 0f))
                    g.FillRectangle(br, hi);
            }
            DrawText(g, caption, fontText, r.X, r.Y + S(2), hot ? Paper : Label);
        }

        void DrawWindowButton(Graphics g, Rectangle r, bool cross, bool hot)
        {
            using (var br = new SolidBrush(Color.FromArgb(18, 0, 8)))
                g.FillRectangle(br, r);
            if (hot)
            {
                using (var br = new LinearGradientBrush(r, Color.FromArgb(230, 255, 255, 255), Color.FromArgb(90, 255, 255, 255), 90f))
                    g.FillRectangle(br, r);
            }
            using (var pen = new Pen(hot ? Paper : Color.FromArgb(255, 196, 220), Math.Max(1f, scale)))
                g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
            Color mark = hot ? Ink : Paper;
            float w = Math.Max(2f, scale * 2f);
            int pad = Math.Max(5, r.Width / 4);
            using (var pen = new Pen(mark, w))
            {
                pen.StartCap = LineCap.Square;
                pen.EndCap = LineCap.Square;
                if (cross)
                {
                    g.DrawLine(pen, r.X + pad, r.Y + pad, r.Right - pad, r.Bottom - pad);
                    g.DrawLine(pen, r.Right - pad, r.Y + pad, r.X + pad, r.Bottom - pad);
                }
                else
                {
                    int y = r.Y + r.Height / 2;
                    g.DrawLine(pen, r.X + pad, y, r.Right - pad, y);
                }
            }
        }

        void DrawText(Graphics g, string text, Font font, float x, float y, Color color)
        {
            int o = Math.Max(1, (int)Math.Round(scale));
            using (var shadow = new SolidBrush(Color.Black))
            {
                for (int dy = -o; dy <= o; dy++)
                {
                    for (int dx = -o; dx <= o; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        g.DrawString(text, font, shadow, x + dx, y + dy, fmt);
                    }
                }
            }
            using (var br = new SolidBrush(color))
                g.DrawString(text, font, br, x, y, fmt);
        }

        string Ellipsize(Graphics g, string text, Font font, int width)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (width < 8) return "";
            if (g.MeasureString(text, font, 4000, fmt).Width <= width) return text;
            int n = text.Length;
            while (n > 4)
            {
                n--;
                string s = text.Substring(0, n) + "...";
                if (g.MeasureString(s, font, 4000, fmt).Width <= width) return s;
            }
            return "...";
        }

        string ScanCaption()
        {
            if (scanning && (deepMode || wantDeep)) return "Стоп";
            return "Поиск";
        }

        int HitTest(Point p)
        {
            if (rClose.Contains(p)) return HitClose;
            if (rMin.Contains(p)) return HitMin;
            if (rPlay.Contains(p)) return HitPlay;
            if (rPath.Contains(p)) return HitPath;
            if (rScan.Contains(p)) return HitScan;
            if (rResL.Contains(p)) return HitResL;
            if (rResR.Contains(p)) return HitResR;
            if (new Rectangle(rResL.X, rResL.Y, rResR.Right - rResL.X, rResL.Height).Contains(p)) return HitResR;
            if (rRow1.Contains(p)) return HitRow1;
            if (rRow2.Contains(p)) return HitRow2;
            return 0;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = HitTest(e.Location);
            int cmd = 0;
            if (hit == HitClose || hit == HitMin || hit == HitPlay || hit == HitPath || hit == HitScan || hit == HitResL || hit == HitResR) cmd = hit;
            if (hit == HitRow1 && sel != 0) { sel = 0; blink = true; }
            if (hit == HitRow2 && sel != 1) { sel = 1; blink = true; }
            if (cmd != hoverCmd || hit == HitRow1 || hit == HitRow2)
            {
                hoverCmd = cmd;
                Cursor = hit != 0 ? Cursors.Hand : Cursors.Default;
                Touch();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverCmd = 0;
            Cursor = Cursors.Default;
            Touch();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            pressed = HitTest(e.Location);
            if (pressed == 0)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, 0x2, 0);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int hit = HitTest(e.Location);
            if (hit != 0 && hit == pressed) Activate(hit);
            pressed = 0;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == Keys.Up || keyData == Keys.Down)
            {
                sel = sel == 0 ? 1 : 0;
                blink = true;
                Touch();
                return true;
            }
            if (keyData == Keys.Left) { StepMode(-1); return true; }
            if (keyData == Keys.Right) { StepMode(1); return true; }
            if (keyData == Keys.Enter) { Launch(sel == 0 ? 1 : 2); return true; }
            if (keyData == Keys.D1 || keyData == Keys.NumPad1) { sel = 0; blink = true; Touch(); return true; }
            if (keyData == Keys.D2 || keyData == Keys.NumPad2) { sel = 1; blink = true; Touch(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Activate(int hit)
        {
            if (hit == HitClose) Close();
            else if (hit == HitMin) WindowState = FormWindowState.Minimized;
            else if (hit == HitRow1) sel = 0;
            else if (hit == HitRow2) sel = 1;
            else if (hit == HitPlay) Launch(sel == 0 ? 1 : 2);
            else if (hit == HitResL) StepMode(-1);
            else if (hit == HitResR) StepMode(1);
            else if (hit == HitPath) Browse(sel == 0 ? 1 : 2);
            else if (hit == HitScan) OnScan();
            Touch();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (blinkTimer == null) return;
            if (WindowState == FormWindowState.Minimized)
            {
                blinkTimer.Stop();
                if (loreTimer != null) loreTimer.Stop();
            }
            else
            {
                if (!blinkTimer.Enabled) blinkTimer.Start();
                if (loreTimer != null && !loreTimer.Enabled) loreTimer.Start();
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            StartScan(false);
        }

        void LoadSaved()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\BlackSoulsLauncher"))
                {
                    if (k == null) return;
                    string a = k.GetValue("Path1") as string;
                    string b = k.GetValue("Path2") as string;
                    if (!string.IsNullOrEmpty(a) && File.Exists(a))
                    {
                        path1 = a;
                        shown1 = 100000;
                        search.Pin(1);
                    }
                    if (!string.IsNullOrEmpty(b) && File.Exists(b))
                    {
                        path2 = b;
                        shown2 = 100000;
                        search.Pin(2);
                    }
                    object savedMode = k.GetValue("Resolution");
                    if (savedMode != null)
                    {
                        int parsed;
                        if (int.TryParse(savedMode.ToString(), out parsed) && parsed >= 0 && parsed < Modes.Length)
                            mode = parsed;
                    }
                }
            }
            catch { }
            if (path1 != null || path2 != null) RefreshStatus();
        }

        void SavePaths()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\BlackSoulsLauncher"))
                {
                    if (k == null) return;
                    k.SetValue("Path1", path1 ?? "");
                    k.SetValue("Path2", path2 ?? "");
                    k.SetValue("Resolution", mode);
                }
            }
            catch { }
        }

        void StepMode(int dir)
        {
            mode = (mode + dir) % Modes.Length;
            if (mode < 0) mode += Modes.Length;
            SavePaths();
            Touch();
        }

        void Launch(int part)
        {
            string exe = part == 1 ? path1 : path2;
            if (string.IsNullOrEmpty(exe))
            {
                Browse(part);
                return;
            }
            if (!File.Exists(exe))
            {
                if (part == 1) { path1 = null; shown1 = 0; search.Forget(1); }
                else { path2 = null; shown2 = 0; search.Forget(2); }
                SavePaths();
                status = "Файл запуска больше не на месте";
                Touch();
                return;
            }
            try
            {
                string name = Path.GetFileNameWithoutExtension(exe);
                Process[] procs = Process.GetProcessesByName(name);
                for (int i = 0; i < procs.Length; i++)
                {
                    try
                    {
                        if (string.Equals(procs[i].MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase))
                        {
                            status = part == 1 ? "Часть I уже запущена" : "Часть II уже запущена";
                            Touch();
                            Process running = procs[i];
                            ScreenMode chosen = Modes[mode];
                            ThreadPool.QueueUserWorkItem(delegate
                            {
                                try { GameWindow.Fit(running, chosen.W, chosen.H, chosen.Screen); }
                                catch { }
                            });
                            return;
                        }
                    }
                    catch { }
                }
                var psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                psi.UseShellExecute = true;
                Process game = Process.Start(psi);
                status = "Запуск...";
                Touch();
                ScreenMode picked = Modes[mode];
                Thread worker = new Thread(delegate ()
                {
                    try { GameWindow.Fit(game, picked.W, picked.H, picked.Screen); }
                    catch { }
                    BeginSafe(delegate { Close(); });
                });
                worker.IsBackground = true;
                worker.Start();
            }
            catch (Exception)
            {
                status = "Не удалось запустить";
                Touch();
            }
        }

        void Browse(int part)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = part == 1 ? "Файл запуска BLACK SOULS" : "Файл запуска BLACK SOULS II";
                dlg.Filter = "Файл запуска (*.exe)|*.exe";
                dlg.CheckFileExists = true;
                string current = part == 1 ? path1 : path2;
                if (!string.IsNullOrEmpty(current))
                {
                    try { dlg.InitialDirectory = Path.GetDirectoryName(current); }
                    catch { }
                }
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Assign(part, dlg.FileName, true);
            }
        }

        void Assign(int part, string exe, bool explicitChoice)
        {
            int detected = search.ClassifyFile(exe);
            if (!explicitChoice && detected != 0 && detected != part) part = detected;
            if (explicitChoice && detected != 0 && detected != part)
                status = detected == 2 ? "Похоже на часть II" : "Похоже на часть I";
            if (part == 1)
            {
                path1 = exe;
                shown1 = 100000;
                search.Pin(1);
                sel = 0;
            }
            else
            {
                path2 = exe;
                shown2 = 100000;
                search.Pin(2);
                sel = 1;
            }
            SavePaths();
            if (detected == 0 && explicitChoice) status = "Путь сохранён";
            else if (!(detected != 0 && explicitChoice && status.StartsWith("Похоже"))) RefreshStatus();
            Touch();
        }

        void OnScan()
        {
            if (scanning)
            {
                if (!deepMode && !wantDeep)
                {
                    wantDeep = true;
                    deepMode = true;
                    PostStatus(scanGen, "Смотрим диски");
                    return;
                }
                search.Cancel();
                PostStatus(scanGen, "Останавливаем поиск");
                return;
            }
            StartScan(true);
        }

        void StartScan(bool deep)
        {
            if (scanThread != null && scanThread.IsAlive) return;
            search.Reset();
            scanning = true;
            deepMode = deep;
            wantDeep = deep;
            int gen = scanGen;
            scanThread = new Thread(delegate () { RunScan(deep, gen); });
            scanThread.IsBackground = true;
            scanThread.Priority = ThreadPriority.BelowNormal;
            scanThread.Start();
        }

        void RunScan(bool deep, int gen)
        {
            try
            {
                PostStatus(gen, "Ищем");
                search.Fast(delegate (int part, string exe, int score) { Suggest(part, exe, score); },
                    delegate (string s) { PostStatus(gen, s); });
                if (search.IsCancelled) return;
                if (deep || wantDeep)
                {
                    deepMode = true;
                    PostStatus(gen, "Смотрим диски");
                    search.Deep(delegate (int part, string exe, int score) { Suggest(part, exe, score); },
                        delegate (string s) { PostStatus(gen, s); });
                }
            }
            catch
            {
                PostStatus(gen, "Поиск прервался");
            }
            finally
            {
                BeginSafe(delegate
                {
                    if (gen != scanGen) return;
                    scanGen++;
                    scanning = false;
                    wantDeep = false;
                    deepMode = false;
                    RefreshStatus();
                    Touch();
                });
            }
        }

        void Suggest(int part, string exe, int score)
        {
            BeginSafe(delegate
            {
                if (part == 1)
                {
                    if (score <= shown1) return;
                    shown1 = score;
                    path1 = exe;
                }
                else
                {
                    if (score <= shown2) return;
                    shown2 = score;
                    path2 = exe;
                }
                SavePaths();
                Touch();
            });
        }

        void PostStatus(int gen, string text)
        {
            if (!string.IsNullOrEmpty(text) && text.Length > 42)
                text = "..." + text.Substring(text.Length - 38);
            BeginSafe(delegate
            {
                if (gen != scanGen) return;
                status = text;
                Touch();
            });
        }

        void RefreshStatus()
        {
            if (!string.IsNullOrEmpty(path1) && !string.IsNullOrEmpty(path2))
                status = "Обе части найдены";
            else if (!string.IsNullOrEmpty(path1))
                status = "Часть II не найдена";
            else if (!string.IsNullOrEmpty(path2))
                status = "Часть I не найдена";
            else
                status = "Укажите файл запуска";
        }

        void BeginSafe(Action action)
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke(action);
            }
            catch (InvalidOperationException) { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            search.Cancel();
            scanGen++;
            base.OnFormClosing(e);
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null) return;
            for (int i = 0; i < files.Length; i++)
                TakeDrop(files[i]);
        }

        void TakeDrop(string file)
        {
            try
            {
                if (Directory.Exists(file))
                {
                    string game = Path.Combine(file, "Game.exe");
                    if (File.Exists(game)) { TakeExe(game); return; }
                    string[] exes = Directory.GetFiles(file, "*.exe");
                    for (int i = 0; i < exes.Length; i++) TakeExe(exes[i]);
                    return;
                }
                if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string target = GameSearch.ResolveLink(file);
                    if (!string.IsNullOrEmpty(target)) TakeExe(target);
                    return;
                }
                if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    TakeExe(file);
            }
            catch { }
        }

        void TakeExe(string exe)
        {
            int part = search.ClassifyFile(exe);
            if (part == 0)
            {
                status = "Это не похоже на Black Souls";
                Touch();
                return;
            }
            Assign(part, exe, false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (blinkTimer != null) blinkTimer.Dispose();
                if (loreTimer != null) loreTimer.Dispose();
                if (plate != null) plate.Dispose();
                if (loreBmp != null) loreBmp.Dispose();
                if (skin != null) skin.Dispose();
                if (fontTitle != null) fontTitle.Dispose();
                if (fontText != null) fontText.Dispose();
                if (fontSmall != null) fontSmall.Dispose();
                fmt.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    static class GameWindow
    {
        const uint SwpFrame = 0x0020;
        const uint SwpShow = 0x0040;

        public static void Fit(Process game, int width, int height, bool screen)
        {
            if (game == null) return;
            IntPtr hwnd = IntPtr.Zero;
            int ready = 0;
            for (int i = 0; i < 40; i++)
            {
                try { if (game.HasExited) return; }
                catch { return; }
                hwnd = Find(game.Id);
                if (hwnd != IntPtr.Zero)
                {
                    int cw;
                    int ch;
                    Client(hwnd, out cw, out ch);
                    if (cw >= 600) ready++;
                    if (ready >= 3) break;
                }
                Thread.Sleep(200);
            }
            if (hwnd == IntPtr.Zero) hwnd = Find(game.Id);
            if (hwnd == IntPtr.Zero) return;
            Place(hwnd, width, height, screen);
            Thread.Sleep(700);
            hwnd = Find(game.Id);
            if (hwnd != IntPtr.Zero) Place(hwnd, width, height, screen);
        }

        static void Place(IntPtr hwnd, int width, int height, bool screen)
        {
            Rect window;
            Rect client;
            if (!GetWindowRect(hwnd, out window) || !GetClientRect(hwnd, out client)) return;
            int frameX = (window.R - window.L) - (client.R - client.L);
            int frameY = (window.B - window.T) - (client.B - client.T);
            Rect work = WorkArea(hwnd);
            int maxW = Math.Max(320, work.R - work.L);
            int maxH = Math.Max(240, work.B - work.T);
            int targetW = width;
            int targetH = height;
            if (screen)
            {
                targetH = maxH;
                targetW = targetH * 4 / 3;
                if (targetW > maxW)
                {
                    targetW = maxW;
                    targetH = targetW * 3 / 4;
                }
                int style = GetStyle(hwnd);
                style &= ~0x00C00000;
                style &= ~0x00040000;
                SetStyle(hwnd, style);
                frameX = 0;
                frameY = 0;
            }
            else if (targetW + frameX > maxW || targetH + frameY > maxH)
            {
                int fitW = maxW - frameX;
                int fitH = maxH - frameY;
                if (targetW * fitH > targetH * fitW)
                {
                    fitH = fitW * targetH / targetW;
                }
                else
                {
                    fitW = fitH * targetW / targetH;
                }
                targetW = Math.Max(320, fitW);
                targetH = Math.Max(240, fitH);
            }
            int outerW = targetW + frameX;
            int outerH = targetH + frameY;
            int x = work.L + Math.Max(0, (maxW - outerW) / 2);
            int y = work.T + Math.Max(0, (maxH - outerH) / 2);
            SetWindowPos(hwnd, IntPtr.Zero, x, y, outerW, outerH, SwpFrame | SwpShow);
        }

        static Rect WorkArea(IntPtr hwnd)
        {
            IntPtr monitor = MonitorFromWindow(hwnd, 2);
            MonitorInfo info = new MonitorInfo();
            info.Size = Marshal.SizeOf(typeof(MonitorInfo));
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)) return info.Work;
            Rect fallback = new Rect();
            fallback.R = 1280;
            fallback.B = 720;
            return fallback;
        }

        static IntPtr Find(int pid)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                uint owner;
                GetWindowThreadProcessId(h, out owner);
                if (owner != (uint)pid || !IsWindowVisible(h)) return true;
                StringBuilder cls = new StringBuilder(64);
                GetClassName(h, cls, 64);
                found = h;
                if (cls.ToString() == "RGSS Player") return false;
                return true;
            }, IntPtr.Zero);
            return found;
        }

        static void Client(IntPtr hwnd, out int w, out int h)
        {
            Rect r;
            if (!GetClientRect(hwnd, out r)) { w = 0; h = 0; return; }
            w = r.R - r.L;
            h = r.B - r.T;
        }

        static int GetStyle(IntPtr hwnd)
        {
            if (IntPtr.Size == 8) return (int)GetWindowLongPtr64(hwnd, -16).ToInt64();
            return GetWindowLong32(hwnd, -16);
        }

        static void SetStyle(IntPtr hwnd, int style)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, -16, new IntPtr(style));
            else SetWindowLong32(hwnd, -16, style);
        }

        struct Rect
        {
            public int L, T, R, B;
        }

        struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public int Flags;
        }

        delegate bool EnumProc(IntPtr h, IntPtr l);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr h, out Rect r);
        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr h, out Rect r);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        static extern int GetWindowLong32(IntPtr h, int n);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        static extern int SetWindowLong32(IntPtr h, int n, int value);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        static extern IntPtr GetWindowLongPtr64(IntPtr h, int n);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        static extern IntPtr SetWindowLongPtr64(IntPtr h, int n, IntPtr value);
    }
}
