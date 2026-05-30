"""
RVC 학습 단일 진입점 드라이버 — 앱(WpfMusicEditor)이 이 스크립트 하나만 호출한다.

데이터셋(wav 폴더) → preprocess → f0(rmvpe) → feature(v2 768) → train → ONNX export
까지를 RVC 저장소의 스크립트들을 순서대로 구동해 처리하고, 진행 상황을 stdout에
'@@' 마커로 출력한다(앱이 정규식으로 파싱).

마커:
  @@STAGE <name>      단계 시작 (preprocess/f0/feature/train/export)
  @@EPOCH <n>/<N>     학습 epoch 진행
  @@DONE <path>       최종 .onnx 경로
  @@ERROR <msg>       실패

사용:
  python rvc_pipeline.py --rvc-root <RVC repo> --dataset <wav dir> --name <exp>
      --out <models/name.onnx> [--sr 40k] [--version v2] [--epochs 150] [--batch 7]

이 스크립트는 RVC 저장소(--rvc-root)를 cwd로 삼아 실행된다. 실행 파이썬은 RVC venv의
python.exe 여야 한다(torch/fairseq 등 설치된 환경).
"""

import argparse
import os
import re
import shutil
import subprocess
import sys
from random import shuffle

# 한국어 Windows 콘솔(cp949)에서도 깨지지 않도록 출력은 utf-8로 고정한다.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass


def emit(marker, *parts):
    print(marker, *parts, flush=True)


def run(cmd, cwd):
    """RVC 하위 스크립트를 실행하며 출력을 실시간으로 흘려보낸다. 0이 아니면 예외."""
    emit("@@RUN", " ".join(cmd))
    # 자식 프로세스도 utf-8로 출력하게 해 디코딩 깨짐을 막는다.
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    proc = subprocess.Popen(
        cmd, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        text=True, encoding="utf-8", errors="replace", bufsize=1, env=env,
    )
    epoch_re = re.compile(r"[Ee]poch:?\s*(\d+)")
    for line in proc.stdout:
        line = line.rstrip()
        if not line:
            continue
        print("  ", line, flush=True)
        m = epoch_re.search(line)
        if m:
            emit("@@EPOCH", m.group(1) + "/" + str(TOTAL_EPOCHS[0]))
    proc.wait()
    if proc.returncode != 0:
        raise RuntimeError("단계 실패 (exit %d): %s" % (proc.returncode, " ".join(cmd)))


TOTAL_EPOCHS = [0]  # run()에서 @@EPOCH n/N 출력용


def build_filelist(now, logs, srn, version, spk=0):
    """RVC WebUI click_train의 filelist 생성 로직을 그대로 옮긴다(v2/f0 기준)."""
    gt = os.path.join(logs, "0_gt_wavs")
    feat = os.path.join(logs, "3_feature768" if version == "v2" else "3_feature256")
    f0d = os.path.join(logs, "2a_f0")
    f0nsf = os.path.join(logs, "2b-f0nsf")
    names = (
        set(n.split(".")[0] for n in os.listdir(gt))
        & set(n.split(".")[0] for n in os.listdir(feat))
        & set(n.split(".")[0] for n in os.listdir(f0d))
        & set(n.split(".")[0] for n in os.listdir(f0nsf))
    )
    fea_dim = 768 if version == "v2" else 256
    opt = []
    for name in names:
        opt.append(
            "%s/%s.wav|%s/%s.npy|%s/%s.wav.npy|%s/%s.wav.npy|%s"
            % (gt, name, feat, name, f0d, name, f0nsf, name, spk)
        )
    # 무음(mute) 샘플 2개를 끼워 넣어 안정화(WebUI click_train과 동일).
    mute = os.path.join(now, "logs", "mute")
    for _ in range(2):
        opt.append(
            "%s/0_gt_wavs/mute%dk.wav|%s/3_feature%d/mute.npy|%s/2a_f0/mute.wav.npy|%s/2b-f0nsf/mute.wav.npy|%s"
            % (mute, srn // 1000, mute, fea_dim, mute, mute, spk)
        )
    shuffle(opt)
    with open(os.path.join(logs, "filelist.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(opt))


def export_onnx(rvc_root, pth_path, out_path, version):
    """학습된 net_g(.pth)를 ONNX로 export. RVC 자체 export 모듈을 그대로 쓴다
    (vec_channels·version·dynamic_axes를 RVC 버전에 맞게 정확히 처리)."""
    sys.path.insert(0, rvc_root)
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    from infer.modules.onnx.export import export_onnx as rvc_export_onnx
    rvc_export_onnx(pth_path, out_path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rvc-root", required=True)
    ap.add_argument("--dataset", default=None)   # 학습 모드에서 필요
    ap.add_argument("--name", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--sr", default="40k")
    ap.add_argument("--version", default="v2")
    ap.add_argument("--epochs", type=int, default=150)
    ap.add_argument("--batch", type=int, default=7)
    ap.add_argument("--pth", default=None)        # 지정 시 학습을 건너뛰고 이 .pth만 ONNX로 export
    args = ap.parse_args()

    now = os.path.abspath(args.rvc_root)
    os.chdir(now)
    py = sys.executable
    exp = args.name
    sr2 = args.sr
    srn = {"32k": 32000, "40k": 40000, "48k": 48000}[sr2]
    ver = args.version
    logs = os.path.join(now, "logs", exp)
    TOTAL_EPOCHS[0] = args.epochs

    # ── export 전용 모드: 받은 .pth를 학습 없이 ONNX로 변환 ──
    if args.pth:
        try:
            emit("@@STAGE", "export")
            export_onnx(now, args.pth, args.out, ver)
            emit("@@DONE", args.out)
        except Exception as e:
            emit("@@ERROR", str(e))
            raise
        return

    os.makedirs(logs, exist_ok=True)
    if not args.dataset:
        emit("@@ERROR", "학습에는 --dataset 이 필요합니다.")
        raise SystemExit(2)

    try:
        emit("@@STAGE", "preprocess")
        run([py, "infer/modules/train/preprocess.py", args.dataset, str(srn), "2",
             logs, "False", "3.0"], now)

        emit("@@STAGE", "f0")
        run([py, "infer/modules/train/extract/extract_f0_rmvpe.py", "1", "0", "0",
             logs, "False"], now)

        emit("@@STAGE", "feature")
        run([py, "infer/modules/train/extract_feature_print.py", "cuda:0", "1", "0", "0",
             logs, ver, "False"], now)

        emit("@@STAGE", "filelist")
        build_filelist(now, logs, srn, ver, spk=0)
        cfg = os.path.join(now, "configs", "v1", "%s.json" % sr2)
        shutil.copy(cfg, os.path.join(logs, "config.json"))

        emit("@@STAGE", "train")
        pg = "assets/pretrained_v2/f0G%s.pth" % sr2
        pd = "assets/pretrained_v2/f0D%s.pth" % sr2
        run([py, "infer/modules/train/train.py", "-e", exp, "-sr", sr2, "-f0", "1",
             "-bs", str(args.batch), "-g", "0", "-te", str(args.epochs),
             "-se", str(args.epochs), "-pg", pg, "-pd", pd,
             "-l", "0", "-c", "0", "-sw", "1", "-v", ver], now)

        emit("@@STAGE", "export")
        pth = os.path.join(now, "assets", "weights", "%s.pth" % exp)
        if not os.path.exists(pth):
            # 혹시 이름이 다르면 가장 최근 weights를 집는다.
            wdir = os.path.join(now, "assets", "weights")
            cands = [os.path.join(wdir, f) for f in os.listdir(wdir) if exp in f and f.endswith(".pth")]
            if not cands:
                raise RuntimeError("학습 결과 .pth를 찾지 못했습니다: %s" % wdir)
            pth = max(cands, key=os.path.getmtime)
        export_onnx(now, pth, args.out, ver)

        emit("@@DONE", args.out)
    except Exception as e:
        emit("@@ERROR", str(e))
        raise


if __name__ == "__main__":
    main()
