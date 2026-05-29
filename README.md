# WpfMusicEditor

WPF(.NET 8)로 만든 간단한 **m4a 오디오 편집기**입니다. 파형을 보면서 원하는 구간을
잘라내고, 미리듣고, m4a/mp3/wav로 내보낼 수 있습니다. 오디오 처리는
[NAudio](https://github.com/naudio/NAudio) + Windows Media Foundation을 사용합니다.

## 주요 기능

- **파일 열기** — `.m4a` / `.mp4` / `.aac` / `.mp3` / `.wav` 디코딩 (메모리 PCM)
- **파형 표시** — 전체 파형 렌더링
  - 드래그 → 구간 선택
  - 클릭 → 재생 위치(커서) 이동
- **구간 잘라내기** — 선택 구간을 삭제하고 뒤쪽을 앞으로 당겨 붙임(파괴적 편집)
- **실행 취소** — `Ctrl+Z` (스택 기반, 여러 번 가능)
- **미리듣기 재생** — 재생/일시정지/정지, 이동하는 재생 위치 커서
  - 구간 안에서 재생하면 구간 끝에서 자동 정지, 구간 밖이면 끝까지 재생
- **내보내기** — 편집된 전체 오디오를 m4a(AAC) / mp3 / wav 로 저장

## 요구 사항

- Windows (오디오 디코딩/인코딩이 **Media Foundation**에 의존)
  - Windows N 에디션은 [Media Feature Pack](https://support.microsoft.com/help/3145500)이 필요할 수 있습니다.
- [.NET 8 SDK](https://dotnet.microsoft.com/download)

## 빌드 & 실행

```powershell
# 빌드
dotnet build WpfMusicEditor.sln

# 실행
dotnet run --project WpfMusicEditor
```

또는 Visual Studio / Rider에서 `WpfMusicEditor.sln`을 열고 `WpfMusicEditor`를 시작
프로젝트로 실행합니다.

## 사용법

1. **파일 열기**로 오디오 파일을 불러옵니다.
2. 파형을 **드래그**해 편집할 구간을 선택합니다.
3. **✂ 구간 잘라내기**로 해당 구간을 삭제합니다(뒤가 당겨옵니다).
4. 잘못 잘랐으면 **Ctrl+Z**(또는 ↶ 실행취소)로 되돌립니다.
5. **▶ 재생**으로 결과를 미리듣습니다(파형 클릭으로 위치 점프).
6. 출력 포맷을 고르고 **내보내기**로 저장합니다.

## 프로젝트 구조

```
WpfMusicEditor.sln
├─ WpfMusicEditor/          진입점(App), DI 구성
├─ WpfMusicEditor.Forms/    뷰(MainWindow) + 뷰모델 + 테마(XAML)
├─ WpfMusicEditor.Main/     오디오 코어
│   └─ Audio/
│       ├─ AudioDocument       메모리 PCM 문서, Cut/Undo
│       ├─ AudioPlayer         WaveOutEvent 기반 재생기
│       ├─ MemorySampleProvider 임의 위치 탐색 가능한 샘플 공급자
│       ├─ NAudioEditor        디코딩/인코딩(IAudioEditor 구현)
│       └─ AudioFormat         출력 포맷
└─ WpfMusicEditor.Support/  공용 커스텀 컨트롤(창 크롬, WaveformControl)
```

- 아키텍처: 커스텀 컨트롤 + `Themes/Generic.xaml` 템플릿, DI(`Microsoft.Extensions.Hosting`),
  MVVM(`CommunityToolkit.Mvvm`).

## 알려진 한계

- 잘라낸 경계에서 미세한 클릭음(팝 노이즈)이 날 수 있습니다(샘플을 바로 이어 붙이므로).
  추후 경계 크로스페이드로 개선 가능.
- 편집 중 오디오 전체를 메모리에 PCM으로 들고 있으므로(예: 4분 스테레오 ≈ 80MB)
  매우 긴 파일에는 메모리 사용량이 큽니다.
