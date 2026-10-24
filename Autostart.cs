using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace SpotifyBar
{
    /// <summary>
    /// Start-at-logon via a per-user Task Scheduler task. The HKCU Run key turned out to be
    /// unreliable on recent Windows 11 builds (Explorer silently skipped the entry), and a
    /// logon task doesn't depend on Explorer's startup-app handling at all.
    /// </summary>
    internal static class Autostart
    {
        private const string TaskName = "SpotifyBar";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        public static bool IsEnabled() => SchTasks($"/query /tn \"{TaskName}\"");

        public static bool Enable()
        {
            string exe = Environment.ProcessPath!;
            string user = WindowsIdentity.GetCurrent().Name;
            string xml = $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <RegistrationInfo><Description>Starts the SpotifyBar taskbar widget at logon</Description></RegistrationInfo>
                  <Triggers>
                    <LogonTrigger>
                      <Enabled>true</Enabled>
                      <UserId>{SecurityElement.Escape(user)}</UserId>
                      <Delay>PT5S</Delay>
                    </LogonTrigger>
                  </Triggers>
                  <Principals>
                    <Principal id="Author">
                      <UserId>{SecurityElement.Escape(user)}</UserId>
                      <LogonType>InteractiveToken</LogonType>
                      <RunLevel>LeastPrivilege</RunLevel>
                    </Principal>
                  </Principals>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <Enabled>true</Enabled>
                  </Settings>
                  <Actions Context="Author">
                    <Exec>
                      <Command>{SecurityElement.Escape(exe)}</Command>
                      <Arguments>--autostart</Arguments>
                    </Exec>
                  </Actions>
                </Task>
                """;

            string file = Path.Combine(Path.GetTempPath(), "SpotifyBar-task.xml");
            try
            {
                File.WriteAllText(file, xml, Encoding.Unicode); // schtasks expects UTF-16 task XML
                return SchTasks($"/create /tn \"{TaskName}\" /xml \"{file}\" /f");
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }

        public static void Disable() => SchTasks($"/delete /tn \"{TaskName}\" /f");

        /// <summary>Moves older installs from the Run key to the logon task.</summary>
        public static void MigrateFromRunKey()
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(TaskName) is not string) return;
            if (!IsEnabled() && !Enable()) return; // keep the old entry if the task couldn't be created
            run.DeleteValue(TaskName, throwOnMissingValue: false);
            using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, writable: true);
            approved?.DeleteValue(TaskName, throwOnMissingValue: false);
        }

        private static bool SchTasks(string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (p == null) return false;
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                return p.WaitForExit(15000) && p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
