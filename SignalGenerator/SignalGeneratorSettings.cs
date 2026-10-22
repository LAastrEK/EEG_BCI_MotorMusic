using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace SignalGenerator
{
    [DataContract]
    internal class SignalGeneratorSettings
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
        public int ChannelsCount { get; set; }

        [DataMember(Order = 6)]
        public int SamplingRate { get; set; }

        [DataMember(Order = 7)]
        public string WaveForm { get; set; }

        [DataMember(Order = 8)]
        public double[] SineFrequenciesHz { get; set; }
    }
}
