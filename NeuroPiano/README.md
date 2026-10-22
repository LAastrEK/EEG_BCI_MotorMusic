# NeuroPiano v1.5

## Level 2 calibration

F5 and command `2001` retain level 1. F6 or a command from PolyHub
(`UserEvent`, `State=1`, `Value0=2006`, `Value1=0`) starts level 2.
PolyHub also sends this command when F6 is pressed in its window. Escape or
command `2002` stops either mode. Starting one stops the other. Use the updated
projects together with an attached EEG stream; EOG validation requires no
trained model or Use.

Three settings are added to `NeuroPiano.json` on startup:

- `CovertCalibrationStartMidiNote`: fixed gaze anchor; defaults to a C among
  the eight visible white keys. A configured note must be visible.
- `CovertCalibrationRepetitionsPerTarget`: 10 by default, counting accepted
  target epochs separately for each of the seven other keys.
- `CovertCalibrationNeutralDurationMs`: 2500 by default.

Each attempt first shows the existing Cue frame on the start key, with all
eight keys flickering. When the frame disappears, the start key turns gray,
stops flickering and sounds simultaneously. After the existing Settling
duration, a neutral epoch is recorded. Keep looking at the start key; the
other seven keys flicker throughout Neutral, Cue, Settling and Stimulus.
Each attempt records a neutral epoch before the
lateral target Cue. After neutral validation, the controller waits for the
existing `SSVEPCalibrationBackgroundInterTrialDurationMs`, keeping the start
key gray and the other seven keys flickering. Then Cue uses the existing
target frame. The frame disappears before Settling; the later Stimulus has target
START/END markers. Once PolyHub accepts the target epoch, the lateral key
turns gray, stops flickering and sounds; the start key resumes flickering.
The lateral key stays gray through Feedback and InterTrial. At the next
attempt, Cue returns to the start key; at Cue onset all eight keys flicker
again, and at Cue disappearance the start key turns gray and sounds again.
Rejected epochs skip target Feedback and repeat the same pair from the start
key Cue. Existing baseline, Cue, Settling, Stimulus, Feedback, InterTrial and
BlockRest timings are reused. Each round presents all seven directed pairs
from the anchor in shuffled order.

PolyHub uses the existing Covert Attention EOG profile and its HEOG/VEOG toggles.
Its Calbr1/Calbr2 buttons send the existing start commands 2001/2006; Stop1/Stop2
both send the existing stop command 2002. The controllers publish started,
stopped and completed notices as State events using their existing command
codes. These keep PolyHub's buttons and status synchronized with F5/F6/Escape,
and introduce no epoch markers. Update both projects together for these notices.

Neutral is checked in full; the target check runs from CueOff through Settling
to END. Absolute differential HEOG amplitude is compared with the existing
numeric threshold in microvolts. A deviation confined to Cue is outside the
target check; a deviation still present after CueOff is included. VEOG follows
its existing toggle and threshold in that profile.

PolyHub replies with `UserEvent`, `State=0`, `Value0=2007`, exact END QPC and
flags in `Value1` (0 accepted, 1 VEOG, 2 HEOG, 4 invalid/unavailable).
Rejected attempts play the existing `2005` notification and repeat the same
pair after InterTrial without consuming a repetition. The next phase waits
for validation; enabled EOG checks require their channels. If both toggles are
off, PolyHub accepts the epoch without EOG checks. No response within five
seconds stops calibration. First-level
decoder commands do not press keys during level 2.

The current run uses one fixed starting C and all seven other target keys.
PHS markers follow the same visible-key numbering as level 1:

- neutral fixation: class `1300`, frequency 0;
- target: class `1300 + targetIndex + 1`, target frequency in mHz;

Only those START/END pairs are published as UserEvent markers. CueOn sends no
event. When the lateral Cue disappears, State (`Code=102`, `State=1`, `Value0=target class`,
`Value1=0`) tells PolyHub to begin the EOG check. PolyHub saves this timestamp
as service data for offline rejection; it is excluded from epoch markers in
live, preview and playback views. The existing PHS record layout is retained.
Start/stop use State with `Value0=2006`, `Value1=anchor MIDI/0` and are not
recorded. State stays 1 because the NeuroPiano service itself remains running.
There are no Cue codes. Rejection sends the existing 2005 notification to
NeuroPiano and fades the epoch with a Rejected label, without an extra marker.

Indices are 0..7 among visible white keys; codes 1301..1308 identify keys 1..8.
The starting key is not presented as a lateral target. No anchor-position or
1400-series marker codes are emitted. Original
START/END markers are kept; rejected epochs fade in PolyHub and the status
reports their count. This mode collects level 2 calibration data; second-level
model training and decoding are separate work. Classes `1200..1208` retain
their first-level meaning.

Minimal PolyHub stimulus service for a virtual piano.

## Current design

- C# 7.3
- .NET Framework 4.8
- WinForms
- OpenTK 3.3.3 / GLControl
- NAudio 2.3.0
- virtual keyboard range: C2..C6 (MIDI 36..84), four octaves
- viewport width: exactly 8 white-key widths
- the viewport is centered on a configurable MIDI note
- default center note: C4 (MIDI 60)
- black keys move naturally with the continuous virtual keyboard
- mouse press gives visual feedback and starts the corresponding note
- WAV continues to its natural end after mouse release
- all 49 note samples are local and loaded into RAM at startup
- PolyHub Start / Stop / Reset / Ping support
- NoteOn / NoteOff are published as ServiceEventCode.UserEvent

## Central note

`NeuroPiano.json` now contains `CenterMidiNote`.

Examples:

- 60 = C4
- 64 = E4
- 67 = G4
- 72 = C5

The keyboard is not rebuilt as a new octave. It is one continuous four-octave
keyboard, and the on-screen viewport simply moves over it. The selected center
note is placed on the horizontal center of the viewport. Since the viewport is
8 white-key widths wide, partial edge keys are normal and intentional.

## CommonUnits

The project deliberately does NOT contain copies of PolyHub IPC units.
It expects the standard shared folder next to the project folder:

```
<PolyHub root>
  CommonUnits
    JsonFileStore.cs
    ServiceEventLink.cs
    ServiceManifest.cs
  NeuroPiano
    NeuroPiano.csproj
    ...
```

The files are linked from `..\CommonUnits\...` in the project.

## Audio events

For `ServiceEventCode.UserEvent`:

- `State = 1` - NoteOn
- `State = 2` - NoteOff
- `Value0` - MIDI note number (36..84)
- `Value1` - velocity on NoteOn, 0 on NoteOff
- `Text` - note name, for example `C3`, `F#4`, `C6`

## Samples

Runtime samples are in `Samples\Piano` and are copied automatically to
`bin\Debug\Samples\Piano` or `bin\Release\Samples\Piano` by MSBuild.
No network access is used at runtime.

The already approved C4..C5 samples from v1.4 are unchanged. Additional notes
were rendered from the same TimGM6mb Acoustic Grand Piano bank and normalized
to the same peak level.

## SSVEP calibration timing

Calibration uses the existing multimedia timer and QPC clock.
For each target:

- Cue: target frame, 500 ms, no PolyHub epoch event.
- Settling: frame removed; the final SSVEP visual mode is already active, 500 ms, no event.
- Stimulus: no visual transition at onset; only here are the PolyHub grouped events emitted (`State=1` start, `State=2` end), default 1500 ms.
- Feedback: target becomes gray and sounds; it starts only after the SSVEP end marker.

Only stimulus intervals are published as calibration epochs. Keys keep classes `1201..1208`.
`SSVEPCalibrationStimulusMode = 0` flickers only the target; `1` flickers all eight targets.

## Background calibration

The six `PianoKeyboardLayout.BackgroundTargets` are ordered top left-to-right,
then bottom left-to-right. Their thin white frames are always static.
Background Cue uses the same inset, color and thickness as the key Cue.
After Cue, the object returns to its original white frame; Settling is unmarked.
Then a START/END pair delimits the background epoch, followed by an InterTrial pause.
Background trials have no note sound or gray-key Feedback.

Each block contains all eight keys once, each followed by one background epoch:
key, background, key, background, including the background after the last key.
The note/background alternation is fixed; the existing shuffled key order is retained.
`SSVEPCalibrationRepetitionsPerTarget` sets the number of key/background pairs
per key: 10 means 80 key epochs plus 80 background epochs; 1 means 8 key epochs
plus 8 background epochs. The six background objects still form one class.
Background objects use shuffled sets of six, avoiding immediate repeats at set
boundaries. Their presentation counts differ by at most one.

Background markers use class `1200`, frequency `0`, `State=1` for START and
`State=2` for END. Object number `1..6` appears only in marker text, so all six
objects form one epoch class. Key marker codes and frequencies are unchanged.

Independent background timings in the settings JSON:

- `SSVEPCalibrationBackgroundCueDurationMs`
- `SSVEPCalibrationBackgroundSettlingDurationMs`
- `SSVEPCalibrationBackgroundStimulusDurationMs`
- `SSVEPCalibrationBackgroundInterTrialDurationMs`

Missing or nonpositive background timings inherit the corresponding current key
timings from an existing JSON file. New files default to 500, 500, 1500 and 1000 ms.
Baseline, block rest and repetition count remain common to the calibration.
All timing fields are written into JSON on startup. The key-to-background pause
uses `SSVEPCalibrationInterTrialDurationMs`; the background-to-key pause uses
`SSVEPCalibrationBackgroundInterTrialDurationMs`. BlockRest follows the last
background pause when another block remains; completion follows the final background pause.

PolyHub trains note classes `1201..1208`; background class `1200` supplies
negative calibration examples.


## EOG rejection notification

PolyHub sends `UserEvent`, `State=0`, `Value0=2005` for an EOG-rejected window.
`Value1` carries rejection flags; `QpcTimestamp` is the window end time.
While the service is running, NeuroPiano plays `Samples/Piano/Rejected.wav`
through the existing NAudio mixer. The sound is 0.375 seconds, PCM16 stereo
at 44100 Hz, matching the piano samples. It does not press a piano key.

During calibration, a notification whose QPC timestamp belongs to the current
Stimulus marks that attempt for retry, including delivery during its Feedback
or InterTrial pause. The original START/END markers and epoch duration remain.
After the usual InterTrial pause, the same key or background object repeats
Cue, Settling and Stimulus without advancing the repetition counter. Feedback
is skipped when rejection is known before the Stimulus ends. Events outside
that Stimulus interval do not change the calibration sequence.
