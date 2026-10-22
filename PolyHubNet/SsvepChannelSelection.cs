using System;
using System.Collections.Generic;

namespace PolyHubNet
{
    internal static class SsvepChannelSelection
    {
        // actiCHamp assigns these fixed names to its differential EOG outputs,
        // independently of the physical electrodes selected for either pair.
        public static bool IsEogOutput(string name)
        {
            string value = (name ?? string.Empty).Trim();
            return string.Equals(value, "HEOG", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "VEOG", StringComparison.OrdinalIgnoreCase);
        }

        public static string[] SelectEegChannels(string[] channelNames)
        {
            if (channelNames == null)
                throw new ArgumentNullException("channelNames");

            List<string> result = new List<string>(channelNames.Length);
            for (int i = 0; i < channelNames.Length; i++)
            {
                if (!IsEogOutput(channelNames[i]))
                    result.Add(channelNames[i]);
            }
            return result.ToArray();
        }
    }
}
