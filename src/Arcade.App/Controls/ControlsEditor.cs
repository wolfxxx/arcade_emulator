using Arcade.App.Ui;
using Arcade.Libretro;
using SDL;

namespace Arcade.App.Controls;

/// <summary>The game the controls screen was opened for, so it can show what each button does in it.</summary>
sealed record ControlsGame(string SetName, string Title, IReadOnlyList<InputDescriptor> Descriptors, bool IsVertical);

/// <summary>
/// The controls screen: one page per player, one for hotkeys and one for the current game.
/// Choose a control and press the key or button you want for it; "Set up every control" asks for
/// each in turn, which is the quickest way to set up a cabinet. Drawn over the game list or the
/// pause menu, and works with a stick alone.
/// </summary>
sealed class ControlsEditor
{
    const float CaptureSeconds = 6f;

    enum RowKind { Control, Hotkey, Enable, Choice, Action }

    sealed class Row
    {
        public required RowKind Kind { get; init; }
        public required string Label { get; init; }
        public ArcadeControl Control { get; init; }
        public Hotkey Hotkey { get; init; }
        public Func<string>? Detail { get; init; }
        public Func<string>? Value { get; init; }
        public IReadOnlyList<string>? Choices { get; init; }
        public Func<int>? GetChoice { get; init; }
        public Action<int>? SetChoice { get; init; }
        public Action? OnAccept { get; init; }
        public string? Hint { get; init; }
        public bool Dim { get; init; }
    }

    readonly ArcadeApp _app;
    readonly ControlsGame? _game;
    readonly List<string> _pages = new();
    int _page;
    int _selected;
    List<Row> _rows = new();
    float _scroll = -1, _highlightY = -1, _openTime;

    // Capture state: which row is waiting for a press, and the "set up every control" queue.
    Row? _capturing;
    float _captureLeft;
    readonly Queue<Row> _walkthrough = new();
    int _walkthroughTotal;
    string? _flash;
    float _flashTime;

    /// <summary>Called after anything changes, so a running game can pick up its new setup.</summary>
    public Action? Changed { get; init; }
    public bool IsOpen { get; private set; } = true;

    ControlConfig Config => _app.Controls;
    InputManager Input => _app.GameInput;

    public ControlsEditor(ArcadeApp app, ControlsGame? game, int startPage = 0)
    {
        _app = app;
        _game = game;
        for (var p = 1; p <= ArcadeControls.MaxPlayers; p++)
            _pages.Add($"Player {p}");
        _pages.Add("Hotkeys");
        if (game != null)
            _pages.Add("This game");
        _page = Math.Clamp(startPage, 0, _pages.Count - 1);
        Build();
    }

    bool IsPlayerPage => _page < ArcadeControls.MaxPlayers;
    bool IsHotkeyPage => _page == ArcadeControls.MaxPlayers;
    int Player => _page;

    // ---- Rows ----

    void Build()
    {
        _rows = IsPlayerPage ? PlayerRows() : IsHotkeyPage ? HotkeyRows() : GameRows();
        _selected = Math.Clamp(_selected, 0, _rows.Count - 1);
    }

    List<Row> PlayerRows()
    {
        var player = Player;
        var rows = new List<Row>();
        foreach (var control in ArcadeControls.All)
        {
            var used = UsedInGame(player, control);
            rows.Add(new Row
            {
                Kind = RowKind.Control, Control = control, Label = control.DisplayName(),
                Detail = () => GameFunction(player, control) ?? "",
                Value = () => ControlBindingsText(player, control),
                Dim = !used,
            });
        }
        rows.Add(new Row
        {
            Kind = RowKind.Action, Label = "Set up every control in turn",
            OnAccept = () => StartWalkthrough(rows.Where(r => r.Kind == RowKind.Control)),
            Hint = "Press each key or button when asked; wait to skip one, Esc to stop",
        });
        rows.Add(new Row
        {
            Kind = RowKind.Action, Label = $"Reset player {player + 1} to defaults",
            OnAccept = () =>
            {
                Config.ResetKeyboard(player);
                if (Input.DeviceName(player) is { } device)
                    Config.Devices.Remove(device);
                AfterChange($"Player {player + 1} controls reset");
            },
        });
        return rows;
    }

    List<Row> HotkeyRows()
    {
        var rows = new List<Row>
        {
            new()
            {
                Kind = RowKind.Enable, Label = "Hotkey enable",
                Value = () => Join(Config.HotkeyEnable ?? []),
                Hint = "Hold this, then a game button, for hotkeys on buttons the game also uses",
            },
        };
        foreach (var hotkey in Enum.GetValues<Hotkey>())
            rows.Add(new Row
            {
                Kind = RowKind.Hotkey, Hotkey = hotkey, Label = hotkey.DisplayName(),
                Value = () => HotkeyText(hotkey),
            });
        float[] holds = [0, 0.25f, 0.5f, 1f];
        rows.Add(new Row
        {
            Kind = RowKind.Choice, Label = "Hold combos for",
            Choices = holds.Select(h => h == 0 ? "No wait" : $"{h:0.##} s").ToList(),
            GetChoice = () => Math.Max(0, Array.IndexOf(holds, Config.ComboHoldSeconds)),
            SetChoice = i => { Config.ComboHoldSeconds = holds[i]; AfterChange(null); },
            Hint = "A short hold stops coin-then-start from opening the menu by accident",
        });
        rows.Add(new Row
        {
            Kind = RowKind.Action, Label = "Reset hotkeys to defaults",
            OnAccept = () =>
            {
                Config.Hotkeys = ControlConfig.DefaultHotkeys();
                Config.HotkeyEnable = ControlConfig.DefaultHotkeyEnable();
                Config.ComboHoldSeconds = 0.5f;
                AfterChange("Hotkeys reset");
            },
        });
        return rows;
    }

    List<Row> GameRows()
    {
        var game = _game!;
        var rows = new List<Row>();
        var setup = () => Config.Game(game.SetName);
        var choices = new List<string> { "Nothing" };
        for (var b = 1; b <= ArcadeControls.ButtonCount; b++)
            choices.Add(GameButtonName(0, b) is { } name ? $"{b} · {name}" : $"Game button {b}");

        for (var panel = 1; panel <= ArcadeControls.ButtonCount; panel++)
        {
            var p = panel;
            rows.Add(new Row
            {
                Kind = RowKind.Choice, Label = $"Panel button {p}", Choices = choices,
                GetChoice = () => setup().GameButton(p),
                SetChoice = i => SetGameButton(game.SetName, p, i),
                Hint = "Which game button this panel button presses in this game",
                Dim = game.Descriptors.Count > 0 && GameButtonName(0, setup().GameButton(p)) == null,
            });
        }
        var rotations = new List<string> { game.IsVertical ? "Default for vertical games" : "Default (upright)", "Upright", "Turned right", "Upside down", "Turned left" };
        rows.Add(new Row
        {
            Kind = RowKind.Choice, Label = "Picture", Choices = rotations,
            GetChoice = () => setup().Rotation is { } r ? r % 4 + 1 : 0,
            SetChoice = i =>
            {
                var s = EnsureGame(game.SetName);
                s.Rotation = i == 0 ? null : i - 1;
                AfterChange(null);
            },
            Hint = "Turn the picture to fill a screen on its side; the stick turns with it",
        });
        rows.Add(new Row
        {
            Kind = RowKind.Action, Label = "Reset this game's setup",
            OnAccept = () =>
            {
                Config.Games.Remove(game.SetName);
                AfterChange($"{game.Title}: back to the normal layout");
            },
        });
        return rows;
    }

    GameSetup EnsureGame(string setName)
    {
        if (!Config.Games.TryGetValue(setName, out var setup))
            Config.Games[setName] = setup = new GameSetup();
        return setup;
    }

    void SetGameButton(string setName, int panel, int game)
    {
        var setup = EnsureGame(setName);
        if (game == panel)
            setup.Buttons.Remove(panel);
        else
            setup.Buttons[panel] = game;
        AfterChange(null);
    }

    // ---- Text for rows ----

    static string Join(IEnumerable<Binding> bindings)
    {
        var text = string.Join("  ·  ", bindings.Select(b => b.Label));
        return text.Length == 0 ? "—" : text;
    }

    string ControlBindingsText(int player, ArcadeControl control)
    {
        var parts = Config.PlayerKeys(player).Get(control).Where(b => b.IsKeyboard).ToList();
        var device = Input.Devices.ElementAtOrDefault(player);
        var map = device != null ? Config.DeviceMap(device.Name, device.IsGamepad) : Config.Gamepad;
        parts.AddRange(map.Get(control).Where(b => !b.IsKeyboard));
        return Join(parts);
    }

    string HotkeyText(Hotkey hotkey)
    {
        var enable = (Config.HotkeyEnable ?? []).FirstOrDefault();
        var parts = Config.HotkeyBindings(hotkey).Select(b =>
            Input.Mapper.NeedsEnable(b) && enable != default ? $"{enable.Label} + {b.Label}" : b.Label);
        var text = string.Join("  ·  ", parts);
        return text.Length == 0 ? "—" : text;
    }

    /// <summary>What a panel control does in the current game, e.g. "Weak Punch", using the game's button layout.</summary>
    string? GameFunction(int player, ArcadeControl control)
    {
        if (_game == null || _game.Descriptors.Count == 0)
            return null;
        if (control.ButtonNumber() is var panel and > 0)
        {
            var game = Config.Game(_game.SetName).GameButton(panel);
            return game == 0 ? "nothing in this game" : GameButtonName(player, game) ?? "not used";
        }
        return Descriptor(player, control.RetroButton());
    }

    bool UsedInGame(int player, ArcadeControl control) =>
        _game == null || _game.Descriptors.Count == 0 || control.ButtonNumber() == 0 || GameFunction(player, control) is not ("not used" or "nothing in this game");

    string? GameButtonName(int player, int gameButton) =>
        gameButton is >= 1 and <= ArcadeControls.ButtonCount ? Descriptor(player, ArcadeControls.Button(gameButton).RetroButton()) : null;

    string? Descriptor(int port, JoypadButton id) =>
        _game?.Descriptors.FirstOrDefault(d => d.Port == port && d.Device == RetroDevice.Joypad && d.Id == (uint)id)?.Description;

    // ---- Changes ----

    void AfterChange(string? message)
    {
        Input.Mapper.ConfigChanged();
        Changed?.Invoke();
        if (message != null)
            Flash(message);
    }

    void Flash(string message)
    {
        _flash = message;
        _flashTime = 2.5f;
    }

    /// <summary>Stores a captured key or button for the row being set up.</summary>
    void Assign(Row row, Binding binding, int deviceIndex)
    {
        string where;
        if (row.Kind == RowKind.Control)
        {
            var player = Player;
            if (binding.IsKeyboard)
            {
                // A key does one thing: take it off every other control first.
                foreach (var map in Config.Keyboard)
                    foreach (var list in map.Values)
                        list.Remove(binding);
                ReplaceSameKind(Config.PlayerKeys(player), row.Control, binding);
                where = "keyboard";
            }
            else
            {
                var device = Input.Devices[deviceIndex];
                if (!Config.Devices.TryGetValue(device.Name, out var profile))
                    Config.Devices[device.Name] = profile = new ControlMap(Config.DeviceMap(device.Name, device.IsGamepad));
                foreach (var list in profile.Values)
                    list.Remove(binding);
                ReplaceSameKind(profile, row.Control, binding);
                where = deviceIndex == player ? device.Name : $"{device.Name} (player {deviceIndex + 1}'s controller)";
            }
            AfterChange($"{row.Label}: {binding.Label} on {where}");
            return;
        }

        var bindings = row.Kind == RowKind.Enable ? Config.HotkeyEnable ??= new() : GetOrAddHotkey(row.Hotkey);
        bindings.RemoveAll(b => SameKind(b, binding));
        bindings.Add(binding);
        AfterChange($"{row.Label}: {binding.Label}");
    }

    List<Binding> GetOrAddHotkey(Hotkey hotkey)
    {
        if (!Config.Hotkeys.TryGetValue(hotkey, out var list))
            Config.Hotkeys[hotkey] = list = new();
        return list;
    }

    /// <summary>
    /// Replaces bindings of the same sort only, so setting a new d-pad button for Up keeps the
    /// stick working, and setting a key keeps the pad buttons.
    /// </summary>
    static void ReplaceSameKind(ControlMap map, ArcadeControl control, Binding binding)
    {
        if (!map.TryGetValue(control, out var list))
            map[control] = list = new();
        list.RemoveAll(b => SameKind(b, binding));
        list.Insert(0, binding);
    }

    static bool SameKind(Binding a, Binding b) =>
        a.Kind == b.Kind || (a.IsKeyboard && b.IsKeyboard);

    void Clear(Row row)
    {
        switch (row.Kind)
        {
            case RowKind.Control:
                Config.PlayerKeys(Player)[row.Control] = new();
                var device = Input.Devices.ElementAtOrDefault(Player);
                if (device != null)
                {
                    if (!Config.Devices.TryGetValue(device.Name, out var profile))
                        Config.Devices[device.Name] = profile = new ControlMap(Config.DeviceMap(device.Name, device.IsGamepad));
                    profile[row.Control] = new();
                }
                AfterChange($"{row.Label} cleared");
                break;
            case RowKind.Hotkey:
                Config.Hotkeys[row.Hotkey] = new();
                AfterChange($"{row.Label} cleared");
                break;
            case RowKind.Enable:
                Config.HotkeyEnable = new();
                AfterChange("Hotkey enable cleared: hotkeys on game buttons are off");
                break;
        }
    }

    // ---- Capture ----

    void StartCapture(Row row)
    {
        _capturing = row;
        _captureLeft = CaptureSeconds;
        Input.BeginCapture();
    }

    void StartWalkthrough(IEnumerable<Row> rows)
    {
        _walkthrough.Clear();
        foreach (var row in rows)
            _walkthrough.Enqueue(row);
        _walkthroughTotal = _walkthrough.Count;
        NextInWalkthrough();
    }

    void NextInWalkthrough()
    {
        if (_walkthrough.TryDequeue(out var row))
        {
            _selected = _rows.IndexOf(row);
            StartCapture(row);
        }
        else if (_walkthroughTotal > 0)
        {
            _walkthroughTotal = 0;
            Flash("All set");
        }
    }

    void EndCapture(bool stopWalkthrough)
    {
        _capturing = null;
        _app.UiInput.SuppressHeld();
        Input.Mapper.ResetHotkeys();
        if (stopWalkthrough)
        {
            _walkthrough.Clear();
            _walkthroughTotal = 0;
        }
        else
        {
            NextInWalkthrough();
        }
    }

    void UpdateCapture(float dt)
    {
        _captureLeft -= dt;
        if (Input.TryCapture(out var binding, out var device))
        {
            if (binding == Binding.Key(SDL_Scancode.SDL_SCANCODE_ESCAPE))
            {
                Flash(_walkthroughTotal > 0 ? "Stopped" : "Not changed");
                EndCapture(stopWalkthrough: true);
                return;
            }
            Assign(_capturing!, binding, device);
            EndCapture(stopWalkthrough: false);
            return;
        }
        if (_captureLeft <= 0)
            EndCapture(stopWalkthrough: _walkthroughTotal == 0); // in the walkthrough, waiting skips to the next one
    }

    // ---- Input ----

    public void Update(IReadOnlyList<UiAction> actions, float dt)
    {
        _openTime += dt;
        _flashTime -= dt;
        if (_capturing != null)
        {
            UpdateCapture(dt);
            return;
        }
        foreach (var action in actions)
        {
            if (!IsOpen)
                return;
            var row = _rows[_selected];
            switch (action)
            {
                case UiAction.Up: _selected = (_selected - 1 + _rows.Count) % _rows.Count; break;
                case UiAction.Down: _selected = (_selected + 1) % _rows.Count; break;
                case UiAction.Left when row.Kind == RowKind.Choice: Cycle(row, -1); break;
                case UiAction.Right when row.Kind == RowKind.Choice: Cycle(row, 1); break;
                case UiAction.Left:
                case UiAction.PrevTab: SwitchPage(-1); break;
                case UiAction.Right:
                case UiAction.NextTab: SwitchPage(1); break;
                case UiAction.PageUp: _selected = 0; break;
                case UiAction.PageDown: _selected = _rows.Count - 1; break;
                case UiAction.Accept:
                    switch (row.Kind)
                    {
                        case RowKind.Choice: Cycle(row, 1); break;
                        case RowKind.Action: row.OnAccept?.Invoke(); break;
                        default: StartCapture(row); break;
                    }
                    break;
                case UiAction.Favorite when row.Kind is RowKind.Control or RowKind.Hotkey or RowKind.Enable:
                    Clear(row);
                    break;
                case UiAction.Back:
                    Close();
                    break;
            }
        }
    }

    static void Cycle(Row row, int delta)
    {
        var count = row.Choices!.Count;
        row.SetChoice!((row.GetChoice!() + delta + count) % count);
    }

    void SwitchPage(int delta)
    {
        _page = (_page + delta + _pages.Count) % _pages.Count;
        _selected = 0;
        _scroll = -1;
        _highlightY = -1;
        Build();
    }

    public void Close()
    {
        IsOpen = false;
        _app.SaveControls();
    }

    // ---- Drawing ----

    public void Draw(UiRenderer r, Theme t, UiInput input, float dt)
    {
        var appear = Easing.SmoothStep(_openTime / 0.15f);
        r.Fill(new RectF(0, 0, r.Width, r.Height), t.Overlay.WithAlpha(appear));

        var width = Math.Min(r.S(1560), r.Width - r.S(60));
        var height = Math.Min(r.S(940), r.Height - r.S(60));
        var panel = new RectF((r.Width - width) / 2, (r.Height - height) / 2, width, height);
        var radius = t.Radius(r) * 1.5f;
        r.Shadow(panel, radius, r.S(40), 0.6f * appear);
        r.FillGradient(panel, Rgba.Lerp(t.Background, t.Background2, 0.35f).WithAlpha(0.98f * appear), t.Background.WithAlpha(0.98f * appear), radius);
        r.Outline(panel, t.PanelBorder.WithAlpha(appear), r.S(2), radius);
        r.Fill(new RectF(panel.X + radius, panel.Y, panel.W - 2 * radius, r.S(4)), t.Accent.WithAlpha(appear));

        var x = panel.X + r.S(48);
        var innerW = panel.W - r.S(96);
        r.Text(t.Title(r, 30), t.Heading("Controls"), x, panel.Y + r.S(40), t.Accent.WithAlpha(appear));
        var small = t.Body(r, 24);
        r.TextRight(small, SubtitleText(), panel.Right - r.S(48), panel.Y + r.S(46), t.TextDim.WithAlpha(appear));

        // Page tabs.
        var tabFont = t.Body(r, 28);
        var tx = x;
        var tabY = panel.Y + r.S(100);
        for (var i = 0; i < _pages.Count; i++)
        {
            var w = UiRenderer.Measure(tabFont, _pages[i]).X + r.S(40);
            var pill = new RectF(tx, tabY, w, r.S(52));
            if (i == _page)
                r.Fill(pill, t.Accent.WithAlpha(appear), pill.H / 2);
            r.Text(tabFont, _pages[i], pill.X + r.S(20), pill.Y + (pill.H - tabFont.LineHeight) / 2, (i == _page ? t.SelectionText : t.TextDim).WithAlpha(appear));
            tx = pill.Right + r.S(10);
        }

        // Device line for player pages.
        var listTop = tabY + r.S(76);
        if (IsPlayerPage)
        {
            r.Text(small, DeviceText(), x, listTop, t.Accent2.WithAlpha(appear), innerW);
            listTop += r.S(44);
        }

        // Rows, scrolling to keep the selection in view.
        var footerH = r.S(96);
        var list = new RectF(panel.X + r.S(24), listTop, panel.W - r.S(48), panel.Bottom - footerH - listTop);
        var rowH = r.S(54);
        var visible = list.H / rowH;
        var target = Math.Clamp(_selected - visible / 2 + 0.5f, 0, Math.Max(0, _rows.Count - visible));
        _scroll = _scroll < 0 ? target : Easing.Approach(_scroll, target, 18, dt);
        var highlightTarget = list.Y + (_selected - _scroll) * rowH;
        _highlightY = _highlightY < 0 ? highlightTarget : Easing.Approach(_highlightY, highlightTarget, 30, dt);

        r.PushClip(list);
        r.Fill(new RectF(list.X, _highlightY, list.W, rowH - r.S(6)), t.Selection.WithAlpha(0.9f * appear), r.S(10));
        var font = t.Body(r, 30);
        var labelW = r.S(330);
        var detailW = IsPlayerPage && _game?.Descriptors.Count > 0 ? r.S(380) : 0;
        for (var i = Math.Max(0, (int)_scroll); i < _rows.Count && i < _scroll + visible + 1; i++)
        {
            var row = _rows[i];
            var y = list.Y + (i - _scroll) * rowH;
            var ty = y + (rowH - r.S(6) - font.LineHeight) / 2;
            var selected = i == _selected;
            var color = selected ? t.SelectionText : row.Dim ? t.TextDim : t.Text;
            var rx = list.X + r.S(24);
            r.Text(font, row.Label, rx, ty, color.WithAlpha(appear), labelW - r.S(10));
            var valueX = rx + labelW;
            if (detailW > 0)
            {
                r.Text(t.Body(r, 26), row.Detail?.Invoke() ?? "", valueX, ty + r.S(2), (selected ? t.SelectionText : t.Accent2).WithAlpha(appear * (row.Dim ? 0.6f : 1)), detailW - r.S(16));
                valueX += detailW;
            }
            var right = list.Right - r.S(24);
            if (row.Kind == RowKind.Choice)
            {
                var value = row.Choices![row.GetChoice!()];
                r.TextRight(font, selected ? $"‹  {value}  ›" : value, right, ty, (selected ? t.SelectionText : t.Accent2).WithAlpha(appear));
            }
            else if (row.Value != null)
            {
                var capturing = _capturing == row;
                var text = capturing ? "press a key or button…" : row.Value();
                r.Text(font, text, valueX, ty, (capturing ? t.Warning : selected ? t.SelectionText : t.Text).WithAlpha(appear), right - valueX);
            }
        }
        r.PopClip();

        // Footer: what the buttons do here, or the last change.
        var fy = panel.Bottom - r.S(70);
        r.Fill(new RectF(panel.X + r.S(24), fy - r.S(16), panel.W - r.S(48), r.S(2)), t.PanelBorder.WithAlpha(appear));
        if (_flashTime > 0 && _flash != null)
            r.Text(small, _flash, x, fy, t.Accent.WithAlpha(appear * Math.Clamp(_flashTime / 0.4f, 0, 1)), innerW);
        else if (_rows[_selected].Hint is { } hint)
            r.Text(small, hint, x, fy, t.TextDim.WithAlpha(appear), innerW);
        else
        {
            var change = _rows[_selected].Kind == RowKind.Choice ? "◂ ▸ change" : $"{input.Label(UiAction.Accept)} change   {input.Label(UiAction.Favorite)} clear";
            r.Text(small, $"{change}   {input.Label(UiAction.PrevTab)} / {input.Label(UiAction.NextTab)} page   {input.Label(UiAction.Back)} done", x, fy, t.TextDim.WithAlpha(appear), innerW);
        }

        if (_capturing != null)
            DrawCapture(r, t, panel);
    }

    string SubtitleText() => _game != null ? _game.Title : "Changes apply to every game";

    string DeviceText()
    {
        var device = Input.Devices.ElementAtOrDefault(Player);
        if (device == null)
            return Player == 0
                ? "Keyboard. Controllers join as players 1, 2… in the order they're connected."
                : $"Keyboard only: no controller connected for player {Player + 1}. Pad buttons shown are the shared pad layout.";
        var own = Config.Devices.ContainsKey(device.Name) ? "its own layout" : device.IsGamepad ? "the shared pad layout" : "the shared joystick layout";
        return $"Keyboard and {device.Name} ({own})";
    }

    void DrawCapture(UiRenderer r, Theme t, RectF panel)
    {
        var row = _capturing!;
        r.Fill(new RectF(0, 0, r.Width, r.Height), t.Overlay);
        var box = new RectF(panel.CenterX - r.S(560), panel.CenterY - r.S(190), r.S(1120), r.S(380));
        var radius = t.Radius(r) * 1.5f;
        r.Shadow(box, radius, r.S(40), 0.7f);
        r.FillGradient(box, Rgba.Lerp(t.Background, t.Background2, 0.5f) with { A = 255 }, t.Background with { A = 255 }, radius);
        r.Outline(box, t.Accent, r.S(3), radius);
        var who = row.Kind == RowKind.Control ? $"Player {Player + 1} · {row.Label}" : row.Label;
        var function = row.Kind == RowKind.Control ? GameFunction(Player, row.Control) : null;
        var progress = _walkthroughTotal > 0 ? $"{_walkthroughTotal - _walkthrough.Count} of {_walkthroughTotal}" : "";
        var maxW = box.W - r.S(80);
        r.TextCentered(t.Body(r, 28), "Press the key or button for", box.CenterX, box.Y + r.S(44), t.TextDim);
        r.TextCentered(t.Title(r, 32), t.Heading(who), box.CenterX, box.Y + r.S(100), t.Accent);
        if (!string.IsNullOrEmpty(function))
            r.TextCentered(t.Body(r, 28), UiRenderer.Ellipsize(t.Body(r, 28), $"In this game: {function}", maxW), box.CenterX, box.Y + r.S(160), t.Accent2);
        var bar = new RectF(box.X + r.S(80), box.Y + r.S(238), box.W - r.S(160), r.S(10));
        r.Fill(bar, t.PanelBorder, r.S(5));
        r.Fill(bar with { W = bar.W * Math.Clamp(_captureLeft / CaptureSeconds, 0, 1) }, t.Accent2, r.S(5));
        var help = _walkthroughTotal > 0 ? $"{progress} · wait to skip this one · Esc stops" : "Esc or wait to leave it as it is";
        r.TextCentered(t.Body(r, 24), help, box.CenterX, box.Y + r.S(290), t.TextDim);
    }
}
