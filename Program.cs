using System;
using System.Threading;
using System.Windows.Forms;

namespace OBSClipper;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Run from a download: install into the programs folder and start that copy.
        if (Installer.RelaunchFromInstallDir()) return;

        using var mutex = new Mutex(true, "OBSClipper.SingleInstance", out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "OBSClipper.Show");
        using var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Installer.ExitSignalName);
        if (!first)
        {
            // Already running: bring the existing window up instead.
            showSignal.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        bool startHidden = Array.IndexOf(args, "--tray") >= 0;
        Application.Run(new MainForm(startHidden, showSignal, exitSignal));
    }
}
