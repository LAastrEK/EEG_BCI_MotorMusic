using System.Collections.Generic;
using System.Runtime.Serialization;

namespace ActiChamp
{
    [DataContract]
    internal sealed class ActiChampChannelSettings
    {
        [DataMember(Order = 1)]
        public int PhysicalIndex { get; set; }

        [DataMember(Order = 2)]
        public string Name { get; set; }

        [DataMember(Order = 3)]
        public string Type { get; set; }

        [DataMember(Order = 4)]
        public bool Enabled { get; set; }

        [DataMember(Order = 5)]
        public bool IsReference { get; set; }
    }

    [DataContract]
    internal sealed class ActiChampSettings
    {
        [DataMember(Order = 1)]
        public int WindowLeft { get; set; }

        [DataMember(Order = 2)]
        public int WindowTop { get; set; }

        [DataMember(Order = 3)]
        public int WindowWidth { get; set; }

        [DataMember(Order = 4)]
        public int WindowHeight { get; set; }

        [DataMember(Order = 5)]
        public int SamplingRateHz { get; set; }

        [DataMember(Order = 6)]
        public int BlockSizeSamples { get; set; }

        [DataMember(Order = 7)]
        public int TimeBaseSeconds { get; set; }

        [DataMember(Order = 8)]
        public string DisplayScale { get; set; }

        [DataMember(Order = 9)]
        public int ActiveShieldGain { get; set; }

        [DataMember(Order = 10)]
        public List<ActiChampChannelSettings> Channels { get; set; }

        // EEG filters. 0 Hz means Off for the corresponding cutoff.
        [DataMember(Order = 11)]
        public double LowCutoffHz { get; set; }

        [DataMember(Order = 12)]
        public double HighCutoffHz { get; set; }

        [DataMember(Order = 13)]
        public bool Notch50Hz { get; set; }

        // EOG filters. 0 Hz means Off for the corresponding cutoff.
        [DataMember(Order = 14)]
        public double EogLowCutoffHz { get; set; }

        [DataMember(Order = 15)]
        public double EogHighCutoffHz { get; set; }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            // Defaults for settings files created before EOG support was added.
            EogLowCutoffHz = 0.1;
            EogHighCutoffHz = 30.0;
        }

    }
}
