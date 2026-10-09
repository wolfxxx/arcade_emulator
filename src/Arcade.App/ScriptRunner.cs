using Arcade.App.Ui;
using SDL;
using static SDL.SDL3;

namespace Arcade.App;

/// <summary>
/// Drives the app from a script for automated checks, e.g.
/// <c>--script "wait 2; down; down; shot list.png; accept; wait 3; hotkey menu; shot pause.png; quit"</c>.
/// Commands: wait &lt;seconds&gt;, any menu action (up, accept, options…), key &lt;name&gt; (a raw key
/// press such as f11), hotkey &lt;name&gt; (menu, savestate… as if its key were pressed in a game),
/// type &lt;text&gt;, shot &lt;file.png&gt;, quit.
/// </summary>
sealed unsafe class ScriptRunner(ArcadeApp app, string script)
{
    readonly Queue<string> _steps = new(script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    float _wait = 0.5f; // let the first frames settle

    public void Update(float dt)
    {
        _wait -= dt;
        while (_wait <= 0 && _steps.TryDequeue(out var step))
        {
            var (command, arg) = step.IndexOf(' ') is var i and > 0 ? (step[..i].ToLowerInvariant(), step[(i + 1)..].Trim()) : (step.ToLowerInvariant(), "");
            switch (command)
            {
                case "wait":
                    _wait = float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "shot":
                    app.SaveWindowScreenshot(Path.GetFullPath(arg));
                    _wait = 0.05f;
                    return;
                case "attract":
                    GameScene.NextAttractGame(app, null, null);
                    _wait = 0.5f;
                    return;
                case "quit":
                    app.Quit();
                    return;
                case "hotkey":
                    app.GameInput.Mapper.Inject(Enum.Parse<Controls.Hotkey>(arg, ignoreCase: true));
                    _wait = 0.1f;
                    return;
                case "key":
                    PushKey(arg);
                    _wait = 0.1f;
                    return;
                case "type":
                    foreach (var c in arg)
                        app.OnScriptText(c.ToString());
                    break;
                default:
                    if (!Enum.TryParse<UiAction>(command, ignoreCase: true, out var action))
                        throw new ArgumentException($"Unknown script command '{step}'.");
                    app.UiInput.Inject(action);
                    _wait = 0.12f; // one action per frame or so, like a person pressing
                    return;
            }
        }
    }

    static void PushKey(string name)
    {
        var scancode = SDL_GetScancodeFromName(name);
        if (scancode == SDL_Scancode.SDL_SCANCODE_UNKNOWN)
            throw new ArgumentException($"Unknown key '{name}'.");
        var e = new SDL_Event();
        e.key.type = SDL_EventType.SDL_EVENT_KEY_DOWN;
        e.key.scancode = scancode;
        e.key.down = true;
        SDL_PushEvent(&e);
    }
}
