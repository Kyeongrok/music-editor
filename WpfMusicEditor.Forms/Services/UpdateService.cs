using Velopack;
using Velopack.Sources;

namespace WpfMusicEditor.Forms.Services;

/// <summary>
/// GitHub 릴리즈 기반 자동 업데이트(Velopack). Setup.exe 로 설치된 경우에만 동작한다.
/// </summary>
public class UpdateService
{
    private const string RepoUrl = "https://github.com/Kyeongrok/music-editor";

    private readonly UpdateManager _updateManager;
    private UpdateInfo? _pendingUpdate;

    /// <summary>마지막 CheckForUpdate 시의 상태/에러 (진단용).</summary>
    public string? LastDiagnostic { get; private set; }

    public UpdateService()
    {
        _updateManager = new UpdateManager(new GithubSource(RepoUrl, null, false));
    }

    public bool IsInstalled => _updateManager.IsInstalled;

    /// <summary>현재 설치된 버전(Velopack 관리). Setup.exe 설치가 아니면 null.</summary>
    public string? CurrentVersion => _updateManager.CurrentVersion?.ToString();

    public async Task<string?> CheckForUpdateAsync()
    {
        if (!IsInstalled)
        {
            LastDiagnostic = "IsInstalled=false (Setup.exe로 설치된 앱이 아님)";
            return null;
        }

        try
        {
            _pendingUpdate = await _updateManager.CheckForUpdatesAsync();
            if (_pendingUpdate == null)
            {
                LastDiagnostic = $"업데이트 없음 (현재 {CurrentVersion} = 최신)";
                return null;
            }

            LastDiagnostic = $"업데이트 감지: {CurrentVersion} → {_pendingUpdate.TargetFullRelease.Version}";
            return _pendingUpdate.TargetFullRelease.Version.ToString();
        }
        catch (Exception ex)
        {
            LastDiagnostic = $"예외: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    public async Task DownloadUpdateAsync(Action<int>? onProgress = null)
    {
        if (_pendingUpdate == null) return;
        await _updateManager.DownloadUpdatesAsync(_pendingUpdate, onProgress);
    }

    public void ApplyUpdateAndRestart()
    {
        if (_pendingUpdate == null) return;
        _updateManager.ApplyUpdatesAndRestart(_pendingUpdate.TargetFullRelease);
    }
}
