namespace Arcade.App.Ui;

/// <summary>One line in a <see cref="Menu"/>: an action, or a setting whose value cycles with left/right.</summary>
sealed class MenuItem
{
    public required string Label { get; init; }
    public Action? OnAccept { get; init; }
    /// <summary>Choices for a setting; the selected one is shown at the right and changed with left/right.</summary>
    public IReadOnlyList<string>? Choices { get; init; }
    public int Choice { get; set; }
    public Action<int>? OnChoice { get; init; }
    public string? Hint { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>Accepting this item closes the menu first (most actions do).</summary>
    public bool Closes { get; init; } = true;
}

/// <summary>
/// A vertical, joystick-driven menu drawn as a centred panel over whatever is behind it.
/// Used for the options menu in the game list and the pause menu in a game.
/// </summary>
sealed class Menu(string title, IReadOnlyList<MenuItem> items)
{
    int _selected = Math.Max(0, items.ToList().FindIndex(i => i.Enabled));
    float _highlightY = -1;
    float _openTime;

    public string Title { get; } = title;
    public string? Subtitle { get; init; }
    /// <summary>A paragraph of text above the items, e.g. an error explanation.</summary>
    public string? Body { get; init; }
    /// <summary>Extra lines shown beside the items (e.g. the game's controls in the pause menu).</summary>
    public IReadOnlyList<(string Label, string Value)>? SidePanel { get; init; }
    public bool IsOpen { get; private set; } = true;
    /// <summary>Called when the menu is closed with Back rather than by choosing an item.</summary>
    public Action? OnCancel { get; init; }
    public IReadOnlyList<MenuItem> Items => items;
    public int Selected => _selected;

    public void Close() => IsOpen = false;

    public void Update(IReadOnlyList<UiAction> actions, float dt)
    {
        _openTime += dt;
        foreach (var action in actions)
        {
            if (!IsOpen)
                return;
            var item = items[_selected];
            switch (action)
            {
                case UiAction.Up: Move(-1); break;
                case UiAction.Down: Move(1); break;
                case UiAction.Left when item.Choices != null: Cycle(item, -1); break;
                case UiAction.Right when item.Choices != null: Cycle(item, 1); break;
                case UiAction.Accept when item.Enabled:
                    if (item.Choices != null && item.OnAccept == null)
                    {
                        Cycle(item, 1);
                        break;
                    }
                    if (item.Closes)
                        IsOpen = false;
                    item.OnAccept?.Invoke();
                    break;
                case UiAction.Back:
                case UiAction.Options:
                    IsOpen = false;
                    OnCancel?.Invoke();
                    break;
            }
        }
    }

    void Move(int delta)
    {
        for (var i = 0; i < items.Count; i++)
        {
            _selected = (_selected + delta + items.Count) % items.Count;
            if (items[_selected].Enabled)
                return;
        }
    }

    static void Cycle(MenuItem item, int delta)
    {
        item.Choice = (item.Choice + delta + item.Choices!.Count) % item.Choices.Count;
        item.OnChoice?.Invoke(item.Choice);
    }

    public void Draw(UiRenderer r, Theme theme, UiInput input, float dt)
    {
        var appear = Easing.SmoothStep(_openTime / 0.15f);
        r.Fill(new RectF(0, 0, r.Width, r.Height), theme.Overlay.WithAlpha(appear));

        var itemFont = theme.Body(r, 34);
        var titleFont = theme.Title(r, 30);
        var smallFont = theme.Body(r, 24);
        var rowH = r.S(64);
        var hasSide = SidePanel is { Count: > 0 };
        var width = r.S(hasSide ? 1180 : 760);
        var listW = hasSide ? r.S(640) : width;
        var bodyFont = theme.Body(r, 28);
        var bodyLines = Body == null ? [] : UiRenderer.Wrap(bodyFont, Body, listW - r.S(96));
        var bodyH = bodyLines.Count * bodyFont.LineHeight * 1.15f + (bodyLines.Count > 0 ? r.S(24) : 0);
        // Long menus (e.g. a game's cheats) scroll to keep the selection in view.
        var rowsShown = Math.Clamp((int)((r.Height - r.S(300) - bodyH) / rowH), 3, items.Count);
        var first = Math.Clamp(_selected - rowsShown / 2, 0, items.Count - rowsShown);
        var height = r.S(150) + bodyH + rowH * rowsShown + r.S(70);
        var panel = new RectF((r.Width - width) / 2, (r.Height - height) / 2 + r.S(20) * (1 - appear), width, height);
        var radius = theme.Radius(r) * 1.5f;

        r.Shadow(panel, radius, r.S(40), 0.6f * appear);
        r.FillGradient(panel, Rgba.Lerp(theme.Background, theme.Background2, 0.35f).WithAlpha(0.97f * appear), theme.Background.WithAlpha(0.97f * appear), radius);
        r.Outline(panel, theme.PanelBorder.WithAlpha(appear), r.S(2), radius);
        r.Fill(new RectF(panel.X + radius, panel.Y, panel.W - 2 * radius, r.S(4)), theme.Accent.WithAlpha(appear));

        var x = panel.X + r.S(48);
        r.Text(titleFont, theme.Heading(Title), x, panel.Y + r.S(44), theme.Accent.WithAlpha(appear), width - r.S(96));
        if (Subtitle != null)
            r.Text(smallFont, Subtitle, x, panel.Y + r.S(92), theme.TextDim.WithAlpha(appear), width - r.S(96));

        var by = panel.Y + r.S(130);
        foreach (var line in bodyLines)
        {
            r.Text(bodyFont, line, x, by, theme.Text.WithAlpha(appear));
            by += bodyFont.LineHeight * 1.15f;
        }
        var top = panel.Y + r.S(140) + bodyH;
        var targetY = top + (_selected - first) * rowH;
        _highlightY = _highlightY < 0 ? targetY : Easing.Approach(_highlightY, targetY, 30, dt);
        var rowRect = new RectF(panel.X + r.S(24), _highlightY, listW - r.S(48), rowH - r.S(6));
        r.Fill(rowRect, theme.Selection.WithAlpha(0.9f * appear), r.S(10));

        for (var i = first; i < first + rowsShown; i++)
        {
            var item = items[i];
            var y = top + (i - first) * rowH;
            var selected = i == _selected;
            var color = !item.Enabled ? theme.TextDim.WithAlpha(0.5f) : selected ? theme.SelectionText : theme.Text;
            var textY = y + (rowH - r.S(6) - itemFont.LineHeight) / 2;
            r.Text(itemFont, item.Label, x, textY, color.WithAlpha(appear), item.Choices != null ? listW * 0.56f : listW - r.S(96));
            if (item.Choices != null)
            {
                var value = item.Choices[item.Choice];
                var right = panel.X + listW - r.S(48);
                r.TextRight(itemFont, selected ? $"‹  {value}  ›" : value, right, textY, (selected ? theme.SelectionText : theme.Accent2).WithAlpha(appear));
            }
        }
        if (first > 0)
            r.TextRight(smallFont, "▲", panel.X + listW - r.S(30), top - r.S(30), theme.TextDim.WithAlpha(appear));
        if (first + rowsShown < items.Count)
            r.TextRight(smallFont, "▼", panel.X + listW - r.S(30), top + rowsShown * rowH - r.S(4), theme.TextDim.WithAlpha(appear));

        if (items[_selected].Hint is { } hint)
            r.Text(smallFont, hint, x, panel.Bottom - r.S(56), theme.TextDim.WithAlpha(appear), listW - r.S(96));
        else
            r.Text(smallFont, $"{input.Label(UiAction.Accept)} select   {input.Label(UiAction.Back)} close", x, panel.Bottom - r.S(56), theme.TextDim.WithAlpha(appear));

        if (hasSide)
        {
            var sx = panel.X + listW + r.S(10);
            var sw = panel.Right - sx - r.S(40);
            r.Fill(new RectF(sx - r.S(20), panel.Y + r.S(120), r.S(2), panel.H - r.S(180)), theme.PanelBorder.WithAlpha(appear));
            var sy = panel.Y + r.S(140);
            var labelFont = theme.Body(r, 26);
            foreach (var (label, value) in SidePanel!)
            {
                if (label.Length == 0)
                {
                    r.Text(smallFont, value, sx, sy, theme.Accent2.WithAlpha(appear), sw);
                }
                else
                {
                    r.Text(labelFont, label, sx, sy, theme.TextDim.WithAlpha(appear), sw * 0.45f);
                    r.Text(labelFont, value, sx + sw * 0.45f, sy, theme.Text.WithAlpha(appear), sw * 0.55f);
                }
                sy += r.S(40);
            }
        }
    }
}
