using System.Drawing;
using System.IO;
using System.Reflection;

namespace PS3DiscordRichPresence.Helpers;

public static class IconHelper
{
    public static Icon GetTrayIcon()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith("ps.ico", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            throw new FileNotFoundException("Ícone embutido não encontrado.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;

        return new Icon(stream);
    }
}