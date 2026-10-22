using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PolyHubNet
{
    //Модели для UI
    public sealed class EpochInfo
    {
        public long EpochId { get; set; }
        public int ClassId { get; set; }
        public int Value1 { get; set; }
        public long StartQpc { get; set; }
        public long EndQpc { get; set; }

        public long StartSampleIndex { get; set; }
        public long EndSampleIndex { get; set; }

        public int SampleCount
        {
            get { return (int)(EndSampleIndex - StartSampleIndex); }
        }
    }

    public sealed class EpochClassSummary
    {
        public int ClassId { get; set; }
        public int Count { get; set; }

        public override string ToString()
        {
            return "Class " + ClassId.ToString() + " (" + Count.ToString() + ")";
        }
    }
}
