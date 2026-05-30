using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WpfMusicEditor.Main;

namespace WpfMusicEditor.Forms.ViewModels;

/// <summary>앱 설정 화면(햄버거 메뉴 → 설정)의 ViewModel. 현재는 모델 생성 위치를 다룬다.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        _modelsDirectory = settings.ModelsDirectory ?? "";
    }

    /// <summary>만든 모델(.onnx) 저장 폴더. 비우면 기본 위치를 쓴다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveModelsDirText))]
    private string _modelsDirectory;

    partial void OnModelsDirectoryChanged(string value)
    {
        _settings.ModelsDirectory = string.IsNullOrWhiteSpace(value) ? null : value;
        _settings.Save();
    }

    /// <summary>실제 적용되는 저장 위치(빈 값이면 기본 위치)를 안내용으로 보여준다.</summary>
    public string EffectiveModelsDirText =>
        string.IsNullOrWhiteSpace(ModelsDirectory)
            ? $"기본 위치 사용: {AppSettings.ModelsDir}"
            : $"적용 위치: {ModelsDirectory}";

    [RelayCommand]
    private void BrowseModelsDir()
    {
        var dialog = new OpenFolderDialog { Title = "모델 생성 위치 선택" };
        if (dialog.ShowDialog() == true)
            ModelsDirectory = dialog.FolderName;
    }

    /// <summary>기본 위치로 되돌린다(설정값 비움).</summary>
    [RelayCommand]
    private void UseDefaultModelsDir() => ModelsDirectory = "";
}
