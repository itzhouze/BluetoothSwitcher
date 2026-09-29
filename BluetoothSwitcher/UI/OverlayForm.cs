using System.Diagnostics;
using BluetoothSwitcher.Audio;
using BluetoothSwitcher.Config;
using BluetoothSwitcher.Logging;

namespace BluetoothSwitcher.UI;

/// <summary>
/// Vollbild-Dimmer (halbtransparent, empfängt die Tastatur) + darüberliegende Card mit der Geräteliste.
/// Der Dimmer ist das Vordergrundfenster und deckt den ganzen Monitor ab – dadurch bleibt die
/// Taskbar versteckt und Harbor wird nie minimiert.
/// </summary>
internal sealed class OverlayForm : Form
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WM_DPICHANGED = 0x02E0;
    private const int SelectionAnimMs = 90;
    private const float SpinnerDegreesPerMs = 0.42f;

    private enum Phase { Hidden, FadingIn, Shown, FadingOut }

    private readonly CardWindow _card = new();
    private readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 10 };
    private readonly System.Windows.Forms.Timer _autoCloseTimer = new();
    private readonly System.Windows.Forms.Timer _idleTimer = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private OverlayConfig _config = new();
    private CardModel _model = new();
    private CardLayout? _layout;
    private Rectangle _monitor;
    private IntPtr _previousForeground;

    private Phase _phase = Phase.Hidden;
    private double _phaseStart;
    private float _fade; // 0..1
    private float _selectionFrom;
    private double _selectionStart = double.NegativeInfinity;
    private double _lastTick;

    /// <summary>Benutzer hat ein Gerät gewählt (Enter, Zifferntaste oder Klick).</summary>
    public event Action<DeviceConfig>? DeviceChosen;

    public bool IsOpen => _phase is Phase.FadingIn or Phase.Shown;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.Black;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        Opacity = 0;
        Text = "BT-Switcher";

        _animTimer.Tick += (_, _) => OnAnimationTick();
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer.Stop();
            CloseOverlay();
        };
        _idleTimer.Tick += (_, _) => OnIdle();
        _card.RowClicked += index =>
        {
            if (!IsOpen || index >= _model.Rows.Count)
                return;
            SetSelection(index);
            Choose();
        };

        // Handles vorab erzeugen → erster Hotkey-Druck muss nichts mehr initialisieren.
        CreateHandle();
        _card.CreateControl();
        _card.Owner = this;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // TopMost über den Style statt über die Property: Form.TopMost ruft SetWindowPos ohne
            // SWP_NOACTIVATE auf und würde das versteckte Fenster beim Start aktivieren.
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST; // nicht in Alt+Tab
            return cp;
        }
    }

    /// <summary>
    /// Bereitet alles für einen schnellen ersten Hotkey-Druck vor (&lt;100 ms-Ziel): JIT, GDI+, Fonts
    /// und das vorgerenderte Card-Chrome für jeden Monitor bei der aktuellen Geräteanzahl.
    /// </summary>
    public void WarmUp(OverlayConfig config, int deviceCount)
    {
        var sw = Stopwatch.StartNew();
        _config = config;

        var device = new DeviceConfig { Name = "Warmup", Bluetooth = true };
        var endpoint = new EndpointInfo("warmup", "Warmup", EndpointState.Active, Guid.Empty, true);
        var model = new CardModel();
        model.Rows.Add(new OverlayRow(new DeviceState(device, endpoint, true, [new BatteryLevel("L", 50), new BatteryLevel(null, 50)], SignalBars: 2)));
        model.Rows.Add(new OverlayRow(new DeviceState(device, endpoint, false, [])) { Status = RowStatus.Busy });

        _card.ClearChromeCache();
        foreach (var screen in Screen.AllScreens.Take(3))
        {
            var layout = CardLayout.Compute(screen.Bounds.Size, config.Scale, deviceCount);
            _card.PrepareChrome(layout);
            using var surface = new DibSurface(layout.SurfaceSize.Width, layout.SurfaceSize.Height);
            using var g = surface.CreateGraphics();
            OverlayRenderer.RenderContent(g, layout, model);
        }
        Log.Info($"Overlay-Warmup in {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>Blendet das Overlay auf dem Monitor des aktuellen Vordergrundfensters ein.</summary>
    /// <param name="busy">Läuft gerade ein Wechsel, wird dessen Zeile als "Verbinde …" angezeigt.</param>
    public void ShowOverlay(IReadOnlyList<DeviceState> states, OverlayConfig config,
        DeviceConfig? busy, long hotkeyTimestamp)
    {
        _autoCloseTimer.Stop();
        _config = config;

        if (_phase == Phase.Hidden)
        {
            var foreground = FocusHelper.Capture();
            _previousForeground = foreground == Handle || foreground == _card.Handle ? IntPtr.Zero : foreground;
        }

        _monitor = _previousForeground != IntPtr.Zero
            ? Screen.FromHandle(_previousForeground).Bounds
            : Screen.FromPoint(Cursor.Position).Bounds;

        _model = BuildModel(states, busy);
        _selectionStart = double.NegativeInfinity;
        LayoutCard();

        Bounds = _monitor;
        if (_phase == Phase.Hidden)
        {
            _fade = 0;
            Opacity = 0;
        }

        var renderStart = Stopwatch.GetTimestamp();
        RenderCard();
        var renderMs = Stopwatch.GetElapsedTime(renderStart).TotalMilliseconds;
        Show();
        _card.ShowAboveOwner();
        FocusHelper.Activate(Handle);

        StartPhase(Phase.FadingIn);
        ResetIdle();
        Log.Info($"Overlay angezeigt nach {Stopwatch.GetElapsedTime(hotkeyTimestamp).TotalMilliseconds:0} ms " +
                 $"(Render {renderMs:0} ms, {_model.Rows.Count} Geräte, Monitor {_monitor.Width}x{_monitor.Height})");
    }

    /// <summary>Gecachte Zustände haben sich geändert – Reihenfolge bleibt stabil, Status-Anzeigen bleiben erhalten.</summary>
    public void UpdateStates(IReadOnlyList<DeviceState> states)
    {
        if (!IsOpen)
            return;

        var byConfig = states.ToDictionary(s => s.Config);
        var rows = _model.Rows;
        rows.RemoveAll(r => !byConfig.ContainsKey(r.State.Config));
        foreach (var row in rows)
            row.State = byConfig[row.State.Config];
        foreach (var state in states.Where(s => rows.All(r => r.State.Config != s.Config)))
            rows.Add(new OverlayRow(state));

        _model.SelectedIndex = Math.Clamp(_model.SelectedIndex, 0, Math.Max(0, rows.Count - 1));
        _model.Selection = _model.SelectedIndex;

        LayoutCard();
        RenderCard();
    }

    /// <summary>Startet die Inaktivitäts-Uhr neu (jeder Tastendruck, jede Statusänderung).</summary>
    private void ResetIdle()
    {
        _idleTimer.Stop();
        if (_config.AutoCloseSec <= 0 || !IsOpen)
            return;
        _idleTimer.Interval = _config.AutoCloseSec * 1000;
        _idleTimer.Start();
    }

    private void OnIdle()
    {
        // Während eines Verbindungsaufbaus nicht wegklappen – der Nutzer schaut gerade zu.
        if (_model.Rows.Any(r => r.Status == RowStatus.Busy))
        {
            ResetIdle();
            return;
        }
        _idleTimer.Stop();
        Log.Info($"Overlay nach {_config.AutoCloseSec} s ohne Eingabe automatisch geschlossen");
        CloseOverlay();
    }

    public void SetRowStatus(DeviceConfig device, RowStatus status, string? message)
    {
        var row = _model.Rows.FirstOrDefault(r => r.State.Config == device);
        if (row is null)
            return;
        row.Status = status;
        row.Message = message;
        if (IsOpen)
        {
            RenderCard();
            EnsureTimer();
            ResetIdle();
        }
    }

    public void CloseAfter(int delayMs)
    {
        _autoCloseTimer.Stop();
        _autoCloseTimer.Interval = Math.Max(1, delayMs);
        _autoCloseTimer.Start();
    }

    /// <summary>Blendet aus und gibt Harbor sofort den Fokus zurück.</summary>
    public void CloseOverlay()
    {
        _autoCloseTimer.Stop();
        _idleTimer.Stop();
        if (!IsOpen)
            return;

        // Fokus sofort zurückgeben – Harbor reagiert schon während des Fade-outs wieder auf Tasten.
        if (_previousForeground != IntPtr.Zero)
            FocusHelper.Activate(_previousForeground);

        StartPhase(Phase.FadingOut);
    }

    private CardModel BuildModel(IReadOnlyList<DeviceState> states, DeviceConfig? busy)
    {
        var model = new CardModel();

        var ordered = _config.ActiveDeviceOnTop
            ? states.OrderByDescending(s => s.IsDefault).ToList() // OrderBy ist stabil → Config-Reihenfolge bleibt
            : states.ToList();

        foreach (var state in ordered)
        {
            var row = new OverlayRow(state);
            if (state.Config == busy)
            {
                row.Status = RowStatus.Busy;
                row.Message = "Verbinde …";
            }
            model.Rows.Add(row);
        }

        // Cursor auf das erste nicht aktive, nutzbare Gerät – der häufigste Fall ist "wechseln".
        var start = model.Rows.FindIndex(r => !r.State.IsDefault && r.State.Availability != Availability.Unavailable);
        if (start < 0)
            start = model.Rows.FindIndex(r => !r.State.IsDefault);
        model.SelectedIndex = Math.Max(0, start);
        model.Selection = model.SelectedIndex;
        return model;
    }

    private void LayoutCard()
    {
        var layout = CardLayout.Compute(_monitor.Size, _config.Scale, _model.Rows.Count);
        if (_layout?.Key != layout.Key)
            _layout = layout;
        _card.EnsureSurface(_layout.SurfaceSize);
    }

    private byte CardAlpha => (byte)Math.Round(255 * Ease(_fade));

    /// <summary>Zeichnet den Inhalt neu (nur bei inhaltlichen Änderungen – das Fade läuft rein über Alpha).</summary>
    private void RenderCard()
    {
        if (_layout is null)
            return;
        var size = _layout.SurfaceSize;
        var location = new Point(
            _monitor.Left + (_monitor.Width - size.Width) / 2,
            _monitor.Top + (_monitor.Height - size.Height) / 2);
        _card.Draw(_layout, _model, location, CardAlpha);
    }

    // ---------------------------------------------------------------- Tastatur

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!IsOpen)
            return true;

        ResetIdle();
        var key = keyData & Keys.KeyCode;
        var hasModifier = (keyData & (Keys.Control | Keys.Alt)) != 0;
        if (hasModifier)
            return base.ProcessCmdKey(ref msg, keyData);

        switch (key)
        {
            case Keys.Up:
                MoveSelection(-1);
                return true;
            case Keys.Down:
                MoveSelection(+1);
                return true;
            case Keys.Home or Keys.PageUp:
                SetSelection(0);
                return true;
            case Keys.End or Keys.PageDown:
                SetSelection(_model.Rows.Count - 1);
                return true;
            case Keys.Enter or Keys.Space:
                Choose();
                return true;
            case Keys.Escape or Keys.Back:
                CloseOverlay();
                return true;
            case >= Keys.D1 and <= Keys.D9:
                ChooseIndex(key - Keys.D1);
                return true;
            case >= Keys.NumPad1 and <= Keys.NumPad9:
                ChooseIndex(key - Keys.NumPad1);
                return true;
        }
        return true; // alle anderen Tasten schlucken
    }

    private void MoveSelection(int delta)
    {
        var count = _model.Rows.Count;
        if (count == 0)
            return;
        SetSelection(((_model.SelectedIndex + delta) % count + count) % count);
    }

    private void SetSelection(int index)
    {
        index = Math.Clamp(index, 0, Math.Max(0, _model.Rows.Count - 1));
        if (index == _model.SelectedIndex)
            return;
        _selectionFrom = _model.Selection;
        _selectionStart = Now;
        _model.SelectedIndex = index;
        EnsureTimer();
    }

    private void ChooseIndex(int index)
    {
        if (index >= _model.Rows.Count)
            return;
        SetSelection(index);
        Choose();
    }

    private void Choose()
    {
        if (_model.Rows.Count == 0)
            return;
        var row = _model.Rows[_model.SelectedIndex];
        DeviceChosen?.Invoke(row.State.Config);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        CloseOverlay(); // Klick neben die Card
    }

    // ---------------------------------------------------------------- Animation

    private double Now => _clock.Elapsed.TotalMilliseconds;

    private void StartPhase(Phase phase)
    {
        // Laufende Fades nahtlos umkehren: Startzeit so wählen, dass _fade stetig bleibt.
        var duration = Math.Max(1, _config.FadeMs);
        _phaseStart = phase switch
        {
            Phase.FadingIn => Now - _fade * duration,
            Phase.FadingOut => Now - (1 - _fade) * duration,
            _ => Now,
        };
        _phase = phase;
        EnsureTimer();
        OnAnimationTick();
    }

    private void EnsureTimer()
    {
        if (!_animTimer.Enabled)
        {
            _lastTick = Now;
            _animTimer.Start();
        }
    }

    private void OnAnimationTick()
    {
        var now = Now;
        var dt = (float)(now - _lastTick);
        _lastTick = now;
        var needsRender = false;
        var fadeChanged = false;
        var animating = false;
        var duration = Math.Max(1, _config.FadeMs);

        switch (_phase)
        {
            case Phase.FadingIn:
                _fade = (float)Math.Clamp((now - _phaseStart) / duration, 0, 1);
                if (_fade >= 1)
                    _phase = Phase.Shown;
                else
                    animating = true;
                fadeChanged = true;
                break;
            case Phase.FadingOut:
                _fade = 1 - (float)Math.Clamp((now - _phaseStart) / duration, 0, 1);
                fadeChanged = true;
                if (_fade <= 0)
                {
                    FinishHide();
                    return;
                }
                animating = true;
                break;
            case Phase.Hidden:
                _animTimer.Stop();
                return;
        }

        Opacity = _config.BackgroundOpacity * Ease(_fade);

        // Auswahl gleitet zur neuen Zeile
        var t = (now - _selectionStart) / SelectionAnimMs;
        if (t < 1)
        {
            _model.Selection = _selectionFrom + (_model.SelectedIndex - _selectionFrom) * Ease((float)t);
            needsRender = animating = true;
        }
        else if (Math.Abs(_model.Selection - _model.SelectedIndex) > 0.0001f)
        {
            _model.Selection = _model.SelectedIndex;
            needsRender = true;
        }

        if (_model.Rows.Any(r => r.Status == RowStatus.Busy))
        {
            _model.SpinnerAngle = (_model.SpinnerAngle + dt * SpinnerDegreesPerMs) % 360;
            needsRender = animating = true;
        }

        if (needsRender)
            RenderCard();
        else if (fadeChanged)
            _card.UpdateAlpha(CardAlpha);
        if (!animating)
            _animTimer.Stop();
    }

    private void FinishHide()
    {
        _animTimer.Stop();
        _phase = Phase.Hidden;
        _fade = 0;
        Opacity = 0;
        _card.Hide();
        Hide();
        _card.ReleaseSurface();
        _model = new CardModel();
        _previousForeground = IntPtr.Zero;

        // Bitmaps & GDI+-Objekte sofort freigeben → niedriger Idle-RAM.
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private static float Ease(float t) => 1 - MathF.Pow(1 - Math.Clamp(t, 0, 1), 3); // easeOutCubic

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DPICHANGED)
            return; // Größe/Position setzen wir selbst in Pixeln
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; // Alt+F4 → nur ausblenden
            CloseOverlay();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animTimer.Dispose();
            _autoCloseTimer.Dispose();
            _idleTimer.Dispose();
            _card.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Per-Pixel-Alpha-Fenster mit der eigentlichen Card.</summary>
    private sealed class CardWindow : LayeredWindow
    {
        private const int MaxCachedChromes = 3;

        // Vorgerenderter statischer Teil der Card je Layout (Schatten ist teuer).
        private readonly Dictionary<string, Bitmap> _chromes = [];
        private DibSurface? _surface;
        private CardLayout? _layout;
        private CardModel? _model;

        public event Action<int>? RowClicked;

        public CardWindow() : base(clickThrough: false) { }

        public void EnsureSurface(Size size)
        {
            if (_surface is not null && _surface.Width == size.Width && _surface.Height == size.Height)
                return;
            _surface?.Dispose();
            _surface = new DibSurface(size.Width, size.Height);
        }

        public Bitmap PrepareChrome(CardLayout layout)
        {
            if (_chromes.TryGetValue(layout.Key, out var cached))
                return cached;

            if (_chromes.Count >= MaxCachedChromes)
                ClearChromeCache();

            var chrome = new Bitmap(layout.SurfaceSize.Width, layout.SurfaceSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(chrome))
            {
                Theme.Prepare(g);
                OverlayRenderer.RenderChrome(g, layout, new CardModel());
            }
            _chromes[layout.Key] = chrome;
            return chrome;
        }

        public void ClearChromeCache()
        {
            foreach (var bitmap in _chromes.Values)
                bitmap.Dispose();
            _chromes.Clear();
        }

        public void Draw(CardLayout layout, CardModel model, Point location, byte alpha)
        {
            if (_surface is null)
                return;
            _layout = layout;
            _model = model;
            var chrome = PrepareChrome(layout);
            using (var g = _surface.CreateGraphics(clear: false))
            {
                // Chrome deckt die komplette Fläche ab → SourceCopy ersetzt das Löschen.
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.DrawImageUnscaled(chrome, 0, 0);
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                OverlayRenderer.RenderContent(g, layout, model);
            }
            Present(_surface, location, alpha);
        }

        public void UpdateAlpha(byte alpha) => SetAlpha(alpha);

        public void ShowAboveOwner()
        {
            if (!Visible)
                Show();
            BringToTopNoActivate();
        }

        public void ReleaseSurface()
        {
            _surface?.Dispose();
            _surface = null;
            _model = null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_layout is null || _model is null)
                return;
            for (var i = 0; i < _model.Rows.Count; i++)
            {
                if (_layout.RowRect(i).Contains(e.Location))
                {
                    RowClicked?.Invoke(i);
                    return;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ReleaseSurface();
                ClearChromeCache();
            }
            base.Dispose(disposing);
        }
    }
}
