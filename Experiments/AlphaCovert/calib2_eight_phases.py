# coding=utf-8
"""
Calib2 v2 — восемь фаз covert attention поверх NeuroPiano-клавиатуры.

Прототип в стиле ssvep+альфа_произвольный_фазы_OPENGL_2.py, но с явной
фазовой структурой (по аналогии с Фаза 2 из исходника: фиксация на якоре,
цели — остальные клавиши). Отличие от текущей C-реализации
(CovertCalibrationController.cs): там одна фаза на пробег по 7 целям,
здесь 8 фаз = 8 якорей; в каждой фазе сначала ЖЁЛТЫМ подсвечивается
стартовая клавиша-якорь (куда фиксировать взгляд), затем КРАСНЫМ — цель
скрытого внимания. Длительность cue не меняется (SSVEPCalibrationCueDurationMs).

Клик мышью сохраняется как в оригинальном Python-прототипе:
  правый клик — стоп/onset события, левый клик по клавише — идентификация
  цели (метка пишется задним числом). В C# это соответствует потоку
  Cue -> Settling -> Stimulus -> CheckStimulus (EOG-валидация) уже
  существующего контроллера.

КЛАССЫ ЭПОХ (пишутся в PHS как UserEvent Value0):
  1300              — neutral (фиксация на якоре, без смещения внимания)
  1300 + p*8 + i+1  — фаза p (0..7), внутрифазовый ранг цели i (0..6)
  ИТОГО 1301..1357 для восьми фаз. Сторона цели относительно якоря НЕ кодируется
  в классе (как и в действующем Calib2); она выводится офлайн из порядка
  белых клавиш PianoKeyboardLayout.WhiteKeys: side = LEFT если target < anchor,
  RIGHT если target > anchor, NEUTRAL для 1300. Именно этот признак — цель
  обучения альфа-классификатора (left/right/neutral, см. README).

Запуск прототипа (Windows, психопай-окружение как в исходном файле):
    python calib2_eight_phases.py
"""

import time

# =====================================================
# 1. ПАРАМЕТРЫ ФАЗ (совпадают с NeuroPianoSettings по смыслу)
# =====================================================
N_PHASES = 8                 # восемь фаз = восемь якорей по белым клавишам
N_WHITE_KEYS = 8             # видимый диапазон клавиатур (C4..B4)
TARGETS_PER_PHASE = N_WHITE_KEYS - 1   # семь целей на фазу

# Длительности, мс. Cue менять НЕ нужно — берём как в C#-настройках.
BASELINE_MS = 30000          # SSVEPCalibrationBaselineDurationMs
CUE_MS = 1000                # SSVEPCalibrationCueDurationMs (БЕЗ ИЗМЕНЕНИЙ)
SETTLING_MS = 500            # SSVEPCalibrationSettlingDurationMs
STIMULUS_MS = 2500           # SSVEPCalibrationStimulusDurationMs
NEUTRAL_MS = 2500            # CovertCalibrationNeutralDurationMs
FEEDBACK_MS = 1000           # SSVEPCalibrationFeedbackDurationMs
INTER_TRIAL_MS = 1000        # SSVEPCalibrationInterTrialDurationMs
BLOCK_REST_MS = 5000         # SSVEPCalibrationBlockRestDurationMs
REPETITIONS_PER_TARGET = 10  # CovertCalibrationRepetitionsPerTarget

NEUTRAL_CLASS_BASE = 1300
PHASE_STRIDE = 8             # 8 фаз по 8 кодов (1 резервный слот на фазу)

YELLOW = (1.0, 1.0, 0.0)     # подсветка якоря (gaze anchor)
RED = (1.0, 0.0, 0.0)        # подсветка цели covert attention
WHITE = (1.0, 1.0, 1.0)


def phase_class(phase_idx, rank_idx):
    """phase_idx 0..7, rank_idx 0..6 -> 1301..1357."""
    return NEUTRAL_CLASS_BASE + phase_idx * PHASE_STRIDE + rank_idx + 1


def decode_class(code):
    """Обратно: (phase_idx, rank_idx) либо ('neutral', None)."""
    if code == NEUTRAL_CLASS_BASE:
        return 'neutral', None
    offset = code - NEUTRAL_CLASS_BASE - 1
    if offset < 0 or offset >= N_PHASES * PHASE_STRIDE:
        return None, None
    phase = offset // PHASE_STRIDE
    rank = offset % PHASE_STRIDE
    if rank >= TARGETS_PER_PHASE:
        return None, None
    return phase, rank


def target_side(anchor_key, target_key):
    """Сторона цели относительно стартовой клавиши-якоря (позиции белых клавиш)."""
    if target_key < anchor_key:
        return 'left'
    if target_key > anchor_key:
        return 'right'
    return 'invalid'  # якорь кликнуть как цель нельзя (как в Фазе 2 оригинала)


# =====================================================
# 2. СОСТОЯНИЯ (те же имена, что в CovertCalibrationState.cs, плюс фаза)
# =====================================================
STATES = ('Idle', 'Baseline', 'PhaseIntro', 'AnchorCueYellow', 'AnchorSettling',
          'Neutral', 'CheckNeutral', 'NeutralInterTrial', 'TargetCueRed',
          'Settling', 'Stimulus', 'CheckStimulus', 'Feedback', 'InterTrial',
          'BlockRest', 'Completed')


class MarkerSink(object):
    """Интерфейс к LSL/PHS: state 1 = START, state 2 = END (SessionRecorder)."""

    def send(self, state, class_code, value1, description, t_ms):
        raise NotImplementedError


class RecordingMarkerSink(MarkerSink):
    """Заглушка: печать; в боёнке заменяется на ServiceEventLink.SendEvent/UserEvent."""

    def send(self, state, class_code, value1, description, t_ms):
        print("[marker] t=%.0fms state=%d class=%d %s" %
              (t_ms, state, class_code, description))


class CovertCalib2EightPhases(object):
    """
    Машина состояний одной сессии Calib2 v2.

    Порядок внутри фазы p (якорь = белая клавиша p):
      PhaseIntro (процедурный текст, не cue внимания — как SUBPHASE_INTRO
        в оригинальном Python-файле)
      AnchorCueYellow: жёлтая подсветка якоря, CUE_MS (не менять!)
      AnchorSettling -> Neutral (класс 1300) -> CheckNeutral (клик/EOG)
      для каждой из семи целей i (перемешанный порядок, CreateOrder()):
        TargetCueRed: красная подсветка цели i, CUE_MS
        Settling -> Stimulus (класс phase_class(p,i)) -> CheckStimulus
        Feedback (подсветка подтверждённой цели) -> InterTrial
      BlockRest, переход к фазе p+1.
    Повторы REPETITIONS_PER_TARGET полных наборов фаз.
    """

    def __init__(self, sink=None, rng_seed=None):
        self.sink = sink or RecordingMarkerSink()
        self._t0 = time.perf_counter()
        self.phase = 0            # текущая фаза 0..7
        self.rank = 0             # ранг цели внутри фазы
        self.round = 0            # номер повторного пробега
        self.state = 'Idle'
        self.order = list(range(TARGETS_PER_PHASE))
        self.anchor_key = 0
        self.target_keys = []
        self.last_click_target = None   # куда кликнул пользователь (левый клик)
        self.stop_requested = False     # правый клик = onset/стоп события

    # ---- public API (соответствует HandleTrialResult/Stop в C#) ----
    def start(self):
        self.state = 'Baseline'
        self.sink.send(1, 0, 0, "Calibration 2 (8 phases) started", self._now_ms())

    def on_left_click_key(self, key_index):
        """Левый клик по клавише — идентификация цели (метка задним числом)."""
        if self.state not in ('CheckStimulus', 'CheckNeutral'):
            return False
        self.last_click_target = key_index
        return True

    def on_right_click(self):
        """Правый клик — стоп/onset текущего события."""
        self.stop_requested = True

    def advance(self):
        """Тик таймера: переход по машине состояний (аналог Advance() в C#)."""
        now = self._now_ms()
        if self.state == 'Baseline':
            self._enter_phase_intro(now)
        elif self.state == 'PhaseIntro':
            self._yellow_anchor(now)
        elif self.state == 'AnchorCueYellow':
            self.anchor_on = False
            self.state = 'AnchorSettling'
            self._after(CUE_MS, self._neutral_start, now)
        elif self.state == 'AnchorSettling':
            self.state = 'Neutral'
            self.sink.send(1, NEUTRAL_CLASS_BASE, 0, "Neutral epoch start", now)
            self._after(NEUTRAL_MS, self._neutral_end, now)
        elif self.state == 'Neutral':
            self.sink.send(2, NEUTRAL_CLASS_BASE, 0, "Neutral epoch end", now)
            self.state = 'CheckNeutral'
        elif self.state == 'CheckNeutral':
            self.state = 'NeutralInterTrial'
            self._after(INTER_TRIAL_MS, self._red_target, now)
        elif self.state == 'TargetCueRed':
            self.state = 'Settling'
            self._after(SETTLING_MS, self._stimulus_start, now)
        elif self.state == 'Stimulus':
            code = phase_class(self.phase, self.order[self.rank])
            self.sink.send(2, code, 0, "Target epoch end", now)
            self.state = 'CheckStimulus'
        elif self.state == 'CheckStimulus':
            self.state = 'Feedback'
            self._after(FEEDBACK_MS, self._next_target_or_block, now)
        elif self.state == 'InterTrial':
            self._next_target_or_block(now)
        elif self.state == 'BlockRest':
            self.phase += 1
            if self.phase >= N_PHASES:
                self.round += 1
                if self.round >= REPETITIONS_PER_TARGET:
                    self.state = 'Completed'
                    self.sink.send(1, 0, 0, "Calibration 2 completed", now)
                    return
                self.phase = 0
            self._enter_phase_intro(now)

    # ---- внутренние шаги ----
    def _enter_phase_intro(self, now):
        self.anchor_key = self.phase
        others = [k for k in range(N_WHITE_KEYS) if k != self.anchor_key]
        self.target_keys = others
        self.rank = 0
        self.state = 'PhaseIntro'
        print("[phase %d] fixation anchor = white key %d, targets = %s" %
              (self.phase + 1, self.anchor_key, others))

    def _yellow_anchor(self, now):
        self.anchor_on = True          # renderer: YELLOW over WhiteKeys[anchor_key]
        self.state = 'AnchorCueYellow'

    def _neutral_start(self, now):
        self.anchor_on = False
        self.state = 'AnchorSettling'

    def _neutral_end(self, now):
        self.state = 'Neutral'

    def _red_target(self, now):
        self.red_target = self.target_keys[self.order[self.rank]]
        side = target_side(self.anchor_key, self.red_target)
        print("[phase %d][target %d/%d] red cue on key %d (%s of anchor)" %
              (self.phase + 1, self.rank + 1, TARGETS_PER_PHASE, self.red_target, side))
        code = phase_class(self.phase, self.order[self.rank])
        self.sink.send(1, code, 0, "Target epoch start", now)
        self.state = 'TargetCueRed'

    def _stimulus_start(self, now):
        self.red_target = None
        self.state = 'Stimulus'

    def _next_target_or_block(self, now):
        self.rank += 1
        if self.rank >= TARGETS_PER_PHASE:
            self.state = 'BlockRest'
            self._after(BLOCK_REST_MS, lambda _n: self.advance(), now)
        else:
            self.state = 'InterTrial'
            self._after(INTER_TRIAL_MS, self._red_target, now)

    def _now_ms(self):
        return (time.perf_counter() - self._t0) * 1000.0

    def _after(self, ms, fn, now):
        # В прототипе — упрощённый планировщик; в C# роль играет Deadline()/QPC.
        fn(now + ms)


if __name__ == '__main__':
    session = CovertCalib2EightPhases()
    session.start()
    # Один проход состояний вручную (для демонстрации структуры без GUI):
    for _ in range(4):
        session.advance()
