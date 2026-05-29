using Velopack;

namespace WpfMusicEditor;

public class Starter
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack: 설치/업데이트 훅 처리 (반드시 앱 로직보다 먼저)
        VelopackApp.Build().Run();
        _ = new App().Run();
    }
}
