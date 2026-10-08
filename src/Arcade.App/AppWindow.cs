using SDL;
using Silk.NET.OpenGL;
using static SDL.SDL3;

namespace Arcade.App;

/// <summary>An SDL window with an OpenGL 3.3 core context.</summary>
sealed unsafe class AppWindow : IDisposable
{
    readonly SDL_Window* _window;
    readonly SDL_GLContextState* _context;

    public GL Gl { get; }
    public bool VsyncEnabled { get; }

    public AppWindow(string title, double aspect, bool fullscreen)
    {
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_MAJOR_VERSION, 3);
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_MINOR_VERSION, 3);
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_PROFILE_MASK, (int)SDL_GLProfile.SDL_GL_CONTEXT_PROFILE_CORE);
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_DOUBLEBUFFER, 1);

        var (width, height) = InitialSize(aspect);
        var flags = SDL_WindowFlags.SDL_WINDOW_OPENGL | SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY;
        if (fullscreen)
            flags |= SDL_WindowFlags.SDL_WINDOW_FULLSCREEN;

        _window = SDL_CreateWindow(title, width, height, flags);
        if (_window == null)
            throw new InvalidOperationException("Could not create window: " + SDL_GetError());
        SDL_SetWindowMinimumSize(_window, 160, 120);

        _context = SDL_GL_CreateContext(_window);
        if (_context == null)
            throw new InvalidOperationException("Could not create an OpenGL 3.3 context: " + SDL_GetError());
        SDL_GL_MakeCurrent(_window, _context);
        VsyncEnabled = SDL_GL_SetSwapInterval(1);

        Gl = GL.GetApi(name => SDL_GL_GetProcAddress(name));
        if (fullscreen)
            SDL_HideCursor();
    }

    /// <summary>A window that fits ~85% of the usable screen height with the game's aspect ratio.</summary>
    static (int Width, int Height) InitialSize(double aspect)
    {
        SDL_Rect usable;
        if (!SDL_GetDisplayUsableBounds(SDL_GetPrimaryDisplay(), &usable))
            usable = new SDL_Rect { w = 1280, h = 720 };
        var height = (int)(usable.h * 0.85);
        var width = (int)Math.Round(height * aspect);
        if (width > usable.w * 0.9)
        {
            width = (int)(usable.w * 0.9);
            height = (int)Math.Round(width / aspect);
        }
        return (width, height);
    }

    public (int Width, int Height) PixelSize
    {
        get
        {
            int w, h;
            SDL_GetWindowSizeInPixels(_window, &w, &h);
            return (w, h);
        }
    }

    /// <summary>Refresh rate of the monitor the window is on, or 0 if unknown.</summary>
    public double RefreshRate
    {
        get
        {
            var mode = SDL_GetCurrentDisplayMode(SDL_GetDisplayForWindow(_window));
            return mode == null ? 0 : mode->refresh_rate;
        }
    }

    public bool IsFullscreen => (SDL_GetWindowFlags(_window) & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0;

    public void ToggleFullscreen()
    {
        var goFullscreen = !IsFullscreen;
        SDL_SetWindowFullscreen(_window, goFullscreen);
        if (goFullscreen) SDL_HideCursor(); else SDL_ShowCursor();
    }

    public string Title
    {
        set => SDL_SetWindowTitle(_window, value);
    }

    public void Swap() => SDL_GL_SwapWindow(_window);

    public void Dispose()
    {
        Gl.Dispose();
        SDL_GL_DestroyContext(_context);
        SDL_DestroyWindow(_window);
    }
}
