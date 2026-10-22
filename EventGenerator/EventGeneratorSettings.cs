using System.Runtime.Serialization;

namespace EventGenerator
{
    [DataContract]
    public sealed class EventGeneratorSettings
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
        public int DefaultEventDurationMs { get; set; }
    }
}