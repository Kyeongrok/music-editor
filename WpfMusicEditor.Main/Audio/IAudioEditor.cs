namespace WpfMusicEditor.Main.Audio;

public interface IAudioEditor
{
    /// <summary>오디오 파일을 메모리로 디코딩해 편집 가능한 문서로 만든다.</summary>
    Task<AudioDocument> LoadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>편집된 문서 전체를 지정 포맷으로 내보낸다.</summary>
    Task ExportAsync(AudioDocument document, string outputPath, AudioFormat format,
        int bitrateBps = 192000, CancellationToken cancellationToken = default);
}
