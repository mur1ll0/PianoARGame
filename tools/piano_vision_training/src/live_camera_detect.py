"""
live_camera_detect.py — Detecção de teclado de piano em tempo real via webcam.

Abre a câmera padrão (ou a indicada por --camera), executa o modelo ONNX a cada
frame e desenha em verde a área detectada com a confiança ao lado.

Pressione Q ou ESC para fechar.

Exemplo de uso:
    python src/live_camera_detect.py --model runs/segment/runs/piano_detector_candidateB_seg_unity_focus/weights/best.onnx

Para especificar outra câmera (índice ou caminho de vídeo):
    python src/live_camera_detect.py --model <onnx> --camera 1
"""
import argparse
import os
import site
import time
from pathlib import Path
from typing import List, Optional, Tuple

import cv2
import numpy as np


# ── CUDA DLL helper (Windows) ──────────────────────────────────────────────────

def _configure_windows_cuda_dll_paths() -> None:
    if os.name != "nt" or not hasattr(os, "add_dll_directory"):
        return
    candidates = [
        Path(r"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.6\bin"),
        Path(r"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8\bin"),
        Path(r"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.9\bin"),
    ]
    for site_path in (site.getusersitepackages(), *site.getsitepackages()):
        site_dir = Path(site_path)
        candidates.append(site_dir / "nvidia" / "cublas" / "bin")
        candidates.append(site_dir / "nvidia" / "cudnn" / "bin")
        candidates.append(site_dir / "nvidia" / "cuda_nvrtc" / "bin")
    for path in candidates:
        if path.exists():
            try:
                os.add_dll_directory(str(path))
            except OSError:
                pass


_configure_windows_cuda_dll_paths()
import onnxruntime as ort  # noqa: E402 (must come after DLL path setup)


# ── Tipos ──────────────────────────────────────────────────────────────────────

Detection = Tuple[float, float, float, float, float]  # x1, y1, x2, y2, score


# ── Pré-processamento ──────────────────────────────────────────────────────────

def preprocess(frame_bgr: np.ndarray, input_size: int) -> np.ndarray:
    """Redimensiona e normaliza o frame para tensor NCHW float32 [0,1]."""
    rgb = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2RGB)
    resized = cv2.resize(rgb, (input_size, input_size), interpolation=cv2.INTER_LINEAR)
    tensor = resized.astype(np.float32) / 255.0
    tensor = np.transpose(tensor, (2, 0, 1))          # HWC → CHW
    return np.expand_dims(tensor, axis=0)              # CHW → NCHW


# ── IoU / NMS ─────────────────────────────────────────────────────────────────

def _iou(a: Detection, b: Detection) -> float:
    x1, y1 = max(a[0], b[0]), max(a[1], b[1])
    x2, y2 = min(a[2], b[2]), min(a[3], b[3])
    inter = max(0.0, x2 - x1) * max(0.0, y2 - y1)
    if inter <= 0.0:
        return 0.0
    union = (a[2]-a[0])*(a[3]-a[1]) + (b[2]-b[0])*(b[3]-b[1]) - inter
    return 0.0 if union <= 0.0 else inter / union


def _nms(detections: List[Detection], iou_threshold: float) -> List[Detection]:
    ordered = sorted(detections, key=lambda d: d[4], reverse=True)
    kept: List[Detection] = []
    for det in ordered:
        if not any(_iou(det, k) > iou_threshold for k in kept):
            kept.append(det)
    return kept


# ── Conversão de caixa ────────────────────────────────────────────────────────

def _cx_cy_wh_to_xyxy(
    cx: float, cy: float, bw: float, bh: float,
    image_w: int, image_h: int, input_size: int,
) -> Tuple[float, float, float, float]:
    normalized = all(abs(v) <= 2.0 for v in (cx, cy, bw, bh))
    sx = image_w if normalized else image_w / float(max(1, input_size))
    sy = image_h if normalized else image_h / float(max(1, input_size))
    cx, cy = cx * sx, cy * sy
    bw, bh = abs(bw * sx), abs(bh * sy)
    x1 = max(0.0, min(image_w - 1.0, cx - bw * 0.5))
    y1 = max(0.0, min(image_h - 1.0, cy - bh * 0.5))
    x2 = max(x1 + 1.0, min(float(image_w), cx + bw * 0.5))
    y2 = max(y1 + 1.0, min(float(image_h), cy + bh * 0.5))
    return x1, y1, x2, y2


# ── Decodificação da saída ONNX ───────────────────────────────────────────────

def decode(
    output: np.ndarray,
    image_w: int,
    image_h: int,
    input_size: int,
    num_classes: int,
    conf_threshold: float,
    iou_threshold: float,
) -> List[Detection]:
    """Suporta shape [1, features, candidates] ou [1, candidates, features]."""
    if output.ndim != 3 or output.shape[0] != 1:
        return []

    dim1, dim2 = output.shape[1], output.shape[2]
    features_first = dim1 <= 256 and dim2 > dim1
    n_candidates = dim2 if features_first else dim1
    n_features   = dim1 if features_first else dim2
    if n_features < 4 + num_classes:
        return []

    data = output.reshape(-1)

    def val(c: int, f: int) -> float:
        idx = (f * n_candidates + c) if features_first else (c * n_features + f)
        return float(data[idx])

    detections: List[Detection] = []
    for c in range(n_candidates):
        cx, cy, bw, bh = val(c, 0), val(c, 1), val(c, 2), val(c, 3)
        best = max(val(c, 4 + cls) for cls in range(num_classes))
        if best < conf_threshold:
            continue
        x1, y1, x2, y2 = _cx_cy_wh_to_xyxy(cx, cy, bw, bh, image_w, image_h, input_size)
        if (x2 - x1) > 1.0 and (y2 - y1) > 1.0:
            detections.append((x1, y1, x2, y2, best))

    return _nms(detections, iou_threshold)


# ── Desenho ───────────────────────────────────────────────────────────────────

_GREEN       = (0, 220, 50)
_GREEN_DARK  = (0, 150, 30)
_WHITE       = (255, 255, 255)
_BLACK       = (0, 0, 0)
_FONT        = cv2.FONT_HERSHEY_SIMPLEX
_BOX_THICK   = 2
_LABEL_SCALE = 0.65
_LABEL_THICK = 2


def draw(frame_bgr: np.ndarray, detections: List[Detection]) -> np.ndarray:
    out = frame_bgr.copy()
    for x1, y1, x2, y2, score in detections:
        p1 = (int(round(x1)), int(round(y1)))
        p2 = (int(round(x2)), int(round(y2)))

        # Retângulo verde
        cv2.rectangle(out, p1, p2, _GREEN, _BOX_THICK)

        # Rótulo: "keyboard_area  87%"
        label = f"keyboard_area  {score * 100:.1f}%"
        (tw, th), baseline = cv2.getTextSize(label, _FONT, _LABEL_SCALE, _LABEL_THICK)
        lx = p1[0]
        ly = max(th + baseline + 2, p1[1] - 6)

        # Fundo escuro para legibilidade
        cv2.rectangle(
            out,
            (lx - 2, ly - th - baseline - 2),
            (lx + tw + 2, ly + baseline),
            _GREEN_DARK,
            cv2.FILLED,
        )
        cv2.putText(out, label, (lx, ly - baseline), _FONT, _LABEL_SCALE, _WHITE, _LABEL_THICK, cv2.LINE_AA)

    return out


# ── Overlay de status (FPS, confiança, sem detecção) ─────────────────────────

def draw_status(frame_bgr: np.ndarray, fps: float, best_conf: Optional[float]) -> None:
    lines = [f"FPS: {fps:.1f}"]
    if best_conf is not None:
        lines.append(f"Melhor conf: {best_conf * 100:.1f}%")
    else:
        lines.append("Sem deteccao")

    y = 28
    for line in lines:
        cv2.putText(frame_bgr, line, (10, y), _FONT, 0.7, _BLACK, 3, cv2.LINE_AA)
        cv2.putText(frame_bgr, line, (10, y), _FONT, 0.7, _WHITE, 2, cv2.LINE_AA)
        y += 28


# ── Seleção do tensor de detecção (YOLOv8 pode ter múltiplas saídas) ──────────

def _pick_detection_output(raw_outputs: List[np.ndarray]) -> Optional[np.ndarray]:
    """Retorna o tensor 3D compatível com YOLOv8 (shape [1, C, N] ou [1, N, C])."""
    for out in raw_outputs:
        if out.ndim == 3 and out.shape[0] == 1:
            dim1, dim2 = out.shape[1], out.shape[2]
            features_first = dim1 <= 256 and dim2 > dim1
            n_features = dim1 if features_first else dim2
            if n_features >= 5:
                return out
    return None


# ── Main ──────────────────────────────────────────────────────────────────────

def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(
        description="Detecção ao vivo de teclado de piano usando modelo ONNX."
    )
    p.add_argument(
        "--model",
        required=True,
        help="Caminho para o arquivo .onnx exportado.",
    )
    p.add_argument(
        "--camera",
        default=0,
        help="Índice (inteiro) ou caminho do dispositivo de vídeo. Padrão: 0.",
    )
    p.add_argument("--size",        type=int,   default=640,  help="Tamanho do input do modelo (padrão: 640).")
    p.add_argument("--num-classes", type=int,   default=1,    help="Número de classes do modelo (padrão: 1).")
    p.add_argument("--conf",        type=float, default=0.25, help="Limiar de confiança (padrão: 0.25).")
    p.add_argument("--iou",         type=float, default=0.45, help="Limiar IoU para NMS (padrão: 0.45).")
    p.add_argument("--width",       type=int,   default=1280, help="Largura da câmera solicitada (padrão: 1280).")
    p.add_argument("--height",      type=int,   default=720,  help="Altura da câmera solicitada (padrão: 720).")
    return p.parse_args()


def main() -> None:
    args = parse_args()

    model_path = Path(args.model)
    if not model_path.exists():
        raise FileNotFoundError(f"Modelo não encontrado: {model_path}")

    # Tenta converter o argumento --camera para inteiro (índice de câmera)
    camera_src = args.camera
    try:
        camera_src = int(camera_src)
    except (ValueError, TypeError):
        pass  # mantém como string (caminho de vídeo)

    print(f"[live_camera_detect] Carregando modelo: {model_path}")
    providers = ["CUDAExecutionProvider", "CPUExecutionProvider"]
    session = ort.InferenceSession(str(model_path), providers=providers)
    input_name   = session.get_inputs()[0].name
    output_names = [o.name for o in session.get_outputs()]
    print(f"  Providers ativos : {session.get_providers()}")
    print(f"  Input             : {input_name}")
    print(f"  Outputs           : {output_names}")

    print(f"[live_camera_detect] Abrindo câmera: {camera_src}")
    cap = cv2.VideoCapture(camera_src)
    if not cap.isOpened():
        raise RuntimeError(f"Não foi possível abrir a câmera: {camera_src}")

    cap.set(cv2.CAP_PROP_FRAME_WIDTH,  args.width)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, args.height)

    actual_w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    actual_h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    print(f"  Resolução real    : {actual_w}×{actual_h}")
    print("Pressione Q ou ESC para fechar.")

    window_title = "Piano Detector — Q/ESC para sair"
    cv2.namedWindow(window_title, cv2.WINDOW_NORMAL)

    fps_t0      = time.perf_counter()
    fps_frames  = 0
    current_fps = 0.0

    while True:
        ret, frame = cap.read()
        if not ret:
            print("[live_camera_detect] Falha ao ler frame da câmera.")
            break

        h, w = frame.shape[:2]

        # ── Inferência ──────────────────────────────────────────────────────
        tensor       = preprocess(frame, args.size)
        raw_outputs  = session.run(output_names, {input_name: tensor})
        det_output   = _pick_detection_output(raw_outputs)

        detections: List[Detection] = []
        if det_output is not None:
            detections = decode(
                output=det_output,
                image_w=w,
                image_h=h,
                input_size=args.size,
                num_classes=args.num_classes,
                conf_threshold=args.conf,
                iou_threshold=args.iou,
            )

        # ── Desenho ─────────────────────────────────────────────────────────
        rendered = draw(frame, detections)

        best_conf = max((d[4] for d in detections), default=None)
        draw_status(rendered, current_fps, best_conf)

        cv2.imshow(window_title, rendered)

        # ── FPS ─────────────────────────────────────────────────────────────
        fps_frames += 1
        elapsed = time.perf_counter() - fps_t0
        if elapsed >= 0.5:
            current_fps = fps_frames / elapsed
            fps_frames  = 0
            fps_t0      = time.perf_counter()

        # ── Teclas ──────────────────────────────────────────────────────────
        key = cv2.waitKey(1) & 0xFF
        if key in (ord("q"), ord("Q"), 27):  # 27 = ESC
            break

    cap.release()
    cv2.destroyAllWindows()
    print("[live_camera_detect] Encerrado.")


if __name__ == "__main__":
    main()
