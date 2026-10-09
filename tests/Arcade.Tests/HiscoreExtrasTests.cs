using Arcade.App;

namespace Arcade.Tests;

public class HiscoreExtrasTests
{
    [Fact]
    public void Missing_entries_are_appended_once_in_the_files_line_endings()
    {
        var system = Path.Combine(Path.GetTempPath(), "arcade-hiscore-" + Guid.NewGuid().ToString("N"));
        var dat = Path.Combine(system, "mame2003-plus", "hiscore.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(dat)!);
        try
        {
            HiscoreExtras.Apply(system); // no file yet: nothing is created, so the core still writes its own
            Assert.False(File.Exists(dat));

            File.WriteAllText(dat, "; header\r\n\r\nrobby:\r\n0:e1c7:21:4a:4b\r\n");
            HiscoreExtras.Apply(system);
            HiscoreExtras.Apply(system);
            var text = File.ReadAllText(dat);
            Assert.EndsWith("robby:\r\n0:e1c7:21:4a:4b\r\n\r\nsupertnk:\r\n0:1bda:2:00:00\r\n", text);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "supertnk:"));
        }
        finally
        {
            Directory.Delete(system, true);
        }
    }
}
