using System;
using System.Linq;
using System.Windows.Forms;

namespace ActiChamp
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool hosted = args != null && args.Contains("--hosted");
            Application.Run(new fmMain(hosted));
        }
    }
}
