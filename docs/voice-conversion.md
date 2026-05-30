# 음색 변환 (Voice Conversion)

선택한 구간의 **발음·내용·타이밍은 그대로 두고, 음색만 타깃 목소리로** 바꾸는 기능입니다.
(STT/TTS가 아니라 음성→음성 변환이며, RVC 모델을 사용합니다.)

앱은 **추론(.onnx)만** 순수 .NET(ONNX Runtime)으로 돌립니다. 모델을 **만드는 과정만 한 번 Python으로** 하면 됩니다.

---

## 한눈에 보는 흐름

1. (1회) 타깃 목소리로 RVC 모델 학습 → `.pth`
2. (1회) `.pth` → **ONNX export** → `<voice>.onnx`
3. 공유 모델 `contentvec.onnx`는 앱이 자동 다운로드
4. 앱에서 구간 선택 → **📂 모델…** 로 `<voice>.onnx` 선택 → **🎤 음색 변환**

> **바로 테스트하려면**: 학습 없이 쓸 수 있는 사전학습 제너레이터 `GuraTalkV2.onnx`를
> 이미 `%LOCALAPPDATA%\WpfMusicEditor\models\` 에 내려받아 뒀습니다.
> 앱에서 **📂 모델…** → 그 파일 선택 → 음성 구간에 **🎤 음색 변환** 하면 됩니다.

---

## 1. 공유 모델 (발음 특징, 모든 음색 공통)

`contentvec.onnx`(ContentVec v2, 768차원) 하나만 있으면 됩니다. **앱이 최초 1회 자동 다운로드**합니다(처음 변환 시 진행률 표시). 저장 위치:

```
%LOCALAPPDATA%\WpfMusicEditor\models\
```

| 파일명 | 역할 | 자동 다운로드 출처 |
|---|---|---|
| `contentvec.onnx` | 발음/내용 특징 추출(ContentVec v2, 768) | `DogManTC/test-rvc-onnx` (`vec-768-layer-12.onnx`) |

> 음높이(f0)는 별도 모델 없이 앱 내장 **YIN 추정기**(순수 C#)로 뽑습니다(RMVPE.onnx 불필요).
> 망 제한 등으로 자동 다운로드가 막히면, 위 파일을 직접 받아 같은 폴더에
> **정확히 `contentvec.onnx`** 라는 이름으로 넣으면 됩니다(아래 \"부록: 수동 다운로드\").

---

## 앱에서 바로 학습하기 (🎓 모델 만들기)

메인 화면의 **🎓 모델 만들기** 버튼을 누르면 학습 창이 열립니다. 앱이 외부 RVC(Python) 프로세스를
구동해 데이터 준비→학습→ONNX 변환→모델 등록까지 자동으로 합니다.

**준비물(1회):** NVIDIA GPU(CUDA) + RVC 환경. 가장 쉬운 방법은 공식 프리빌트 패키지입니다:
- `RVC20240604Nvidia.7z`를 받아 압축을 풀면 `runtime\python.exe`와 전체 RVC가 들어 있습니다
  (HuggingFace `lj1995/VoiceConversionWebUI`). fairseq 등 까다로운 의존성이 이미 포함돼 있습니다.

**학습 창 사용:**
1. **RVC 환경**: RVC 폴더와 그 안의 `runtime\python.exe`를 지정(한 번만, 저장됨).
2. **데이터셋**: 내 녹음 파일을 **오디오 불러오기** → 깨끗한 구간을 **구간 추가**(또는 **전체 추가**).
   단일 화자, 무잡음/무리버브, **총 5~30분** 권장.
3. **학습 설정**: 모델 이름, epoch(기본 150), batch(8GB 기준 7).
4. **🎓 학습 시작** → 진행률 표시(전처리→f0→특징→학습→ONNX). 끝나면 모델이 자동 등록되고
   메인 창의 모델로 선택됩니다 → 바로 **🎤 음색 변환**.

> 학습은 데이터·epoch에 따라 **수 분~수 시간** 걸립니다. **취소**로 중단할 수 있습니다.
> 앱이 만든 모델은 공식 export(동적 길이)라 변환 시 청크가 필요 없습니다.

---

## (수동) 타깃 목소리 학습 → `.pth` (Python + GPU, 1회)

학습은 RVC가 PyTorch 기반이라 Python이 필요합니다. **추론은 Python이 필요 없습니다.**

1. [RVC-Project/Retrieval-based-Voice-Conversion-WebUI](https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI) 설치
   (Python 3.10, CUDA 지원 PyTorch).
2. 타깃 목소리 **10~30분** 준비:
   - 단일 화자, **잡음·리버브 없는** 깨끗한 녹음, 16kHz 이상.
   - 노래보다 말소리가 안정적입니다(노래면 so-vits-svc도 고려).
3. WebUI에서 데이터 전처리 → 특징 추출(**f0 방식은 rmvpe 권장**) → 학습 실행.
4. 결과로 `net_g` 가중치(`G_xxxx.pth` / `<name>.pth`)가 생성됩니다.

> 빠른 검증만 하려면 짧게 학습한 모델로 먼저 4단계(Export)까지 진행해 동작을 확인해도 됩니다.

---

## 3. `.pth` → ONNX export (1회)

RVC WebUI의 **Export ONNX** 탭에서:

- **ModelPath**: 위에서 만든 `.pth`
- **ExportedPath**: 저장할 `<voice>.onnx`
- Export 실행 → `<voice>.onnx` 생성

(스크립트로는 `infer/modules/onnx/export.py` 사용)

생성된 모델은 **f0 조건부 모델**이며 입력은 `feats / p_len / pitch / pitchf / sid` 형태입니다.
앱은 입력 이름·dtype(fp16/fp32)을 **모델에서 자동으로 읽어** 맞춥니다.

> **샘플레이트 주의**: RVC 모델은 32k/40k/48k 중 하나로 학습됩니다.
> ONNX 메타데이터에 `sr`이 있으면 자동 사용하고, 없으면 앱 기본값(40000)을 씁니다.
> 모델 SR이 40k가 아니라면 출력 음높이/속도가 어긋날 수 있으니
> `RvcVoiceConverter.DefaultModelSampleRate` 를 모델 SR에 맞추세요.

---

## 4. 앱에서 사용

1. 오디오 파일 열기 → 파형에서 **변환할 구간 드래그**.
2. **📂 모델…** 버튼으로 `<voice>.onnx` 선택.
3. (선택) **반음**: 피치 시프트. 0이면 원래 음높이 유지. 예) 남→여 변환은 `+12` 등.
4. **🎤 음색 변환** 클릭 → 백그라운드 추론(진행바 표시) → 구간이 타깃 음색으로 교체됩니다.
5. 마음에 안 들면 **Ctrl+Z**(실행 취소)로 원복.

---

## 동작 원리(요약)

```
선택 구간 PCM
  → 16kHz 모노 리샘플
  → ContentVec(contentvec.onnx): 발음/내용 특징 [T,768]
  → YIN(앱 내장): 음높이 f0 → (반음 시프트) → coarse 양자화
  → Generator(<voice>.onnx): 특징+f0 → 타깃 음색 파형
  → 원본 샘플레이트/채널로 리샘플 → 구간 교체(Undo 가능)
```

GPU가 있으면 **DirectML**(NVIDIA/AMD/Intel 공통)로 가속하고, 없으면 CPU로 폴백합니다.

## 고정 길이 export 모델 (자동 청크)

RVC 공식 **Export ONNX**(dynamic_axes 적용)로 만든 모델은 임의 길이로 한 번에 처리됩니다.
일부 모델은 어텐션이 **고정 길이**로 굳어 export되는데(예: `GuraTalkV2.onnx`는 200프레임=2초),
앱이 첫 추론에서 그 길이를 **자동 감지해 청크 단위로** 변환합니다.

## 알려진 제약 / 후속

- 고정 길이 모델은 청크 경계(약 2초마다)에서 **미세한 끊김**이 있을 수 있습니다(크로스페이드는 후속).
- **retrieval index(faiss) 미적용**: 음색 닮음이 약간 낮을 수 있습니다(후속 추가 예정).
- f0는 YIN 추정기 사용. 더 높은 정확도가 필요하면 RMVPE 연동을 추가할 수 있습니다.
- 출력 샘플레이트 기본값은 40kHz. 모델 SR이 다르면 `RvcVoiceConverter.DefaultModelSampleRate`를 맞추세요.
- 타인의 목소리 복제·변환은 **본인 동의**가 필요합니다(딥페이크 음성 규제 대상). 본인 또는 동의받은 목소리에만 사용하세요.

---

## 부록: 수동 다운로드

자동 다운로드가 막힐 때만 사용하세요. PowerShell:

```powershell
$dir = "$env:LOCALAPPDATA\WpfMusicEditor\models"
New-Item -ItemType Directory -Force $dir | Out-Null
# 공유 모델(발음 특징)
Invoke-WebRequest "https://huggingface.co/DogManTC/test-rvc-onnx/resolve/main/vec-768-layer-12.onnx" -OutFile "$dir\contentvec.onnx"
# (선택) 학습 없이 바로 테스트할 사전학습 제너레이터
Invoke-WebRequest "https://huggingface.co/DogManTC/test-rvc-onnx/resolve/main/GuraTalkV2.onnx" -OutFile "$dir\GuraTalkV2.onnx"
```
