using System.Globalization;
using System.Xml;

namespace Arcade.Library;

public sealed record DatRom(string Name, uint? Crc, long Size, bool NoDump);

public sealed record DatGame(string Name, string Description, string? CloneOf, string? RomOf, bool IsBios, IReadOnlyList<DatRom> Roms);

/// <summary>
/// A core's list of supported ROM sets, read from a Logiqx/ClrMamePro XML DAT (FBNeo) or an
/// old-style MAME <c>-listxml</c> file (MAME 2003-Plus). Both use &lt;game&gt;/&lt;machine&gt; with &lt;rom&gt; children.
/// </summary>
public sealed class DatFile
{
    readonly Dictionary<string, DatGame> _games;

    DatFile(Dictionary<string, DatGame> games) => _games = games;

    public int Count => _games.Count;

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
            var isBios = reader.GetAttribute("isbios") == "yes";
            var description = name;
            var roms = new List<DatRom>();

            if (!reader.IsEmptyElement)
            {
                var depth = reader.Depth;
                reader.Read();
                while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.Name == "description" && reader.Depth == depth + 1)
                    {
                        // Moves the reader past </description> itself, so skip the Read() below.
                        description = reader.ReadElementContentAsString();
                        continue;
                    }
                    if (reader.NodeType == XmlNodeType.Element && reader.Name == "rom")
                    {
                        var crcText = reader.GetAttribute("crc");
                        uint? crc = uint.TryParse(crcText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var c) ? c : null;
                        long.TryParse(reader.GetAttribute("size"), out var size);
                        roms.Add(new DatRom(
                            reader.GetAttribute("name") ?? "",
                            crc,
                            size,
                            reader.GetAttribute("status") == "nodump"));
                    }
                    reader.Read();
                }
            }

            games[name] = new DatGame(name, description, cloneOf, romOf, isBios, roms);
        }

        return new DatFile(games);
    }
}
