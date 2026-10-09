using Arcade.App.Controls;
using Arcade.App.Ui;
using Arcade.Library;
using SDL;

namespace Arcade.App.Browser;

/// <summary>
/// The game list: categories along the top, the list on one side and a preview of the selected
/// game on the other. Everything works with a stick and a few buttons; keyboard and mouse also work.
/// </summary>
sealed class BrowserScene : Scene
{
    /// <summary>In cabinet mode, holding Back this long opens the operator menu.</summary>
    const float OperatorHoldSeconds = 5f;

    readonly GameListModel _model = new();
    Menu? _menu;
    ControlsEditor? _controls;
    bool _searching;
    string _search = "";
    float _tabOffset;
    float _scroll = -1;      // list scroll position in rows (animated)
    float _time;
    float _selectionPulse;
    string? _launchPending;  // set name to start once the "Loading" frame has been drawn
    int _launchFrames;
    RectF _listArea;
    float _rowHeight;
    int _lastClickRow = -1;

    public BrowserScene(ArcadeApp app, string? selectSet = null) : base(app)
    {
        _model.SetSort(app.Settings.Sort);
        _model.SetShowClones(app.Settings.ShowClones);
        if (Enum.TryParse<BrowserTab>(app.Settings.LastTab, out var tab))
            _model.SetTab(tab);
        Reload(selectSet ?? app.Settings.LastGame);
    }

    public override bool CapturesText => _searching;

    public override void Enter()
    {
        App.WindowTitle = "Arcade";
        App.ScanCompleted += OnScanCompleted;

        // First run: pick up the default roms folder automatically.
        if (App.Library.Folders.Count == 0 && Directory.Exists(App.Paths.Roms) && Directory.EnumerateFiles(App.Paths.Roms, "*.zip").Any())
        {
            App.Library.AddFolder(App.Paths.Roms);
            App.StartScan();
        }
    }

    public override void Leave()
    {
        App.ScanCompleted -= OnScanCompleted;
        if (_searching)
            App.Window.StopTextInput();
        SaveState();
    }

    void SaveState()
    {
        if (App.ReadOnly)
            return;
        App.Settings.LastTab = _model.Tab.ToString();
        App.Settings.LastGame = _model.Current?.SetName ?? App.Settings.LastGame;
        App.Settings.Save(App.SettingsPath);
    }

    void OnScanCompleted(ScanSummary? summary) => Reload(_model.Current?.SetName);

    void Reload(string? select)
    {
        _model.SetGames(App.Library.Games(new LibraryQuery(IncludeUnplayable: true)));
        if (select != null)
            _model.Select(select);
    }

    // ---- Input ----

    public override void Update(float dt, double elapsed)
    {
        _time += dt;
        _selectionPulse = Math.Max(0, _selectionPulse - dt * 3);

        if (_launchPending != null)
        {
            // Draw one "Loading" frame before the (blocking) core load.
            if (++_launchFrames >= 2)
            {
                var game = _model.Visible.FirstOrDefault(g => g.SetName == _launchPending);
                _launchPending = null;
                if (game != null && App.Launch(game) is { } error)
                    ShowError(game, error);
            }
            return;
        }

        if (_controls != null)
        {
            _controls.Update(App.UiInput.Actions, dt);
            if (!_controls.IsOpen)
                _controls = null;
            return;
        }

        if (_menu != null)
        {
            _menu.Update(App.UiInput.Actions, dt);
            if (!_menu.IsOpen)
                _menu = null; // an item that opened another menu (or the controls) has replaced it already
            return;
        }

        if (App.Kiosk && App.UiInput.HeldFor(UiAction.Back) >= OperatorHoldSeconds)
        {
            App.OperatorUnlocked = true;
            App.UiInput.SuppressHeld();
            App.ShowMessage("Operator menu: cabinet mode is off until the app restarts");
            OpenOptions();
            return;
        }

        if (_searching)
            return; // keys arrive through OnKey/OnTextInput

        foreach (var action in App.UiInput.Actions)
            HandleAction(action);

        if (App.Settings.AttractMinutes > 0 && App.UiInput.IdleSeconds > App.Settings.AttractMinutes * 60 && _model.PlayableCount > 0 && !App.Scanning)
        {
            App.UiInput.MarkActivity();
            SaveState();
            GameScene.NextAttractGame(App, null, null);
        }
    }

    void HandleAction(UiAction action)
    {
        var page = Math.Max(1, (int)(_listArea.H / Math.Max(_rowHeight, 1)) - 1);
        var before = _model.Selected;
        switch (action)
        {
            case UiAction.Up: _model.Move(-1); break;
            case UiAction.Down: _model.Move(1); break;
            case UiAction.Left: _model.JumpLetter(-1); break;
            case UiAction.Right: _model.JumpLetter(1); break;
            case UiAction.PageUp: _model.Move(-page); break;
            case UiAction.PageDown: _model.Move(page); break;
            case UiAction.Home: _model.SelectIndex(0); break;
            case UiAction.End: _model.SelectIndex(int.MaxValue); break;
            case UiAction.PrevTab: _model.NextTab(-1); _scroll = -1; break;
            case UiAction.NextTab: _model.NextTab(1); _scroll = -1; break;
            case UiAction.Accept: Activate(); break;
            case UiAction.Favorite: ToggleFavorite(); break;
            case UiAction.Options: OpenOptions(); break;
            case UiAction.Search: StartSearch(); break;
            case UiAction.Back:
                if (_model.Search.Length > 0)
                    _model.SetSearch(_search = "");
                else if (!App.Kiosk)
                    OpenQuitMenu();
                break;
        }
        if (_model.Selected != before)
            _selectionPulse = 1;
    }

    void Activate()
    {
        if (App.Library.Folders.Count == 0 || _model.All.Count == 0)
        {
            AddFolder();
            return;
        }
        if (_model.Current is not { } game)
            return;
        if (game.Status != GameStatus.Playable)
        {
            ShowError(game, game.Problem ?? LibraryCommands.Label(game.Status));
            return;
        }
        _launchPending = game.SetName;
        _launchFrames = 0;
    }

    void ToggleFavorite()
    {
        if (_model.Current is not { } game)
            return;
        App.Library.SetFavorite(game.SetName, !game.Favorite);
        App.ShowMessage(game.Favorite ? $"Removed from favourites: {game.Title}" : $"★ Added to favourites: {game.Title}");
        Reload(game.SetName);
    }

    void StartSearch()
    {
        _searching = true;
        _search = _model.Search;
        App.Window.StartTextInput();
    }

    void EndSearch(bool keep)
    {
        _searching = false;
        App.Window.StopTextInput();
        if (!keep)
            _model.SetSearch(_search = "");
        App.UiInput.SuppressHeld();
    }

    public override bool OnKey(SDL_KeyboardEvent key)
    {
        if (!_searching)
            return false;
        switch (key.scancode)
        {
            case SDL_Scancode.SDL_SCANCODE_ESCAPE: EndSearch(keep: false); break;
            case SDL_Scancode.SDL_SCANCODE_RETURN:
            case SDL_Scancode.SDL_SCANCODE_KP_ENTER:
            case SDL_Scancode.SDL_SCANCODE_DOWN: EndSearch(keep: true); break;
            case SDL_Scancode.SDL_SCANCODE_BACKSPACE when _search.Length > 0:
                _search = _search[..^1];
                _model.SetSearch(_search);
                break;
        }
        return true;
    }

    public override void OnTextInput(string text)
    {
        if (!_searching)
            return;
        _search += text;
        _model.SetSearch(_search);
    }

    public override void OnMouseWheel(float delta)
    {
        if (_menu != null || _controls != null || _searching) return;
        _model.Move(delta > 0 ? -3 : 3);
    }

    public override void OnMouseButton(float x, float y, int clicks)
    {
        if (_menu != null || _controls != null || _searching || !_listArea.Contains(x, y) || _rowHeight <= 0)
            return;
        var row = (int)((y - _listArea.Y) / _rowHeight + Math.Max(_scroll, 0));
        if (row < 0 || row >= _model.Visible.Count)
            return;
        if (clicks >= 2 && row == _lastClickRow)
            Activate();
        else
            _model.SelectIndex(row);
        _lastClickRow = row;
    }

    // ---- Menus ----

    void ShowError(LibraryGame game, string message)
    {
        _menu = new Menu($"Can't start {game.Title}", [new MenuItem { Label = "OK" }]) { Body = message };
    }

    void OpenQuitMenu()
    {
        _menu = new Menu("Quit Arcade?",
        [
            new MenuItem { Label = "Keep playing" },
            new MenuItem { Label = "Quit", OnAccept = App.Quit },
        ]);
    }

    void OpenOptions()
    {
        var game = _model.Current;
        var themes = Theme.List(App.ThemesDir);
        var themeIndex = Math.Max(0, themes.FindIndex(t => t.Id == App.Theme.Id));
        var cores = new List<(string? Id, string Name)> { (null, "Automatic") };
        cores.AddRange(CoreCatalog.Known.Where(c => App.Catalog.LibraryCores.Any(lc => lc.Id == c.Id)).Select(c => ((string?)c.Id, c.DisplayName)));
        var sorts = Enum.GetValues<SortOrder>();
        int[] attractOptions = [0, 1, 3, 5, 10];

        var items = new List<MenuItem>();
        if (game != null)
        {
            items.Add(new MenuItem { Label = game.Favorite ? "Remove from favourites" : "Add to favourites", OnAccept = ToggleFavorite });
            items.Add(new MenuItem
            {
                Label = "Run with", Choices = cores.Select(c => c.Name).ToList(),
                Choice = Math.Max(0, cores.FindIndex(c => c.Id == game.CoreOverride)),
                OnChoice = i => SetCore(game, cores[i].Id),
                Hint = "Which emulator core plays this game",
            });
        }
        if (App.Kiosk)
        {
            // Cabinet mode: players only get what changes the list, not the setup.
            items.RemoveAll(i => i.Label == "Run with");
            items.Add(new MenuItem { Label = "Search…", OnAccept = StartSearch });
            items.Add(SortItem(sorts));
            items.Add(ClonesItem());
            _menu = new Menu("Options", items) { Subtitle = game?.Title };
            return;
        }
        items.Add(new MenuItem { Label = "Search…", OnAccept = StartSearch, Hint = $"Shortcut: {App.UiInput.Label(UiAction.Search)}" });
        items.Add(SortItem(sorts));
        items.Add(ClonesItem());
        items.Add(new MenuItem
        {
            Label = "Controls…", OnAccept = () => OpenControls(game),
            Hint = "Keys, pads and arcade sticks for each player, hotkeys" + (game != null ? ", and this game's button layout" : ""),
        });
        items.Add(new MenuItem { Label = "Cabinet setup…", OnAccept = OpenCabinet, Hint = "Screen rotation, vertical games, free play, cabinet mode" });
        items.Add(new MenuItem { Label = "Gameplay…", OnAccept = OpenGameplay, Hint = "Rewind, fast-forward and slow motion" });
        items.Add(new MenuItem
        {
            Label = "Picture…", OnAccept = () => new Video.PictureMenu(App, null, m => _menu = m, OpenOptions).Open(),
            Hint = "Scanlines and CRT effects, size and shape, bezel artwork",
        });
        if (themes.Count > 0)
            items.Add(new MenuItem
            {
                Label = "Theme", Choices = themes.Select(t => t.Name).ToList(), Choice = themeIndex,
                OnChoice = i => App.ApplyTheme(themes[i].Id),
            });
        items.Add(new MenuItem
        {
            Label = "Attract mode", Choices = attractOptions.Select(m => m == 0 ? "Off" : $"after {m} min").ToList(),
            Choice = Math.Max(0, Array.IndexOf(attractOptions, App.Settings.AttractMinutes)),
            OnChoice = i => { App.Settings.AttractMinutes = attractOptions[i]; App.Settings.Save(App.SettingsPath); },
            Hint = "When idle, games play their own demos like in an arcade",
        });
        items.Add(new MenuItem
        {
            Label = "Fullscreen", Choices = ["Off", "On"], Choice = App.Window.IsFullscreen ? 1 : 0,
            OnChoice = i => { App.Window.ToggleFullscreen(); App.Settings.StartFullscreen = i == 1; App.Settings.Save(App.SettingsPath); },
            Hint = "Also remembered for next time · F11 or Alt+Enter",
        });
        items.Add(new MenuItem { Label = "Add ROM folder…", OnAccept = AddFolder });
        items.Add(new MenuItem { Label = "Rescan library", OnAccept = App.StartScan, Enabled = !App.Scanning });
        items.Add(new MenuItem { Label = "Quit", OnAccept = App.Quit });

        _menu = new Menu("Options", items) { Subtitle = game != null ? game.Title : null };
    }

    MenuItem SortItem(SortOrder[] sorts) => new()
    {
        Label = "Sort by", Choices = sorts.Select(SortName).ToList(), Choice = Array.IndexOf(sorts, _model.Sort),
        OnChoice = i => { _model.SetSort(sorts[i]); App.Settings.Sort = sorts[i]; App.Settings.Save(App.SettingsPath); },
    };

    MenuItem ClonesItem() => new()
    {
        Label = "Show clones", Choices = ["Off", "On"], Choice = _model.ShowClones ? 1 : 0,
        OnChoice = i => { _model.SetShowClones(i == 1); App.Settings.ShowClones = i == 1; App.Settings.Save(App.SettingsPath); },
        Hint = "Alternative versions of a game (other regions, revisions, bootlegs)",
    };

    void OpenControls(LibraryGame? game)
    {
        var context = game == null ? null
            : new ControlsGame(game.SetName, game.Title, [], game.Orientation == Orientation.Vertical);
        _controls = new ControlsEditor(App, context);
    }

    /// <summary>Settings for a cabinet: how the screen is mounted, vertical games, free play and cabinet mode.</summary>
    void OpenCabinet()
    {
        var settings = App.Settings;
        void Save() => settings.Save(App.SettingsPath);
        int[] verticalTurns = [0, 1, 3];
        _menu = new Menu("Cabinet setup",
        [
            new MenuItem
            {
                Label = "Screen", Choices = ["Normal", "Turned right", "Upside down", "Turned left"], Choice = App.ScreenTurns,
                OnChoice = i => { settings.ScreenRotation = i; Save(); },
                Hint = "For a monitor mounted on its side: turns everything, menus included",
            },
            new MenuItem
            {
                Label = "Vertical games", Choices = ["Upright", "Turned right", "Turned left"],
                Choice = Math.Max(0, Array.IndexOf(verticalTurns, settings.VerticalGameRotation)),
                OnChoice = i => { settings.VerticalGameRotation = verticalTurns[i]; Save(); },
                Hint = "Turn vertical games to fill the screen (each game can override this)",
            },
            new MenuItem
            {
                Label = "Stick turns with picture", Choices = ["Off", "On"], Choice = settings.RotateControls ? 1 : 0,
                OnChoice = i => { settings.RotateControls = i == 1; Save(); },
                Hint = "On: up on the stick is up on screen. Off: for a monitor you turn by hand",
            },
            new MenuItem
            {
                Label = "Free play", Choices = ["Off", "On"], Choice = settings.FreePlay ? 1 : 0,
                OnChoice = i => { settings.FreePlay = i == 1; Save(); },
                Hint = "Start inserts a coin by itself",
            },
            new MenuItem
            {
                Label = "Cabinet mode", Choices = ["Off", "On"], Choice = settings.Kiosk ? 1 : 0,
                OnChoice = i => { settings.Kiosk = i == 1; Save(); },
                Hint = $"Fullscreen, no settings or Quit for players. Operator: hold {App.UiInput.Label(UiAction.Back)} for {OperatorHoldSeconds:0} s",
            },
            new MenuItem { Label = "Done", OnAccept = OpenOptions },
        ])
        {
            OnCancel = OpenOptions,
        };
    }

    /// <summary>Rewind length and the fast-forward and slow-motion speeds.</summary>
    void OpenGameplay()
    {
        var settings = App.Settings;
        void Save() => settings.Save(App.SettingsPath);
        int[] rewind = [0, 15, 30, 60, 120, 300];
        double[] fast = [2, 3, 4, 6, 8];
        double[] slow = [0.5, 0.25];
        string Key(Hotkey hotkey) => App.Controls.HotkeyBindings(hotkey).FirstOrDefault() is var b && b != default ? b.Label : "its hotkey";
        _menu = new Menu("Gameplay",
        [
            new MenuItem
            {
                Label = "Rewind", Choices = rewind.Select(s => s == 0 ? "Off" : s < 60 ? $"{s} seconds" : $"{s / 60} min").ToList(),
                Choice = Math.Max(0, Array.IndexOf(rewind, settings.RewindSeconds)),
                OnChoice = i => { settings.RewindSeconds = rewind[i]; Save(); },
                Hint = $"Hold {Key(Hotkey.Rewind)} to run the game backwards · from the next game you start",
            },
            new MenuItem
            {
                Label = "Fast-forward", Choices = fast.Select(s => $"{s:0}× speed").ToList(),
                Choice = Math.Max(0, Array.IndexOf(fast, settings.FastForwardSpeed)),
                OnChoice = i => { settings.FastForwardSpeed = fast[i]; Save(); },
                Hint = $"Hold {Key(Hotkey.FastForward)} to speed the game up",
            },
            new MenuItem
            {
                Label = "Slow motion", Choices = ["½ speed", "¼ speed"],
                Choice = Math.Max(0, Array.IndexOf(slow, settings.SlowMotionSpeed)),
                OnChoice = i => { settings.SlowMotionSpeed = slow[i]; Save(); },
                Hint = $"{Key(Hotkey.SlowMotion)} turns slow motion on and off",
            },
            new MenuItem { Label = "Done", OnAccept = OpenOptions },
        ])
        {
            OnCancel = OpenOptions,
        };
    }

    static string SortName(SortOrder sort) => sort switch
    {
        SortOrder.Title => "Title",
        SortOrder.Year => "Year",
        SortOrder.Manufacturer => "Manufacturer",
        SortOrder.MostPlayed => "Most played",
        SortOrder.RecentlyPlayed => "Last played",
        _ => sort.ToString(),
    };

    void SetCore(LibraryGame game, string? coreId)
    {
        App.Library.SetCoreOverride(game.SetName, coreId);
        App.ShowMessage(coreId == null ? $"{game.Title}: core chosen automatically" : $"{game.Title}: runs with {coreId}");
        App.StartScan(); // re-checks the game against the chosen core
    }

    void AddFolder()
    {
        App.PickFolder(folder =>
        {
            App.Library.AddFolder(folder);
            App.ShowMessage($"Added {folder}");
            App.StartScan();
        });
    }

    // ---- Drawing ----

    public override void Draw(float dt)
    {
        var (w, h) = App.ScreenSize;
        var r = App.Ui;
        var t = App.Theme;
        r.Begin(w, h);

        DrawBackground(r, t, w, h);
        var margin = r.S(64);
        var header = new RectF(margin, r.S(36), w - 2 * margin, r.S(96));
        var footer = new RectF(margin, h - r.S(84), w - 2 * margin, r.S(60));
        DrawHeader(r, t, header, dt);

        var body = new RectF(margin, header.Bottom + r.S(28), w - 2 * margin, footer.Y - header.Bottom - r.S(48));
        if (_model.All.Count == 0)
        {
            DrawWelcome(r, t, body);
        }
        else
        {
            var gap = r.S(48);
            RectF list, preview;
            if (h > w * 1.1f)
            {
                // Portrait (a monitor on its side): preview on top, list below.
                var previewH = body.H * 0.5f;
                preview = new RectF(body.X, body.Y, body.W, previewH);
                list = new RectF(body.X, preview.Bottom + gap, body.W, body.H - previewH - gap);
            }
            else
            {
                var listW = body.W * 0.44f;
                var listOnLeft = !t.File.ListSide.Equals("right", StringComparison.OrdinalIgnoreCase);
                list = new RectF(listOnLeft ? body.X : body.Right - listW, body.Y, listW, body.H);
                preview = new RectF(listOnLeft ? list.Right + gap : body.X, body.Y, body.W - listW - gap, body.H);
            }
            DrawList(r, t, list, dt);
            DrawPreview(r, t, preview);
        }
        DrawFooter(r, t, footer);

        if (_launchPending != null)
            DrawLoading(r, t, w, h);
        if (_controls != null)
            _controls.Draw(r, t, App.UiInput, dt);
        else
            _menu?.Draw(r, t, App.UiInput, dt);
        if (App.Kiosk && App.UiInput.HeldFor(UiAction.Back) is var held and > 1f && _menu == null && _controls == null)
        {
            var progress = Math.Clamp((held - 1f) / (OperatorHoldSeconds - 1f), 0, 1);
            var bar = new RectF(w / 2f - r.S(200), r.S(24), r.S(400), r.S(10));
            r.Fill(bar, t.PanelBorder, r.S(5));
            r.Fill(bar with { W = bar.W * progress }, t.Warning, r.S(5));
        }
        r.End();
    }

    void DrawBackground(UiRenderer r, Theme t, int w, int h)
    {
        r.FillGradient(new RectF(0, 0, w, h), t.Background2, t.Background);
        if (t.BackgroundImage != null && App.Images.Get(t.BackgroundImage, out var fade) is { } bg)
        {
            var cover = bg.Aspect > (float)w / h
                ? new RectF((w - h * bg.Aspect) / 2, 0, h * bg.Aspect, h)
                : new RectF(0, (h - w / bg.Aspect) / 2, w, w / bg.Aspect);
            r.Image(bg, cover, fade);
            r.FillGradient(new RectF(0, 0, w, h), t.Background.WithAlpha(0.35f), t.Background.WithAlpha(0.85f));
        }
        // A soft glow behind the header in the accent colour.
        r.FillGradient(new RectF(0, 0, w, r.S(260)), t.Accent.WithAlpha(0.10f), t.Accent.WithAlpha(0));
        if (t.File.Scanlines)
        {
            var step = Math.Max(2f, r.S(4));
            for (var y = 0f; y < h; y += step)
                r.Fill(new RectF(0, y, w, Math.Max(1, step / 2)), Rgba.Black.WithAlpha(0.18f));
        }
    }

    void DrawHeader(UiRenderer r, Theme t, RectF area, float dt)
    {
        var logoFont = t.Title(r, 40);
        var logo = t.Heading("Arcade");
        var logoY = area.Y + (area.H - logoFont.LineHeight) / 2;
        r.Text(logoFont, logo, area.X + r.S(3), logoY + r.S(3), t.Accent2.WithAlpha(0.6f));
        r.Text(logoFont, logo, area.X, logoY, t.Accent);
        var x = area.X + UiRenderer.Measure(logoFont, logo).X + r.S(56);

        // Right side: game count and clock.
        var small = t.Body(r, 26);
        var clock = DateTime.Now.ToString("HH:mm");
        r.TextRight(t.Body(r, 34), clock, area.Right, area.Y + (area.H - t.Body(r, 34).LineHeight) / 2, t.Text);
        var countText = App.Scanning ? (App.ScanStatus ?? "Scanning…") : $"{_model.Visible.Count} of {_model.PlayableCount} games";
        var countRight = area.Right - UiRenderer.Measure(t.Body(r, 34), clock).X - r.S(40);
        r.TextRight(small, countText, countRight, area.Y + (area.H - small.LineHeight) / 2, App.Scanning ? t.Accent2 : t.TextDim);
        var tabsRight = countRight - UiRenderer.Measure(small, countText).X - r.S(40);

        if (_searching || _model.Search.Length > 0)
        {
            var box = new RectF(x, area.Y + r.S(14), Math.Min(r.S(760), tabsRight - x), area.H - r.S(28));
            r.Fill(box, t.Panel, box.H / 2);
            r.Outline(box, _searching ? t.Accent : t.PanelBorder, r.S(2), box.H / 2);
            var font = t.Body(r, 30);
            var caret = _searching && (_time % 1f) < 0.55f ? "▌" : "";
            var text = (_searching ? _search : _model.Search) + caret;
            r.Text(font, "Search:", box.X + r.S(28), box.Y + (box.H - font.LineHeight) / 2, t.TextDim);
            r.Text(font, text, box.X + r.S(150), box.Y + (box.H - font.LineHeight) / 2, t.Text, box.W - r.S(180));
            return;
        }

        // Tabs scroll sideways when they don't all fit, keeping the active one in view.
        var tabFont = t.Body(r, 28);
        var widths = GameListModel.Tabs.Select(tab => UiRenderer.Measure(tabFont, GameListModel.TabName(tab)).X + r.S(44)).ToArray();
        var gap = r.S(10);
        var available = tabsRight - x;
        var activeIndex = Array.IndexOf(GameListModel.Tabs, _model.Tab);
        var activeStart = widths.Take(activeIndex).Sum() + gap * activeIndex;
        var total = widths.Sum() + gap * (widths.Length - 1);
        var targetOffset = total <= available ? 0 : Math.Clamp(activeStart + widths[activeIndex] / 2 - available / 2, 0, total - available);
        _tabOffset = Easing.Approach(_tabOffset, targetOffset, 14, dt);

        r.PushClip(new RectF(x, area.Y, available, area.H));
        var tx = x - _tabOffset;
        for (var i = 0; i < GameListModel.Tabs.Length; i++)
        {
            var tab = GameListModel.Tabs[i];
            var pill = new RectF(tx, area.Y + (area.H - r.S(56)) / 2, widths[i], r.S(56));
            var active = tab == _model.Tab;
            if (active)
                r.Fill(pill, t.Accent, pill.H / 2);
            r.Text(tabFont, GameListModel.TabName(tab), pill.X + r.S(22), pill.Y + (pill.H - tabFont.LineHeight) / 2, active ? t.SelectionText : t.TextDim);
            tx = pill.Right + gap;
        }
        r.PopClip();
        // Fade the clipped edges so it's clear there are more tabs.
        var fadeW = r.S(40);
        if (_tabOffset > 1)
            r.Fill(new RectF(x, area.Y, fadeW, area.H), t.Background2.WithAlpha(0.6f));
        if (total - _tabOffset > available + 1)
            r.Fill(new RectF(tabsRight - fadeW, area.Y, fadeW, area.H), t.Background2.WithAlpha(0.6f));
    }

    void DrawList(UiRenderer r, Theme t, RectF area, float dt)
    {
        var radius = t.Radius(r);
        r.Fill(area, t.Panel, radius);
        r.Outline(area, t.PanelBorder, r.S(1.5f), radius);

        var inner = area.Inset(r.S(14));
        _listArea = inner;
        _rowHeight = r.S(64);
        var games = _model.Visible;
        if (games.Count == 0)
        {
            var message = _model.Search.Length > 0 ? $"No games match \"{_model.Search}\"" : _model.Tab switch
            {
                BrowserTab.Favorites => $"No favourites yet. Press {App.UiInput.Label(UiAction.Favorite)} on a game to add it.",
                BrowserTab.Recent => "Games you play will appear here.",
                BrowserTab.Attention => "Every game in your library is ready to play.",
                _ => "No games in this category.",
            };
            var font = t.Body(r, 28);
            var lines = UiRenderer.Wrap(font, message, inner.W - r.S(60));
            var y = inner.CenterY - lines.Count * font.LineHeight / 2;
            foreach (var line in lines)
            {
                r.TextCentered(font, line, inner.CenterX, y, t.TextDim);
                y += font.LineHeight;
            }
            return;
        }

        var visibleRows = inner.H / _rowHeight;
        var target = Math.Clamp(_model.Selected - visibleRows / 2 + 0.5f, 0, Math.Max(0, games.Count - visibleRows));
        _scroll = _scroll < 0 ? target : Easing.Approach(_scroll, target, 18, dt);

        r.PushClip(inner);
        var first = Math.Max(0, (int)_scroll);
        var last = Math.Min(games.Count - 1, (int)(_scroll + visibleRows) + 1);
        var titleFont = t.Body(r, 30);
        var smallFont = t.Body(r, 24);
        for (var i = first; i <= last; i++)
        {
            var g = games[i];
            var row = new RectF(inner.X, inner.Y + (i - _scroll) * _rowHeight, inner.W, _rowHeight - r.S(4));
            var selected = i == _model.Selected;
            if (selected)
            {
                var glow = row.Inset(-r.S(2) * _selectionPulse);
                r.Fill(glow, t.Selection, r.S(10));
                r.Outline(glow, t.Accent2.WithAlpha(0.35f + 0.65f * _selectionPulse), r.S(2), r.S(10));
            }
            else if (i % 2 == 1)
            {
                r.Fill(row, Rgba.White.WithAlpha(0.02f), r.S(10));
            }

            var textColor = selected ? t.SelectionText : g.Status == GameStatus.Playable ? t.Text : t.TextDim;
            var ty = row.Y + (row.H - titleFont.LineHeight) / 2;
            var x = row.X + r.S(20);
            if (g.Favorite)
            {
                r.Text(titleFont, "★", x, ty, selected ? t.SelectionText : t.Warning);
                x += r.S(38);
            }

            var right = row.Right - r.S(20);
            var tag = g.Status != GameStatus.Playable ? LibraryCommands.Label(g.Status)
                : _model.Sort == SortOrder.Manufacturer ? g.Manufacturer ?? ""
                : _model.Sort == SortOrder.MostPlayed ? (g.PlayCount > 0 ? $"{g.PlayCount}×" : "")
                : g.Year ?? "";
            var tagColor = g.Status != GameStatus.Playable ? t.Error : selected ? t.SelectionText.WithAlpha(0.85f) : t.TextDim;
            var tagW = Math.Min(UiRenderer.Measure(smallFont, tag).X, row.W * 0.35f);
            r.Text(smallFont, tag, right - tagW, row.Y + (row.H - smallFont.LineHeight) / 2, tagColor, row.W * 0.35f);
            if (g.Warnings.Count > 0 && g.Status == GameStatus.Playable)
            {
                right -= tagW + r.S(36);
                r.Text(smallFont, "⚠", right, row.Y + (row.H - smallFont.LineHeight) / 2, selected ? t.SelectionText : t.Warning);
            }
            r.Text(titleFont, g.Title, x, ty, textColor, right - tagW - r.S(24) - x);
        }
        r.PopClip();

        // Scroll bar.
        if (games.Count > visibleRows)
        {
            var trackH = inner.H;
            var thumbH = Math.Max(r.S(40), trackH * visibleRows / games.Count);
            var thumbY = inner.Y + (trackH - thumbH) * (_scroll / Math.Max(1, games.Count - visibleRows));
            r.Fill(new RectF(area.Right - r.S(8), thumbY, r.S(4), thumbH), t.TextDim.WithAlpha(0.5f), r.S(2));
        }
    }

    void DrawPreview(UiRenderer r, Theme t, RectF area)
    {
        if (_model.Current is not { } g)
            return;
        var radius = t.Radius(r);

        // Artwork box: the game's picture at its screen shape, or a placeholder.
        var artBox = new RectF(area.X, area.Y, area.W, area.H * 0.52f);
        var path = App.FindPreview(g);
        var texture = path == null ? null : App.Images.Get(path, out var fade) is { } tex ? (tex, fade) : default((Texture, float)?);
        // Snaps are raw game pixels; show them at the screen's real shape (4:3, or 3:4 for vertical games).
        var aspect = texture?.Item1 is { } loaded && path != null && path.Contains("snap", StringComparison.OrdinalIgnoreCase)
            ? (g.Orientation == Orientation.Vertical ? 3f / 4 : 4f / 3)
            : texture?.Item1.Aspect ?? (g.Orientation == Orientation.Vertical ? 3f / 4 : 4f / 3);
        var art = artBox.Fit(aspect);
        art = art with { X = area.X }; // line up with the text below
        r.Shadow(art, radius, r.S(30), 0.55f);
        r.Fill(art, Rgba.Black, radius);
        if (texture is var (img, alpha))
        {
            r.Image(img, art, alpha, radius);
        }
        else
        {
            r.FillGradient(art, Rgba.Lerp(t.Background2, t.Accent, 0.25f), t.Background, radius);
            var font = t.Title(r, 26);
            r.TextCentered(font, t.Heading(path == null ? "No preview yet" : "Loading…"), art.CenterX, art.CenterY - font.LineHeight, t.Text.WithAlpha(0.8f));
            if (path == null)
            {
                var hint = t.Body(r, 22);
                foreach (var (line, i) in UiRenderer.Wrap(hint, "A picture is saved the first time you play. Artwork packs go in the artwork folder.", art.W - r.S(80)).Select((l, i) => (l, i)))
                    r.TextCentered(hint, line, art.CenterX, art.CenterY + r.S(10) + i * hint.LineHeight, t.TextDim);
            }
        }
        r.Outline(art, t.PanelBorder, r.S(2), radius);

        // Title and details.
        var y = artBox.Bottom + r.S(36);
        var titleFont = t.Title(r, 30);
        foreach (var line in UiRenderer.Wrap(titleFont, t.Heading(g.Title), area.W).Take(2))
        {
            r.Text(titleFont, line, area.X, y, t.Text, area.W);
            y += titleFont.LineHeight * 1.45f;
        }
        y += r.S(10);

        var labelFont = t.Body(r, 26);
        var labelW = r.S(190);
        void Row(string label, string? value, Rgba? color = null)
        {
            if (string.IsNullOrWhiteSpace(value) || y > area.Bottom - labelFont.LineHeight) return;
            r.Text(labelFont, label, area.X, y, t.TextDim);
            r.Text(labelFont, value, area.X + labelW, y, color ?? t.Text, area.W - labelW);
            y += labelFont.LineHeight * 1.15f;
        }

        Row("Year", string.Join(" · ", new[] { g.Year, g.Manufacturer }.Where(s => !string.IsNullOrEmpty(s))));
        Row("Genre", g.Genre);
        var players = g.Players is { } p ? $"{p} player{(p == 1 ? "" : "s")}" : null;
        Row("Players", string.Join(" · ", new[] { players, g.Control, g.Orientation == Orientation.Vertical ? "vertical screen" : null }.Where(s => s != null)));
        Row("Runs on", CoreName(g.CoreOverride ?? g.CoreId) + (g.CoreOverride != null ? " (your choice)" : ""));
        Row("Played", g.PlayCount == 0 ? "never" : $"{g.PlayCount} time{(g.PlayCount == 1 ? "" : "s")}" + (g.PlayTime >= TimeSpan.FromMinutes(1) ? $" · {Duration(g.PlayTime)}" : "") + (g.LastPlayed is { } lp ? $", last {Ago(lp)}" : ""));
        Row("Set", g.SetName + (g.Parent != null ? $" (version of {g.Parent})" : ""), t.TextDim);
        void Paragraph(string label, string text, Rgba color)
        {
            // Paths are shown relative to the app folder to keep notes short.
            text = text.Replace(App.Paths.Root + Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase);
            foreach (var (line, i) in UiRenderer.Wrap(labelFont, text, area.W - labelW).Select((l, i) => (l, i)))
            {
                if (y > area.Bottom - labelFont.LineHeight) return;
                if (i == 0) r.Text(labelFont, label, area.X, y, t.TextDim);
                r.Text(labelFont, line, area.X + labelW, y, color, area.W - labelW);
                y += labelFont.LineHeight * 1.15f;
            }
        }

        if (g.Problem != null)
            Paragraph("Problem", g.Problem, t.Error);
        foreach (var warning in g.Warnings)
            Paragraph("Note", warning, t.Warning);
    }

    string CoreName(string? id) => CoreCatalog.Find(id ?? "")?.DisplayName ?? id ?? "—";

    static string Ago(DateTime when)
    {
        var span = DateTime.Now - when.ToLocalTime();
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
            : span.TotalDays < 2 ? "yesterday"
            : $"{(int)span.TotalDays} days ago";
    }

    /// <summary>Time played, e.g. "25 min" or "3 h 10 min".</summary>
    public static string Duration(TimeSpan time) =>
        time.TotalHours < 1 ? $"{(int)time.TotalMinutes} min" : $"{(int)time.TotalHours} h {time.Minutes:00} min";

    void DrawWelcome(UiRenderer r, Theme t, RectF area)
    {
        var box = new RectF(area.CenterX - r.S(560), area.CenterY - r.S(220), r.S(1120), r.S(440));
        var radius = t.Radius(r) * 1.5f;
        r.Shadow(box, radius, r.S(40));
        r.Fill(box, t.Panel, radius);
        r.Outline(box, t.PanelBorder, r.S(2), radius);
        var title = t.Title(r, 36);
        r.TextCentered(title, t.Heading(App.Scanning ? "Scanning your games…" : "Welcome!"), box.CenterX, box.Y + r.S(60), t.Accent);
        var font = t.Body(r, 30);
        var text = App.Scanning
            ? App.ScanStatus ?? ""
            : App.Library.Folders.Count == 0
                ? $"Your library is empty. Press {App.UiInput.Label(UiAction.Accept)} to choose the folder with your ROM zips."
                : "No ROM zips were found in your library folders. Add zips there, or another folder, then rescan.";
        var y = box.Y + r.S(160);
        foreach (var line in UiRenderer.Wrap(font, text, box.W - r.S(140)))
        {
            r.TextCentered(font, line, box.CenterX, y, t.Text);
            y += font.LineHeight * 1.2f;
        }
        if (!App.Scanning)
        {
            var hint = t.Body(r, 24);
            r.TextCentered(hint, $"Folders: {(App.Library.Folders.Count == 0 ? "none yet" : string.Join(", ", App.Library.Folders))}", box.CenterX, box.Bottom - r.S(90), t.TextDim);
            r.TextCentered(hint, $"{App.UiInput.Label(UiAction.Options)} for options", box.CenterX, box.Bottom - r.S(56), t.TextDim);
        }
        else
        {
            // Spinner.
            for (var i = 0; i < 8; i++)
            {
                var angle = _time * 4 + i * MathF.PI / 4;
                var c = new RectF(box.CenterX + MathF.Cos(angle) * r.S(30) - r.S(6), box.Bottom - r.S(90) + MathF.Sin(angle) * r.S(30) - r.S(6), r.S(12), r.S(12));
                r.Fill(c, t.Accent.WithAlpha((i + 1) / 8f), r.S(6));
            }
        }
    }

    void DrawFooter(UiRenderer r, Theme t, RectF area)
    {
        var input = App.UiInput;
        (UiAction Action, string Label)[] hints = _searching
            ? [(UiAction.Accept, "Done"), (UiAction.Back, "Clear")]
            :
            [
                (UiAction.Accept, "Play"),
                (UiAction.Favorite, "Favourite"),
                (UiAction.Options, "Options"),
                (UiAction.Search, "Search"),
                (UiAction.PrevTab, "◂ Category ▸"),
                (UiAction.Back, _model.Search.Length > 0 ? "Clear search" : "Quit"),
            ];
        var font = t.Body(r, 24);
        var x = area.X;
        foreach (var (action, label) in hints)
        {
            if (action == UiAction.Search && input.LastDevice == InputDevice.Gamepad)
                continue;
            if (action == UiAction.Back && App.Kiosk && _model.Search.Length == 0 && !_searching)
                continue; // cabinet mode: there is no Quit
            var key = action == UiAction.PrevTab ? $"{input.Label(UiAction.PrevTab)}/{input.Label(UiAction.NextTab)}" : input.Label(action);
            var keySize = UiRenderer.Measure(font, key);
            var chip = new RectF(x, area.Y + (area.H - r.S(40)) / 2, keySize.X + r.S(24), r.S(40));
            r.Fill(chip, t.Panel, r.S(8));
            r.Outline(chip, t.PanelBorder, r.S(1.5f), r.S(8));
            r.Text(font, key, chip.X + r.S(12), chip.Y + (chip.H - font.LineHeight) / 2, t.Text);
            r.Text(font, label, chip.Right + r.S(12), chip.Y + (chip.H - font.LineHeight) / 2, t.TextDim);
            x = chip.Right + r.S(12) + UiRenderer.Measure(font, label).X + r.S(36);
        }
        var letters = _model.Sort == SortOrder.Title ? "◂ ▸ jump letter" : "◂ ▸ jump 10";
        r.TextRight(font, letters, area.Right, area.Y + (area.H - font.LineHeight) / 2, t.TextDim.WithAlpha(0.7f));
    }

    void DrawLoading(UiRenderer r, Theme t, int w, int h)
    {
        r.Fill(new RectF(0, 0, w, h), t.Overlay);
        var font = t.Title(r, 36);
        var title = _model.Visible.FirstOrDefault(g => g.SetName == _launchPending)?.Title ?? "";
        r.TextCentered(font, t.Heading("Loading"), w / 2f, h / 2f - r.S(60), t.Accent);
        r.TextCentered(t.Body(r, 32), title, w / 2f, h / 2f + r.S(10), t.Text);
    }
}
