using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Text.Json;

namespace BilljaminLauncher;

/// <summary>
/// Gothic launcher presentation. The supplied 1536x1024 reference art is an EMBEDDED
/// background plate, not a bitmap button or a screenshot of game status: all user
/// controls are real hit targets, and the version/status/leaderboard are rendered live.
/// Coordinates below are in reference-image pixels and are scaled uniformly at paint time.
/// </summary>
public sealed class MainForm : Form
{
    private const float ArtWidth = 1536f;
    private const float ArtHeight = 1024f;
    private static readonly Color Gold = Color.FromArgb(218, 179, 111);
    private static readonly Color PaleGold = Color.FromArgb(238, 214, 171);
    private static readonly Color SoftWhite = Color.FromArgb(235, 230, 223);
    private static readonly Color Good = Color.FromArgb(111, 234, 130);
    private static readonly Color Warning = Color.FromArgb(243, 186, 92);
    private static readonly Color Muted = Color.FromArgb(173, 171, 162);

    private readonly LauncherService _service = new();
    private readonly System.Windows.Forms.Timer _leaderTimer = new() { Interval = 60_000 };
    private static readonly HttpClient LeaderHttp = new() { Timeout = TimeSpan.FromSeconds(12) };

    private Image? _referenceArt;
    private Image? _stone;
    private RemoteRelease? _latest;
    private readonly List<(string Name, decimal Score)> _leaders = new();
    private string _leaderboardMessage = "Loading live leaderboard...";
    private string _message = "Preparing launcher...";
    private int _progressValue;
    private bool _busy;
    private bool _checkingLauncher;
    private bool _filesVerified;
    private bool _integrityFailed;
    private bool _checkedGameRelease;
    private bool _isDragging;
    private Point _dragOrigin;
    private Point _windowOrigin;
    private HitTarget _hover;
    private HitTarget _pressed;
    private Page _page = Page.Home;

    private enum Page { Home, Leaderboard, Settings, About }
    private enum HitTarget
    {
        None, Home, Settings, Leaderboard, About,
        Minimize, Close, Play, CheckUpdates, UpdateRepair, GameFolder, SettingsIcon,
        FullLeaderboard, ModalClose, ModalButton1, ModalButton2
    }

    public MainForm()
    {
        Text = "The Tower of Billjamin Launcher";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1350, 900);
        MinimumSize = new Size(1250, 850);
        BackColor = Color.FromArgb(7, 9, 12);
        DoubleBuffered = true;
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _referenceArt = LoadEmbedded("BilljaminLauncher.Assets.launcher-reference.png");
        _stone = LoadEmbedded("BilljaminLauncher.Assets.dark-stone-tile.png");

        _leaderTimer.Tick += async (_, _) => await RefreshLeaderboardAsync();
        Shown += async (_, _) => await StartupAsync();
        FormClosed += (_, _) =>
        {
            _leaderTimer.Stop();
            _leaderTimer.Dispose();
            _referenceArt?.Dispose();
            _stone?.Dispose();
            _service.Dispose();
        };
    }

    private static Image? LoadEmbedded(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) return null;
        using var img = Image.FromStream(stream);
        return new Bitmap(img);
    }

    private float Scale => Math.Min(ClientSize.Width / ArtWidth, ClientSize.Height / ArtHeight);
    private PointF Origin => new((ClientSize.Width - ArtWidth * Scale) / 2,
                                  (ClientSize.Height - ArtHeight * Scale) / 2);
    private PointF ToReference(Point point)
    {
        var o = Origin;
        return new PointF((point.X - o.X) / Scale, (point.Y - o.Y) / Scale);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var origin = Origin;
        var saved = g.Save();
        g.TranslateTransform(origin.X, origin.Y);
        g.ScaleTransform(Scale, Scale);

        if (_referenceArt is not null)
            g.DrawImage(_referenceArt, 0, 0, ArtWidth, ArtHeight);
        else
        {
            using var fallback = new SolidBrush(Color.FromArgb(15, 19, 26));
            g.FillRectangle(fallback, 0, 0, ArtWidth, ArtHeight);
            Write(g, "THE TOWER OF BILLJAMIN", new RectangleF(45, 65, 750, 100), 43, PaleGold, true);
        }

        DrawStatus(g);
        DrawLeaderboard(g);
        DrawFooter(g);
        DrawButtonStates(g);
        if (_page != Page.Home) DrawPage(g);
        g.Restore(saved);
    }

    private void FillStone(Graphics g, RectangleF rect, Color? wash = null)
    {
        using (var baseBrush = new SolidBrush(Color.FromArgb(13, 14, 16)))
            g.FillRectangle(baseBrush, rect);
        if (_stone is not null)
        {
            using var texture = new TextureBrush(_stone, WrapMode.Tile);
            g.FillRectangle(texture, rect);
        }
        if (wash is not null)
        {
            using var veil = new SolidBrush(wash.Value);
            g.FillRectangle(veil, rect);
        }
    }

    private static void Frame(Graphics g, RectangleF rect, bool strong = false)
    {
        using var shadow = new Pen(Color.FromArgb(185, 7, 8, 9), strong ? 5 : 3);
        using var bronze = new Pen(Color.FromArgb(strong ? 215 : 125, 179, 136, 76), strong ? 2.4f : 1.2f);
        g.DrawRectangle(shadow, rect.X, rect.Y, rect.Width, rect.Height);
        g.DrawRectangle(bronze, rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4);
    }

    private static void Write(Graphics g, string value, RectangleF bounds, float pixels,
                              Color color, bool bold = false, bool centered = false,
                              bool right = false, bool serif = true)
    {
        using var font = new Font(serif ? "Georgia" : "Segoe UI", pixels,
                                  bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = centered ? StringAlignment.Center : right ? StringAlignment.Far : StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        g.DrawString(value, font, brush, bounds, format);
    }

    private static void Icon(Graphics g, float x, float y, Color color, bool check)
    {
        using var fill = new SolidBrush(color);
        using var border = new Pen(Color.FromArgb(24, 15, 12), 1.5f);
        g.FillEllipse(fill, x, y, 24, 24);
        g.DrawEllipse(border, x, y, 24, 24);
        using var pen = new Pen(Color.FromArgb(16, 31, 18), 2.8f)
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
        };
        if (check)
        {
            g.DrawLine(pen, x + 6, y + 12, x + 10, y + 17);
            g.DrawLine(pen, x + 10, y + 17, x + 19, y + 7);
        }
        else
        {
            g.DrawLine(pen, x + 12, y + 6, x + 12, y + 13);
            g.FillEllipse(Brushes.Black, x + 10.5f, y + 17, 3, 3);
        }
    }

    private bool HasGame => File.Exists(Path.Combine(LauncherSettings.GameDirectory, LauncherSettings.GameExecutable));
    private string InstalledVersion
    {
        get
        {
            try { return LauncherService.GetLocalVersion(); }
            catch { return "Unknown"; }
        }
    }
    private bool UpdateAvailable => _latest is not null &&
                                    !string.Equals(InstalledVersion, _latest.Manifest.Version, StringComparison.OrdinalIgnoreCase);
    private bool CanPlay => !_busy && !_checkingLauncher && HasGame && !UpdateAvailable && !_integrityFailed;

    private void DrawStatus(Graphics g)
    {
        // Opaque live status plate. The reference screenshot has illustrative text here;
        // it must never be shown as a real install/verification claim.
        var content = new RectangleF(1098, 151, 371, 126);
        FillStone(g, content, Color.FromArgb(55, 6, 7, 8));
        // Keeps the existing ornate enclosing border from the original image.
        using (var border = new Pen(Color.FromArgb(45, 174, 128, 79), 0.8f))
            g.DrawRectangle(border, content.X, content.Y, content.Width, content.Height);

        var versionBlank = new RectangleF(1412, 101, 92, 37);
        FillStone(g, versionBlank);
        string version = InstalledVersion == "Not installed" ? "—" : InstalledVersion;
        Write(g, "v" + version.TrimStart('v', 'V'), new RectangleF(1416, 103, 86, 35),
              18, SoftWhite, right: true);

        string row1;
        Color color1;
        bool good1;
        if (_checkingLauncher) { row1 = "Checking launcher updates"; color1 = Warning; good1 = false; }
        else if (_busy && !_checkedGameRelease) { row1 = "Checking for game updates"; color1 = Warning; good1 = false; }
        else if (!HasGame) { row1 = "Game not installed"; color1 = Warning; good1 = false; }
        else if (UpdateAvailable) { row1 = "Game update required"; color1 = Warning; good1 = false; }
        else if (!_checkedGameRelease) { row1 = "Update status not confirmed"; color1 = Warning; good1 = false; }
        else { row1 = "Game is up to date"; color1 = Good; good1 = true; }

        Icon(g, 1110, 164, color1, good1);
        Write(g, row1, new RectangleF(1152, 157, 302, 36), 19.5f, color1, bold: true);

        Icon(g, 1110, 202, HasGame ? Good : Warning, HasGame);
        Write(g, "Version: " + version, new RectangleF(1152, 195, 302, 36),
              19.5f, SoftWhite);

        string integrity = !HasGame ? "Install to start playing" :
                           _integrityFailed ? "Damaged files — repair needed" :
                           _filesVerified ? "All files verified" :
                           "File integrity not checked";
        bool integrityGood = _filesVerified && !_integrityFailed;
        Icon(g, 1110, 240, integrityGood ? Good : Warning, integrityGood);
        Write(g, integrity, new RectangleF(1152, 233, 302, 36), 18.6f,
              integrityGood ? SoftWhite : Warning);

        if (_busy && _progressValue > 0)
        {
            using var back = new SolidBrush(Color.FromArgb(180, 9, 11, 9));
            using var fill = new SolidBrush(Color.FromArgb(215, 82, 143, 92));
            g.FillRectangle(back, 1110, 277, 346, 5);
            g.FillRectangle(fill, 1110, 277, 346 * Math.Clamp(_progressValue, 0, 100) / 100f, 5);
        }
    }

    private void DrawLeaderboard(Graphics g)
    {
        // Replace all five sample players/scores with genuine Supabase data.
        var body = new RectangleF(1099, 694, 369, 174);
        FillStone(g, body, Color.FromArgb(45, 1, 2, 4));

        for (int i = 0; i < 5; i++)
        {
            float top = 701 + i * 35;
            string name = i < _leaders.Count ? _leaders[i].Name : "—";
            string score = i < _leaders.Count ? _leaders[i].Score.ToString("N0") : "";
            Write(g, (i + 1).ToString(), new RectangleF(1108, top, 26, 31),
                  16, i == 0 ? Good : SoftWhite, centered: true);
            Write(g, name, new RectangleF(1147, top, 220, 31), 17, SoftWhite);
            Write(g, score, new RectangleF(1355, top, 93, 31), 17, SoftWhite, right: true);
            if (i != 4)
            {
                using var line = new Pen(Color.FromArgb(80, 74, 70, 60), 1);
                g.DrawLine(line, 1100, top + 33, 1466, top + 33);
            }
        }
        if (_leaders.Count == 0)
        {
            FillStone(g, new RectangleF(1145, 768, 297, 47));
            Write(g, _leaderboardMessage, new RectangleF(1147, 767, 294, 45),
                  14.5f, Muted, centered: true);
        }
    }

    private void DrawFooter(Graphics g)
    {
        // Status strip lives in previously unused scenic space; it also conveys errors.
        var box = new RectangleF(337, 969, 700, 37);
        using (var background = new SolidBrush(Color.FromArgb(209, 5, 8, 10)))
            g.FillRectangle(background, box);
        using (var border = new Pen(Color.FromArgb(150, 144, 105, 59), 1))
            g.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);
        Write(g, _message, new RectangleF(353, 971, 674, 32), 14, PaleGold);
        Write(g, "Launcher v" + LauncherSettings.LauncherVersion,
              new RectangleF(24, 973, 280, 35), 14, PaleGold);
    }

    private void DrawButtonStates(Graphics g)
    {
        if (!CanPlay)
        {
            using var disabled = new SolidBrush(Color.FromArgb(146, 3, 6, 6));
            g.FillRectangle(disabled, 1093, 304, 378, 92);
        }
        if (_busy || _checkingLauncher)
        {
            using var veil = new SolidBrush(Color.FromArgb(90, 5, 6, 8));
            g.FillRectangle(veil, 1091, 417, 380, 174);
        }
        if (_hover != HitTarget.None && !_busy && !_checkingLauncher && _page == Page.Home)
        {
            RectangleF area = Area(_hover);
            if (area.Width > 0 && _hover is not HitTarget.Minimize and not HitTarget.Close)
            {
                using var glow = new Pen(Color.FromArgb(205, 242, 200, 116), 2.8f);
                using var tint = new SolidBrush(Color.FromArgb(_pressed == _hover ? 78 : 33, 231, 187, 102));
                g.FillRectangle(tint, area);
                g.DrawRectangle(glow, area.X + 2, area.Y + 2, area.Width - 4, area.Height - 4);
            }
        }
    }

    private static RectangleF Area(HitTarget target) => target switch
    {
        HitTarget.Home => new(27, 236, 265, 77),
        HitTarget.Settings => new(27, 331, 265, 76),
        HitTarget.Leaderboard => new(27, 424, 265, 76),
        HitTarget.About => new(27, 511, 265, 78),
        HitTarget.Minimize => new(1395, 5, 69, 65),
        HitTarget.Close => new(1466, 5, 65, 65),
        HitTarget.Play => new(1090, 296, 386, 105),
        HitTarget.CheckUpdates => new(1085, 416, 396, 58),
        HitTarget.UpdateRepair => new(1085, 477, 396, 58),
        HitTarget.GameFolder => new(1087, 538, 296, 58),
        HitTarget.SettingsIcon => new(1387, 538, 91, 58),
        HitTarget.FullLeaderboard => new(1087, 883, 393, 53),
        _ => RectangleF.Empty
    };

    private HitTarget HitTest(PointF point)
    {
        if (point.X < 0 || point.X >= ArtWidth || point.Y < 0 || point.Y >= ArtHeight)
            return HitTarget.None;
        if (_page != Page.Home)
        {
            foreach (var nav in new[] { HitTarget.Minimize, HitTarget.Close,
                        HitTarget.Home, HitTarget.Settings, HitTarget.Leaderboard, HitTarget.About })
                if (Area(nav).Contains(point)) return nav;
            if (new RectangleF(951, 211, 55, 55).Contains(point)) return HitTarget.ModalClose;
            if (new RectangleF(389, 748, 275, 54).Contains(point)) return HitTarget.ModalButton1;
            if (new RectangleF(691, 748, 276, 54).Contains(point)) return HitTarget.ModalButton2;
            return HitTarget.None;
        }
        foreach (var target in new[] { HitTarget.Minimize, HitTarget.Close, HitTarget.Home,
            HitTarget.Settings, HitTarget.Leaderboard, HitTarget.About, HitTarget.Play,
            HitTarget.CheckUpdates, HitTarget.UpdateRepair, HitTarget.GameFolder,
            HitTarget.SettingsIcon, HitTarget.FullLeaderboard })
        {
            if (Area(target).Contains(point)) return target;
        }
        return HitTarget.None;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging && e.Button == MouseButtons.Left)
        {
            var screen = Cursor.Position;
            Location = new Point(_windowOrigin.X + screen.X - _dragOrigin.X,
                                 _windowOrigin.Y + screen.Y - _dragOrigin.Y);
            return;
        }
        var hit = HitTest(ToReference(e.Location));
        if (hit != _hover)
        {
            _hover = hit;
            Invalidate();
        }
        Cursor = hit == HitTarget.None || (_busy && hit is not HitTarget.Close and not HitTarget.Minimize)
            ? Cursors.Default : Cursors.Hand;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = HitTarget.None;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var point = ToReference(e.Location);
        _pressed = HitTest(point);
        if (_pressed == HitTarget.None && _page == Page.Home && point.X > 305 && point.Y < 170)
        {
            _isDragging = true;
            _dragOrigin = Cursor.Position;
            _windowOrigin = Location;
            Capture = true;
        }
        Invalidate();
    }

    protected override async void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (_isDragging)
        {
            _isDragging = false;
            Capture = false;
            _pressed = HitTarget.None;
            return;
        }
        var hit = HitTest(ToReference(e.Location));
        var action = _pressed == hit ? hit : HitTarget.None;
        _pressed = HitTarget.None;
        Invalidate();
        await ExecuteAsync(action);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _page != Page.Home)
        {
            _page = Page.Home;
            Invalidate();
            return true;
        }
        if (keyData == Keys.F5 && !_busy)
        {
            _ = RunBusyAsync(CheckGameAsync);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private async Task ExecuteAsync(HitTarget target)
    {
        if (target == HitTarget.Close) { Close(); return; }
        if (target == HitTarget.Minimize) { WindowState = FormWindowState.Minimized; return; }
        if (target == HitTarget.ModalClose || target == HitTarget.Home)
        {
            _page = Page.Home;
            Invalidate();
            return;
        }
        if (_page != Page.Home)
        {
            if (target == HitTarget.Leaderboard) _page = Page.Leaderboard;
            else if (target == HitTarget.Settings) _page = Page.Settings;
            else if (target == HitTarget.About) _page = Page.About;
            else if (target == HitTarget.ModalButton1) ExecuteModalButton(1);
            else if (target == HitTarget.ModalButton2) ExecuteModalButton(2);
            Invalidate();
            return;
        }
        if (target == HitTarget.Leaderboard || target == HitTarget.FullLeaderboard)
        {
            _page = Page.Leaderboard; Invalidate(); return;
        }
        if (target == HitTarget.Settings || target == HitTarget.SettingsIcon)
        {
            _page = Page.Settings; Invalidate(); return;
        }
        if (target == HitTarget.About)
        {
            _page = Page.About; Invalidate(); return;
        }
        if (_busy || _checkingLauncher) return;
        switch (target)
        {
            case HitTarget.Play:
                if (!CanPlay) return;
                try { LauncherService.LaunchGame(); _message = "Game launched. Have fun ascending the tower!"; }
                catch (Exception ex) { Error(ex); }
                Invalidate();
                break;
            case HitTarget.CheckUpdates: await RunBusyAsync(CheckGameAsync); break;
            case HitTarget.UpdateRepair: await RunBusyAsync(UpdateRepairAsync); break;
            case HitTarget.GameFolder: OpenGameFolder(); break;
        }
    }

    private void ExecuteModalButton(int which)
    {
        switch (_page)
        {
            case Page.Leaderboard:
                if (which == 1) _ = RefreshLeaderboardAsync();
                else _page = Page.Home;
                break;
            case Page.Settings:
                if (which == 1) OpenGameFolder();
                else _ = RunBusyAsync(CheckGameAsync);
                break;
            case Page.About:
                if (which == 1) OpenUrl($"https://github.com/{LauncherSettings.GitHubOwner}/{LauncherSettings.GitHubRepo}/releases");
                else _page = Page.Home;
                break;
        }
        Invalidate();
    }

    private void DrawPage(Graphics g)
    {
        // Dim the baked-in Home highlight and accent the currently selected sidebar tab.
        using (var dimHome = new SolidBrush(Color.FromArgb(176, 3, 5, 8)))
            g.FillRectangle(dimHome, 30, 238, 257, 71);
        var currentTab = _page switch
        {
            Page.Settings => Area(HitTarget.Settings),
            Page.Leaderboard => Area(HitTarget.Leaderboard),
            _ => Area(HitTarget.About)
        };
        using (var active = new Pen(Color.FromArgb(239, 235, 184, 98), 2.5f))
            g.DrawRectangle(active, currentTab.X + 2, currentTab.Y + 2,
                            currentTab.Width - 4, currentTab.Height - 4);
        var bounds = new RectangleF(357, 190, 670, 687);
        using (var backdrop = new SolidBrush(Color.FromArgb(176, 3, 5, 8)))
            g.FillRectangle(backdrop, 321, 179, 720, 713);
        FillStone(g, bounds, Color.FromArgb(125, 2, 4, 7));
        Frame(g, bounds, true);
        using (var header = new Pen(Color.FromArgb(150, 157, 120, 71), 1))
            g.DrawLine(header, 385, 281, 999, 281);
        string title = _page switch
        {
            Page.Leaderboard => "TOP 10 PLAYERS",
            Page.Settings => "LAUNCHER SETTINGS",
            _ => "ABOUT THE TOWER"
        };
        Write(g, title, new RectangleF(391, 218, 508, 59), 31, PaleGold, true);
        Write(g, "×", new RectangleF(951, 213, 49, 49), 33, PaleGold, centered: true);
        if (_page == Page.Leaderboard) DrawFullLeaderboard(g);
        else if (_page == Page.Settings) DrawSettings(g);
        else DrawAbout(g);
        ButtonLabel(g, new RectangleF(389, 748, 275, 54), _page switch
        {
            Page.Leaderboard => "REFRESH SCORES",
            Page.Settings => "OPEN GAME FOLDER",
            _ => "GAME RELEASES"
        });
        ButtonLabel(g, new RectangleF(691, 748, 276, 54), _page switch
        {
            Page.Leaderboard => "RETURN HOME",
            Page.Settings => "CHECK GAME UPDATES",
            _ => "RETURN HOME"
        });
    }

    private void ButtonLabel(Graphics g, RectangleF area, string label)
    {
        FillStone(g, area, Color.FromArgb(110, 14, 15, 18));
        Frame(g, area, true);
        Write(g, label, area, 17, PaleGold, true, centered: true);
    }

    private void DrawFullLeaderboard(Graphics g)
    {
        Write(g, "#", new RectangleF(404, 301, 37, 28), 17, Gold, true);
        Write(g, "PLAYER", new RectangleF(468, 301, 295, 28), 17, Gold, true);
        Write(g, "SCORE", new RectangleF(829, 301, 135, 28), 17, Gold, true, right: true);
        for (int i = 0; i < 10; i++)
        {
            float top = 337 + 36 * i;
            if (i % 2 == 0)
            {
                using var alternate = new SolidBrush(Color.FromArgb(79, 75, 59, 39));
                g.FillRectangle(alternate, 395, top, 580, 34);
            }
            Write(g, (i + 1).ToString(), new RectangleF(402, top, 39, 31),
                  18, i == 0 ? Good : SoftWhite);
            Write(g, i < _leaders.Count ? _leaders[i].Name : "—",
                  new RectangleF(465, top, 317, 31), 18, SoftWhite);
            Write(g, i < _leaders.Count ? _leaders[i].Score.ToString("N0") : "",
                  new RectangleF(817, top, 146, 31), 18, SoftWhite, right: true);
        }
        Write(g, _leaderboardMessage, new RectangleF(399, 708, 560, 33), 15, Muted);
    }

    private void DrawSettings(Graphics g)
    {
        Write(g, "Installed game", new RectangleF(398, 319, 250, 34), 19, Gold, true);
        Write(g, "Version: " + InstalledVersion, new RectangleF(664, 319, 297, 34), 19, SoftWhite);
        Write(g, "Latest published game", new RectangleF(398, 367, 295, 35), 19, Gold, true);
        Write(g, "Version: " + (_latest?.Manifest.Version ?? "Unavailable"),
              new RectangleF(698, 367, 260, 35), 19, SoftWhite);
        Write(g, "Launcher", new RectangleF(398, 415, 260, 35), 19, Gold, true);
        Write(g, "Version: " + LauncherSettings.LauncherVersion,
              new RectangleF(664, 415, 294, 35), 19, SoftWhite);
        Write(g, "Launcher updates are checked before game updates on startup.",
              new RectangleF(399, 485, 570, 53), 18, SoftWhite);
        Write(g, "Game install location:", new RectangleF(399, 559, 500, 36), 19, Gold, true);
        Write(g, LauncherSettings.GameDirectory,
              new RectangleF(399, 601, 560, 68), 14, SoftWhite, serif: false);
    }

    private void DrawAbout(Graphics g)
    {
        Write(g, "THE TOWER OF BILLJAMIN", new RectangleF(392, 337, 577, 53), 29, PaleGold, true, centered: true);
        Write(g, "Ascend the tower. Grow stronger. Conquer the darkness.",
              new RectangleF(399, 401, 559, 50), 19, SoftWhite, centered: true);
        Write(g, "Launcher v" + LauncherSettings.LauncherVersion,
              new RectangleF(399, 495, 559, 37), 20, Gold, centered: true);
        Write(g, "Windows launcher with verified game downloads, repairs,",
              new RectangleF(399, 562, 559, 34), 18, SoftWhite, centered: true);
        Write(g, "and independent automatic launcher updates.",
              new RectangleF(399, 602, 559, 34), 18, SoftWhite, centered: true);
    }

    private async Task StartupAsync()
    {
        _checkingLauncher = true;
        _message = "Checking for launcher updates first...";
        Invalidate();
        bool startingUpdate = false;
        try { startingUpdate = await LauncherSelfUpdater.CheckAndStageUpdateAsync(); }
        finally { _checkingLauncher = false; Invalidate(); }
        if (startingUpdate) { Close(); return; }

        await RunBusyAsync(CheckGameAsync);
        if (IsDisposed) return;
        await RefreshLeaderboardAsync();
        if (!IsDisposed) _leaderTimer.Start();
    }

    private async Task RunBusyAsync(Func<Task> operation)
    {
        if (_busy || IsDisposed) return;
        _busy = true;
        _progressValue = 0;
        Invalidate();
        try { await operation(); }
        catch (Exception ex) { Error(ex); }
        finally
        {
            _busy = false;
            if (!IsDisposed) Invalidate();
        }
    }

    private async Task CheckGameAsync()
    {
        _message = "Checking GitHub for the newest game release...";
        _checkedGameRelease = false;
        _progressValue = 0;
        Invalidate();
        _latest = await _service.GetLatestAsync(CancellationToken.None);
        _checkedGameRelease = true;
        _message = !HasGame ? "Game not installed. Click UPDATE / REPAIR to install." :
                   UpdateAvailable ? $"Game update {_latest.Manifest.Version} is available. Click UPDATE / REPAIR." :
                   $"Game version {InstalledVersion} is current.";
        Invalidate();
        if (HasGame && !UpdateAvailable)
        {
            _message = "Checking installed game files...";
            Invalidate();
            var progress = new Progress<string>(s => { _message = s; Invalidate(); });
            var result = await _service.VerifyAsync(_latest.Manifest, progress, CancellationToken.None);
            _filesVerified = result.IsValid;
            _integrityFailed = !result.IsValid;
            _message = result.IsValid ? $"Ready to play — verified {result.Good} game files." :
                       $"{result.MissingOrBad} game files need repair. Click UPDATE / REPAIR.";
            Invalidate();
        }
        else
        {
            _filesVerified = false;
            _integrityFailed = false;
        }
    }

    private async Task UpdateRepairAsync()
    {
        if (_latest is null) await CheckGameAsync();
        if (_latest is null) throw new InvalidOperationException("No game release information is available.");
        var progress = new Progress<string>(s => { _message = s; Invalidate(); });
        var percent = new Progress<int>(p => { _progressValue = Math.Clamp(p, 0, 100); Invalidate(); });

        if (!HasGame || UpdateAvailable || _integrityFailed)
        {
            _filesVerified = false;
            await _service.InstallAsync(_latest, progress, percent, CancellationToken.None);
            _filesVerified = true;
            _integrityFailed = false;
            _progressValue = 100;
            _message = "Installation complete. All files verified. Ready to play.";
        }
        else
        {
            _message = "Verifying installed game files...";
            var verification = await _service.VerifyAsync(_latest.Manifest, progress, CancellationToken.None);
            if (!verification.IsValid)
            {
                _message = "Repairing damaged files...";
                _filesVerified = false;
                await _service.InstallAsync(_latest, progress, percent, CancellationToken.None);
            }
            _filesVerified = true;
            _integrityFailed = false;
            _progressValue = 100;
            _message = "All game files verified. Ready to play.";
        }
        Invalidate();
    }

    private async Task RefreshLeaderboardAsync()
    {
        const string endpoint = "https://lpdjolhgqsixljrwngbb.supabase.co/rest/v1/run_records?select=player_name,score&order=score.desc&limit=10";
        const string publicClientKey = "sb_publishable_eFz2VJumefd_7iRxGcBO4Q_IdshILqM";
        _leaderboardMessage = "Loading live scores...";
        Invalidate();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.TryAddWithoutValidation("apikey", publicClientKey);
            using var response = await LeaderHttp.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (data.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Leaderboard returned an unexpected response.");
            var results = new List<(string Name, decimal Score)>();
            foreach (var item in data.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("player_name", out var name) ||
                    !item.TryGetProperty("score", out var score) ||
                    name.ValueKind != JsonValueKind.String ||
                    score.ValueKind != JsonValueKind.Number || !score.TryGetDecimal(out decimal points)) continue;
                string nickname = name.GetString() ?? "Unknown";
                // Keep remote usernames bounded to the UI, without touching source data.
                if (nickname.Length > 24) nickname = nickname[..23] + "…";
                results.Add((nickname, points));
            }
            _leaders.Clear();
            _leaders.AddRange(results.OrderByDescending(x => x.Score).Take(10));
            _leaderboardMessage = _leaders.Count == 0 ? "No scores posted yet" : "Updated from the live leaderboard";
        }
        catch (Exception ex)
        {
            _leaderboardMessage = "Leaderboard unavailable";
            Debug.WriteLine("Leaderboard: " + ex.Message);
            // Do not show fictional scores if the service is offline.
            _leaders.Clear();
        }
        if (!IsDisposed) Invalidate();
    }

    private void OpenGameFolder()
    {
        try
        {
            Directory.CreateDirectory(LauncherSettings.GameDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", LauncherSettings.GameDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { Error(ex); }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Unable to open link", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void Error(Exception ex)
    {
        _message = "Error: " + ex.Message;
        _filesVerified = false;
        Invalidate();
        MessageBox.Show(this, ex.Message, "Billjamin Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // Borderless resizing. The artwork is always letterboxed rather than distorted.
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84;
        const int HTCLIENT = 1;
        const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13;
        const int HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        base.WndProc(ref m);
        if (m.Msg != WM_NCHITTEST || m.Result != (IntPtr)HTCLIENT || WindowState != FormWindowState.Normal)
            return;
        Point cursor = PointToClient(Cursor.Position);
        const int edge = 7;
        bool left = cursor.X < edge, right = cursor.X >= ClientSize.Width - edge;
        bool top = cursor.Y < edge, bottom = cursor.Y >= ClientSize.Height - edge;
        if (left && top) m.Result = (IntPtr)HTTOPLEFT;
        else if (right && top) m.Result = (IntPtr)HTTOPRIGHT;
        else if (left && bottom) m.Result = (IntPtr)HTBOTTOMLEFT;
        else if (right && bottom) m.Result = (IntPtr)HTBOTTOMRIGHT;
        else if (left) m.Result = (IntPtr)HTLEFT;
        else if (right) m.Result = (IntPtr)HTRIGHT;
        else if (top) m.Result = (IntPtr)HTTOP;
        else if (bottom) m.Result = (IntPtr)HTBOTTOM;
    }
}
