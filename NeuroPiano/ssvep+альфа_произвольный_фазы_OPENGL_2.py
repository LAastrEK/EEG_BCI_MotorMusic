# coding=utf-8
"""
SSVEP + произвольное (self-initiated) скрытое внимание, с явной фазовой
структурой поверх клик-based разметки (правый клик - стоп/onset, левый клик
по квадрату - идентификация цели, метка пишется задним числом - см.
register_attention_event()).

СТРУКТУРА:
  Baseline: 2 минуты, квадраты статичны, без мерцания (фоновая альфа).

  Фаза 1: фиксация на центральном кресте. Мерцание всех 8 квадратов идёт
    непрерывно. Испытуемый в произвольном порядке и в произвольные моменты
    сдвигает скрытое внимание на любой из 8 квадратов и фиксирует выбор
    кликом (право - стоп, лево - на какой квадрат). Фаза длится произвольное
    время и завершается по нажатию ПРОБЕЛА.5

  Переходный экран "Начало Фазы 2" - ожидание ПРОБЕЛА.

  Фаза 2: 8 подфаз. В подфазе i взгляд фиксируется на квадрате i (вместо
    креста - индикатор фиксации прямо на этом квадрате), доступные цели
    скрытого внимания - остальные 7 квадратов (квадрат, на котором зафиксирован
    взгляд, кликнуть как цель нельзя). Подфаза завершается по ПРОБЕЛУ - это
    сразу переводит к подфазе i+1. Пробел в подфазе 8 завершает эксперимент.

Мерцание всех 8 квадратов идёт непрерывно на протяжении Фазы 1 и Фазы 2 без
пропусков (единый фазовый аккумулятор, не сбрасывается между фазами/подфазами).

Никакой визуальной подсказки (cue) для внимания нет - только процедурная
информация о том, ГДЕ фиксировать взгляд (крест или точка на квадрате), это
не задаёт зону внимания, а не задаёт цель.
"""

import os
import time
import pickle
import numpy as np
import scipy.io as sio
from scipy.signal import butter, filtfilt, iirnotch

from pyglet.gl import (
    glColor4f, glRectf, glLineWidth, glBegin, glEnd, glVertex2f,
    GL_LINE_LOOP, glUseProgram,
    glMatrixMode, glPushMatrix, glPopMatrix, glLoadIdentity, glOrtho,
    glDisable, glEnable, GL_PROJECTION, GL_MODELVIEW, GL_TEXTURE_2D
)
from psychopy import visual, core, event
from pylsl import StreamInlet, resolve_byprop, StreamInfo, StreamOutlet, local_clock

# =====================================================
# 1. LSL: ПОДКЛЮЧЕНИЕ К EEG
# =====================================================
print("Searching EEG stream...")
streams = resolve_byprop('type', 'EEG')
inlet = StreamInlet(streams[0])
info = inlet.info()

n_channels = info.channel_count()
srate_expected = info.nominal_srate() or 500.0

desc = info.desc()
channels_node = desc.child("channels").child("channel")
channel_names = []
for k in range(n_channels):
    label = channels_node.child_value("label") or f"Ch{k + 1}"
    channel_names.append(label)
    channels_node = channels_node.next_sibling()

print(f"Channels ({n_channels}): {channel_names}")


def infer_channel_type(name):
    n = name.upper()
    return 'EOG' if 'EOG' in n else 'EEG'


channel_types = [infer_channel_type(n) for n in channel_names]

# =====================================================
# 2. ПАРАМЕТРЫ ЭКСПЕРИМЕНТА
# =====================================================
frequencies = [18, 22, 17, 21, 16, 20, 15, 19]
n_squares = len(frequencies)

square_size = 100
spacing_x = 180
font_size = 20
show_labels = True

DISPLAY_GAMMA = 2.2   # приближённая гамма экрана
BG_LEVEL = 0.5  

square_color = 'white'
bg_color = 'grey'

baseline_duration = 12.0

ANALYSIS_WINDOW_SEC = 2.5
POST_CONFIRM_FREEZE_SEC = 0.3
SHOW_CONFIRM_FEEDBACK = True
SHOW_SUBPHASE_INTRO = True       # короткий текстовый экран "Подфаза i/8" в начале
                                  # каждой подфазы (процедурная информация, НЕ cue
                                  # для внимания - можно отключить, поставив False)
SUBPHASE_INTRO_DURATION = 1.5    # c

MARKER_BASELINE = 900
PHASE1_MARKER_BASE = 100         # + (target_idx+1), target_idx=0..7  -> 101..108
PHASE2_MARKER_BASE = 200         # + (fixation_idx+1)*10 + (target_idx+1) -> 211..288

ASSUMED_SESSION_MINUTES = 35     # только для первичного резервирования буфера
ENABLE_TRIAL_LIMITS = False       # общий тумблер - False отключает оба лимита сразу
PHASE1_MAX_TRIALS = 80           # автозавершение Фазы 1 после 80 подтверждённых событий
PHASE2_MAX_TRIALS_TOTAL = 168    # автозавершение ВСЕЙ Фазы 2 (суммарно по всем 8 подфазам)

savepath_base = "D:/BCIresults/SSVEP_SelfInitiated_Phased/"

# =====================================================
# 3. LSL МАРКЕРЫ
# =====================================================
marker_info = StreamInfo(
    name='SSVEP_SelfInitiated_Markers',
    type='Markers',
    channel_count=1,
    nominal_srate=0,
    channel_format='int32',
    source_id='ssvep_selfinit_phased_psychopy')
marker_outlet = StreamOutlet(marker_info)

# =====================================================
# 4. БУФЕРЫ (растущие)
# =====================================================
n_samples_max = int((baseline_duration + ASSUMED_SESSION_MINUTES * 60) * srate_expected * 1.3)
eeg_buffer = np.zeros((n_channels, n_samples_max))
marker_buffer = np.zeros((1, n_samples_max), dtype=int)
write_pos = 0


def _grow_buffers(extra_needed):
    global eeg_buffer, marker_buffer, n_samples_max
    add = max(extra_needed, n_samples_max // 2)
    eeg_buffer = np.concatenate([eeg_buffer, np.zeros((n_channels, add))], axis=1)
    marker_buffer = np.concatenate([marker_buffer, np.zeros((1, add), dtype=int)], axis=1)
    n_samples_max += add


def record_eeg(marker):
    global write_pos
    chunk, timestamps = inlet.pull_chunk(timeout=0.0)
    if not chunk:
        return
    data = np.asarray(chunk).T
    n_new = data.shape[1]
    if write_pos + n_new > n_samples_max:
        _grow_buffers(n_new)
    end = write_pos + n_new
    eeg_buffer[:, write_pos:end] = data
    marker_buffer[:, write_pos:end] = marker
    write_pos = end

# =====================================================
# 5. ОКНО
# =====================================================
win = visual.Window(
    fullscr=True, winType='pyglet', screen=0, color=bg_color,
    units='pix', waitBlanking=True)
win.mouseVisible = True

win_w, win_h = win.size
monitor_hz = win.getActualFrameRate()  or 165
print("Monitor Hz:", monitor_hz)
win.recordFrameIntervals = True
win.refreshThreshold = (1.0 / monitor_hz) * 1.2

# =====================================================
# 6. СТИМУЛЫ
# =====================================================
left_offsets = [-3.5, -2.5, -1.5, -0.5]
right_offsets = [0.5, 1.5, 2.5, 3.5]
offsets = left_offsets + right_offsets
positions = [(off * spacing_x, 0) for off in offsets]

stim_rects = []
stim_labels = []
for i, pos in enumerate(positions):
    rect = visual.Rect(win, width=square_size, height=square_size, pos=pos,
                        fillColor=square_color, lineColor='black')
    stim_rects.append(rect)
    label = visual.TextStim(win, text=f"{frequencies[i]:.2f} Hz",
                             pos=(pos[0], pos[1] - square_size / 2 - 30),
                             color='white', height=font_size)
    stim_labels.append(label)

fixation_cross = visual.TextStim(win, text='+', height=50, color='white', pos=(0, 0))
fixation_dot = visual.Circle(win, radius=6, fillColor='red', lineColor='red')

instruction_text = visual.TextStim(
    win,
    text=("Кликните ЛЕВОЙ кнопкой по квадрату, на который было направлено внимание.\n"
          "(ПРАВАЯ кнопка ещё раз или Esc - отменить)"),
    pos=(0, -win_h / 2 + 60), color='white', height=24, wrapWidth=win_w * 0.85)

stim_clock = core.Clock()
_L_MIN = BG_LEVEL ** DISPLAY_GAMMA   # физическая яркость фона = "тёмная" фаза
_L_MAX = 1.0


def flicker_levels():
    """Уровень яркости (0..1) для каждого квадрата на текущий кадр.
    Синус по реальному времени + гамма-коррекция."""
    t = stim_clock.getTime()
    levels = []
    for f in frequencies:
        s = 0.5 * (1.0 + np.sin(2.0 * np.pi * f * t))
        lin = _L_MIN + (_L_MAX - _L_MIN) * s
        levels.append(lin ** (1.0 / DISPLAY_GAMMA))
    return levels

GL_COLORS = {
    'white': (1.0, 1.0, 1.0),
    'grey':  (0.5, 0.5, 0.5),
    'lime':  (0.0, 1.0, 0.0),
    'black': (0.0, 0.0, 0.0),
}


def draw_squares_gl(states, frozen, highlight=None, highlight_color='lime'):
    """Замена цикла rect.draw() в draw_scene. states - результат flicker_states()
    или None при frozen=True."""
    glUseProgram(0)
    glDisable(GL_TEXTURE_2D)

    half_w, half_h = win_w / 2, win_h / 2
    glMatrixMode(GL_PROJECTION)
    glPushMatrix()
    glLoadIdentity()
    glOrtho(-half_w, half_w, -half_h, half_h, -1, 1)
    glMatrixMode(GL_MODELVIEW)
    glPushMatrix()
    glLoadIdentity()

    for j, pos in enumerate(positions):
        lvl = 1.0 if frozen else states[j]
        cx, cy = pos
        half_size = square_size / 2
        left, right = cx - half_size, cx + half_size
        bottom, top = cy - half_size, cy + half_size

        glColor4f(lvl, lvl, lvl, 1.0)
        glRectf(left, bottom, right, top)

        if highlight is not None and j == highlight:
            glColor4f(*GL_COLORS[highlight_color], 1.0)
            glLineWidth(6)
        else:
            glColor4f(*GL_COLORS['black'], 1.0)
            glLineWidth(1)

        glBegin(GL_LINE_LOOP)
        glVertex2f(left, bottom); glVertex2f(right, bottom)
        glVertex2f(right, top);   glVertex2f(left, top)
        glEnd()

    glMatrixMode(GL_PROJECTION)
    glPopMatrix()
    glMatrixMode(GL_MODELVIEW)
    glPopMatrix()
    glEnable(GL_TEXTURE_2D)

def draw_scene(states, frozen, fixation_display='cross', highlight=None, highlight_color='lime'):
    """fixation_display: 'cross' - крест в центре; int - точка фиксации на
    квадрате с этим индексом (Фаза 2), крест не рисуется."""
    if fixation_display == 'cross':
        fixation_cross.draw()

    draw_squares_gl(states, frozen, highlight=highlight, highlight_color=highlight_color)

    if isinstance(fixation_display, int):
        fixation_dot.pos = positions[fixation_display]
        fixation_dot.draw()

    if frozen:
        instruction_text.draw()
    win.flip()

def hit_test(pos):
    for j, rect in enumerate(stim_rects):
        if rect.contains(pos):
            return j
    return None


def wait_for_space(message):
    """Информационный экран (мерцание приостановлено), ожидание ПРОБЕЛА."""
    global escape_pressed
    text = visual.TextStim(win, text=message, color='white', height=28, wrapWidth=win_w * 0.8)
    while True:
        text.draw()
        win.flip()
        keys = event.getKeys(['space', 'escape'])
        if 'escape' in keys:
            escape_pressed = True
            return
        if 'space' in keys:
            return
        record_eeg(0)

# =====================================================
# 7. РЕГИСТРАЦИЯ СОБЫТИЯ ВНИМАНИЯ (постфактум-разметка)
# =====================================================
click_log = []


def register_attention_event(click_write_pos, click_lsl_time, marker_code, meta=None):
    window_samples = int(round(ANALYSIS_WINDOW_SEC * srate_expected))
    start_idx = max(0, click_write_pos - window_samples)

    if np.any(marker_buffer[0, start_idx:click_write_pos] != 0):
        print("ВНИМАНИЕ: окно разметки пересекается с уже размеченным участком.")

    marker_buffer[0, start_idx:click_write_pos] = marker_code
    marker_outlet.push_sample([marker_code], click_lsl_time)

    entry = {
        'marker_code': int(marker_code),
        'click_write_pos': int(click_write_pos),
        'window_start_pos': int(start_idx),
        'click_lsl_time': float(click_lsl_time),
        'confirm_wall_time': time.time(),
    }
    if meta:
        entry.update(meta)
    click_log.append(entry)
    print(f"[event] marker={marker_code}  window=[{start_idx}:{click_write_pos})  meta={meta}")

# =====================================================
# 8. ОБЩИЙ ЦИКЛ "СВОБОДНОЕ МЕРЦАНИЕ + КЛИК-РАЗМЕТКА" ДЛЯ ОДНОЙ ФАЗЫ/ПОДФАЗЫ
# =====================================================
mouse = event.Mouse(win=win)
escape_pressed = False


def run_attention_segment(marker_fn, excluded_idx=None, fixation_display='cross', meta_base=None,
                           max_trials=None, global_counter=None, global_max=None):
    global escape_pressed
    event.clearEvents()
    prev_buttons = [0, 0, 0]
    mode = 'free'
    pending_write_pos = None
    pending_lsl_time = None
    local_count = 0

    while True:
        keys = event.getKeys(['escape', 'space'])
        if 'escape' in keys:
            escape_pressed = True
            return False

        buttons = mouse.getPressed()
        right_edge = buttons[2] and not prev_buttons[2]
        left_edge = buttons[0] and not prev_buttons[0]
        prev_buttons = buttons

        if mode == 'free':
            states = flicker_levels()
            draw_scene(states, frozen=False, fixation_display=fixation_display)
            record_eeg(0)

            if 'space' in keys:
                return True

            if max_trials is not None and local_count >= max_trials:
                print(f"[лимит] Достигнуто {local_count}/{max_trials} проб в сегменте - "
                      f"автоматическое завершение (как по пробелу).")
                return True

            if global_max is not None and global_counter is not None and global_counter[0] >= global_max:
                print(f"[лимит] Достигнуто {global_counter[0]}/{global_max} проб суммарно - "
                      f"автоматическое завершение сегмента.")
                return True

            if right_edge:
                record_eeg(0)
                pending_write_pos = write_pos
                pending_lsl_time = local_clock()
                mode = 'awaiting_confirmation'

        elif mode == 'awaiting_confirmation':
            draw_scene(states=None, frozen=True, fixation_display=fixation_display)
            record_eeg(0)

            if left_edge:
                pos = mouse.getPos()
                target_idx = hit_test(pos)
                if target_idx is not None and target_idx != excluded_idx:
                    marker_code = marker_fn(target_idx)
                    meta = dict(meta_base or {})
                    meta['target_idx'] = target_idx + 1
                    register_attention_event(pending_write_pos, pending_lsl_time, marker_code, meta)
                    local_count += 1
                    if global_counter is not None:
                        global_counter[0] += 1
                    if SHOW_CONFIRM_FEEDBACK:
                        fb_t0 = time.time()
                        while time.time() - fb_t0 < POST_CONFIRM_FREEZE_SEC:
                            draw_scene(states=None, frozen=True,
                                       fixation_display=fixation_display, highlight=target_idx)
                            record_eeg(0)
                    pending_write_pos, pending_lsl_time = None, None
                    mode = 'free'
                elif target_idx == excluded_idx:
                    print("Нельзя выбрать целью квадрат текущей точки фиксации взгляда.")
            elif right_edge:
                pending_write_pos, pending_lsl_time = None, None
                mode = 'free'
# =====================================================
# 9. BASELINE (2 минуты, без мерцания) - фоновая альфа
# =====================================================
print("Baseline recording start in 2 sec")
core.wait(2)

t0 = time.time()
while time.time() - t0 < baseline_duration and not escape_pressed:
    if event.getKeys(['escape']):
        escape_pressed = True
        break
    fixation_cross.draw()
    for rect in stim_rects:
        rect.fillColor = square_color
        rect.lineColor = 'black'
        rect.lineWidth = 1
        rect.draw()
    win.flip()
    record_eeg(MARKER_BASELINE)

# =====================================================
# 10. ФАЗА 1: фиксация в центре, все 8 целей доступны
# =====================================================
if not escape_pressed:
    wait_for_space("Фаза 1.\n\nФиксируйтесь на центральном кресте.\n"
                    "Боковым зрением переносите внимание на любой квадрат в удобном темпе.\n\n"
                    "Пробел - начать.")

if not escape_pressed:
    run_attention_segment(
        marker_fn=lambda idx: PHASE1_MARKER_BASE + (idx + 1),
        excluded_idx=None,
        fixation_display='cross',
        meta_base={'phase': 1},
        max_trials=PHASE1_MAX_TRIALS if ENABLE_TRIAL_LIMITS else None)
    # завершение по пробелу (или escape - тогда escape_pressed уже True)

# =====================================================
# 11. ФАЗА 2: 8 подфаз, фиксация поочерёдно на каждом квадрате
# =====================================================
if not escape_pressed:
    wait_for_space("Фаза 1 завершена.\n\nФаза 2.\n"
                    "Взгляд будет поочерёдно фиксироваться на каждом из 8 квадратов.\n\n"
                    "Пробел - начать.")

if not escape_pressed:
    phase2_counter = [0]
    phase2_global_max = PHASE2_MAX_TRIALS_TOTAL if ENABLE_TRIAL_LIMITS else None

    for fix_idx in range(n_squares):
        if escape_pressed:
            break

        if SHOW_SUBPHASE_INTRO:
            intro = visual.TextStim(
                win,
                text=f"Подфаза {fix_idx + 1}/{n_squares}.\n"
                     f"Зафиксируйте взгляд на отмеченном квадрате.",
                color='white', height=28, wrapWidth=win_w * 0.8)
            intro_t0 = time.time()
            while time.time() - intro_t0 < SUBPHASE_INTRO_DURATION:
                if event.getKeys(['escape']):
                    escape_pressed = True
                    break
                intro.draw()
                fixation_dot.pos = positions[fix_idx]
                fixation_dot.draw()
                win.flip()
                record_eeg(0)
            if escape_pressed:
                break

        def phase2_marker_fn(idx, _fix=fix_idx):
            return PHASE2_MARKER_BASE + (_fix + 1) * 10 + (idx + 1)

        run_attention_segment(
            marker_fn=phase2_marker_fn,
            excluded_idx=fix_idx,
            fixation_display=fix_idx,
            meta_base={'phase': 2, 'fixation_idx': fix_idx + 1},
            global_counter=phase2_counter,
            global_max=phase2_global_max)

        if phase2_global_max is not None and phase2_counter[0] >= phase2_global_max:
            print(f"[лимит] Суммарный лимит {phase2_global_max} проб для Фазы 2 достигнут - "
                  f"эксперимент завершается (оставшиеся подфазы пропускаются).")
            break
        # завершение подфазы (пробел) -> следующая итерация; на подфазе 8
        # пробел просто выводит из цикла и переходит к сохранению данных

fi = np.array(win.frameIntervals)
n_dropped = win.nDroppedFrames
print(f"Frame interval: mean {fi.mean()*1000:.3f} ms, std {fi.std()*1000:.3f} ms, "
      f"max {fi.max()*1000:.2f} ms, dropped {n_dropped}")

win.close()
print("Experiment finished" if not escape_pressed else "Experiment interrupted (Escape)")

# =====================================================
# 12. ОБРЕЗКА ДАННЫХ
# =====================================================
final_eeg = eeg_buffer[:, :write_pos]
final_markers = marker_buffer[:, :write_pos]
print("Samples recorded:", final_eeg.shape[1])
print("Confirmed attention events:", len(click_log))

# =====================================================
# 13. EVENTS
# =====================================================
marks = final_markers[0]
change_points = np.where(np.diff(marks) != 0)[0] + 1
bounds = np.concatenate(([0], change_points, [len(marks)]))

events = []
for i in range(len(bounds) - 1):
    s, e = bounds[i], bounds[i + 1]
    val = marks[s]
    if val == 0:
        continue
    events.append({'type': float(val), 'latency': float(s + 1), 'duration': float(e - s)})

events_type = np.array([ev['type'] for ev in events])
events_latency = np.array([ev['latency'] for ev in events])
events_duration = np.array([ev['duration'] for ev in events])

# =====================================================
# 14. ЧАСТИЧНАЯ ПРЕДОБРАБОТКА
# =====================================================
def bandpass_filter(data, srate, low=1.0, high=40.0, order=4):
    nyq = srate / 2.0
    b, a = butter(order, [low / nyq, high / nyq], btype='band')
    return filtfilt(b, a, data, axis=1)


def notch_filter(data, srate, freq=50.0, q=30.0):
    b, a = iirnotch(freq / (srate / 2.0), q)
    return filtfilt(b, a, data, axis=1)


final_eeg_filtered = final_eeg.copy()
try:
    if final_eeg_filtered.shape[1] > 50:
        final_eeg_filtered = notch_filter(final_eeg_filtered, srate_expected, freq=50.0)
        final_eeg_filtered = bandpass_filter(final_eeg_filtered, srate_expected, low=1.0, high=40.0)
    else:
        print("Слишком мало сэмплов для фильтрации, сохраняю только сырые данные.")
except Exception as exc:
    print("Фильтрация не удалась, сохраняю только сырые данные:", exc)
    final_eeg_filtered = final_eeg.copy()

# =====================================================
# 15. СОХРАНЕНИЕ
# =====================================================
timestamp = time.strftime("%Y-%m-%d_%H-%M-%S")
save_dir = os.path.join(savepath_base, timestamp)
os.makedirs(save_dir, exist_ok=True)

save_dict = {
    'EEG_data_raw': final_eeg.astype(np.float64),
    'EEG_data_filtered': final_eeg_filtered.astype(np.float64),
    'filter_info': 'notch 50Hz (Q=30) + bandpass 1-40Hz Butterworth order 4, filtfilt. '
                    'Для CCA/FBCCA рекомендуется своя фильтрация из EEG_data_raw.',
    'Markers': final_markers.astype(np.float64),
    'channel_names': np.array(channel_names, dtype=object),
    'channel_types': np.array(channel_types, dtype=object),
    'events_type': events_type,
    'events_latency': events_latency,
    'events_duration': events_duration,
    'srate': float(srate_expected),
    'monitor_hz': monitor_hz,
    'frequencies': np.array(frequencies),
    'square_positions_offsets': np.array(offsets),
    'baseline_duration': baseline_duration,
    'analysis_window_sec': ANALYSIS_WINDOW_SEC,
    'marker_scheme': {
        'baseline': MARKER_BASELINE,
        'phase1': '100 + target_idx (target_idx=1..8)',
        'phase2': '200 + fixation_idx*10 + target_idx (fixation_idx,target_idx=1..8)',
    },
    'click_log': click_log,
}

mat_file = os.path.join(save_dir, "ssvep_selfinit_phased_data.mat")
sio.savemat(mat_file, save_dict)
print("Saved:", mat_file)

pkl_file = os.path.join(save_dir, "ssvep_selfinit_phased_data.pkl")
with open(pkl_file, "wb") as f:
    pickle.dump(save_dict, f)
print("Backup:", pkl_file)
