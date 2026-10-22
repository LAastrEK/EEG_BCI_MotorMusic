using System;

namespace PolyHubNet
{
    public sealed class SignalStream
    {
        public string Name { get; private set; }

        public string[] ChannelNames { get; private set; }

        public event Action<SignalBlock> BlockPublished;
        public event Action ChannelNamesChanged;

        public SignalStream(string name)
        {
            Name = name;
            ChannelNames = new string[0];
        }

        public void SetChannelNames(string[] names)
        {
            if (names == null)
            {
                ChannelNames = new string[0];
            }
            else
            {
                ChannelNames =
                    new string[names.Length];

                Array.Copy(
                    names,
                    ChannelNames,
                    names.Length);
            }

            Action handler =
                ChannelNamesChanged;

            if (handler != null)
                handler();
        }

        public void Publish(SignalBlock block)
        {
            Action<SignalBlock> handler =
                BlockPublished;

            if (handler != null)
                handler(block);
        }
    }
}