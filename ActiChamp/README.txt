ActiChamp service for PolyHub - first working stage

Project layout assumption:
  <solution root>\CommonUnits\JsonFileStore.cs
  <solution root>\CommonUnits\ServiceEventLink.cs
  <solution root>\CommonUnits\ServiceManifest.cs
  <solution root>\ActiChamp\ActiChamp.csproj

Build/runtime:
  - .NET Framework 4.8
  - C# 7.3
  - Platform target: x86 (required)
  - ActiChamp_x86.dll must be available next to ActiChamp.exe at runtime.
  - ActiChamp.bit from the working actiCHamp installation/test folder must also be next to ActiChamp.exe.
  - OpenTK 3.3.3 + OpenTK.GLControl 3.3.3 (same versions as SignalGenerator).

Modes:
  ActiChamp.exe           - standalone window
  ActiChamp.exe --hosted  - borderless hosted service window for PolyHub

Generated automatically next to the exe:
  ActiChamp.json
  ActiChamp.service.json

Current stage:
  - init hardware
  - Normal acquisition
  - configurable supported sample rate
  - configurable exact requested block size
  - selectable EEG/AUX channels (Use / Ref / Channel / Type)
  - software EEG reference before MMF/display; multiple REF channels are averaged
  - REF can be used even when Use is off; AUX cannot be a reference
  - only selected output channels are copied into the managed output block
  - compact channel-major float data in volts
  - RawSignal MMF ring compatible in layout with PolyHub ProcessingBlockRing
  - OpenGL local visualization
  - fixed display baseline correction; first displayed sample sets the baseline, New captures a fresh baseline
  - impedance mode on the same GLControl: selected/reference EEG channels + GND
  - impedance values update once per second
  - PyCorder-style default impedance colors: 0..50 kOhm, green < 16.7, yellow < 33.3, red >= 33.3, disconnected gray
  - Normal acquisition and Impedance are mutually exclusive; the Impedance button toggles impedance mode
  - ServiceEventLink Start / Stop / Reset / Ping

The lower driver layer still contains Test and LED primitives for later UI stages.

Fix: after leaving Impedance mode the controller restores Normal setup at the selected sampling rate before returning to Idle.
