using System.IO;
using System.Text.Json;

namespace WpfMusicEditor.Main;

/// <summary>
/// 앱 설정(JSON, %LOCALAPPDATA%\WpfMusicEditor\settings.json). 현재는 음색 모델 학습용
/// RVC 경로만 보관한다. 모델 폴더 경로(ModelsDir) 등 공용 위치도 여기서 제공한다.
/// </summary>
public sealed class AppSettings
{
    /// <summary>RVC 저장소(또는 프리빌트 패키지) 폴더. 학습 스크립트를 이 폴더에서 실행한다.</summary>
    public string? RvcRoot { get; set; }

    /// <summary>학습에 쓸 Python 실행 파일(보통 RVC venv/runtime의 python.exe).</summary>
    public string? PythonPath { get; set; }

    /// <summary>만든 모델(.onnx)을 저장할 폴더. 비어 있으면 기본 위치(<see cref="ModelsDir"/>)를 쓴다.</summary>
    public string? ModelsDirectory { get; set; }

    /// <summary>만든 모델을 실제로 저장할 폴더. 설정값이 있으면 그곳, 없으면 기본 위치.</summary>
    public string EffectiveModelsDir
    {
        get
        {
            var dir = string.IsNullOrWhiteSpace(ModelsDirectory) ? ModelsDir : ModelsDirectory!;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string AppDataDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WpfMusicEditor");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>음색 변환 모델(.onnx)과 공유 모델을 두는 폴더.</summary>
    public static string ModelsDir
    {
        get
        {
            var dir = Path.Combine(AppDataDir, "models");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string SettingsPath => Path.Combine(AppDataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath))
                       ?? new AppSettings();
        }
        catch
        {
            // 손상된 설정은 무시하고 기본값으로 시작한다.
        }
        return new AppSettings();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
