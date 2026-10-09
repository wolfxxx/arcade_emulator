using Arcade.App.Controls;
using Arcade.App.Ui;

namespace Arcade.App;

/// <summary>
/// The save-state screen opened from the pause menu: the game's slots as cards with a picture of
/// each saved moment. Save and Load are two pages (switch with the page buttons or left/right past
/// the edge); choosing a slot saves or loads it and goes back to the game.
/// </summary>
sealed class StatePicker
{
    const int Columns = 4;

    readonly ArcadeApp _app;
    readonly GameSession _session;
    IReadOnlyList<StateSlot> _slots = [];
    int _selected;
    bool _saving;
    float _openTime;
    string? _flash;
    float _flashTime;
    int _confirmDelete = -1;

    public bool IsOpen { get; private set; } = true;
    /// <summary>True when a slot was saved or loaded, so the game should carry on rather than return to the pause menu.</summary>
    public bool Done { get; private set; }

    public StatePicker(ArcadeApp app, GameSession session, bool saving)
    {
        _app = app;
        _session = session;
        _saving = saving;
        Refresh();
        _selected = session.CurrentSlot - 1;
    }

    void Refresh() => _slots = _session.Slots?.All() ?? [];

    void Flash(string text)
    {
        _flash = text;
        _flashTime = 2.5f;
    }

    public void Update(IReadOnlyList<UiAction> actions, float dt)
    {
        _openTime += dt;
        _flashTime -= dt;
        foreach (var action in actions)
        {
            if (!IsOpen || _slots.Count == 0)
            {
                IsOpen = false;
                return;
            }
            if (action != UiAction.Favorite)
                _confirmDelete = -1;
            var column = _selected % Columns;
            switch (action)
            {
                case UiAction.Left when column == 0:
                case UiAction.PrevTab:
                case UiAction.Right when column == Columns - 1:
                case UiAction.NextTab:
                    _saving = !_saving;
                    break;
                case UiAction.Left: _selected--; break;
                case UiAction.Right: _selected++; break;
                case UiAction.Up: _selected = (_selected - Columns + _slots.Count) % _slots.Count; break;
                case UiAction.Down: _selected = (_selected + Columns) % _slots.Count; break;
                case UiAction.Accept:
                    Choose();
                    break;
                case UiAction.Favorite when !_slots[_selected].IsEmpty:
                    if (_confirmDelete != _selected)
                    {
                        _confirmDelete = _selected;
                        Flash($"Press {_app.UiInput.Label(UiAction.Favorite)} again to delete slot {_selected + 1}");
                        break;
                    }
                    _session.Slots!.Delete(_selected + 1);
                    _app.Images.Invalidate(_slots[_selected].ThumbnailPath);
                    _confirmDelete = -1;
                    Refresh();
                    Flash($"Slot {_selected + 1} deleted");
                    break;
                case UiAction.Back:
                    IsOpen = false;
                    break;
            }
        }
    }

    void Choose()
    {
        var slot = _slots[_selected];
        if (!_saving && slot.IsEmpty)
        {
            Flash($"Slot {slot.Number} is empty");
            return;
        }
        var message = _saving ? _session.SaveState(slot.Number) : _session.LoadState(slot.Number);
        _app.Images.Invalidate(slot.ThumbnailPath);
        _app.ShowMessage(message);
        Done = true;
        IsOpen = false;
    }

    static string When(DateTime saved)
    {
        var day = saved.Date == DateTime.Today ? "Today" : saved.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : saved.ToString("d MMM yyyy");
        return $"{day} {saved:HH:mm}";
    }

    public void Draw(UiRenderer r, Theme t, UiInput input, float dt)
    {
        var appear = Easing.SmoothStep(_openTime / 0.15f);
        r.Fill(new RectF(0, 0, r.Width, r.Height), t.Overlay.WithAlpha(appear));

        var width = Math.Min(r.S(1500), r.Width - r.S(60));
        var height = Math.Min(r.S(860), r.Height - r.S(60));
        var panel = new RectF((r.Width - width) / 2, (r.Height - height) / 2, width, height);
        var radius = t.Radius(r) * 1.5f;
        r.Shadow(panel, radius, r.S(40), 0.6f * appear);
        r.FillGradient(panel, Rgba.Lerp(t.Background, t.Background2, 0.35f).WithAlpha(0.98f * appear), t.Background.WithAlpha(0.98f * appear), radius);
        r.Outline(panel, t.PanelBorder.WithAlpha(appear), r.S(2), radius);
        r.Fill(new RectF(panel.X + radius, panel.Y, panel.W - 2 * radius, r.S(4)), t.Accent.WithAlpha(appear));

        var x = panel.X + r.S(48);
        var innerW = panel.W - r.S(96);
        var small = t.Body(r, 24);
        r.Text(t.Title(r, 30), t.Heading(_session.Title), x, panel.Y + r.S(40), t.Accent.WithAlpha(appear), innerW * 0.55f);
        r.TextRight(small, HotkeyText(), panel.Right - r.S(48), panel.Y + r.S(46), t.TextDim.WithAlpha(appear));

        // Save / Load pages.
        var tabFont = t.Body(r, 28);
        var tx = x;
        var tabY = panel.Y + r.S(100);
        foreach (var (label, active) in new[] { ("Save state", _saving), ("Load state", !_saving) })
        {
            var w = UiRenderer.Measure(tabFont, label).X + r.S(40);
            var pill = new RectF(tx, tabY, w, r.S(52));
            if (active)
                r.Fill(pill, t.Accent.WithAlpha(appear), pill.H / 2);
            r.Text(tabFont, label, pill.X + r.S(20), pill.Y + (pill.H - tabFont.LineHeight) / 2, (active ? t.SelectionText : t.TextDim).WithAlpha(appear));
            tx = pill.Right + r.S(10);
        }

        // Slot cards.
        var footerH = r.S(96);
        var gridTop = tabY + r.S(84);
        var gap = r.S(24);
        var rows = (_slots.Count + Columns - 1) / Columns;
        var cardW = (innerW - gap * (Columns - 1)) / Columns;
        var cardH = Math.Min((panel.Bottom - footerH - gridTop - gap * (rows - 1)) / Math.Max(rows, 1), cardW * 0.95f);
        var labelFont = t.Body(r, 26);
        var dateFont = t.Body(r, 22);
        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            var card = new RectF(x + i % Columns * (cardW + gap), gridTop + i / Columns * (cardH + gap), cardW, cardH);
            var selected = i == _selected;
            r.Fill(card, (selected ? t.Selection : t.Panel).WithAlpha((selected ? 0.9f : 0.6f) * appear), r.S(12));
            if (selected)
                r.Outline(card, t.Accent.WithAlpha(appear), r.S(3), r.S(12));

            var picture = new RectF(card.X + r.S(12), card.Y + r.S(12), card.W - r.S(24), card.H - r.S(88));
            r.Fill(picture, Rgba.Black.WithAlpha(0.85f * appear), r.S(8));
            if (!slot.IsEmpty && File.Exists(slot.ThumbnailPath) && _app.Images.Get(slot.ThumbnailPath, out var fade) is { } texture)
            {
                // Fit the picture inside, keeping its shape.
                var scale = Math.Min(picture.W / texture.Width, picture.H / texture.Height);
                var w = texture.Width * scale;
                var h = texture.Height * scale;
                r.Image(texture, new RectF(picture.CenterX - w / 2, picture.CenterY - h / 2, w, h), appear * fade);
            }
            else
            {
                r.TextCentered(labelFont, slot.IsEmpty ? "Empty" : "", picture.CenterX, picture.CenterY - labelFont.LineHeight / 2, t.TextDim.WithAlpha(appear));
            }

            var textColor = selected ? t.SelectionText : t.Text;
            var current = slot.Number == _session.CurrentSlot ? "  ●" : "";
            r.Text(labelFont, $"Slot {slot.Number}{current}", card.X + r.S(16), card.Bottom - r.S(70), textColor.WithAlpha(appear), card.W - r.S(32));
            r.Text(dateFont, slot.Saved is { } saved ? When(saved) : "—", card.X + r.S(16), card.Bottom - r.S(38), (selected ? t.SelectionText : t.TextDim).WithAlpha(appear), card.W - r.S(32));
        }

        // Footer.
        var fy = panel.Bottom - r.S(70);
        r.Fill(new RectF(panel.X + r.S(24), fy - r.S(16), panel.W - r.S(48), r.S(2)), t.PanelBorder.WithAlpha(appear));
        if (_flashTime > 0 && _flash != null)
        {
            r.Text(small, _flash, x, fy, t.Accent.WithAlpha(appear * Math.Clamp(_flashTime / 0.4f, 0, 1)), innerW);
        }
        else
        {
            var verb = _saving ? (_slots.Count > 0 && !_slots[_selected].IsEmpty ? "save over" : "save") : "load";
            r.Text(small, $"{input.Label(UiAction.Accept)} {verb}   {input.Label(UiAction.Favorite)} delete   {input.Label(UiAction.PrevTab)} / {input.Label(UiAction.NextTab)} save or load   {input.Label(UiAction.Back)} back", x, fy, t.TextDim.WithAlpha(appear), innerW);
        }
    }

    /// <summary>Which keys save and load the ● slot during play, e.g. "F2 saves and F4 loads slot 3 (●) during play".</summary>
    string HotkeyText()
    {
        string? Key(Hotkey hotkey) => _app.Controls.HotkeyBindings(hotkey).FirstOrDefault(b => !_app.GameInput.Mapper.NeedsEnable(b)) is { } b && b != default ? b.Label : null;
        var (save, load) = (Key(Hotkey.SaveState), Key(Hotkey.LoadState));
        return save != null && load != null ? $"{save} saves and {load} loads slot {_session.CurrentSlot} (●) during play" : $"Slot {_session.CurrentSlot} (●) is used by the save and load hotkeys";
    }
}
