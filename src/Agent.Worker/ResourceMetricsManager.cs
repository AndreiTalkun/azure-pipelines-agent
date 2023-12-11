// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

using Agent.Sdk;

using Microsoft.VisualStudio.Services.Agent.Util;
using static Microsoft.VisualStudio.Services.Agent.Worker.ResourceMetricsManager;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    [ServiceLocator(Default = typeof(ResourceMetricsManager))]
    public interface IResourceMetricsManager : IAgentService, IDisposable
    {
        Task Run();
        Task RunResourceUtilizationMonitor();
        void Setup(IExecutionContext context);
        void SetContext(IExecutionContext context);
    }

    public sealed class ResourceMetricsManager : AgentService, IResourceMetricsManager
    {
        const int ACTIVE_MODE_INTERVAL = 5000;
        const int WARNING_MESSAGE_INTERVAL = 10000;
        const int AVALIABLE_DISC_SPACE_PERCENAGE_THRESHOLD = 5;
        const int AVALIABLE_MEMORY_PERCENTAGE_THRESHOLD = 5;
        IExecutionContext _context;

        public void Setup(IExecutionContext context)
        {
            //initial context
            ArgUtil.NotNull(context, nameof(context));
            _context = context;

            try
            {
                _currentProcess = Process.GetCurrentProcess();
            }
            catch (Exception ex)
            {
                _context.Warning($"Unable to get current process, ex:{ex.Message}");
            }
        }
        public void SetContext(IExecutionContext context)
        {
            ArgUtil.NotNull(context, nameof(context));
            _context = context;
        }
        public async Task Run()
        {
            while (!_context.CancellationToken.IsCancellationRequested)
            {
                _context.Debug($"Agent running environment resource - {GetDiskInfoString()}, {GetMemoryInfoString()}, {GetCpuInfoString()}");
                await Task.Delay(ACTIVE_MODE_INTERVAL, _context.CancellationToken);
            }
        }

        public async Task RunResourceUtilizationMonitor() 
        {
            while (!_context.CancellationToken.IsCancellationRequested)
            {
                try
                {
                    var diskInfo = GetDiskInfo();

                    var freeDiskSpacePercentage = Math.Round(((diskInfo.FreeDiskSpaceMB / (double)diskInfo.TotalDiskSpaceMB) * 100.0), 2);
                    var usedDiskSpacePercentage = 100.0 - freeDiskSpacePercentage;

                    if (freeDiskSpacePercentage <= AVALIABLE_DISC_SPACE_PERCENAGE_THRESHOLD)
                    {
                        _context.Warning($"Free disk space on volume {diskInfo.VolumeLabel} is lower than {AVALIABLE_DISC_SPACE_PERCENAGE_THRESHOLD}%; Currently used: {usedDiskSpacePercentage}%");
                    }
                }
                catch (Exception ex)
                {
                    _context.Warning($"Unable to get Disk info, ex:{ex.Message}");
                }

                try
                {
                    var memoryInfo = GetMemoryInfo();

                    var usedMemoryPercentage = Math.Round(((memoryInfo.UsedMemoryMB / (double)memoryInfo.TotalMemoryMB) * 100.0), 2);
                    var freeMemoryPercentage = 100.0 - usedMemoryPercentage;

                    if (freeMemoryPercentage <= AVALIABLE_MEMORY_PERCENTAGE_THRESHOLD)
                    {
                        _context.Warning($"Free memory is lower than {AVALIABLE_MEMORY_PERCENTAGE_THRESHOLD}%; Currently used: {usedMemoryPercentage}");
                    }
                }
                catch (Exception ex)
                {
                    _context.Warning($"Unable to get Memory info, ex:{ex.Message}");
                }

                await Task.Delay(WARNING_MESSAGE_INTERVAL, _context.CancellationToken);
            }
        }

        public struct DiskInfo
        {
            public long TotalDiskSpaceMB;
            public long FreeDiskSpaceMB;
            public string VolumeLabel;
        }

        public DiskInfo GetDiskInfo()
        {
            DiskInfo diskInfo = new();

            string root = Path.GetPathRoot(System.Reflection.Assembly.GetEntryAssembly().Location);
            var driveInfo = new DriveInfo(root);

            diskInfo.TotalDiskSpaceMB = driveInfo.TotalSize / 1048576;
            diskInfo.FreeDiskSpaceMB = driveInfo.AvailableFreeSpace / 1048576;

            if (PlatformUtil.RunningOnWindows)
            {
                diskInfo.VolumeLabel = $"{root} {driveInfo.VolumeLabel}";
            }

            return diskInfo;
        }

        public string GetDiskInfoString()
        {
            try
            {
                var diskInfo = GetDiskInfo();

                return $"Disk: Volume {diskInfo.VolumeLabel} - Available {diskInfo.FreeDiskSpaceMB:0.00} MB out of {diskInfo.TotalDiskSpaceMB:0.00} MB";

            }
            catch (Exception ex)
            {
                return $"Unable to get Disk info, ex:{ex.Message}";
            }
        }

        private Process _currentProcess;

        public string GetCpuInfoString()
        {
            if (_currentProcess == null)
            {
                return $"Unable to get CPU info";
            }

            try
            {
                TimeSpan totalCpuTime = _currentProcess.TotalProcessorTime;
                TimeSpan elapsedTime = DateTime.Now - _currentProcess.StartTime;
                double cpuUsage = (totalCpuTime.TotalMilliseconds / elapsedTime.TotalMilliseconds) * 100.0;

                return $"CPU: usage {cpuUsage:0.00}";
            }
            catch (Exception ex)
            {
                return $"Unable to get CPU info, ex:{ex.Message}";
            }
        }

        public struct MemoryInfo
        {
            public int TotalMemoryMB;
            public int UsedMemoryMB;
        }

        public MemoryInfo GetMemoryInfo()
        {
            MemoryInfo memoryInfo = new();

            ProcessStartInfo processStartInfo = new ProcessStartInfo();
            var processStartInfoOutput = "";

            if (PlatformUtil.RunningOnWindows)
            {
                processStartInfo.FileName = "wmic";
                processStartInfo.Arguments = "OS GET FreePhysicalMemory,TotalVisibleMemorySize /Value";
                processStartInfo.RedirectStandardOutput = true;

                using (var process = Process.Start(processStartInfo))
                {
                    processStartInfoOutput = process.StandardOutput.ReadToEnd();
                }

                var processStartInfoOutputString = processStartInfoOutput.Trim().Split("\n");

                var freeMemory = Int32.Parse(processStartInfoOutputString[0].Split("=", StringSplitOptions.RemoveEmptyEntries)[1]);
                var totalMemory = Int32.Parse(processStartInfoOutputString[1].Split("=", StringSplitOptions.RemoveEmptyEntries)[1]);

                memoryInfo.TotalMemoryMB = totalMemory / 1024;
                memoryInfo.UsedMemoryMB = (totalMemory - freeMemory) / 1024;
            }

            if (PlatformUtil.RunningOnLinux)
            {
                processStartInfo.FileName = "/bin/sh";
                processStartInfo.Arguments = "-c \"free -m\"";
                processStartInfo.RedirectStandardOutput = true;

                using (var process = Process.Start(processStartInfo))
                {
                    processStartInfoOutput = process.StandardOutput.ReadToEnd();
                }

                var processStartInfoOutputString = processStartInfoOutput.Split("\n");
                var memoryInfoString = processStartInfoOutputString[1].Split(" ", StringSplitOptions.RemoveEmptyEntries);

                memoryInfo.TotalMemoryMB = Int32.Parse(memoryInfoString[1]);
                memoryInfo.UsedMemoryMB = Int32.Parse(memoryInfoString[2]);
            }

            if (PlatformUtil.RunningOnMacOS)
            {
                processStartInfo.FileName = "/bin/sh";
                processStartInfo.Arguments = "-c \"vm_stat\"";
                processStartInfo.RedirectStandardOutput = true;

                using (var process = Process.Start(processStartInfo))
                {
                    processStartInfoOutput = process.StandardOutput.ReadToEnd();
                }

                var processStartInfoOutputString = processStartInfoOutput.Split("\n");

                var pageSize = Int32.Parse(processStartInfoOutputString[0].Split(" ", StringSplitOptions.RemoveEmptyEntries)[7]);

                var pagesFree = Int32.Parse(processStartInfoOutputString[1].Split(" ", StringSplitOptions.RemoveEmptyEntries)[2]);
                var pagesActive = Int32.Parse(processStartInfoOutputString[2].Split(" ", StringSplitOptions.RemoveEmptyEntries)[2]);
                var pagesInactive = Int32.Parse(processStartInfoOutputString[3].Split(" ", StringSplitOptions.RemoveEmptyEntries)[2]);
                var pagesSpeculative = Int32.Parse(processStartInfoOutputString[4].Split(" ", StringSplitOptions.RemoveEmptyEntries)[2]);
                var pagesWiredDown = Int32.Parse(processStartInfoOutputString[6].Split(" ", StringSplitOptions.RemoveEmptyEntries)[3]);
                var pagesOccupied = Int32.Parse(processStartInfoOutputString[16].Split(" ", StringSplitOptions.RemoveEmptyEntries)[4]);

                var freeMemory = (pagesFree + pagesInactive) * pageSize;
                var usedMemory = (pagesActive + pagesSpeculative + pagesWiredDown + pagesOccupied) * pageSize;

                memoryInfo.TotalMemoryMB = (freeMemory + usedMemory) / 1048576;
                memoryInfo.UsedMemoryMB = usedMemory / 1048576;
            }

            return memoryInfo;
        }

        public string GetMemoryInfoString()
        {
            try
            {
                var memoryInfo = GetMemoryInfo();

                return $"Memory: Used {memoryInfo.UsedMemoryMB:0.00} MB out of {memoryInfo.TotalMemoryMB:0.00} MB";
            }
            catch (Exception ex)
            {
                return $"Unable to get Memory info, ex:{ex.Message}";
            }
        }

        public void Dispose()
        {
            _currentProcess?.Dispose();
        }
    }
}
