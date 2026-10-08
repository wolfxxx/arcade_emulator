using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Arcade.Libretro;

/// <summary>
/// Minimal printf for the libretro log callback, which is variadic. On Windows x64 variadic
/// arguments occupy ordinary 8-byte argument slots (doubles are passed as their bit pattern),
/// so the callback receives them as raw <c>nint</c> slots and we interpret them here.
/// </summary>
internal static unsafe class CFormat
{
    public static string Format(byte* fmt, ReadOnlySpan<nint> args)
    {
        if (fmt == null)
            return string.Empty;
        var format = Marshal.PtrToStringUTF8((nint)fmt) ?? string.Empty;
        var sb = new StringBuilder(format.Length + 32);
        var argIndex = 0;

        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c != '%')
            {
                sb.Append(c);
                continue;
            }
            if (++i >= format.Length)
                break;
            if (format[i] == '%')
            {
                sb.Append('%');
                continue;
            }

            var leftAlign = false;
            var zeroPad = false;
            while (i < format.Length && "-+ #0".IndexOf(format[i]) >= 0)
            {
                if (format[i] == '-') leftAlign = true;
                if (format[i] == '0') zeroPad = true;
                i++;
            }

            var width = 0;
            if (i < format.Length && format[i] == '*')
            {
                width = (int)Next(args, ref argIndex);
                i++;
            }
            while (i < format.Length && char.IsDigit(format[i]))
                width = width * 10 + (format[i++] - '0');

            var precision = -1;
            if (i < format.Length && format[i] == '.')
            {
                i++;
                precision = 0;
                if (i < format.Length && format[i] == '*')
                {
                    precision = (int)Next(args, ref argIndex);
                    i++;
                }
                while (i < format.Length && char.IsDigit(format[i]))
                    precision = precision * 10 + (format[i++] - '0');
            }

            // Length modifiers. On Windows `long` is 32-bit; `ll`, `z`, `j`, `t`, `I64` are 64-bit.
            var bits = 32;
            while (i < format.Length && "hlzjtLI".IndexOf(format[i]) >= 0)
            {
                switch (format[i])
                {
                    case 'l' when i + 1 < format.Length && format[i + 1] == 'l':
                        bits = 64;
                        i++;
                        break;
                    case 'z' or 'j' or 't':
                        bits = 64;
                        break;
                    case 'I' when format.AsSpan(i).StartsWith("I64"):
                        bits = 64;
                        i += 2;
                        break;
                }
                i++;
            }
            if (i >= format.Length)
                break;

            string text;
            var conv = format[i];
            switch (conv)
            {
                case 'd' or 'i':
                {
                    var raw = Next(args, ref argIndex);
                    var v = bits == 64 ? (long)raw : (int)raw;
                    text = v.ToString(CultureInfo.InvariantCulture);
                    break;
                }
                case 'u':
                {
                    var raw = Next(args, ref argIndex);
                    var v = bits == 64 ? (ulong)raw : (uint)raw;
                    text = v.ToString(CultureInfo.InvariantCulture);
                    break;
                }
                case 'x' or 'X':
                {
                    var raw = Next(args, ref argIndex);
                    var v = bits == 64 ? (ulong)raw : (uint)raw;
                    text = v.ToString(conv == 'x' ? "x" : "X", CultureInfo.InvariantCulture);
                    break;
                }
                case 'o':
                    text = Convert.ToString(bits == 64 ? (long)Next(args, ref argIndex) : (uint)Next(args, ref argIndex), 8);
                    break;
                case 'c':
                    text = ((char)(byte)Next(args, ref argIndex)).ToString();
                    break;
                case 's':
                {
                    var p = Next(args, ref argIndex);
                    text = p == 0 ? "(null)" : Marshal.PtrToStringUTF8(p) ?? string.Empty;
                    if (precision >= 0 && text.Length > precision)
                        text = text[..precision];
                    break;
                }
                case 'p':
                    text = "0x" + ((ulong)Next(args, ref argIndex)).ToString("x16", CultureInfo.InvariantCulture);
                    break;
                case 'f' or 'F' or 'e' or 'E' or 'g' or 'G':
                {
                    var v = BitConverter.Int64BitsToDouble(Next(args, ref argIndex));
                    var p = precision < 0 ? 6 : precision;
                    text = conv switch
                    {
                        'f' or 'F' => v.ToString("F" + p, CultureInfo.InvariantCulture),
                        'e' or 'E' => v.ToString((conv == 'e' ? "e" : "E") + p, CultureInfo.InvariantCulture),
                        _ => v.ToString("G" + (p == 0 ? 1 : p), CultureInfo.InvariantCulture),
                    };
                    break;
                }
                default:
                    text = "%" + conv;
                    break;
            }

            if (text.Length < width)
                text = leftAlign ? text.PadRight(width) : text.PadLeft(width, zeroPad && conv is not ('s' or 'c') ? '0' : ' ');
            sb.Append(text);
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    static nint Next(ReadOnlySpan<nint> args, ref int index) => index < args.Length ? args[index++] : 0;
}
