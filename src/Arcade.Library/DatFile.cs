using System.Globalization;
using System.Xml;

namespace Arcade.Library;

public sealed record DatRom(string Name, uint? Crc, long Size, bool NoDump);

public enum Orientation { Unknown, Horizontal, Vertical }

/// <summary>How well the core emulates the game, from the DAT's &lt;driver status&gt;.</summary>
public enum DriverStatus { Unknown, Good, Imperfect, Preliminary }

public sealed record DatGame(string Name, string Description, string? CloneOf, string? RomOf, bool IsBios, IReadOnlyList<DatRom> Roms)
{
    public string? Year { get; init; }
    public string? Manufacturer { get; init; }
    /// <summary>Set whose samples zip holds this game's samples (MAME's sampleof); defaults to the game itself.</summary>
    public string? SampleOf { get; init; }
    /// <summary>Sample file names without extension (e.g. "bounce1"): sound effects played from WAV files, not ROMs.</summary>
    public IReadOnlyList<string> Samples { get; init; } = [];
    public Orientation Orientation { get; init; }
    public int? Players { get; init; }
    public string? Control { get; init; }
    public int? Buttons { get; init; }
    public DriverStatus Status { get; init; }

    public string SamplesZipName => SampleOf ?? Name;
}

/// <summary>
/// A core's list of supported ROM sets, read from a Logiqx/ClrMamePro XML DAT (FBNeo) or an
/// old-style MAME <c>-listxml</c> file (MAME 2003-Plus). Both use &lt;game&gt;/&lt;machine&gt; with &lt;rom&gt; children.
/// </summary>
public sealed class DatFile
{
    readonly Dictionary<string, DatGame> _games;

    DatFile(Dictionary<string, DatGame> games) => _games = games;

    public int Count => _games.Count;

    public IEnumerable<DatGame> Games => _games.Values;

    public DatGame? Find(string setName) => _games.GetValueOrDefault(setName);

    public static DatFile Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static DatFile Load(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };
        var games = new Dictionary<string, DatGame>(StringComparer.OrdinalIgnoreCase);
        using var reader = XmlReader.Create(stream, settings);

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name is not ("game" or "machine"))
                continue;

            var name = reader.GetAttribute("name");
            if (name == null)
                continue;
            var cloneOf = reader.GetAttribute("cloneof");
            var romOf = reader.GetAttribute("romof");
            var sampleOf = reader.GetAttribute("sampleof");
            // MAME 2003 marks BIOS sets as runnable="no" rather than isbios="yes".
            var isBios = reader.GetAttribute("isbios") == "yes" || reader.GetAttribute("runnable") == "no";
            var description = name;
            string? year = null, manufacturer = null, control = null;
            int? players = null, buttons = null;
            var orientation = Orientation.Unknown;
            var status = DriverStatus.Unknown;
            var roms = new List<DatRom>();
            var samples = new List<string>();

            if (!reader.IsEmptyElement)
            {
                var depth = reader.Depth;
                reader.Read();
                while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1)
                    {
                        switch (reader.Name)
                        {
                            // These move the reader past the end tag themselves, so skip the Read() below.
                            case "description": description = reader.ReadElementContentAsString(); continue;
                            case "year": year = reader.ReadElementContentAsString(); continue;
                            case "manufacturer": manufacturer = reader.ReadElementContentAsString(); continue;

                            case "rom":
                                var crcText = reader.GetAttribute("crc");
                                uint? crc = uint.TryParse(crcText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var c) ? c : null;
                                long.TryParse(reader.GetAttribute("size"), out var size);
                                roms.Add(new DatRom(
                                    reader.GetAttribute("name") ?? "",
                                    crc,
                                    size,
                                    reader.GetAttribute("status") == "nodump"));
                                break;
                            case "sample" when reader.GetAttribute("name") is { } sample:
                                samples.Add(Path.GetFileNameWithoutExtension(sample));
                                break;
                            case "video":
                                orientation = reader.GetAttribute("orientation") switch
                                {
                                    "horizontal" => Orientation.Horizontal,
                                    "vertical" => Orientation.Vertical,
                                    _ => orientation,
                                };
                                break;
                            case "input":
                                players = ParseInt(reader.GetAttribute("players"));
                                buttons = ParseInt(reader.GetAttribute("buttons"));
                                control = reader.GetAttribute("control");
                                break;
                            case "driver":
                                status = reader.GetAttribute("status") switch
                                {
                                    "good" => DriverStatus.Good,
                                    "imperfect" => DriverStatus.Imperfect,
                                    "preliminary" => DriverStatus.Preliminary,
                                    _ => DriverStatus.Unknown,
                                };
                                break;
                        }
                    }
                    reader.Read();
                }
            }

            games[name] = new DatGame(name, description, cloneOf, romOf, isBios, roms)
            {
                Year = year,
                Manufacturer = manufacturer,
                SampleOf = sampleOf,
                Samples = samples,
                Orientation = orientation,
                Players = players,
                Control = control,
                Buttons = buttons,
                Status = status,
            };
        }

        return new DatFile(games);
    }

    static int? ParseInt(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
