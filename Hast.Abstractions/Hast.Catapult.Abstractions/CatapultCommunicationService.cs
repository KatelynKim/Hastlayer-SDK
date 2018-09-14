﻿using System.Threading.Tasks;
using Hast.Communication.Models;
using Hast.Communication.Services;
using Hast.Layer;
using Hast.Transformer.Abstractions.SimpleMemory;
using Orchard.Logging;

namespace Hast.Catapult.Abstractions
{
    public class CatapultCommunicationService : CommunicationServiceBase
    {
        private readonly IDevicePoolPopulator _devicePoolPopulator;
        private readonly IDevicePoolManager _devicePoolManager;


        public override string ChannelName => Constants.ChannelName;


        public CatapultCommunicationService(
            IDevicePoolPopulator devicePoolPopulator,
            IDevicePoolManager devicePoolManager)
        {
            _devicePoolPopulator = devicePoolPopulator;
            _devicePoolManager = devicePoolManager;
        }


        public override async Task<IHardwareExecutionInformation> Execute(
            SimpleMemory simpleMemory,
            int memberId,
            IHardwareExecutionContext executionContext)
        {
            _devicePoolPopulator.PopulateDevicePoolIfNew(async () =>
            {
                var device = await Task.Run(() =>
                {
                    try
                    {
                        var config = executionContext.ProxyGenerationConfiguration.CustomConfiguration;
                        var libraryPath = config.ContainsKey(Constants.ConfigKeys.LibraryPath) ?
                            config[Constants.ConfigKeys.LibraryPath] ?? Constants.DefaultLibraryPath :
                            Constants.DefaultLibraryPath;
                        var versionDefinitionsFile = config.ContainsKey(Constants.ConfigKeys.VersionDefinitionsFile) ?
                            config[Constants.ConfigKeys.VersionDefinitionsFile] : null;
                        var versionManifestFile = config.ContainsKey(Constants.ConfigKeys.VersionManifestFile) ?
                            config[Constants.ConfigKeys.VersionManifestFile] : null;

                        return new CatapultLibrary(
                            (string)libraryPath,
                            (string)versionDefinitionsFile,
                            (string)versionManifestFile,
                            logFunction: (flagValue, text) =>
                            {
                                var flag = (Constants.Log)flagValue;
                                if (flag == Constants.Log.None) return;
                                
                                if (flag.HasFlag(Constants.Log.Debug) || flag.HasFlag(Constants.Log.Verbose))
                                    Logger.Debug(text);
                                else if (flag.HasFlag(Constants.Log.Info))
                                    Logger.Information(text);
                                else if (flag.HasFlag(Constants.Log.Error))
                                    Logger.Error(text);
                                else if (flag.HasFlag(Constants.Log.Fatal))
                                    Logger.Fatal(text);
                                else if (flag.HasFlag(Constants.Log.Warn))
                                    Logger.Warning(text);
                            });
                    }
                    catch (CatapultFunctionResultException ex)
                    {
                        Logger.Error(ex, $"Received {ex.Status} while trying to instantiate CatapultLibrary.");
                        return null;
                    }
                });
                return device is null ? new Device[0] :
                    new[] { new Device { Identifier = Constants.ChannelName, Metadata = device } };
            });

            using (var device = await _devicePoolManager.ReserveDevice())
            {
                var context = BeginExecution();
                CatapultLibrary lib = device.Metadata;

                // This actually happens inside lib.ExecuteJob.
                int memoryLength = simpleMemory.CellCount * (int)SimpleMemory.MemoryCellSizeBytes;
                if (memoryLength < Constants.BufferMessageSizeMinByte)
                    Logger.Warning("Incoming data is {0}B! Padding with zeros to reach the minimum of {1}B...",
                        memoryLength, Constants.BufferMessageSizeMinByte);
                else if (memoryLength % 16 != 0)
                    Logger.Warning("Incoming data ({0}B) must be aligned to 16B! Padding for {1}B...",
                        memoryLength, 16 - (memoryLength % 16));

                // Sending the data.
                var outputBuffer = await lib.ExecuteJob(memberId, simpleMemory.ReadAllBytes());

                // Processing the response.

                // TODO get execution time
                //var executionTimeClockCycles = BitConverter.ToUInt64(outputBuffer, 0);
                //SetHardwareExecutionTime(context, executionContext, executionTimeClockCycles);

                var memory = outputBuffer;
                // TODO take only the indicated length from the response
                //var outputByteCount = BitConverter.ToUInt32(outputBuffer, 8);
                //byte[] outputData = new byte[outputByteCount];
                //Array.Copy(outputBuffer, sizeof(ulong) + sizeof(uint), outputData, 0, outputByteCount);
                //memory = outputData;
                simpleMemory.WriteOverAllBytes(0, memory);
                Logger.Information("Incoming data size in bytes: {0}", outputBuffer.Length);

                EndExecution(context);

                return context.HardwareExecutionInformation;
            }
        }
    }
}
