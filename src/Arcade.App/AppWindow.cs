using Silk.NET.OpenGL;
using SDL;
using static SDL.SDL3;

namespace Arcade.App;

/// <summary>
/// An SDL window with an OpenGL 3.3 core context. In offscreen mode the window stays hidden and
/// everything renders into a framebuffer of a fixed size, which automated checks can screenshot.
/// </summary>
sealed unsafe class AppWindow : IDisposable
{
    readonly SDL_Window* _window;
    readonly SDL_GLContextState* _context;
    readonly uint _fbo, _colorBuffer;
    readonly (int W, int H) _offscreenSize;

    public GL Gl { get; }
    public bool VsyncEnabled { get; }
    public bool Offscreen { get; }

    /// <param name="aspect">Aspect ratio for the initial window size (16:9 for the game list, the game's for a direct launch).</param>
    /// <param name="offscreenSize">If set, render hidden into a framebuffer of this size instead of showing a window.</param>
    public AppWindow(string title, double aspect, bool fullscreen, (int W, int H)? offscreenSize = null)
    {
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_MAJOR_VERSION, 3);
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_MINOR_VERSION, 3);
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_PROFILE_MASK, (int)SDL_GLProfile.SDL_GL_CONTEXT_PROFILE_CORE);
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_DOUBLEBUFFER, 1);

        Offscreen = offscreenSize != null;
        var (width, height) = offscreenSize ?? InitialSize(aspect);
        var flags = SDL_WindowFlags.SDL_WINDOW_OPENGL | SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY;
        if (Offscreen)
            flags |= SDL_WindowFlags.SDL_WINDOW_HIDDEN;
        else if (fullscreen)
            flags |= SDL_WindowFlags.SDL_WINDOW_FULLSCREEN;

        _window = SDL_CreateWindow(title, width, height, flags);
        if (_window == null)
            throw new InvalidOperationException("Could not create window: " + SDL_GetError());
        SDL_SetWindowMinimumSize(_window, 480, 270);

        _context = SDL_GL_CreateContext(_window);
        if (_context == null)
            throw new InvalidOperationException("Could not create an OpenGL 3.3 context: " + SDL_GetError());
        SDL_GL_MakeCurrent(_window, _context);
        VsyncEnabled = !Offscreen && SDL_GL_SetSwapInterval(1);

        Gl = GL.GetApi(name => SDL_GL_GetProcAddress(name));
        if (fullscreen && !Offscreen)
            SDL_HideCursor();

        if (Offscreen)
        {
            _offscreenSize = (width, height);
            _fbo = Gl.GenFramebuffer();
            _colorBuffer = Gl.GenRenderbuffer();
            Gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _colorBuffer);
            Gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, (uint)width, (uint)height);
            Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            Gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _colorBuffer);
        }
    }

    /// <summary>A window that fits ~85% of the usable screen height with the given aspect ratio.</summary>
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

    public SDL_Window* Handle => _window;

    public (int Width, int Height) PixelSize
    {
        get
        {
            if (Offscreen)
                return _offscreenSize;
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
            if (Offscreen)
                return 60;
            var mode = SDL_GetCurrentDisplayMode(SDL_GetDisplayForWindow(_window));
            return mode == null ? 0 : mode->refresh_rate;
        }
    }

    public bool IsFullscreen => (SDL_GetWindowFlags(_window) & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0;

    public void ToggleFullscreen()
    {
        if (Offscreen)
            return;
        var goFullscreen = !IsFullscreen;
        SDL_SetWindowFullscreen(_window, goFullscreen);
        if (goFullscreen) SDL_HideCursor(); else SDL_ShowCursor();
    }

    public string Title
    {
        set => SDL_SetWindowTitle(_window, value);
    }

    public void StartTextInput() => SDL_StartTextInput(_window);
    public void StopTextInput() => SDL_StopTextInput(_window);

    /// <summary>Call before drawing each frame: binds the offscreen framebuffer when there is one.</summary>
    public void BeginFrame() => Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

    public void Swap()
    {
        if (Offscreen)
            Gl.Finish();
        else
            SDL_GL_SwapWindow(_window);
    }

    /// <summary>Reads the frame just drawn as top-down RGBA.</summary>
    public Libretro.Rgba32Image ReadPixels()
    {
        var (w, h) = PixelSize;
        var pixels = new byte[w * h * 4];
        fixed (byte* p = pixels)
            Gl.ReadPixels(0, 0, (uint)w, (uint)h, Silk.NET.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, p);
        // OpenGL rows run bottom-up; images run top-down. Alpha is forced opaque.
        var flipped = new byte[pixels.Length];
        for (var y = 0; y < h; y++)
            pixels.AsSpan((h - 1 - y) * w * 4, w * 4).CopyTo(flipped.AsSpan(y * w * 4));
        for (var i = 3; i < flipped.Length; i += 4)
            flipped[i] = 255;
        return new Libretro.Rgba32Image(w, h, flipped);
    }

    public void Dispose()
    {
        if (Offscreen)
        {
            Gl.DeleteFramebuffer(_fbo);
            Gl.DeleteRenderbuffer(_colorBuffer);
        }
        Gl.Dispose();
        SDL_GL_DestroyContext(_context);
        SDL_DestroyWindow(_window);
    }
}
