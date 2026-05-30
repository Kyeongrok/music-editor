namespace WpfMusicEditor.Main.Audio;

/// <summary>
/// 메모리에 디코딩된 편집 가능한 오디오. 인터리브된 float PCM 샘플을 들고 있으며
/// 구간 삭제(Cut)·구간 볼륨 조절(ApplyGain)·구간 교체(ReplaceRange)와 실행 취소(Undo)를 지원한다.
/// </summary>
public sealed class AudioDocument
{
    private float[] _samples;
    private readonly Stack<IUndoStep> _undo = new();

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

    /// <summary>
    /// [start, end) 구간의 볼륨을 <paramref name="gainDb"/> 데시벨만큼 키운다(음수면 줄인다).
    /// 샘플이 [-1, 1] 범위를 넘으면 클리핑되지 않도록 잘라 낸다.
    /// </summary>
    public void ApplyGain(TimeSpan start, TimeSpan end, double gainDb)
    {
        var startFrame = ClampFrame(start);
        var endFrame = ClampFrame(end);
        if (endFrame <= startFrame)
            return;

        var startIdx = startFrame * Channels;
        var endIdx = endFrame * Channels;

        // 원본 구간을 백업해 두었다가 실행 취소 시 그대로 되돌린다.
        var original = new float[endIdx - startIdx];
        Array.Copy(_samples, startIdx, original, 0, original.Length);
        _undo.Push(new GainOperation(startIdx, original));

        var factor = (float)Math.Pow(10, gainDb / 20.0);
        for (var i = startIdx; i < endIdx; i++)
        {
            var v = _samples[i] * factor;
            if (v > 1f) v = 1f;
            else if (v < -1f) v = -1f;
            _samples[i] = v;
        }
    }

    /// <summary>
    /// [start, end) 구간을 <paramref name="newInterleaved"/>(인터리브, 채널 수는 이 문서와 동일)로 교체한다.
    /// 음색 변환처럼 길이가 거의 같지만 리샘플 왕복으로 ±수 샘플 차이가 날 수 있어 스플라이스로 안전하게 갈아 끼운다.
    /// </summary>
    public void ReplaceRange(TimeSpan start, TimeSpan end, float[] newInterleaved)
    {
        var startFrame = ClampFrame(start);
        var endFrame = ClampFrame(end);
        if (endFrame <= startFrame)
            return;

        var startIdx = startFrame * Channels;
        var endIdx = endFrame * Channels;

        // 교체될 원본 구간을 백업해 두었다가 실행 취소 시 그대로 되돌린다(길이 변화까지 복원).
        var original = new float[endIdx - startIdx];
        Array.Copy(_samples, startIdx, original, 0, original.Length);
        _undo.Push(new ReplaceOperation(startIdx, original, newInterleaved.Length));

        var result = new float[_samples.Length - original.Length + newInterleaved.Length];
        Array.Copy(_samples, 0, result, 0, startIdx);
        Array.Copy(newInterleaved, 0, result, startIdx, newInterleaved.Length);
        Array.Copy(_samples, endIdx, result, startIdx + newInterleaved.Length, _samples.Length - endIdx);
        _samples = result;
    }

    /// <summary>마지막 편집(Cut/ApplyGain/ReplaceRange)을 되돌린다.</summary>
    public void Undo()
    {
        if (_undo.Count == 0)
            return;

        _undo.Pop().Undo(ref _samples);
    }

    /// <summary>[start, end) 구간의 인터리브 샘플을 복사해 돌려준다(원본은 그대로 둔다).</summary>
    public float[] CopyRange(TimeSpan start, TimeSpan end)
    {
        var startIdx = ClampFrame(start) * Channels;
        var endIdx = ClampFrame(end) * Channels;
        if (endIdx <= startIdx)
            return Array.Empty<float>();

        var slice = new float[endIdx - startIdx];
        Array.Copy(_samples, startIdx, slice, 0, slice.Length);
        return slice;
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

    private interface IUndoStep
    {
        void Undo(ref float[] samples);
    }

    /// <summary>삭제했던 샘플을 원위치에 다시 끼워 넣어 길이를 복원한다.</summary>
    private readonly record struct CutOperation(int Index, float[] Removed) : IUndoStep
    {
        public void Undo(ref float[] samples)
        {
            var result = new float[samples.Length + Removed.Length];
            Array.Copy(samples, 0, result, 0, Index);
            Array.Copy(Removed, 0, result, Index, Removed.Length);
            Array.Copy(samples, Index, result, Index + Removed.Length, samples.Length - Index);
            samples = result;
        }
    }

    /// <summary>볼륨을 바꾸기 전 원본 구간을 제자리에 덮어써 되돌린다.</summary>
    private readonly record struct GainOperation(int Index, float[] Original) : IUndoStep
    {
        public void Undo(ref float[] samples)
            => Array.Copy(Original, 0, samples, Index, Original.Length);
    }

    /// <summary>교체로 끼워 넣은 구간을 들어내고 원본 구간을 도로 끼워 길이까지 복원한다.</summary>
    private readonly record struct ReplaceOperation(int Index, float[] Original, int NewLength) : IUndoStep
    {
        public void Undo(ref float[] samples)
        {
            var result = new float[samples.Length - NewLength + Original.Length];
            Array.Copy(samples, 0, result, 0, Index);
            Array.Copy(Original, 0, result, Index, Original.Length);
            var tailSrc = Index + NewLength;
            Array.Copy(samples, tailSrc, result, Index + Original.Length, samples.Length - tailSrc);
            samples = result;
        }
    }
}
