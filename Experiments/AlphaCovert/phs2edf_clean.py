# coding=utf-8
"""
Конвертор PolyHub Session (*.phs, контейнер PHSX) -> EDF+ (*.edf).

Формат PHSX разобран напрямую из SessionRecorder.cs / SessionReader.cs
(PolyHubNet), без зависимостей от C#:

  FileHeader (Pack=1, little-endian, Marshal.SizeOf = 64 байта):
    int32 Magic = 0x50485358 ("PHSX"), int32 Version = 1,
    int32 ChannelCount, int32 SampleRate,
    int64 QpcFrequency, DataStartOffset, CommittedBytes,
    TotalSamplesPerChannel, GrowStepBytes, Reserved0
  Затем ChannelCount имён каналов по 32 wchar_t (UTF-16LE, 64 байта каждое).

  С DataStartOffset идут записи:
  RecordHeader (Pack=1, 56 байт):
    int32 RecordType (1 = SignalBlock, 2 = UserEvent), int32 SampleCount,
    int32 PayloadBytes, int32 Reserved0,
    int64 Sequence, int64 QpcTimestamp, int64 FirstSampleIndex,
    int32 Code, int32 State, int32 Value0, int32 Value1
  Payload SignalBlock: float32 channel-major (Data[ch*SampleCount + i]),
  значения в вольтах.

Эпохи калибровки (ServiceEventCode.UserEvent = 1000, state 1 = START,
state 2 = END, Value0 = класс 1300/1301..1308) пишутся в маркерный канал
EDF+ "Status" как аннотации "Start/1301" и "End/1301"; onset считается из
QPC относительно первого блока сигнала.

Использование:
    python phs2edf_clean.py input.phs [-o output.edf] [--events events.tsv]
"""

import argparse
import os
import struct
import sys

MAGIC = 0x50485358          # "PHSX"
VERSION = 1
CHANNEL_NAME_BYTES = 32 * 2  # 32 wchar_t UTF-16LE

# '<' отключает выравнивание — эквивалент StructLayout(Pack=1) в C#.
FILE_HEADER_FMT = "<iiiiqqqqqq"     # 64 bytes: Magic,Version,ChannelCount,SampleRate + 6 x int64
RECORD_HEADER_FMT = "<iiiiqqqiiii"  # 56 bytes: RecordType,SampleCount,PayloadBytes,Reserved0 +
                                    # Sequence,QpcTimestamp,FirstSampleIndex + Code,State,Value0,Value1

REC_SIGNAL = 1
REC_EVENT = 2
CODE_USER_EVENT = 1000


def read_phs(path):
    """Возвращает (header_dict, blocks, events)."""
    with open(path, "rb") as f:
        raw = f.read()

    magic, version, n_ch, fs, qpc_freq, data_start, committed, total_samples = \
        struct.unpack_from(FILE_HEADER_FMT, raw, 0)[:8]
    if magic != MAGIC or version != VERSION:
        raise ValueError("Не PHSX v1: magic=0x%08X version=%d" % (magic, version))
    if n_ch <= 0 or fs <= 0:
        raise ValueError("Некорректный заголовок сессии.")

    off = struct.calcsize(FILE_HEADER_FMT)
    names = []
    for _ in range(n_ch):
        chunk = raw[off:off + CHANNEL_NAME_BYTES]
        off += CHANNEL_NAME_BYTES
        names.append(chunk.decode("utf-16-le", errors="replace").split("\x00")[0])

    expected_data_start = struct.calcsize(FILE_HEADER_FMT) + n_ch * CHANNEL_NAME_BYTES
    if data_start != expected_data_start:
        raise ValueError("Unexpected DataStartOffset: %d (ожидалось %d)" %
                         (data_start, expected_data_start))

    end = committed if committed > 0 else len(raw)
    rh_size = struct.calcsize(RECORD_HEADER_FMT)
    blocks, events = [], []
    pos = data_start
    while pos + rh_size <= end:
        rec_type, sample_count, payload_bytes, _res, seq, qpc, first_idx, \
            code, state, value0, value1 = struct.unpack_from(RECORD_HEADER_FMT, raw, pos)
        payload_off = pos + rh_size
        if rec_type == REC_SIGNAL:
            need = payload_off + payload_bytes
            if need > end:
                break  # хвостовая недописанная запись
            data = struct.unpack_from("<%df" % (n_ch * sample_count), raw, payload_off)
            blocks.append({
                "seq": seq, "qpc": qpc, "first_index": first_idx,
                "samples": sample_count,
                "data": [data[ch * sample_count:(ch + 1) * sample_count]
                         for ch in range(n_ch)],
            })
            pos = payload_off + payload_bytes
        elif rec_type == REC_EVENT:
            events.append({"qpc": qpc, "code": code, "state": state,
                           "value0": value0, "value1": value1})
            pos = payload_off + max(payload_bytes, 0)
        else:
            break

    header = {"fs": fs, "n_ch": n_ch, "qpc_freq": qpc_freq, "names": names,
              "total_samples": total_samples}
    return header, blocks, events


def build_edf(header, blocks, out_path, events=None):
    """Пишет EDF+ c одним сигнальным блоком на пробу и маркерным каналом Status."""
    n_ch, fs = header["n_ch"], header["fs"]
    qpc_freq = header["qpc_freq"] or 1
    if not blocks:
        raise ValueError("В сессии нет сигнальных блоков.")

    # Длительность записи должна быть целым числом секунд; если блоки не
    # покрывают полный секундный рекорд, берём столько секунд, сколько есть
    # (полные), а оставшийся хвост добиваем нулевыми отсчётами.
    total = sum(b["samples"] for b in blocks)
    n_full_records = max(1, total // fs)
    pad = n_full_records * fs - total
    sig = [bytearray() for _ in range(n_ch)]
    for b in blocks:
        for ch in range(n_ch):
            sig[ch].extend(struct.pack("<%df" % b["samples"], *b["data"][ch]))
    if pad:
        zeros = b"\x00" * (4 * pad)
        for ch in range(n_ch):
            sig[ch].extend(zeros)
    total += pad
    t0 = blocks[0]["qpc"]

    phys_min, phys_max = -5e-3, 5e-3
    dig_min, dig_max = -32767, 32767
    gain = float(dig_max - dig_min) / (phys_max - phys_min)  # counts per volt

    annotations = {}
    if events:
        for e in events:
            if e["code"] != CODE_USER_EVENT or e["state"] not in (1, 2):
                continue
            onset_s = (e["qpc"] - t0) / float(qpc_freq)
            if onset_s < 0:
                continue
            idx = min(int(round(onset_s * fs)), total - 1)
            prefix = "Start" if e["state"] == 1 else "End"
            ann = "%s/%d" % (prefix, e["value0"])
            prev = annotations.get(idx, "")
            annotations[idx] = (prev + "\x00" + ann) if prev else ann

    record_duration = 1.0 / fs
    hbuf = bytearray(b" " * 256)

    def put(offset, width, text):
        t = ("%-" + str(width) + "s") % str(text)
        hbuf[offset:offset + width] = t[:width].encode("latin-1", "replace")

    # EDF+ аннотации пишутся в Signal Persistence (SP) маркерного канала,
    # поэтому в поле Annotations (88,80) пусто, а в Patientname (8,80) —
    # обязательный "X" для recording without ECG/EMG channels.
    put(0, 8, "0.")                       # Version, reserved for EDF+ extensions
    put(8, 80, "X PolyHub PhsToEdf ")     # Local recording identification
    put(88, 80, "")                        # Startdate of recording session
    put(168, 8, "01.01.70")
    put(176, 8, "00.00.00")
    put(184, 8, "256")
    put(192, 4, str(n_ch + 1))
    put(196, 44, "")

    off = 240
    for i, name in enumerate(header["names"]):
        put(off + i * 16, 16, (name or ("Ch%d" % (i + 1))).replace(" ", "_"))
    put(off + n_ch * 16, 16, "EDF Annotations")
    trans = off + (n_ch + 1) * 16
    for i in range(n_ch):
        put(trans + i * 8, 8, "V")
    put(trans + n_ch * 8, 8, "")
    pdim = trans + (n_ch + 1) * 8
    for i in range(n_ch):
        put(pdim + i * 8, 8, "%.4f" % phys_min)
    put(pdim + n_ch * 8, 8, "0")
    pmax = pdim + (n_ch + 1) * 8
    for i in range(n_ch):
        put(pmax + i * 8, 8, "%.4f" % phys_max)
    put(pmax + n_ch * 8, 8, "1")
    dmin = pmax + (n_ch + 1) * 8
    for i in range(n_ch):
        put(dmin + i * 8, 8, str(dig_min))
    put(dmin + n_ch * 8, 8, "0")
    dmax = dmin + (n_ch + 1) * 8
    for i in range(n_ch):
        put(dmax + i * 8, 8, str(dig_max))
    put(dmax + n_ch * 8, 8, "1")
    tdur = dmax + (n_ch + 1) * 8
    for i in range(n_ch + 1):
        put(tdur + i * 8, 8, "%.6f" % record_duration)
    nsamp = tdur + (n_ch + 1) * 8
    for i in range(n_ch):
        put(nsamp + i * 8, 8, str(fs))
    put(nsamp + n_ch * 8, 8, "1")
    reserved = nsamp + (n_ch + 1) * 8   # поле Reserved — последние 32 байта заголовка
    assert reserved + 32 <= 256, \
        "EDF header overflow: %d каналов не помещаются в фиксированный заголовок" % (n_ch + 1)
    put(reserved, 32, "")

    floats_per_channel = [struct.unpack_from("<%df" % total, bytes(sig[ch]), 0)
                          for ch in range(n_ch)]
    rec_fmt = "<%dh" % (n_ch + 1)
    with open(out_path, "wb") as f:
        f.write(bytes(hbuf))
        for s in range(total):
            vals = []
            for ch in range(n_ch):
                v = int(round(floats_per_channel[ch][s] * gain))
                vals.append(max(min(v, dig_max), dig_min))
            ann_bytes = annotations.get(s, "").encode("latin-1", "replace")
            marker_len = (len(ann_bytes) + 1) if ann_bytes else 0
            vals.append(marker_len)
            f.write(struct.pack(rec_fmt, *vals))
            if marker_len:
                # EDF+ SP-формат: текстовые поля, завершённые одним \x00;
                # пустое Onset/Duration поле даёт terminating "\x00\x00".
                f.write(b"\x00\x00" + ann_bytes + b"\x00")
    return out_path


def main():
    ap = argparse.ArgumentParser(description="PHS (PolyHub) -> EDF конвертор")
    ap.add_argument("input", help="session.phs")
    ap.add_argument("-o", "--output", help="выходной .edf")
    ap.add_argument("--events", help="дополнительно выгрузить события в TSV")
    args = ap.parse_args()

    header, blocks, events = read_phs(args.input)
    out = args.output or os.path.splitext(args.input)[0] + ".edf"
    if args.events:
        with open(args.events, "w", encoding="utf-8") as f:
            f.write("qpc\tcode\tstate\tvalue0\tvalue1\n")
            for e in events:
                f.write("%d\t%d\t%d\t%d\t%d\n" %
                        (e["qpc"], e["code"], e["state"], e["value0"], e["value1"]))
    build_edf(header, blocks, out, events=events)
    print("OK: %s -> %s (%d ch, %d Hz, %d blocks, %d events)" %
          (os.path.basename(args.input), out, header["n_ch"], header["fs"],
           len(blocks), len(events)))


if __name__ == "__main__":
    sys.exit(main())
