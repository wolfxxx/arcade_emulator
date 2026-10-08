using System.Runtime.InteropServices;
using Arcade.Libretro;

namespace Arcade.Tests;

public unsafe class CFormatTests
{
    static string Format(string fmt, params nint[] args)
    {
        var p = Marshal.StringToCoTaskMemUTF8(fmt);
        try { return CFormat.Format((byte*)p, args); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    [Fact]
    public void Formats_integers_strings_and_padding()
    {
        var s = Marshal.StringToCoTaskMemUTF8("neogeo");
        try
        {
            Assert.Equal("set neogeo has 42 roms, 0x1f, [   7], 007\n".TrimEnd('\n'),
                Format("set %s has %d roms, 0x%x, [%4u], %03d\n", s, 42, 0x1f, 7, 7));
        }
        finally { Marshal.FreeCoTaskMem(s); }
    }

    [Fact]
    public void Formats_doubles_passed_as_bit_patterns() =>
        Assert.Equal("Timing set to 59.185 Hz", Format("Timing set to %.3f Hz", (nint)BitConverter.DoubleToInt64Bits(59.185)));

    [Fact]
    public void Treats_plain_int_as_32_bit_and_ll_as_64_bit()
    {
        Assert.Equal("-1", Format("%d", unchecked((nint)0x7FFFFFFF_FFFFFFFF)));
        Assert.Equal("4294967296", Format("%llu", unchecked((nint)0x1_0000_0000)));
    }

    [Fact]
    public void Handles_percent_literal_and_null_string() =>
        Assert.Equal("100% (null)", Format("100%% %s", 0));
}

public class CoreOptionTests
{
    [Fact]
    public void Parses_legacy_declaration_with_default_first()
    {
        var option = CoreOption.Parse("fbneo-cpu-speed", "CPU clock; 100%|110%|120%");
        Assert.Equal("CPU clock", option.Description);
        Assert.Equal(["100%", "110%", "120%"], option.Values);
        Assert.Equal("100%", option.Value);
    }
}

public class ImageTests
{
    [Fact]
    public void Rotating_90_ccw_moves_top_right_pixel_to_top_left()
    {
        // 2x1 image: red, green.
        var img = new Rgba32Image(2, 1, [255, 0, 0, 255, 0, 255, 0, 255]);
        var rotated = img.RotateCcw(1);
        Assert.Equal((1, 2), (rotated.Width, rotated.Height));
        Assert.Equal<byte>([0, 255, 0, 255], rotated.Pixels[..4]); // green now on top
        Assert.Equal(img.Pixels, img.RotateCcw(4).Pixels);
    }

    [Fact]
    public void Png_has_valid_signature_and_dimensions()
    {
        var img = new Rgba32Image(3, 2, new byte[3 * 2 * 4]);
        using var ms = new MemoryStream();
        PngEncoder.Write(img, ms);
        var bytes = ms.ToArray();
        Assert.Equal<byte>([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[..4]);
        Assert.Equal(3, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)));
        Assert.Equal(2, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)));
    }
}
