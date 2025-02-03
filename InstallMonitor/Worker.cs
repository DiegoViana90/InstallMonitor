using System;
using System.IO;
using System.Diagnostics;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;

namespace InstallMonitor
{
    [SupportedOSPlatform("windows")]
    public class Worker : BackgroundService
    {
        private readonly string logFilePath = "C:\\InstallMonitor\\install_log.txt";

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("Installation monitor started...");
            Directory.CreateDirectory("C:\\InstallMonitor");

            await Task.Run(() => MonitorProcesses());
            await Task.Run(() => MonitorRegistry());
            await Task.Run(() => MonitorFiles());

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

    List<string> installedPrograms = new List<string>(key.GetSubKeyNames());

    while (true)
    {
        Thread.Sleep(5000); // Check every 5 seconds
        key = Registry.LocalMachine.OpenSubKey(registryPath, true);
        if (key == null) continue;

        List<string> newPrograms = new List<string>(key.GetSubKeyNames());
        foreach (var program in newPrograms)
        {
            if (!installedPrograms.Contains(program))
            {
                Log($"🟢 New software installed: {program}");
                installedPrograms.Add(program);
            }
        }

        // Capturar mudanças nos valores do registro (como "RegisteredOwner")
        string registeredOwner = key.GetValue("RegisteredOwner")?.ToString() ?? "Unknown";
        if (registeredOwner != previousOwner)
        {
            Log($"📝 Registry modified: {registryPath}\\RegisteredOwner | New Value: {registeredOwner}");
            previousOwner = registeredOwner;
        }
    }
}

// Variável global para armazenar o valor anterior
private string previousOwner = string.Empty;


        private void MonitorFiles()
        {
            Console.WriteLine("Starting file monitoring...");

            string[] drives = Environment.GetLogicalDrives(); // Get all drives (C:, D:, etc.)

            foreach (string drive in drives)
            {
                try
                {
                    FileSystemWatcher watcher = new FileSystemWatcher(drive)
                    {
                        IncludeSubdirectories = true,
                        EnableRaisingEvents = true
                    };

                    watcher.Created += (sender, e) => LogFileDetails(e.FullPath, "📂 New file created");
                    watcher.Changed += (sender, e) => LogFileDetails(e.FullPath, "📝 File modified");
                    watcher.Deleted += (sender, e) => LogFileDetails(e.FullPath, "🗑️ File deleted");
                    watcher.Renamed += (sender, e) => LogFileDetails(e.FullPath, "🔄 File renamed");
                }
                catch (Exception ex)
                {
                    Log($"❌ Error monitoring {drive}: {ex.Message}");
                }
            }
        }

        private void LogFileDetails(string filePath, string action)
        {
            try
            {
                FileInfo fileInfo = new FileInfo(filePath);
                string logMessage = $"{action}: {fileInfo.FullName} | " +
                                    $"Size: {fileInfo.Length / 1024} KB | " +
                                    $"Last Modified: {fileInfo.LastWriteTime}";

                Log(logMessage);
            }
            catch (Exception)
            {
                Log($"{action}: {filePath} (File details unavailable)");
            }
        }

        private void Log(string message)
        {
            string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {message}";
            File.AppendAllText(logFilePath, logMessage + "\n");

            // Exibir com cor no terminal
            Console.ForegroundColor = message.Contains("New software installed") ? ConsoleColor.Green :
                                      message.Contains("Process started") ? ConsoleColor.Cyan :
                                      message.Contains("File modified") ? ConsoleColor.Yellow :
                                      message.Contains("File deleted") ? ConsoleColor.Red :
                                      message.Contains("New file created") ? ConsoleColor.Magenta :
                                      ConsoleColor.White;

            Console.WriteLine(logMessage);
            Console.ResetColor();
        }
    }
}
