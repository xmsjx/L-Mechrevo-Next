using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using MechrevoLite.UI;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Form host = new Form();
        host.ClientSize = new Size(400, 600);
        host.Show();
        Application.DoEvents();
        for (int round = 0; round < 3; round++)
        {
            Stopwatch w = Stopwatch.StartNew();
            for (int i = 0; i < 30; i++)
            {
                RCheckBox cb = new RCheckBox();
                cb.AutoSize = true;
                cb.Text = "开关 " + i;
                cb.CheckedChanged += delegate { };
                host.Controls.Add(cb);
            }
            long ms = w.ElapsedMilliseconds;
            Console.WriteLine("round " + round + ": 30 x RCheckBox = " + ms + " ms");
            host.Controls.Clear();
        }
        host.Close();
    }
}
