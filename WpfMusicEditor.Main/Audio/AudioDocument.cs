namespace WpfMusicEditor.Main.Audio;

/// <summary>
/// 메모리에 디코딩된 편집 가능한 오디오. 인터리브된 float PCM 샘플을 들고 있으며
/// 구간 삭제(Cut)와 실행 취소(Undo)를 지원한다.
/// </summary>
public sealed class AudioDocument
{
    private float[] _samples;
    private readonly Stack<CutOperation> _undo = new();

    public AudioDocument(int sampleRate, int channels, float[] samples)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _samples = samples;
    }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>현재 편집 상태의 전체 샘플(인터리브). 내보내기/재생에 사용한다.</summary>
    public float[] Samples => _samples;

    public int FrameCount => _samples.Length / Channels;

    public TimeSpan Duration => TimeSpan.FromSeconds((double)FrameCount / SampleRate);

    public bool CanUndo => _undo.Count > 0;

    /// <summary>[start, end) 구간을 삭제하고 뒤쪽을 앞으로 당겨 붙인다.</summary>
    public void Cut(TimeSpan start, TimeSpan end)
    {
        var startFrame = ClampFrame(start);
        var endFrame = ClampFrame(end);
        if (endFrame <= startFrame)
            return;

        var startIdx = startFrame * Channels;
        var endIdx = endFrame * Channels;

        var removed = new float[endIdx - startIdx];
        Array.Copy(_samples, startIdx, removed, 0, removed.Length);
        _undo.Push(new CutOperation(startIdx, removed));

        var result = new float[_samples.Length - removed.Length];
        Array.Copy(_samples, 0, result, 0, startIdx);
        Array.Copy(_samples, endIdx, result, startIdx, _samples.Length - endIdx);
        _samples = result;
    }

    /// <summary>마지막 Cut을 되돌린다(삭제했던 샘플을 원위치에 다시 끼워 넣는다).</summary>
    public void Undo()
    {
        if (_undo.Count == 0)
            return;

        var op = _undo.Pop();
        var result = new float[_samples.Length + op.Removed.Length];
        Array.Copy(_samples, 0, result, 0, op.Index);
        Array.Copy(op.Removed, 0, result, op.Index, op.Removed.Length);
        Array.Copy(_samples, op.Index, result, op.Index + op.Removed.Length, _samples.Length - op.Index);
        _samples = result;
    }

    /// <summary>파형 표시용으로 전체를 <paramref name="buckets"/>개로 나눠 피크(0~1 정규화)를 계산한다.</summary>
    public float[] ComputePeaks(int buckets)
    {
        if (buckets < 1) buckets = 1;
        var peaks = new float[buckets];
        long total = _samples.Length;
        if (total == 0)
            return peaks;

        var samplesPerBucket = (double)total / buckets;
        for (long i = 0; i < total; i++)
        {
            var bucket = (int)(i / samplesPerBucket);
            if (bucket >= buckets) bucket = buckets - 1;
            var abs = Math.Abs(_samples[i]);
            if (abs > peaks[bucket]) peaks[bucket] = abs;
        }

        var max = 0f;
        foreach (var p in peaks)
            if (p > max) max = p;
        if (max > 0)
            for (var i = 0; i < buckets; i++)
                peaks[i] /= max;

        return peaks;
    }

    private int ClampFrame(TimeSpan time)
    {
        var frame = (long)Math.Round(time.TotalSeconds * SampleRate);
        if (frame < 0) frame = 0;
        if (frame > FrameCount) frame = FrameCount;
        return (int)frame;
    }

    private readonly record struct CutOperation(int Index, float[] Removed);
}
