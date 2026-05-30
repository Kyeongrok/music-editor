using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WpfMusicEditor.Main.Audio;

/// <summary>
/// 외부 RVC(Python) 프로세스를 구동해 음색 모델을 학습한다.
/// tools/rvc_pipeline.py 를 실행하고 stdout의 '@@' 마커로 진행 상황을 파싱한다.
/// </summary>
public sealed class RvcVoiceTrainer : IVoiceTrainer
{
    private readonly AppSettings _settings;

    public RvcVoiceTrainer(AppSettings settings) => _settings = settings;

    /// <summary>앱과 함께 배포되는 드라이버 스크립트 경로(출력 폴더의 tools\rvc_pipeline.py).</summary>
    private static string DriverScript =>
        Path.Combine(AppContext.BaseDirectory, "tools", "rvc_pipeline.py");

    public async Task<string> CreateModelAsync(
        string modelName, string datasetDir, TrainOptions options,
        IProgress<string>? progress = null,
        IProgress<double>? percentProgress = null,
        CancellationToken cancellationToken = default)
    {
        var name = Sanitize(modelName);
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("모델 이름이 비어 있습니다.", nameof(modelName));
        ValidateEnv();
        if (!Directory.Exists(datasetDir) || Directory.GetFiles(datasetDir, "*.wav").Length == 0)
            throw new DirectoryNotFoundException("데이터셋(wav) 폴더가 비어 있습니다.");

        var outPath = Path.Combine(AppSettings.ModelsDir, name + ".onnx");
        var args = new List<string>
        {
            "--rvc-root", _settings.RvcRoot!,
            "--dataset", datasetDir,
            "--name", name,
            "--out", outPath,
            "--sr", options.SampleRate,
            "--version", options.Version,
            "--epochs", options.Epochs.ToString(),
            "--batch", options.BatchSize.ToString(),
        };
        return await RunDriverAsync(args, "학습", progress, percentProgress, cancellationToken);
    }

    public async Task<string> ExportOnnxAsync(
        string pthPath, string modelName,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var name = Sanitize(modelName);
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("모델 이름이 비어 있습니다.", nameof(modelName));
        ValidateEnv();
        if (string.IsNullOrWhiteSpace(pthPath) || !File.Exists(pthPath))
            throw new FileNotFoundException(".pth 모델 파일을 찾을 수 없습니다.", pthPath);

        var outPath = Path.Combine(AppSettings.ModelsDir, name + ".onnx");
        var args = new List<string>
        {
            "--rvc-root", _settings.RvcRoot!,
            "--name", name,
            "--out", outPath,
            "--pth", pthPath,
        };
        return await RunDriverAsync(args, "ONNX 변환", progress, null, cancellationToken);
    }

    private void ValidateEnv()
    {
        if (string.IsNullOrWhiteSpace(_settings.RvcRoot) || !Directory.Exists(_settings.RvcRoot))
            throw new DirectoryNotFoundException("RVC 폴더가 설정되지 않았습니다. 🎓 모델 만들기에서 RVC 경로를 지정하세요.");
        if (string.IsNullOrWhiteSpace(_settings.PythonPath) || !File.Exists(_settings.PythonPath))
            throw new FileNotFoundException("Python 실행 파일이 설정되지 않았습니다. 🎓 모델 만들기에서 python.exe 경로를 지정하세요.");
        if (!File.Exists(DriverScript))
            throw new FileNotFoundException("드라이버(rvc_pipeline.py)를 찾을 수 없습니다.", DriverScript);
    }

    /// <summary>드라이버(rvc_pipeline.py)를 실행하고 '@@' 마커로 진행을 파싱한다. 성공 시 결과 .onnx 경로.</summary>
    private async Task<string> RunDriverAsync(
        IReadOnlyList<string> driverArgs, string failVerb,
        IProgress<string>? progress, IProgress<double>? percentProgress, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _settings.PythonPath,
            WorkingDirectory = _settings.RvcRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(DriverScript);
        foreach (var a in driverArgs)
            psi.ArgumentList.Add(a);

        // PYTHONIOENCODING로 자식 프로세스 출력 인코딩을 UTF-8로 고정(한글/마커 깨짐 방지).
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        using var process = new Process { StartInfo = psi };
        var tail = new Queue<string>();           // 실패 시 보여줄 마지막 출력
        string? resultPath = null;
        string? errorMessage = null;

        process.Start();

        var stderrTask = DrainStderrAsync(process, tail);
        await foreach (var line in ReadLinesAsync(process.StandardOutput, cancellationToken))
        {
            KeepTail(tail, line);
            if (line.StartsWith("@@STAGE", StringComparison.Ordinal))
            {
                var stage = line[7..].Trim();
                progress?.Report(StageText(stage));
                percentProgress?.Report(StagePercent(stage));
            }
            else if (line.StartsWith("@@EPOCH", StringComparison.Ordinal))
            {
                var m = Regex.Match(line, @"(\d+)\s*/\s*(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var n)
                    && int.TryParse(m.Groups[2].Value, out var total) && total > 0)
                {
                    progress?.Report($"학습 중... epoch {n}/{total}");
                    // 학습 구간(15~95%)을 epoch 비율로 채운다.
                    percentProgress?.Report(15 + 80.0 * Math.Min(n, total) / total);
                }
            }
            else if (line.StartsWith("@@DONE", StringComparison.Ordinal))
            {
                resultPath = line[6..].Trim();
                percentProgress?.Report(100);
            }
            else if (line.StartsWith("@@ERROR", StringComparison.Ordinal))
            {
                errorMessage = line[7..].Trim();
            }
        }

        try { await stderrTask; } catch { /* 무시: 종료 처리에서 다룬다 */ }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (resultPath is not null && File.Exists(resultPath))
            return resultPath;

        var detail = errorMessage ?? string.Join("\n", tail);
        throw new InvalidOperationException(
            $"{failVerb}에 실패했습니다 (exit {process.ExitCode}).\n{detail}");
    }

    /// <summary>취소 시 파이썬 자식까지 포함해 프로세스 트리를 종료한다.</summary>
    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* 이미 종료됨 등 */ }
    }

    private static async Task DrainStderrAsync(Process process, Queue<string> tail)
    {
        string? line;
        while ((line = await process.StandardError.ReadLineAsync()) is not null)
            lock (tail) KeepTail(tail, line);
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(
        StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
            yield return line;
    }

    private static void KeepTail(Queue<string> tail, string line)
    {
        tail.Enqueue(line);
        while (tail.Count > 30) tail.Dequeue();
    }

    private static string StageText(string stage) => stage switch
    {
        "preprocess" => "데이터 전처리 중...",
        "f0" => "음높이(f0) 추출 중...",
        "feature" => "발음 특징 추출 중...",
        "filelist" => "학습 목록 준비 중...",
        "train" => "학습 시작...",
        "export" => "ONNX로 변환 중...",
        _ => $"진행 중... ({stage})"
    };

    private static double StagePercent(string stage) => stage switch
    {
        "preprocess" => 3,
        "f0" => 7,
        "feature" => 11,
        "filelist" => 14,
        "train" => 15,
        "export" => 96,
        _ => 0
    };

    /// <summary>모델 이름을 RVC 실험명/파일명으로 안전하게 정규화(영숫자·밑줄).</summary>
    private static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim())
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString().Trim('_');
    }
}
