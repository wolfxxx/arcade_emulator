using SDL;

namespace Arcade.App;

/// <summary>A full-window screen of the app: the game list, a running game, or attract mode.</summary>
abstract class Scene(ArcadeApp app)
{
    protected ArcadeApp App { get; } = app;

    public virtual void Enter() { }
    public virtual void Leave() { }

    /// <param name="elapsed">Real seconds since the previous update (games pace their frames with this).</param>
    public abstract void Update(float dt, double elapsed);
    public abstract void Draw(float dt);

    /// <summary>Raw key presses, before menu actions; return true to swallow the key.</summary>
    public virtual bool OnKey(SDL_KeyboardEvent key) => false;
    public virtual void OnTextInput(string text) { }
    public virtual void OnMouseButton(float x, float y, int clicks) { }
    public virtual void OnMouseWheel(float delta) { }

    /// <summary>While true, typing goes to a text field and letter keys don't act as menu buttons.</summary>
    public virtual bool CapturesText => false;

    /// <summary>True when the loop should sleep between frames instead of relying on vsync to pace it.</summary>
    public virtual bool ClockPaced => false;
}
