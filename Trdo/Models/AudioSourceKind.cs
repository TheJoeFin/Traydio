namespace Trdo.Models;

/// <summary>
/// What kind of audio a station actually plays, which determines the playback path
/// <see cref="Services.RadioPlayerService"/> uses for it.
/// </summary>
public enum AudioSourceKind
{
    /// <summary>
    /// A live internet radio stream at <see cref="RadioStation.StreamUrl"/>. The default, so a
    /// station saved before this field existed - which is every station prior to white noise -
    /// deserialises as one without needing a migration.
    /// </summary>
    Radio,

    /// <summary>Generated noise, played locally. See <see cref="RadioStation.WhiteNoiseColor"/>.</summary>
    WhiteNoise,

    /// <summary>
    /// A folder of local audio files, played in filename order. See
    /// <see cref="RadioStation.LocalFolderPath"/>.
    /// </summary>
    Files,
}
