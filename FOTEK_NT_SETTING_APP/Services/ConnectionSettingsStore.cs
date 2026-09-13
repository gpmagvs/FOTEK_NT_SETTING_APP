using System.IO;
using System.Text.Json;

namespace FOTEK_NT_SETTING_APP.Services;

public sealed class ConnectionSettings
{
    public string ProfileName { get; set; } = "預設";
    public bool UseTcp { get; set; }
    public string PortName { get; set; } = string.Empty;
    public int BaudRate { get; set; } = 9600;
    public string Parity { get; set; } = "None";
    public int DataBits { get; set; } = 8;
    public string StopBits { get; set; } = "1";
    public byte SlaveId { get; set; } = 1;
    public string TcpHost { get; set; } = "192.168.1.100";
    public int TcpPort { get; set; } = 502;
}

public sealed class AppSettings
{
    public List<ConnectionSettings> Profiles { get; set; } = [];
    public string SelectedProfileName { get; set; } = "預設";
    public int DashboardPollMs { get; set; } = 800;
    public int ParameterPollMs { get; set; } = 3000;

    /// <summary>Last used connection fields (also mirrored into selected profile).</summary>
    public ConnectionSettings Current { get; set; } = new();
}

public static class ConnectionSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FOTEK_NT_SETTING_APP");

    public static string FilePath => Path.Combine(DirectoryPath, "connection-settings.json");

    private static string SettingsDirectory => DirectoryPath;
    private static string SettingsPath => FilePath;

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return CreateDefault();

            var json = File.ReadAllText(SettingsPath);

            // Backward compatible: old file was a flat ConnectionSettings
            if (json.Contains("\"UseTcp\"", StringComparison.Ordinal) &&
                !json.Contains("\"Profiles\"", StringComparison.Ordinal))
            {
                var legacy = JsonSerializer.Deserialize<ConnectionSettings>(json);
                var app = CreateDefault();
                if (legacy is not null)
                {
                    legacy.ProfileName = "預設";
                    app.Current = legacy;
                    app.Profiles = [Clone(legacy)];
                    app.SelectedProfileName = "預設";
                }
                return app;
            }

            var loaded = JsonSerializer.Deserialize<AppSettings>(json);
            if (loaded is null)
                return CreateDefault();

            if (loaded.Profiles.Count == 0)
                loaded.Profiles.Add(Clone(loaded.Current));

            if (loaded.DashboardPollMs < 200)
                loaded.DashboardPollMs = 800;
            if (loaded.ParameterPollMs < loaded.DashboardPollMs)
                loaded.ParameterPollMs = Math.Max(loaded.DashboardPollMs * 3, 3000);

            return loaded;
        }
        catch
        {
            return CreateDefault();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // ignore
        }
    }

    public static ConnectionSettings Clone(ConnectionSettings s) => new()
    {
        ProfileName = s.ProfileName,
        UseTcp = s.UseTcp,
        PortName = s.PortName,
        BaudRate = s.BaudRate,
        Parity = s.Parity,
        DataBits = s.DataBits,
        StopBits = s.StopBits,
        SlaveId = s.SlaveId,
        TcpHost = s.TcpHost,
        TcpPort = s.TcpPort
    };


    private static void MigrateLegacySettingsIfNeeded()
    {
        if (File.Exists(SettingsPath))
            return;

        var legacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TemperatureControllerAPP");
        var legacyFile = Path.Combine(legacyDir, "connection-settings.json");
        if (!File.Exists(legacyFile))
            return;

        Directory.CreateDirectory(SettingsDirectory);
        File.Copy(legacyFile, SettingsPath, overwrite: false);
    }

    private static AppSettings CreateDefault()
    {
        var current = new ConnectionSettings();
        return new AppSettings
        {
            Current = current,
            Profiles = [Clone(current)],
            SelectedProfileName = "預設"
        };
    }
}
