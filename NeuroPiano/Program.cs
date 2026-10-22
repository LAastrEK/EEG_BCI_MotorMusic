using System;
using System.Linq;
using System.Windows.Forms;

namespace NeuroPiano
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool hostedMode = args != null &&
                              args.Any(a => string.Equals(a, "--hosted", StringComparison.OrdinalIgnoreCase));

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new fmMain(hostedMode));
        }
    }
}
