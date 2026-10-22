using System;
using System.Collections.Generic;

namespace PolyHubNet
{
    public sealed class SignalProcessorCatalog
    {
        private readonly List<SignalProcessorSlot> _signalProcessorSlotList =
            new List<SignalProcessorSlot>();

        public IList<SignalProcessorSlot> SignalProcessorSlotList
        {
            get { return _signalProcessorSlotList; }
        }

        public void Add(SignalProcessorSlot slot)
        {
            if (slot == null)
                return;

            if (string.IsNullOrWhiteSpace(slot.ProcessorNodeId))
                return;

            if (FindByNodeId(slot.ProcessorNodeId) != null)
                return;

            _signalProcessorSlotList.Add(slot);
        }

        public SignalProcessorSlot FindByNodeId(string processorNodeId)
        {
            for (int i = 0; i < _signalProcessorSlotList.Count; i++)
            {
                SignalProcessorSlot slot = _signalProcessorSlotList[i];
                if (string.Equals(slot.ProcessorNodeId, processorNodeId, StringComparison.OrdinalIgnoreCase))
                    return slot;
            }

            return null;
        }

        public SignalProcessorSlot FindByOutputPin(string processorNodeId, string outputPinId)
        {
            SignalProcessorSlot slot = FindByNodeId(processorNodeId);
            if (slot == null)
                return null;

            if (!string.Equals(slot.OutputPinId, outputPinId, StringComparison.OrdinalIgnoreCase))
                return null;

            return slot;
        }

        public SignalProcessorSlot FindByInputPin(string processorNodeId, string inputPinId)
        {
            SignalProcessorSlot slot = FindByNodeId(processorNodeId);
            if (slot == null)
                return null;

            if (!string.Equals(slot.InputPinId, inputPinId, StringComparison.OrdinalIgnoreCase))
                return null;

            return slot;
        }
    }
}