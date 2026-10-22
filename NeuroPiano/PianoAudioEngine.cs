using System;
using System.Collections.Generic;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NeuroPiano
{
    internal sealed class PianoAudioEngine : IDisposable
    {
        private const int OutputSampleRate = 44100;
        private const int OutputChannels = 2;

        private readonly Dictionary<int, CachedPianoSample> _noteSamples =
            new Dictionary<int, CachedPianoSample>();

        private CachedPianoSample _rejectedSample;
        private WasapiOut _wasapiOut;
        private MixingSampleProvider _mixer;
        private bool _initialized;
        private bool _disposed;

        public void Initialize(string sampleFolder)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PianoAudioEngine));

            if (_initialized)
                return;

            if (string.IsNullOrWhiteSpace(sampleFolder))
                throw new ArgumentNullException(nameof(sampleFolder));

            LoadSamples(sampleFolder);

            _mixer = new MixingSampleProvider(
                WaveFormat.CreateIeeeFloatWaveFormat(OutputSampleRate, OutputChannels));
            _mixer.ReadFully = true;

            // Современный Windows-аудиотракт:
            // WASAPI Shared + event-driven synchronization.
            // 50 ms — начальное безопасное значение для проверки стабильности.
            _wasapiOut = new WasapiOut(
                AudioClientShareMode.Shared,
                true,
                50);

            _wasapiOut.Init(new SampleToWaveProvider(_mixer));
            _wasapiOut.Play();

            _initialized = true;
        }

        // Сэмпл является готовой записью ноты с естественным затуханием.
        // Поэтому при нажатии мы просто запускаем WAV с начала и больше
        // никак его огибающую не изменяем.
        public void PlayNote(int midiNumber)
        {
            if (!_initialized || _disposed)
                return;

            CachedPianoSample sample;
            if (!_noteSamples.TryGetValue(midiNumber, out sample))
                return;

            _mixer.AddMixerInput(new CachedPianoSampleProvider(sample));
        }

        public void PlayRejected()
        {
            if (!_initialized || _disposed || _rejectedSample == null)
                return;

            _mixer.AddMixerInput(new CachedPianoSampleProvider(_rejectedSample));
        }

        public void AllNotesOff()
        {
            if (!_initialized || _disposed || _mixer == null)
                return;

            _mixer.RemoveAllMixerInputs();
        }

        private void LoadSamples(string sampleFolder)
        {
            _noteSamples.Clear();

            for (int midi = PianoNotes.FirstMidiNote;
                 midi <= PianoNotes.LastMidiNote;
                 midi++)
            {
                string fileName = PianoNotes.GetSampleFileName(midi);
                string filePath = Path.Combine(sampleFolder, fileName);

                if (!File.Exists(filePath))
                    throw new FileNotFoundException(
                        "Не найден фортепианный сэмпл: " + filePath,
                        filePath);

                _noteSamples.Add(midi, LoadSample(filePath));
            }

            _rejectedSample = LoadSample(Path.Combine(sampleFolder, "Rejected.wav"));
        }

        private static CachedPianoSample LoadSample(string filePath)
        {
            using (AudioFileReader reader = new AudioFileReader(filePath))
            {
                if (reader.WaveFormat.SampleRate != OutputSampleRate)
                    throw new InvalidOperationException(
                        Path.GetFileName(filePath) + ": sample rate должен быть 44100 Hz.");

                if (reader.WaveFormat.Channels != OutputChannels)
                    throw new InvalidOperationException(
                        Path.GetFileName(filePath) + ": сэмпл должен быть stereo.");

                List<float> data = new List<float>(OutputSampleRate * OutputChannels * 8);
                float[] buffer = new float[16384];

                while (true)
                {
                    int read = reader.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;

                    for (int i = 0; i < read; i++)
                        data.Add(buffer[i]);
                }

                return new CachedPianoSample(
                    WaveFormat.CreateIeeeFloatWaveFormat(OutputSampleRate, OutputChannels),
                    data.ToArray());
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_mixer != null)
            {
                try
                {
                    _mixer.RemoveAllMixerInputs();
                }
                catch
                {
                }
            }

            if (_wasapiOut != null)
            {
                try
                {
                    _wasapiOut.Stop();
                }
                catch
                {
                }

                _wasapiOut.Dispose();
                _wasapiOut = null;
            }

            _mixer = null;
            _rejectedSample = null;
            _noteSamples.Clear();
            _initialized = false;
        }

        private sealed class CachedPianoSample
        {
            public CachedPianoSample(WaveFormat waveFormat, float[] audioData)
            {
                WaveFormat = waveFormat;
                AudioData = audioData;
            }

            public WaveFormat WaveFormat { get; private set; }
            public float[] AudioData { get; private set; }
        }

        private sealed class CachedPianoSampleProvider : ISampleProvider
        {
            private readonly CachedPianoSample _sample;
            private long _position;

            public CachedPianoSampleProvider(CachedPianoSample sample)
            {
                if (sample == null)
                    throw new ArgumentNullException(nameof(sample));

                _sample = sample;
            }

            public WaveFormat WaveFormat
            {
                get { return _sample.WaveFormat; }
            }

            public int Read(float[] buffer, int offset, int count)
            {
                long available = _sample.AudioData.LongLength - _position;
                if (available <= 0)
                    return 0;

                int samplesToCopy = (int)Math.Min((long)count, available);
                Array.Copy(_sample.AudioData, _position, buffer, offset, samplesToCopy);
                _position += samplesToCopy;
                return samplesToCopy;
            }
        }
    }
}
