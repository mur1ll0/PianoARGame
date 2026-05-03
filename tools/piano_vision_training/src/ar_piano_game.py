"""
AR Piano Game (Python 3.13)

Fluxo:
1) Lista arquivos MIDI de uma pasta e permite selecionar com clique.
2) Fase de alinhamento: detecta area do teclado com ONNX, estabiliza por tracking temporal.
3) Fase de jogo: notas MIDI caem em overlay na area detectada, apontando a tecla e tempo corretos.

Controles:
- Mouse: selecionar musica e clicar botoes na UI.
- Teclas:
  - ESC / Q: sair
  - R: voltar para selecao de musica
"""

from __future__ import annotations

import argparse
import math
import os
import site
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional, Tuple

import cv2
import mido
import numpy as np


# ---- CUDA DLL helper (Windows) -------------------------------------------------

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
import onnxruntime as ort  # noqa: E402


Detection = Tuple[float, float, float, float, float]  # x1, y1, x2, y2, score
Rect = Tuple[int, int, int, int]


@dataclass
class MidiNoteEvent:
    pitch: int
    start: float
    end: float
    velocity: int
    hand: str = "R"


# ---- Model preprocessing / decode ----------------------------------------------

def preprocess(frame_bgr: np.ndarray, input_w: int, input_h: int) -> np.ndarray:
    rgb = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2RGB)
    resized = cv2.resize(rgb, (input_w, input_h), interpolation=cv2.INTER_LINEAR)
    tensor = resized.astype(np.float32) / 255.0
    tensor = np.transpose(tensor, (2, 0, 1))
    return np.expand_dims(tensor, axis=0)


def _iou(a: Detection, b: Detection) -> float:
    x1, y1 = max(a[0], b[0]), max(a[1], b[1])
    x2, y2 = min(a[2], b[2]), min(a[3], b[3])
    inter = max(0.0, x2 - x1) * max(0.0, y2 - y1)
    if inter <= 0.0:
        return 0.0
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return 0.0 if union <= 0.0 else inter / union


def _nms(detections: List[Detection], iou_threshold: float) -> List[Detection]:
    ordered = sorted(detections, key=lambda d: d[4], reverse=True)
    kept: List[Detection] = []
    for det in ordered:
        if not any(_iou(det, k) > iou_threshold for k in kept):
            kept.append(det)
    return kept


def _cx_cy_wh_to_xyxy(
    cx: float,
    cy: float,
    bw: float,
    bh: float,
    image_w: int,
    image_h: int,
    input_w: int,
    input_h: int,
) -> Tuple[float, float, float, float]:
    normalized = all(abs(v) <= 2.0 for v in (cx, cy, bw, bh))
    sx = image_w if normalized else image_w / float(max(1, input_w))
    sy = image_h if normalized else image_h / float(max(1, input_h))
    cx, cy = cx * sx, cy * sy
    bw, bh = abs(bw * sx), abs(bh * sy)
    x1 = max(0.0, min(image_w - 1.0, cx - bw * 0.5))
    y1 = max(0.0, min(image_h - 1.0, cy - bh * 0.5))
    x2 = max(x1 + 1.0, min(float(image_w), cx + bw * 0.5))
    y2 = max(y1 + 1.0, min(float(image_h), cy + bh * 0.5))
    return x1, y1, x2, y2


def decode(
    output: np.ndarray,
    image_w: int,
    image_h: int,
    input_w: int,
    input_h: int,
    num_classes: int,
    conf_threshold: float,
    iou_threshold: float,
) -> List[Detection]:
    if output.ndim != 3 or output.shape[0] != 1:
        return []

    dim1, dim2 = output.shape[1], output.shape[2]
    features_first = dim1 <= 256 and dim2 > dim1
    n_candidates = dim2 if features_first else dim1
    n_features = dim1 if features_first else dim2
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
        x1, y1, x2, y2 = _cx_cy_wh_to_xyxy(cx, cy, bw, bh, image_w, image_h, input_w, input_h)
        if (x2 - x1) > 1.0 and (y2 - y1) > 1.0:
            detections.append((x1, y1, x2, y2, best))

    return _nms(detections, iou_threshold)


def _pick_detection_output(raw_outputs: List[np.ndarray]) -> Optional[np.ndarray]:
    for out in raw_outputs:
        if out.ndim == 3 and out.shape[0] == 1:
            dim1, dim2 = out.shape[1], out.shape[2]
            features_first = dim1 <= 256 and dim2 > dim1
            n_features = dim1 if features_first else dim2
            if n_features >= 5:
                return out
    return None


# ---- MIDI ----------------------------------------------------------------------

def load_midi_events(midi_path: Path) -> Tuple[List[MidiNoteEvent], float]:
    mid = mido.MidiFile(str(midi_path))
    active: Dict[Tuple[int, int], List[Tuple[float, int]]] = {}
    temp_events: List[Tuple[MidiNoteEvent, int]] = []
    channel_pitch_sum: Dict[int, float] = {}
    channel_pitch_count: Dict[int, int] = {}

    t = 0.0
    for msg in mid:
        t += float(msg.time)
        channel = int(getattr(msg, "channel", -1))

        if msg.type == "note_on" and msg.velocity > 0:
            key = (int(msg.note), channel)
            active.setdefault(key, []).append((t, int(msg.velocity)))
            if channel >= 0:
                channel_pitch_sum[channel] = channel_pitch_sum.get(channel, 0.0) + float(msg.note)
                channel_pitch_count[channel] = channel_pitch_count.get(channel, 0) + 1
            continue

        if msg.type in ("note_off", "note_on") and getattr(msg, "velocity", 0) == 0:
            key = (int(msg.note), channel)
            queue = active.get(key)
            if queue:
                start, vel = queue.pop(0)
                if t > start:
                    temp_events.append((MidiNoteEvent(pitch=int(msg.note), start=start, end=t, velocity=vel), channel))

    for (note, channel), note_list in active.items():
        for start, vel in note_list:
            temp_events.append((MidiNoteEvent(pitch=note, start=start, end=start + 0.25, velocity=vel), channel))

    channel_to_hand: Dict[int, str] = {}
    channel_avgs = [
        (ch, channel_pitch_sum[ch] / max(1, channel_pitch_count[ch]))
        for ch in channel_pitch_count
        if channel_pitch_count[ch] > 0
    ]
    if len(channel_avgs) >= 2:
        ordered = sorted(channel_avgs, key=lambda item: item[1])
        split_idx = len(ordered) // 2
        for idx, (ch, _avg_pitch) in enumerate(ordered):
            channel_to_hand[ch] = "L" if idx < split_idx else "R"

    events: List[MidiNoteEvent] = []
    for ev, channel in temp_events:
        if channel in channel_to_hand:
            ev.hand = channel_to_hand[channel]
        else:
            # Fallback por altura da nota quando nao ha separacao explicita por canais.
            ev.hand = "R" if ev.pitch >= 60 else "L"
        events.append(ev)

    events.sort(key=lambda e: e.start)
    duration = max((e.end for e in events), default=0.0)
    return events, duration


def list_midi_files(midi_dir: Path) -> List[Path]:
    if not midi_dir.exists() or not midi_dir.is_dir():
        return []
    files = [p for p in midi_dir.iterdir() if p.is_file() and p.suffix.lower() in {".mid", ".midi"}]
    files.sort(key=lambda p: p.name.lower())
    return files


# ---- Visual helpers -------------------------------------------------------------

def clamp(v: float, vmin: float, vmax: float) -> float:
    return max(vmin, min(vmax, v))


def lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


def smooth_rect(prev: Optional[Rect], new_det: Detection, alpha: float) -> Rect:
    nx1, ny1, nx2, ny2, _ = new_det
    if prev is None:
        return int(nx1), int(ny1), int(nx2), int(ny2)
    px1, py1, px2, py2 = prev
    return (
        int(lerp(px1, nx1, alpha)),
        int(lerp(py1, ny1, alpha)),
        int(lerp(px2, nx2, alpha)),
        int(lerp(py2, ny2, alpha)),
    )


def pitch_to_x(pitch: int, area: Rect) -> int:
    x1, _, x2, _ = area
    # Mapeamento linear para 88 teclas (A0=21 ate C8=108).
    norm = clamp((pitch - 21) / 87.0, 0.0, 1.0)
    return int(round(x1 + norm * max(1, (x2 - x1))))


def is_black_key(pitch: int) -> bool:
    return (pitch % 12) in {1, 3, 6, 8, 10}


def draw_text_with_box(
    img: np.ndarray,
    text: str,
    org: Tuple[int, int],
    scale: float = 0.65,
    text_color: Tuple[int, int, int] = (235, 235, 235),
    bg_color: Tuple[int, int, int] = (10, 10, 10),
    thickness: int = 2,
    pad: int = 6,
    alpha: float = 0.55,
) -> None:
    (tw, th), baseline = cv2.getTextSize(text, cv2.FONT_HERSHEY_SIMPLEX, scale, thickness)
    x, y = org
    x1 = max(0, x - pad)
    y1 = max(0, y - th - baseline - pad)
    x2 = min(img.shape[1] - 1, x + tw + pad)
    y2 = min(img.shape[0] - 1, y + baseline + max(2, pad // 2))

    overlay = img.copy()
    cv2.rectangle(overlay, (x1, y1), (x2, y2), bg_color, cv2.FILLED)
    cv2.addWeighted(overlay, alpha, img, 1.0 - alpha, 0.0, img)
    cv2.putText(img, text, (x, y), cv2.FONT_HERSHEY_SIMPLEX, scale, text_color, thickness, cv2.LINE_AA)


class Button:
    def __init__(self, rect: Rect, label: str) -> None:
        self.rect = rect
        self.label = label

    def contains(self, x: int, y: int) -> bool:
        x1, y1, x2, y2 = self.rect
        return x1 <= x <= x2 and y1 <= y <= y2

    def draw(self, img: np.ndarray, enabled: bool = True, active: bool = False) -> None:
        x1, y1, x2, y2 = self.rect
        base = (20, 140, 30) if enabled else (90, 90, 90)
        if active and enabled:
            base = (30, 170, 40)

        cv2.rectangle(img, (x1, y1), (x2, y2), base, cv2.FILLED)
        cv2.rectangle(img, (x1, y1), (x2, y2), (230, 230, 230), 1)

        scale = 0.8
        thickness = 2
        (tw, th), bl = cv2.getTextSize(self.label, cv2.FONT_HERSHEY_SIMPLEX, scale, thickness)
        tx = x1 + (x2 - x1 - tw) // 2
        ty = y1 + (y2 - y1 + th) // 2
        cv2.putText(img, self.label, (tx, ty), cv2.FONT_HERSHEY_SIMPLEX, scale, (240, 240, 240), thickness, cv2.LINE_AA)


class ARPianoGame:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.midi_dir = Path(args.midi_dir)
        self.midi_files = list_midi_files(self.midi_dir)

        self.state = "menu"  # menu | align | game | end
        self.selected_index = 0

        self.events: List[MidiNoteEvent] = []
        self.song_duration = 0.0
        self.song_name = ""

        self.game_start_time = 0.0
        self.countdown_seconds = float(args.countdown)
        self.travel_time = float(args.travel_time)
        self.song_speed = float(clamp(args.song_speed, 0.5, 2.0))

        self.last_frame_time = time.perf_counter()
        self.render_fps = 0.0
        self.render_fps_ema = 0.0

        self.detect_interval = int(args.detect_interval)
        self.frame_count = 0
        self.stable_hits = 0
        self.best_conf = 0.0
        self.keyboard_area: Optional[Rect] = None
        self.menu_scroll = 0
        self._menu_scroll_steps = 0

        self._mouse_click: Optional[Tuple[int, int]] = None
        self._mouse_wheel_delta = 0

        self.session = self._load_model(Path(args.model))
        self.input_name = self.session.get_inputs()[0].name
        self.output_names = [o.name for o in self.session.get_outputs()]
        self.model_input_w, self.model_input_h = self._resolve_model_input_size(self.session, int(args.size))

        self.cap = self._open_camera(args.camera, args.width, args.height, args.fps)
        self.real_cam_w = int(self.cap.get(cv2.CAP_PROP_FRAME_WIDTH))
        self.real_cam_h = int(self.cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
        self.real_cam_fps = float(self.cap.get(cv2.CAP_PROP_FPS))

        self.window_name = "AR Piano Game - Mouse para UI | Q/ESC sair"
        cv2.namedWindow(self.window_name, cv2.WINDOW_NORMAL)
        cv2.setMouseCallback(self.window_name, self._on_mouse)

        if getattr(args, "auto_start_first", False) and self.midi_files:
            self.selected_index = 0
            self._start_selected_song()

    @staticmethod
    def _load_model(model_path: Path) -> ort.InferenceSession:
        if not model_path.exists():
            raise FileNotFoundError(f"Modelo ONNX nao encontrado: {model_path}")
        providers = ["CUDAExecutionProvider", "CPUExecutionProvider"]
        return ort.InferenceSession(str(model_path), providers=providers)

    @staticmethod
    def _resolve_model_input_size(session: ort.InferenceSession, fallback_size: int) -> Tuple[int, int]:
        input_meta = session.get_inputs()[0]
        shape = list(input_meta.shape)

        if len(shape) >= 4:
            h = shape[2]
            w = shape[3]
            if isinstance(h, int) and isinstance(w, int) and h > 0 and w > 0:
                return int(w), int(h)

        return int(fallback_size), int(fallback_size)

    @staticmethod
    def _open_camera(camera: str, width: int, height: int, fps: int) -> cv2.VideoCapture:
        cam_src: object = camera
        try:
            cam_src = int(camera)
        except ValueError:
            cam_src = camera

        # CAP_DSHOW costuma ser mais estavel em webcam no Windows.
        cap = cv2.VideoCapture(cam_src, cv2.CAP_DSHOW)
        if not cap.isOpened():
            cap = cv2.VideoCapture(cam_src)
        if not cap.isOpened():
            raise RuntimeError(f"Nao foi possivel abrir a camera: {camera}")

        cap.set(cv2.CAP_PROP_FRAME_WIDTH, width)
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, height)
        cap.set(cv2.CAP_PROP_FPS, fps)
        cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
        return cap

    def _on_mouse(self, event: int, x: int, y: int, _flags: int, _userdata: object) -> None:
        if event == cv2.EVENT_LBUTTONDOWN:
            self._mouse_click = (x, y)
        elif event == cv2.EVENT_MOUSEWHEEL:
            self._mouse_wheel_delta += self._extract_wheel_delta(_flags)

    @staticmethod
    def _extract_wheel_delta(flags: int) -> int:
        if hasattr(cv2, "getMouseWheelDelta"):
            return int(cv2.getMouseWheelDelta(flags))

        # Fallback para builds sem getMouseWheelDelta: delta esta no high-word signed.
        high = (int(flags) >> 16) & 0xFFFF
        if high >= 0x8000:
            high -= 0x10000
        return int(high)

    def _consume_click(self) -> Optional[Tuple[int, int]]:
        click = self._mouse_click
        self._mouse_click = None
        return click

    def _consume_wheel_delta(self) -> int:
        delta = self._mouse_wheel_delta
        self._mouse_wheel_delta = 0
        return delta

    def _enqueue_menu_scroll(self, steps: int) -> None:
        self._menu_scroll_steps += int(steps)

    def _consume_menu_scroll(self) -> int:
        steps = self._menu_scroll_steps
        self._menu_scroll_steps = 0
        return int(steps)

    def _detect_keyboard(self, frame: np.ndarray) -> Optional[Detection]:
        tensor = preprocess(frame, self.model_input_w, self.model_input_h)
        raw_outputs = self.session.run(self.output_names, {self.input_name: tensor})
        det_output = _pick_detection_output(raw_outputs)
        if det_output is None:
            return None

        detections = decode(
            output=det_output,
            image_w=frame.shape[1],
            image_h=frame.shape[0],
            input_w=self.model_input_w,
            input_h=self.model_input_h,
            num_classes=self.args.num_classes,
            conf_threshold=self.args.conf,
            iou_threshold=self.args.iou,
        )
        if not detections:
            return None
        return max(detections, key=lambda d: d[4])

    def _update_tracker(self, frame: np.ndarray) -> None:
        self.frame_count += 1
        should_detect = self.keyboard_area is None or (self.frame_count % self.detect_interval == 0)

        if should_detect:
            best = self._detect_keyboard(frame)
            if best is not None:
                self.best_conf = float(best[4])
                self.keyboard_area = smooth_rect(self.keyboard_area, best, alpha=0.25)
                self.stable_hits = min(200, self.stable_hits + 1)
            else:
                self.stable_hits = max(0, self.stable_hits - 1)

        # Ajuste dinamico para manter desempenho acima de 30 FPS.
        if self.render_fps_ema < 28.0:
            self.detect_interval = min(12, self.detect_interval + 1)
        elif self.render_fps_ema > 40.0:
            self.detect_interval = max(3, self.detect_interval - 1)

    def _start_selected_song(self) -> None:
        if not self.midi_files:
            return
        midi_path = self.midi_files[self.selected_index]
        self.events, self.song_duration = load_midi_events(midi_path)
        self.song_name = midi_path.name

        if not self.events:
            self.song_duration = 1.0

        self.state = "align"
        self.stable_hits = 0
        self.best_conf = 0.0
        self.keyboard_area = None

    def _start_gameplay(self) -> None:
        self.state = "game"
        self.game_start_time = time.perf_counter()

    def _reset_to_menu(self) -> None:
        self.state = "menu"
        self.stable_hits = 0
        self.best_conf = 0.0
        self.keyboard_area = None

    def _draw_header(self, frame: np.ndarray) -> None:
        h, w = frame.shape[:2]
        overlay = frame.copy()
        cv2.rectangle(overlay, (0, 0), (w, 84), (20, 20, 20), cv2.FILLED)
        cv2.addWeighted(overlay, 0.45, frame, 0.55, 0.0, frame)

        title = "AR Piano Trainer"
        cv2.putText(frame, title, (18, 35), cv2.FONT_HERSHEY_SIMPLEX, 1.0, (240, 240, 240), 2, cv2.LINE_AA)

        info = f"Render FPS: {self.render_fps:5.1f} | Camera: {self.real_cam_w}x{self.real_cam_h} @ {self.real_cam_fps:4.1f}"
        cv2.putText(frame, info, (18, 67), cv2.FONT_HERSHEY_SIMPLEX, 0.65, (210, 210, 210), 2, cv2.LINE_AA)

    def _draw_menu(self, frame: np.ndarray) -> None:
        h, w = frame.shape[:2]
        panel = frame.copy()
        cv2.rectangle(panel, (40, 110), (w - 40, h - 70), (15, 15, 15), cv2.FILLED)
        cv2.addWeighted(panel, 0.55, frame, 0.45, 0.0, frame)

        cv2.putText(frame, "1) Escolha uma musica MIDI", (72, 150), cv2.FONT_HERSHEY_SIMPLEX, 0.85, (230, 230, 230), 2, cv2.LINE_AA)
        cv2.putText(frame, "2) Clique em Jogar", (72, 182), cv2.FONT_HERSHEY_SIMPLEX, 0.75, (210, 210, 210), 2, cv2.LINE_AA)

        list_top = 220
        row_h = 36
        max_rows = max(1, min(12, (h - 340) // row_h))

        if not self.midi_files:
            cv2.putText(
                frame,
                f"Nenhum MIDI encontrado em: {self.midi_dir}",
                (74, list_top + 28),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.7,
                (80, 180, 255),
                2,
                cv2.LINE_AA,
            )
        else:
            if self.selected_index >= len(self.midi_files):
                self.selected_index = 0

            max_top = max(0, len(self.midi_files) - max_rows)
            self.menu_scroll = int(clamp(self.menu_scroll, 0, max_top))

            wheel_delta = self._consume_wheel_delta()
            key_steps = self._consume_menu_scroll()
            if key_steps != 0:
                self.menu_scroll = int(clamp(self.menu_scroll + key_steps, 0, max_top))

            if wheel_delta != 0:
                notch_steps = int(wheel_delta / 120) if abs(wheel_delta) >= 120 else (1 if wheel_delta > 0 else -1)
                self.menu_scroll = int(clamp(self.menu_scroll - notch_steps, 0, max_top))

            top_idx = self.menu_scroll
            visible = self.midi_files[top_idx : top_idx + max_rows]

            click = self._consume_click()
            for i, path in enumerate(visible):
                y1 = list_top + i * row_h
                y2 = y1 + row_h - 4
                idx = top_idx + i
                active = idx == self.selected_index
                row_color = (40, 100, 40) if active else (32, 32, 32)
                cv2.rectangle(frame, (70, y1), (w - 70, y2), row_color, cv2.FILLED)
                cv2.rectangle(frame, (70, y1), (w - 70, y2), (70, 70, 70), 1)
                cv2.putText(frame, path.name, (80, y1 + 24), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (235, 235, 235), 1, cv2.LINE_AA)

                if click and 70 <= click[0] <= w - 70 and y1 <= click[1] <= y2:
                    self.selected_index = idx

            draw_text_with_box(
                frame,
                "Use a roda do mouse para rolar a lista",
                (72, list_top - 14),
                scale=0.55,
                text_color=(220, 220, 220),
                alpha=0.45,
                thickness=1,
            )

            if click is not None:
                self._mouse_click = click

        play_button = Button((70, h - 110, 250, h - 62), "Jogar")
        rescan_button = Button((270, h - 110, 450, h - 62), "Recarregar MIDIs")

        click = self._consume_click()
        play_button.draw(frame, enabled=bool(self.midi_files))
        rescan_button.draw(frame, enabled=True)

        if click:
            if play_button.contains(*click) and self.midi_files:
                self._start_selected_song()
            elif rescan_button.contains(*click):
                self.midi_files = list_midi_files(self.midi_dir)
                self.selected_index = 0

    def _draw_alignment(self, frame: np.ndarray) -> None:
        h, w = frame.shape[:2]
        self._update_tracker(frame)

        area = self.keyboard_area
        if area is not None:
            x1, y1, x2, y2 = area
            cv2.rectangle(frame, (x1, y1), (x2, y2), (20, 220, 60), 2)
            draw_text_with_box(
                frame,
                f"Area detectada conf: {self.best_conf * 100:.1f}%",
                (x1, max(26, y1 - 8)),
                scale=0.62,
            )

        guide_w = int(w * 0.7)
        guide_h = int(h * 0.22)
        gx1 = (w - guide_w) // 2
        gy1 = int(h * 0.55)
        gx2 = gx1 + guide_w
        gy2 = gy1 + guide_h
        cv2.rectangle(frame, (gx1, gy1), (gx2, gy2), (90, 90, 90), 1)
        draw_text_with_box(frame, "Centralize o teclado real dentro da caixa", (gx1 + 12, gy1 - 14), scale=0.62)

        stable_target = max(8, self.args.stable_hits)
        stable_ratio = clamp(self.stable_hits / float(stable_target), 0.0, 1.0)

        bar_x1, bar_y1 = 70, h - 120
        bar_x2, bar_y2 = w - 70, h - 94
        cv2.rectangle(frame, (bar_x1, bar_y1), (bar_x2, bar_y2), (40, 40, 40), cv2.FILLED)
        fill_w = int((bar_x2 - bar_x1) * stable_ratio)
        cv2.rectangle(frame, (bar_x1, bar_y1), (bar_x1 + fill_w, bar_y2), (30, 180, 50), cv2.FILLED)
        cv2.rectangle(frame, (bar_x1, bar_y1), (bar_x2, bar_y2), (220, 220, 220), 1)
        draw_text_with_box(
            frame,
            f"Tracking estavel: {self.stable_hits}/{stable_target}",
            (bar_x1, bar_y1 - 10),
            scale=0.62,
        )

        draw_text_with_box(
            frame,
            f"Velocidade da musica: {self.song_speed:.2f}x",
            (70, h - 166),
            scale=0.66,
        )

        slower_button = Button((360, h - 186, 430, h - 138), "-")
        faster_button = Button((440, h - 186, 510, h - 138), "+")

        start_button = Button((70, h - 76, 280, h - 28), "Iniciar jogo")
        back_button = Button((300, h - 76, 510, h - 28), "Trocar musica")
        can_start = self.keyboard_area is not None and self.stable_hits >= stable_target

        click = self._consume_click()
        start_button.draw(frame, enabled=can_start)
        back_button.draw(frame, enabled=True)
        slower_button.draw(frame, enabled=True)
        faster_button.draw(frame, enabled=True)

        if click:
            if start_button.contains(*click) and can_start:
                self._start_gameplay()
            elif back_button.contains(*click):
                self._reset_to_menu()
            elif slower_button.contains(*click):
                self.song_speed = float(clamp(self.song_speed - 0.1, 0.5, 2.0))
            elif faster_button.contains(*click):
                self.song_speed = float(clamp(self.song_speed + 0.1, 0.5, 2.0))

    def _draw_keyboard_overlay(self, frame: np.ndarray, area: Rect) -> None:
        x1, y1, x2, y2 = area
        cv2.rectangle(frame, (x1, y1), (x2, y2), (0, 170, 255), 2)

        # Linhas-guia para orientar leitura visual das notas no teclado detectado.
        for i in range(1, 16):
            x = x1 + int((x2 - x1) * (i / 16.0))
            cv2.line(frame, (x, y1), (x, y2), (40, 80, 110), 1)

    def _draw_gameplay(self, frame: np.ndarray) -> None:
        self._update_tracker(frame)

        if self.keyboard_area is None:
            draw_text_with_box(frame, "Perdi o teclado. Reposicione e aguarde redeteccao.", (40, 130), scale=0.7, text_color=(60, 200, 255))
            return

        area = self.keyboard_area
        self._draw_keyboard_overlay(frame, area)

        x1, y1, x2, y2 = area
        strike_y = int(y1 + 0.86 * (y2 - y1))
        spawn_y = y1 - int(0.32 * frame.shape[0])
        spawn_y = min(spawn_y, y1 - 40)
        spawn_y = max(16, spawn_y)
        cv2.line(frame, (x1, strike_y), (x2, strike_y), (70, 220, 255), 2)

        now = time.perf_counter()
        t = now - self.game_start_time - self.countdown_seconds

        if t < 0.0:
            count = int(math.ceil(-t))
            draw_text_with_box(frame, f"Prepare-se... {count}", (40, 130), scale=0.95)
            draw_text_with_box(frame, f"Inicio em {self.countdown_seconds:.0f}s | Velocidade {self.song_speed:.2f}x", (40, 160), scale=0.62)
            return

        music_t = t * self.song_speed
        visual_t = music_t - self.travel_time

        window_start = visual_t - 0.2
        window_end = visual_t + self.travel_time + 0.2

        for ev in self.events:
            if ev.end < window_start or ev.start > window_end:
                continue

            x = pitch_to_x(ev.pitch, area)
            approach = (visual_t - (ev.start - self.travel_time)) / max(0.001, self.travel_time)
            y = int(lerp(spawn_y, strike_y, clamp(approach, 0.0, 1.0)))

            # Comprimento da nota baseado na duracao musical.
            dur_px = int(clamp((ev.end - ev.start) * 140.0, 20.0, 300.0))
            y_top = y - dur_px
            black_key = is_black_key(ev.pitch)
            half_width = 4 if black_key else 9

            if ev.hand == "R":
                border = (70, 230, 90)  # Verde (mao direita)
                color = (20, 70, 20) if black_key else (185, 245, 185)
            else:
                border = (255, 120, 40)  # Azul (mao esquerda)
                color = (90, 35, 18) if black_key else (255, 210, 170)

            cv2.rectangle(frame, (x - half_width, y_top), (x + half_width, y), color, cv2.FILLED)
            cv2.rectangle(frame, (x - half_width, y_top), (x + half_width, y), border, 1)

            if abs(ev.start - music_t) <= 0.08:
                cv2.circle(frame, (x, strike_y), 12, (0, 255, 255), 2)
                cv2.putText(frame, str(ev.pitch), (x - 10, strike_y - 16), cv2.FONT_HERSHEY_SIMPLEX, 0.45, (240, 240, 240), 1, cv2.LINE_AA)

        progress = clamp(music_t / max(0.001, self.song_duration), 0.0, 1.0)
        h, w = frame.shape[:2]
        cv2.rectangle(frame, (40, h - 36), (w - 40, h - 16), (35, 35, 35), cv2.FILLED)
        cv2.rectangle(frame, (40, h - 36), (40 + int((w - 80) * progress), h - 16), (20, 180, 50), cv2.FILLED)
        cv2.rectangle(frame, (40, h - 36), (w - 40, h - 16), (220, 220, 220), 1)

        remaining = max(0.0, self.song_duration - music_t)
        draw_text_with_box(frame, f"Musica: {self.song_name}", (40, 130), scale=0.7)
        draw_text_with_box(frame, f"Tempo restante: {remaining:5.1f}s | Velocidade: {self.song_speed:.2f}x", (40, 160), scale=0.62)

        if music_t > self.song_duration + 0.5:
            self.state = "end"

    def _draw_end(self, frame: np.ndarray) -> None:
        h, w = frame.shape[:2]
        panel = frame.copy()
        cv2.rectangle(panel, (80, 140), (w - 80, h - 120), (10, 10, 10), cv2.FILLED)
        cv2.addWeighted(panel, 0.6, frame, 0.4, 0.0, frame)

        cv2.putText(frame, "Musica finalizada!", (120, 220), cv2.FONT_HERSHEY_SIMPLEX, 1.2, (240, 240, 240), 2, cv2.LINE_AA)
        cv2.putText(frame, self.song_name, (120, 260), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (220, 220, 220), 2, cv2.LINE_AA)

        menu_button = Button((120, h - 190, 350, h - 140), "Voltar ao menu")
        replay_button = Button((380, h - 190, 610, h - 140), "Jogar novamente")

        click = self._consume_click()
        menu_button.draw(frame, enabled=True)
        replay_button.draw(frame, enabled=True)

        if click:
            if menu_button.contains(*click):
                self._reset_to_menu()
            elif replay_button.contains(*click):
                self.state = "align"
                self.stable_hits = 0

    def _update_render_fps(self) -> None:
        now = time.perf_counter()
        dt = max(1e-6, now - self.last_frame_time)
        self.last_frame_time = now
        self.render_fps = 1.0 / dt
        if self.render_fps_ema <= 0.0:
            self.render_fps_ema = self.render_fps
        else:
            self.render_fps_ema = 0.9 * self.render_fps_ema + 0.1 * self.render_fps

    def run(self) -> None:
        print("[AR Piano Game] Iniciado")
        print(f"  Camera real: {self.real_cam_w}x{self.real_cam_h} @ {self.real_cam_fps:.2f} FPS")
        print(f"  Providers ONNX: {self.session.get_providers()}")
        print(f"  Input ONNX usado: {self.model_input_w}x{self.model_input_h}")

        while True:
            ok, frame = self.cap.read()
            if not ok:
                print("[AR Piano Game] Falha ao ler frame da camera.")
                break

            self._update_render_fps()
            self._draw_header(frame)

            if self.state == "menu":
                self._draw_menu(frame)
            elif self.state == "align":
                self._draw_alignment(frame)
            elif self.state == "game":
                self._draw_gameplay(frame)
            elif self.state == "end":
                self._draw_end(frame)

            cv2.imshow(self.window_name, frame)
            key = cv2.waitKeyEx(1)
            if key in (27, ord("q"), ord("Q")):
                break
            if key in (ord("r"), ord("R")):
                self._reset_to_menu()

            # Fallback de navegacao do menu por teclado.
            if self.state == "menu":
                if key in (2490368, ord("k"), ord("K")):  # Up
                    self._enqueue_menu_scroll(-1)
                elif key in (2621440, ord("j"), ord("J")):  # Down
                    self._enqueue_menu_scroll(1)
                elif key == 2162688:  # PageUp
                    self._enqueue_menu_scroll(-5)
                elif key == 2228224:  # PageDown
                    self._enqueue_menu_scroll(5)

        self.cap.release()
        cv2.destroyAllWindows()
        print("[AR Piano Game] Encerrado")


# ---- CLI -----------------------------------------------------------------------

def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="AR Piano Game com ONNX + MIDI")
    parser.add_argument("--model", default="piano_SGD.onnx", help="Caminho do modelo ONNX")
    parser.add_argument("--camera", default="0", help="Indice da camera ou caminho de video")
    parser.add_argument("--midi-dir", default=r"C:\Users\Murillo\Music\MIDI", help="Pasta com arquivos MIDI")

    parser.add_argument("--width", type=int, default=1920, help="Largura solicitada da camera")
    parser.add_argument("--height", type=int, default=1080, help="Altura solicitada da camera")
    parser.add_argument("--fps", type=int, default=60, help="FPS solicitado para webcam")

    parser.add_argument("--size", type=int, default=512, help="Input quadrado para inferencia ONNX")
    parser.add_argument("--num-classes", type=int, default=1, help="Numero de classes do modelo")
    parser.add_argument("--conf", type=float, default=0.30, help="Limiar de confianca")
    parser.add_argument("--iou", type=float, default=0.45, help="Limiar IoU NMS")

    parser.add_argument("--detect-interval", type=int, default=5, help="Inferencia de deteccao a cada N frames")
    parser.add_argument("--stable-hits", type=int, default=12, help="Hits de deteccao estavel para liberar jogo")
    parser.add_argument("--travel-time", type=float, default=2.0, help="Tempo para nota cair ate a tecla")
    parser.add_argument("--song-speed", type=float, default=1.0, help="Velocidade da musica (0.5 a 2.0)")
    parser.add_argument("--countdown", type=float, default=3.0, help="Contagem regressiva antes das notas (segundos)")
    parser.add_argument("--auto-start-first", action="store_true", help="Inicia automaticamente em align com o primeiro MIDI (teste sem clique)")
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    game = ARPianoGame(args)
    game.run()


if __name__ == "__main__":
    main()
