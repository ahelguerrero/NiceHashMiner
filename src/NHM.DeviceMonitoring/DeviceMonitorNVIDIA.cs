﻿using ManagedCuda.Nvml;
using NHM.DeviceMonitoring.NVIDIA;
using NHM.Common;
using NHM.Common.Enums;
using NVIDIA.NVAPI;
using System;
using System.Diagnostics;

namespace NHM.DeviceMonitoring
{
    internal class DeviceMonitorNVIDIA : DeviceMonitor,
        IFanSpeed, ILoad, IPowerUsage, ITemp,
        IPowerTarget, IPowerLevel, ISetPowerLevel, ISetPowerTargetPercentage
    {
        public class PowerOutOfRangeException : ArgumentOutOfRangeException
        {
            /// <summary>
            /// The next closest power limit that can be set
            /// </summary>
            public double ClosestValue;

            public PowerOutOfRangeException(double closest)
            {
                ClosestValue = closest;
            }
        }

        public static object _lock = new object();

        private static readonly TimeSpan _delayedLogging = TimeSpan.FromMinutes(5);

        public int BusID { get; private set; }
        private NvPhysicalGpuHandle _nvHandle; // For NVAPI
        private nvmlDevice _nvmlDevice; // For NVML
        private const int GpuCorePState = 0; // memcontroller = 1, videng = 2
        private uint _minPowerLimit;
        private uint _defaultPowerLimit;
        private uint _maxPowerLimit;

        public bool PowerLimitsEnabled { get; private set; }

        internal DeviceMonitorNVIDIA(NvapiNvmlInfo info)
        {
            UUID = info.UUID;
            BusID = info.BusID;
            _nvHandle = info.nvHandle;
            _nvmlDevice = info.nvmlHandle;

            try
            {
                trySetPowerInfo(); // new function() more cleaner code

                PowerLimitsEnabled = true;
                // set to high by default
                var defaultLevel = PowerLevel.High;
                if (!DeviceMonitorManager.DisableDevicePowerModeSettings)
                {
                    var success = SetPowerTarget(defaultLevel);
                    if (!success)
                    {
                        Logger.Info("NVML", $"Cannot set power target ({defaultLevel.ToString()}) for device with BusID={BusID}");
                    }
                }
                else
                {
                    PowerLevel = PowerLevel.Disabled; // what the purpose of this?
                }
            }
            catch (Exception e)
            {
                Logger.Error("NVML", $"Getting power info failed with message \"{e.Message}\", disabling power setting");
                PowerLimitsEnabled = false;
                PowerLevel = PowerLevel.Disabled;
            }
        }
        private void trySetPowerInfo()
        {

            //var powerInfo = new NvGPUPowerInfo
            //{
            //    Version = NVAPI.GPU_POWER_INFO_VER,
            //    Entries = new NvGPUPowerInfoEntry[4]
            //};

            //var ret = NVAPI.NvAPI_DLL_ClientPowerPoliciesGetInfo(_nvHandle, ref powerInfo);
            var _min = 0u;
            var _max = 0u;
            var ret = NvmlNativeMethods.nvmlDeviceGetPowerManagementLimitConstraints(_nvmlDevice, ref _min, ref _max);
            if (ret != nvmlReturn.Success)
                throw new Exception(ret.ToString());
            var _default = 0u;
            ret = NvmlNativeMethods.nvmlDeviceGetPowerManagementDefaultLimit(_nvmlDevice, ref _default);
            if (ret != nvmlReturn.Success)
                throw new Exception(ret.ToString());

            //Debug.Assert(powerInfo.Entries.Length == 4);

            //if (powerInfo.Entries[0].MinPower == 0 || powerInfo.Entries[0].MaxPower == 0)
            //{
            //    throw new Exception("Power control not available!");
            //}

            //_minPowerLimit = powerInfo.Entries[0].MinPower;
            //_maxPowerLimit = powerInfo.Entries[0].MaxPower;
            //_defaultPowerLimit = powerInfo.Entries[0].DefPower;

            _minPowerLimit = _min;
            _maxPowerLimit = _max;
            _defaultPowerLimit = _default;

        }
        public void ResetHandles(NvapiNvmlInfo info)
        {
            _nvHandle = info.nvHandle;
            _nvmlDevice = info.nvmlHandle;
            if (PowerLevel != PowerLevel.Disabled)
            {
                try
                {
                    trySetPowerInfo(); // new function make cleaner code
                }
                catch (Exception e)
                {
                    Logger.Error("NVAPI", $"Getting power info failed with message \"{e.Message}\", disabling power setting");
                    PowerLimitsEnabled = false;
                }
            }
        }


        public float Load
        {
            get
            {
                var load = -1;

                using (var tryLock = new TryLock(_lock))
                {
                    if(tryLock.HasLock)
                    {
                        try
                        {
                            var rates = new nvmlUtilization();
                            var ret = NvmlNativeMethods.nvmlDeviceGetUtilizationRates(_nvmlDevice, ref rates);
                            if (ret == nvmlReturn.NotSupported)
                                return load;
                            if( ret == nvmlReturn.Unknown)
                                throw new Exception($"NVML get load failed with code: {ret}");

                            load = (int)rates.gpu;
                        }
                        catch (Exception e)
                        {
                            Logger.ErrorDelayed("NVML", e.ToString(), _delayedLogging);
                            DeviceMonitorManager.TryRestartNVIDIAMonitoring();
                        }
                    }
                    else
                    {
                        Logger.Error("NVML", "Load locked");
                    }
                }

                return load;
            }
        }

        public float Temp
        {
            get
            {
                var temp = -1f;
                using (var tryLock = new TryLock(_lock))
                {
                    if (tryLock.HasLock)
                    {
                        try
                        {
                            var utemp = 0u;
                            var ret = NvmlNativeMethods.nvmlDeviceGetTemperature(_nvmlDevice, nvmlTemperatureSensors.Gpu,
                                ref utemp);
                            if (ret == nvmlReturn.NotSupported)
                                return temp;
                            if (ret == nvmlReturn.Unknown)
                                throw new Exception($"NVML get temp failed with code: {ret}");
                            

                            temp = utemp;
                        }
                        catch (Exception e)
                        {
                            Logger.ErrorDelayed("NVML", e.ToString(), _delayedLogging);
                            DeviceMonitorManager.TryRestartNVIDIAMonitoring();
                        }
                    }
                    else
                    {
                        Logger.Error("NVML", "Temp locked");
                    }
                }

                return temp;
            }
        }

        public int FanSpeed
        {
            get
            {
                var fanSpeed = -1;
                using (var tryLock = new TryLock(_lock))
                {
                    if (tryLock.HasLock)
                    {
                        if (NVAPI.NvAPI_GPU_GetTachReading != null)
                        {
                            var result = NVAPI.NvAPI_GPU_GetTachReading(_nvHandle, out fanSpeed);
                            if (result != NvStatus.OK && result != NvStatus.NOT_SUPPORTED)
                            {
                                // GPUs without fans are not uncommon, so don't treat as error and just return -1
                                Logger.ErrorDelayed("NVAPI", $"Tach get failed with status: {result}", _delayedLogging);
                                DeviceMonitorManager.TryRestartNVIDIAMonitoring();
                                return -1;
                            }
                        }
                    }
                    else
                    {
                        Logger.Error("NVML", "FanSpeed locked");
                    }
                }
                return fanSpeed;
            }
        }

        public double PowerUsage
        {
            get
            {
                using (var tryLock = new TryLock(_lock))
                {
                    if (tryLock.HasLock)
                    {
                        try
                        {
                            var power = 0u;
                            var ret = NvmlNativeMethods.nvmlDeviceGetPowerUsage(_nvmlDevice, ref power);
                            if (ret == nvmlReturn.NotSupported)
                                return power;
                            if(ret == nvmlReturn.Unknown)
                                throw new Exception($"NVML power get failed with status: {ret}");

                            return power * 0.001;
                        }
                        catch (Exception e)
                        {
                            Logger.ErrorDelayed("NVML", e.ToString(), _delayedLogging);
                            DeviceMonitorManager.TryRestartNVIDIAMonitoring();
                        }
                    }
                    else
                    {
                        Logger.Error("NVML", "PowerUsage locked");
                    }
                }

                return -1;
            }
        }

        public uint PowerTarget { get; private set; }

        public PowerLevel PowerLevel { get; private set; } = PowerLevel.High;

        private static uint GetUintFromPowerLevel(PowerLevel powerLevel)
        {
            switch (powerLevel)
            {
                case PowerLevel.Low: return 0;
                case PowerLevel.Medium: return 1;
                case PowerLevel.High: return 2;
                default: throw new Exception("GetUintFromPowerLevel - Unknown PowerLevel");
            }
        }

        // TODO monitor recovery
        private bool SetTdp(double tdpPerc, uint defaultLimit)
        {
            uint currentLimit = (uint)(tdpPerc * (double)defaultLimit) / 100;
            var ret = NvmlNativeMethods.nvmlDeviceSetPowerManagementLimit(_nvmlDevice, currentLimit);
            if (ret != nvmlReturn.Success)
                throw new Exception($"NVML nvmlDeviceGetPowerManagementLimitConstraints failed with status: {ret}");
            
            return true;
        }

        private bool SetTdpSimple(PowerLevel level)
        {
            try
            {
                uint minLimit = 0;
                uint maxLimit = 0;
                var ret = NvmlNativeMethods.nvmlDeviceGetPowerManagementLimitConstraints(_nvmlDevice, ref minLimit, ref maxLimit);
                if (ret != nvmlReturn.Success)
                    throw new Exception($"NVML nvmlDeviceGetPowerManagementLimitConstraints failed with status: {ret}");

                uint defaultLimit = 0;
                ret = NvmlNativeMethods.nvmlDeviceGetPowerManagementDefaultLimit(_nvmlDevice, ref defaultLimit);
                if (ret != nvmlReturn.Success)
                    throw new Exception($"NVML nvmlDeviceGetPowerManagementDefaultLimit failed with status: {ret}");

                var limit = minLimit + (defaultLimit - minLimit) * GetUintFromPowerLevel(level) / 2;
                var tdpPerc = (limit * 100.0 / defaultLimit);
                return SetTdp(tdpPerc, defaultLimit);
            }
            catch (Exception e)
            {
                Logger.ErrorDelayed("NVML", e.ToString(), _delayedLogging);
                return false;
            }
        }

        // nvPercent in thousands of percent, e.g. 100000 for 100%
        private bool SetPowerTarget(uint nvPercent)
        {
            if (!PowerLimitsEnabled) return false;
            if (NVAPI.NvAPI_DLL_ClientPowerPoliciesSetStatus == null)
            {
                Logger.InfoDelayed("NVAPI", "Missing power set delegate, disabling power", _delayedLogging);
                PowerLimitsEnabled = false;
                return false;
            }

            // Value of 0 corresponds to not touching anything
            if (nvPercent == uint.MinValue)
            {
                PowerTarget = nvPercent;
                return true;
            }

            // Check if given value is within bounds
            if (nvPercent < _minPowerLimit)
                throw new PowerOutOfRangeException(_minPowerLimit);
            if (nvPercent > _maxPowerLimit)
                throw new PowerOutOfRangeException(_maxPowerLimit);

            var status = new NvGPUPowerStatus
            {
                Flags = 1,
                Entries = new NvGPUPowerStatusEntry[4]
            };
            status.Entries[0].Power = nvPercent;  // percent * 1000
            status.Version = NVAPI.GPU_POWER_STATUS_VER;

            try
            {
                var ret = NVAPI.NvAPI_DLL_ClientPowerPoliciesSetStatus(_nvHandle, ref status);
                if (ret != NvStatus.OK)
                    throw new Exception($"NVAPI failed with return {ret}");
            }
            catch (Exception e)
            {
                Logger.InfoDelayed("NVAPI", e.Message, _delayedLogging);
                return false;
            }

            PowerTarget = nvPercent;

            return true;
        }

        public bool SetPowerTarget(PowerLevel level)
        {
            PowerLevel = level;
            if(SetTdpSimple(level)) return true;

            switch (level)
            {
                case PowerLevel.Low:
                    PowerLevel = level;
                    return SetPowerTarget(_minPowerLimit);
                case PowerLevel.Medium:
                    PowerLevel = level;
                    return SetPowerTarget((uint)Math.Round((_minPowerLimit + _defaultPowerLimit) / 2d));
                case PowerLevel.High:
                    PowerLevel = level;
                    return SetPowerTarget(_defaultPowerLimit);
            }

            return false;
        }

        // percent is in hundreds, e.g. 100%
        public bool SetPowerTarget(double percent)
        {
            PowerLevel = PowerLevel.Custom;
            return SetPowerTarget((uint)Math.Round(percent * 1000));
        }
    }

}
