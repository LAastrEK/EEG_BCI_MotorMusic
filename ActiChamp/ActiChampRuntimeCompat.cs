using System;
using System.Diagnostics;

namespace ActiChamp
{
    internal static class ActiChampRuntimeCompat
    {
        public static void ApplyPyCorderLikeProcessSettings(Action<string> log)
        {
            try
            {
                Process p = Process.GetCurrentProcess();

                try
                {
                    p.PriorityClass = ProcessPriorityClass.Normal;
                    if (log != null)
                        log("Process priority: Normal");
                }
                catch (Exception ex)
                {
                    if (log != null)
                        log("Не удалось установить PriorityClass: " + ex.Message);
                }

                try
                {
                    long currentMask = p.ProcessorAffinity.ToInt64();
                    long newMask = BuildFirstTwoCpuMask(currentMask);

                    if (newMask != 0 && newMask != currentMask)
                    {
                        p.ProcessorAffinity = new IntPtr(newMask);
                        if (log != null)
                            log("ProcessorAffinity: 0x" + currentMask.ToString("X") + " -> 0x" + newMask.ToString("X"));
                    }
                    else if (log != null)
                    {
                        log("ProcessorAffinity: 0x" + currentMask.ToString("X"));
                    }
                }
                catch (Exception ex)
                {
                    if (log != null)
                        log("Не удалось установить ProcessorAffinity: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                if (log != null)
                    log("ApplyPyCorderLikeProcessSettings error: " + ex.Message);
            }
        }

        private static long BuildFirstTwoCpuMask(long currentMask)
        {
            long result = 0;
            int count = 0;
            long bit = 1;

            while (bit != 0 && count < 2)
            {
                if ((currentMask & bit) != 0)
                {
                    result |= bit;
                    count++;
                }

                bit <<= 1;
            }

            return result;
        }
    }
}
