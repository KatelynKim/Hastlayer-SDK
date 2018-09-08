﻿using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Hast.Communication.Constants;
using Hast.Communication.Exceptions;
using Hast.Communication.Models;
using Hast.Layer;
using Hast.Transformer.Abstractions.SimpleMemory;
using Orchard.Logging;

using Catapult = Hast.Communication.Constants.CommunicationConstants.Catapult;

namespace Hast.Communication.Services
{
    public class CatapultCommunicationService : CommunicationServiceBase
    {
        private readonly IDevicePoolPopulator _devicePoolPopulator;
        private readonly IDevicePoolManager _devicePoolManager;


        public override string ChannelName =>
            CommunicationConstants.Catapult.ChannelName;


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
                        var libraryPath = config.ContainsKey(Catapult.ConfigKeys.LibraryPath) ?
                            config[Catapult.ConfigKeys.LibraryPath] ?? Catapult.DefaultLibraryPath : Catapult.DefaultLibraryPath;
                        var versionDefinitionsFile = config.ContainsKey(Catapult.ConfigKeys.VersionDefinitionsFile) ?
                            config[Catapult.ConfigKeys.VersionDefinitionsFile] : null;
                        var versionManifestFile = config.ContainsKey(Catapult.ConfigKeys.VersionManifestFile) ?
                            config[Catapult.ConfigKeys.VersionManifestFile] : null;

                        return new CatapultLibrary((string)libraryPath, (string)versionDefinitionsFile, (string)versionManifestFile);
                    }
                    catch (CatapultFunctionResultException ex)
                    {
                        Logger.Error(ex, $"Received {ex.Status} while trying to instantiate CatapultLibrary.");
                        return null;
                    }
                });
                return device is null ? new Device[0] :
                    new[] { new Device { Identifier = Catapult.ChannelName, Metadata = device } };
            });

            using (var device = await _devicePoolManager.ReserveDevice())
            {
                var context = BeginExecution();
                CatapultLibrary lib = device.Metadata;

                // This actually happens inside lib.ExecuteJob.
                if (simpleMemory.Memory.Length < Catapult.BufferMessageSizeMin)
                    Logger.Warning("Incoming data is {0}B! Padding with zeroes to reach the minimum of {1}B...",
                        simpleMemory.Memory.Length, Catapult.BufferMessageSizeMin);
                else if (simpleMemory.Memory.Length % 16 != 0)
                    Logger.Warning("Incoming data ({0}B) must be aligned to 16B! Padding for {1}B...",
                        simpleMemory.Memory.Length, 16 - (simpleMemory.Memory.Length % 16));

                // Sending the data.
                var outputBuffer = await lib.ExecuteJob(memberId, simpleMemory.Memory);

                // Processing the response.

                // TODO get execution time
                //var executionTimeClockCycles = BitConverter.ToUInt64(outputBuffer, 0);
                //SetHardwareExecutionTime(context, executionContext, executionTimeClockCycles);

                simpleMemory.Memory = outputBuffer;
                // TODO take only the indicated length from the response
                //var outputByteCount = BitConverter.ToUInt32(outputBuffer, 8);
                //byte[] outputData = new byte[outputByteCount];
                //Array.Copy(outputBuffer, sizeof(ulong) + sizeof(uint), outputData, 0, outputByteCount);
                //simpleMemory.Memory = outputData;
                Logger.Information("Incoming data size in bytes: {0}", outputBuffer.Length);

                EndExecution(context);

                return context.HardwareExecutionInformation;
            }
        }
    }
}
