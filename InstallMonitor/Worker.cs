using System;
using System.IO;
using System.Diagnostics;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace InstallMonitor
{
    [SupportedOSPlatform("windows")]
    public class Worker : BackgroundService
    {
        private readonly string logFilePath = "C:\\InstallMonitor\\install_log.txt";
        private readonly ConcurrentDictionary<string, FileInfo> trackedFiles = new ConcurrentDictionary<string, FileInfo>();
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();

        private readonly List<string> ignoredPaths;
        private readonly List<string> monitoredPaths;

        public Worker()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            ignoredPaths = new List<string>
            {
                Path.Combine(userProfile, "AppData", "Local", "Google", "Chrome"),
                Path.Combine(userProfile, "AppData", "Local", "Temp"),
                Path.Combine(userProfile, "AppData", "Roaming", "Microsoft", "Windows", "Recent"),
                Path.Combine(userProfile, "AppData", "Local", "Microsoft", "VSApplicationInsights"),
                @"C:\Windows\Prefetch",
                @"C:\Windows\System32\LogFiles"
            };

            monitoredPaths = new List<string>
            {
                Path.Combine(userProfile, "Desktop"),
                Path.Combine(userProfile, "Documents"),
                Path.Combine(userProfile, "Downloads"),
                @"C:\Program Files",
                @"C:\Program Files (x86)"
            };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("Installation monitor started...");
            Directory.CreateDirectory("C:\\InstallMonitor");

            _ = Task.Run(() => MonitorProcesses(), stoppingToken);
            _ = Task.Run(() => MonitorRegistry(), stoppingToken);
            _ = Task.Run(() => MonitorFiles(), stoppingToken);
            _ = Task.Run(() => ScanFileSystem(stoppingToken), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }

        private void MonitorProcesses()
        {
            Console.WriteLine("Starting process monitoring...");
            try
            {
                ManagementEventWatcher watcher = new ManagementEventWatcher(
                    new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));

                watcher.EventArrived += (sender, e) =>
                {
                    string? processName = e.NewEvent["ProcessName"]?.ToString();
                    int processId = Convert.ToInt32(e.NewEvent["ProcessID"]);

                    if (!string.IsNullOrEmpty(processName))
                    {
                        string processPath = GetProcessPath(processId);
                        Log($"🔵 Process started: {processName} | Path: {processPath}");
                    }
                };

                watcher.Start();
            }
            catch (Exception ex)
            {
                Log($"❌ Error monitoring processes: {ex.Message}");
            }
        }

        private string GetProcessPath(int processId)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                           $"SELECT ExecutablePath FROM Win32_Process WHERE ProcessID = {processId}"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        return obj["ExecutablePath"]?.ToString() ?? "Path not found";
                    }
                }
            }
            catch (Exception)
            {
                return "Access denied or path not available";
            }
            return "Path not found";
        }

        private void MonitorRegistry()
        {
            Console.WriteLine("Starting Windows registry monitoring...");

            string registryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            RegistryKey? key = Registry.LocalMachine.OpenSubKey(registryPath, true);

            if (key == null) return;

            previousOwner = key.GetValue("RegisteredOwner")?.ToString() ?? "Unknown";

            while (true)
            {
                Thread.Sleep(5000);
                key = Registry.LocalMachine.OpenSubKey(registryPath, true);
                if (key == null) continue;

                string registeredOwner = key.GetValue("RegisteredOwner")?.ToString() ?? "Unknown";
                if (registeredOwner != previousOwner)
                {
                    Log($"📝 Registry modified: {registryPath}\\RegisteredOwner | New Value: {registeredOwner}");
                    previousOwner = registeredOwner;
                }
            }
        }

        private string previousOwner = string.Empty;

        private void MonitorFiles()
        {
            Console.WriteLine("Starting file monitoring...");

            foreach (string path in monitoredPaths)
            {
                try
                {
                    if (!Directory.Exists(path)) continue;

                    FileSystemWatcher watcher = new FileSystemWatcher(path)
                    {
                        IncludeSubdirectories = true,
                        EnableRaisingEvents = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                    };

                    watcher.Created += (sender, e) => { if (!IsIgnored(e.FullPath)) Log($"📂 New file created: {e.FullPath}"); };
                    watcher.Changed += (sender, e) => { if (!IsIgnored(e.FullPath)) Log($"📝 File modified: {e.FullPath}"); };
                    watcher.Deleted += (sender, e) => { if (!IsIgnored(e.FullPath)) Log($"🗑️ File deleted: {e.FullPath}"); };
                    watcher.Renamed += (sender, e) => { if (!IsIgnored(e.FullPath)) Log($"🔄 File renamed: {e.OldFullPath} -> {e.FullPath}"); };

                    watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    Log($"❌ Error monitoring {path}: {ex.Message}");
                }
            }
        }

        private bool IsIgnored(string filePath)
        {
            return ignoredPaths.Any(path => filePath.StartsWith(path, StringComparison.OrdinalIgnoreCase)) || 
                   filePath.Equals(logFilePath, StringComparison.OrdinalIgnoreCase);
        }

        private async Task ScanFileSystem(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(5000, stoppingToken);

                var filesToRemove = new List<string>();

                foreach (var file in trackedFiles.Keys)
                {
                    if (!File.Exists(file))
                    {
                        Log($"🗑️ File deleted (detected by scan): {file}");
                        filesToRemove.Add(file);
                    }
                }

                foreach (var file in filesToRemove)
                {
                    trackedFiles.TryRemove(file, out _);
                }
            }
        }

        private void Log(string message)
        {
            string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {message}";
            File.AppendAllText(logFilePath, logMessage + "\n");

            Console.ForegroundColor = message.Contains("New file created") ? ConsoleColor.Magenta :
                                      message.Contains("Process started") ? ConsoleColor.Cyan :
                                      message.Contains("File modified") ? ConsoleColor.Yellow :
                                      message.Contains("File deleted") ? ConsoleColor.Red :
                                      message.Contains("File renamed") ? ConsoleColor.Blue :
                                      ConsoleColor.White;

            Console.WriteLine(logMessage);
            Console.ResetColor();
        }
    }
}
