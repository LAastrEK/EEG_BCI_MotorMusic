using System.Runtime.Serialization;

namespace NeuroPiano
{
    [DataContract]
    internal sealed class NeuroPianoSettings
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
        public int CenterMidiNote { get; set; }

        [DataMember(Order = 6)]
        public int FirstVisibleWhiteKeyIndex { get; set; }

        [DataMember(Order = 7)]
        public double MonitorRefreshRate { get; set; }

        [DataMember(Order = 8)]
        public double[] SSVEPFrequencies { get; set; }

        [DataMember(Order = 9)]
        public int ActiveStimulusVisibleIndex { get; set; }

        [DataMember(Order = 10)]
        public int SSVEPCalibrationBaselineDurationMs { get; set; }

        [DataMember(Order = 11)]
        public int SSVEPCalibrationCueDurationMs { get; set; }

        [DataMember(Order = 12)]
        public int SSVEPCalibrationStimulusDurationMs { get; set; }

        [DataMember(Order = 13)]
        public int SSVEPCalibrationFeedbackDurationMs { get; set; }

        [DataMember(Order = 14)]
        public int SSVEPCalibrationInterTrialDurationMs { get; set; }

        [DataMember(Order = 15)]
        public int SSVEPCalibrationBlockRestDurationMs { get; set; }

        [DataMember(Order = 16)]
        public int SSVEPCalibrationRepetitionsPerTarget { get; set; }

        [DataMember(Order = 17)]
        public int SSVEPCalibrationStimulusMode { get; set; }

        [DataMember(Order = 18)]
        public int SSVEPCalibrationSettlingDurationMs { get; set; }

        [DataMember(Order = 19)]
        public int SSVEPCalibrationBackgroundCueDurationMs { get; set; }

        [DataMember(Order = 20)]
        public int SSVEPCalibrationBackgroundSettlingDurationMs { get; set; }

        [DataMember(Order = 21)]
        public int SSVEPCalibrationBackgroundStimulusDurationMs { get; set; }

        [DataMember(Order = 22)]
        public int SSVEPCalibrationBackgroundInterTrialDurationMs { get; set; }

        [DataMember(Order = 23)]
        public int CovertCalibrationStartMidiNote { get; set; }

        [DataMember(Order = 24)]
        public int CovertCalibrationRepetitionsPerTarget { get; set; }

        [DataMember(Order = 25)]
        public int CovertCalibrationNeutralDurationMs { get; set; }
    }
}
