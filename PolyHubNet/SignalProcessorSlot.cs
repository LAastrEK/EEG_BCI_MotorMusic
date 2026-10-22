namespace PolyHubNet
{
    public sealed class SignalProcessorSlot
    {
        public string ProcessorNodeId { get; set; }
        public string ProcessorDisplayName { get; set; }

        public string InputPinId { get; set; }
        public string OutputPinId { get; set; }

        public SignalProcessor SignalProcessor { get; set; }
        public SignalStream ProcessorOutputStream { get; set; }
    }
}