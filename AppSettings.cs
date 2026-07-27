using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowAudioRecorder;

public enum OutputFormat
{
    Wav16,
    Wav24,
    Wav32Float,
    Mp3
}

public enum ChannelMode
{
    Native,
    Stereo,
    Mono
}

/// <summary>User preferences, persisted to %AppData%\WindowAudioRecorder\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string? DeviceId { get; set; }
    public bool FollowDefaultDevice { get; set; } = true;
    public bool MonitorWhenIdle { get; set; } = true;
    public bool MinimizeToTray { get; set; }

    public OutputFormat Format { get; set; } = OutputFormat.Wav16;
    public int Mp3Bitrate { get; set; } = 192;
    public int SampleRate { get; set; }                 // 0 = follow the device
    public ChannelMode Channels { get; set; } = ChannelMode.Stereo;
    public int GainDb { get; set; }

    public string Folder { get; set; } = DefaultFolder();
    public string FileTemplate { get; set; } = "rec_{date}_{time}";
    public int SplitMinutes { get; set; }               // 0 = one continuous file
    public int MaxMinutes { get; set; }                 // 0 = no limit

    public List<string> Recent { get; set; } = [];

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WindowAudioRecorder", "settings.json");

    public static string DefaultFolder()
    {
        string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (string.IsNullOrEmpty(music)) music = AppContext.BaseDirectory;
        return Path.Combine(music, "Recordings");
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded is not null)
                {
                    loaded.Validate();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            // A corrupt or unreadable settings file must never stop the app from starting.
            Debug.WriteLine($"Settings load failed: {ex.Message}");
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Settings save failed: {ex.Message}");
        }
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Folder)) Folder = DefaultFolder();
        if (string.IsNullOrWhiteSpace(FileTemplate)) FileTemplate = "rec_{date}_{time}";
        if (SampleRate is not 0 and (< 4000 or > 384000)) SampleRate = 0;
        if (!Mp3Bitrates.Contains(Mp3Bitrate)) Mp3Bitrate = 192;
        GainDb = Math.Clamp(GainDb, -20, 20);
        SplitMinutes = Math.Clamp(SplitMinutes, 0, 1440);
        MaxMinutes = Math.Clamp(MaxMinutes, 0, 1440);
        Recent ??= [];
    }

    public static readonly int[] Mp3Bitrates = [96, 128, 160, 192, 256, 320];

    public static readonly int[] StandardRates =
        [8000, 11025, 16000, 22050, 32000, 44100, 48000, 88200, 96000, 192000];

    /// <summary>Sample rates LAME can actually encode (MPEG 1, 2 and 2.5).</summary>
    public static readonly int[] Mp3Rates =
        [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000];

    public static int NearestMp3Rate(int rate)
    {
        int best = Mp3Rates[0];
        foreach (int candidate in Mp3Rates)
        {
            if (Math.Abs(candidate - rate) < Math.Abs(best - rate)) best = candidate;
        }
        return best;
    }
}
