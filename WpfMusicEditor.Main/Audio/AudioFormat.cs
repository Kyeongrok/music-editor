namespace WpfMusicEditor.Main.Audio;

public enum AudioFormat
{
    M4a,
    Mp3,
    Wav
}

public static class AudioFormatExtensions
{
    public static string ToExtension(this AudioFormat format) => format switch
    {
        AudioFormat.M4a => ".m4a",
        AudioFormat.Mp3 => ".mp3",
        AudioFormat.Wav => ".wav",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    public static string ToFilter(this AudioFormat format) => format switch
    {
        AudioFormat.M4a => "AAC 오디오 (*.m4a)|*.m4a",
        AudioFormat.Mp3 => "MP3 오디오 (*.mp3)|*.mp3",
        AudioFormat.Wav => "WAV 오디오 (*.wav)|*.wav",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };
}
