using System.IO;
using System.Text.Json;
using System.Windows;

namespace Caisse.Client;

public partial class App : Application
{
    public static string ApiBaseUrl { get; private set; } = "http://localhost:5080/";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
            return;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.TryGetProperty("ApiBaseUrl", out var value)
            && value.ValueKind == JsonValueKind.String
            && Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            ApiBaseUrl = uri.ToString().EndsWith('/') ? uri.ToString() : uri + "/";
        }
        else
        {
            throw new InvalidDataException("ApiBaseUrl dans appsettings.json doit être une URL HTTP ou HTTPS absolue.");
        }
    }
}
