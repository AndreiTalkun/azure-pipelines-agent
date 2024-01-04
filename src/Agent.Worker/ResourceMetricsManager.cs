// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Agent.Sdk;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Agent.Worker.Telemetry;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    [ServiceLocator(Default = typeof(ResourceMetricsManager))]
    public interface IResourceMetricsManager : IAgentService, IDisposable
    {
        Task RunDebugResourceMonitor();
        Task RunMemoryUtilizationMonitor();
        Task RunDiskSpaceUtilizationMonitor();
        Task RunCpuUtilizationMonitor(string taskId);
        void Setup(IExecutionContext context);
        void SetContext(IExecutionContext context);
    }

    public sealed class ResourceMetricsManager : AgentService, IResourceMetricsManager
    {
        const int ACTIVE_MODE_INTERVAL = 5000;
        const int WARNING_MESSAGE_INTERVAL = 10000;
        const int AVALIABLE_DISK_SPACE_PERCENAGE_THRESHOLD = 5;
        const int AVALIABLE_MEMORY_PERCENTAGE_THRESHOLD = 5;
        const int AVALIABLE_CPU_PERCENTAGE_THRESHOLD = 5;

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
                _context.Warning(StringUtil.Loc("ResourceMonitorProcessError", ex.Message));
            }
        }
        public void SetContext(IExecutionContext context)
        {
            ArgUtil.NotNull(context, nameof(context));
            _context = context;
        }
        public async Task RunDebugResourceMonitor()
        {
            while (!_context.CancellationToken.IsCancellationRequested)
            {
                _context.Debug(StringUtil.Loc("ResourceMonitorAgentEnvironmentResource", GetDiskInfoString(), GetMemoryInfoString(), GetCpuInfoString()));
                await Task.Delay(ACTIVE_MODE_INTERVAL, _context.CancellationToken);
            }
        }

        private void PublishTelemetry(string message, string taskId)
        {
            try
            {
                Dictionary<string, string> telemetryData = new Dictionary<string, string>
                        {
                            { "TaskId", taskId },
                            { "JobId", _context.Variables.System_JobId.ToString() },
                            { "PlanId", _context.Variables.Get(Constants.Variables.System.PlanId) },
                            { "Warning", message }
                        };

                var cmd = new Command("telemetry", "publish")
                {
                    Data = JsonConvert.SerializeObject(telemetryData, Formatting.None)
                };

                cmd.Properties.Add("area", "AzurePipelinesAgent");
                cmd.Properties.Add("feature", "ResourceUtilization");

                var publishTelemetryCmd = new TelemetryCommandExtension();
                publishTelemetryCmd.Initialize(HostContext);
                publishTelemetryCmd.ProcessCommand(_context, cmd);
            }
            catch (Exception ex)
            {
                _context.Debug(ex.ToString());
            }
        }

        public async Task RunDiskSpaceUtilizationMonitor() 
        {
            while (!_context.CancellationToken.IsCancellationRequested)
            {
                try
                {
                    var diskInfo = GetDiskInfo();

                    var freeDiskSpacePercentage = Math.Round(((diskInfo.FreeDiskSpaceMB / (double)diskInfo.TotalDiskSpaceMB) * 100.0), 2);
                    var usedDiskSpacePercentage = 100.0 - freeDiskSpacePercentage;

                    if (freeDiskSpacePercentage <= AVALIABLE_DISK_SPACE_PERCENAGE_THRESHOLD)
                    {
                        _context.Warning(StringUtil.Loc("ResourceMonitorFreeDiskSpaceIsLowerThanThreshold", diskInfo.VolumeLabel, AVALIABLE_DISK_SPACE_PERCENAGE_THRESHOLD, $"{usedDiskSpacePercentage:0.00}"));
                        
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _context.Warning(StringUtil.Loc("ResourceMonitorDiskInfoError", ex.Message));

                    break;
                }

                await Task.Delay(WARNING_MESSAGE_INTERVAL, _context.CancellationToken);
            }
        }

        public async Task RunMemoryUtilizationMonitor()
        {
            while (!_context.CancellationToken.IsCancellationRequested)
            {
                try
                {
                    var memoryInfo = GetMemoryInfo();

                    var usedMemoryPercentage = Math.Round(((memoryInfo.UsedMemoryMB / (double)memoryInfo.TotalMemoryMB) * 100.0), 2);
                    var freeMemoryPercentage = 100.0 - usedMemoryPercentage;

                    if (freeMemoryPercentage <= AVALIABLE_MEMORY_PERCENTAGE_THRESHOLD)
                    {
                        _context.Warning(StringUtil.Loc("ResourceMonitorMemorySpaceIsLowerThanThreshold", AVALIABLE_MEMORY_PERCENTAGE_THRESHOLD, $"{usedMemoryPercentage:0.00}"));
                        
                        break;
                    }
                }
                catch (MemoryMonitoringUtilityIsNotAvaliableException ex)
                {
                    Trace.Warning($"Unable to get memory info using \"free\" utility; {ex.Message}");

                    break;
                }
                catch (Exception ex)
                {
                    _context.Warning(StringUtil.Loc("ResourceMonitorMemoryInfoError", ex.Message));

                    break;
                }

                await Task.Delay(WARNING_MESSAGE_INTERVAL, _context.CancellationToken);
            }
        }

        public async Task RunCpuUtilizationMonitor(string taskId)
        {
            while (!_context.CancellationToken.IsCancellationRequested)
            {
                try
                {
                    var usedCpuPercentage = GetCpuInfo();
                    var freeCpuPercentage = 100.0 - usedCpuPercentage;

                    if (freeCpuPercentage <= AVALIABLE_CPU_PERCENTAGE_THRESHOLD)
                    {
                        string message = $"CPU usage is higher than {AVALIABLE_CPU_PERCENTAGE_THRESHOLD}%; currently used: {usedCpuPercentage:0.00}%";

                        PublishTelemetry(message, taskId);

                        break;
                    }

                }
                catch (Exception ex)
                {
                    Trace.Warning($"Unable to get CPU info; {ex.Message}");

                    break;
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

                return StringUtil.Loc("ResourceMonitorDiskInfo", diskInfo.VolumeLabel, $"{diskInfo.FreeDiskSpaceMB:0.00}", $"{diskInfo.TotalDiskSpaceMB:0.00}");

            }
            catch (Exception ex)
            {
                return StringUtil.Loc("ResourceMonitorDiskInfoError", ex.Message);
            }
        }

        private Process _currentProcess;

        public double GetCpuInfo()
        {
            if (_currentProcess == null)
            {
                throw new Exception("Current process is unavailable");
            }

            TimeSpan totalCpuTime = _currentProcess.TotalProcessorTime;
            TimeSpan elapsedTime = DateTime.Now - _currentProcess.StartTime;
            double cpuUsage = (totalCpuTime.TotalMilliseconds / elapsedTime.TotalMilliseconds) * 100.0;

            return cpuUsage;
        }

        public string GetCpuInfoString()
        {
            try
            {
                return StringUtil.Loc("ResourceMonitorCPUInfo", $"{GetCpuInfo():0.00}");
            }
            catch (Exception ex)
            {
                return StringUtil.Loc("ResourceMonitorCPUInfoError", ex.Message);
            }
        }

        // Some compact Linux distributives like UBI may not have "free" utility installed,
        // but we don't want to break currently existing pipelines, so ADO warning should be mitigated to the trace warning
        public class MemoryMonitoringUtilityIsNotAvaliableException : Exception
        {
            public MemoryMonitoringUtilityIsNotAvaliableException(string message)
                : base(message)
            {
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
                try
                {
                    processStartInfo.FileName = "free";
                    processStartInfo.Arguments = "-m";
                    processStartInfo.RedirectStandardOutput = true;

                    using (var process = Process.Start(processStartInfo))
                    {
                        processStartInfoOutput = process.StandardOutput.ReadToEnd();
                    }

                    var processStartInfoOutputString = processStartInfoOutput.Split("\n");
                    var memoryInfoString = processStartInfoOutputString[1].Split(" ", StringSplitOptions.RemoveEmptyEntries);

                    if (memoryInfoString.Length != 7)
                    {
                        throw new MemoryMonitoringUtilityIsNotAvaliableException("Utility has non-default output");
                    }

                    memoryInfo.TotalMemoryMB = Int32.Parse(memoryInfoString[1]);
                    memoryInfo.UsedMemoryMB = Int32.Parse(memoryInfoString[2]);
                }
                catch (Win32Exception e)
                {
                    throw new MemoryMonitoringUtilityIsNotAvaliableException(e.Message);
                }
            }

            if (PlatformUtil.RunningOnMacOS)
            {
                processStartInfo.FileName = "vm_stat";
                processStartInfo.RedirectStandardOutput = true;

                using (var process = Process.Start(processStartInfo))
                {
                    processStartInfoOutput = process.StandardOutput.ReadToEnd();
                }

                var processStartInfoOutputString = processStartInfoOutput.Split("\n");

                var pageSize = Int32.Parse(processStartInfoOutputString[0].Split(" ", StringSplitOptions.RemoveEmptyEntries)[7]);

                var pagesFree = Int64.Parse(processStartInfoOutputString[1].Split(" ", StringSplitOptions.RemoveEmptyEntries)[2].Trim('.'));
                var pagesActive = Int64.Parse(processStartInfoOutputString[2].Split(" ", StringSplitOptions.RemoveEmptyEntries)[2].Trim('.'));
                var pagesInactive = Int64.Parse(processStartInfoOutputString[3].Split(" ", StringSplitOptions.RemoveEmptyEntries)[2].Trim('.'));
                var pagesSpeculative = Int64.Parse(processStartInfoOutputString[4].Split(" ", StringSplitOptions.RemoveEmptyEntries)[2].Trim('.'));
                var pagesWiredDown = Int64.Parse(processStartInfoOutputString[6].Split(" ", StringSplitOptions.RemoveEmptyEntries)[3].Trim('.'));
                var pagesOccupied = Int64.Parse(processStartInfoOutputString[16].Split(" ", StringSplitOptions.RemoveEmptyEntries)[4].Trim('.'));

                var freeMemory = (pagesFree + pagesInactive) * pageSize;
                var usedMemory = (pagesActive + pagesSpeculative + pagesWiredDown + pagesOccupied) * pageSize;

                memoryInfo.TotalMemoryMB = (int)((freeMemory + usedMemory) / 1048576);
                memoryInfo.UsedMemoryMB = (int)(usedMemory / 1048576);
            }

            return memoryInfo;
        }

        public string GetMemoryInfoString()
        {
            try
            {
                var memoryInfo = GetMemoryInfo();

                return StringUtil.Loc("ResourceMonitorMemoryInfo", $"{memoryInfo.UsedMemoryMB:0.00}", $"{memoryInfo.TotalMemoryMB:0.00}");
            }
            catch (Exception ex)
            {
                return StringUtil.Loc("ResourceMonitorMemoryInfoError", ex.Message);
            }
        }

        public void Dispose()
        {
            _currentProcess?.Dispose();
        }
    }
}
