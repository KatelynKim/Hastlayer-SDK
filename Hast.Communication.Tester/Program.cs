﻿using CommandLine;
using Hast.Communication.Exceptions;
using Hast.Layer;
using Hast.Transformer.Abstractions.SimpleMemory;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Hast.Communication.Tester
{
    partial class Program
    {
        
        public const string DefaultHexdumpFileName = "dump.txt";
        public const string DefaultBinaryFileName = "dump.bin";

        public const int HexDumpDigits = SimpleMemory.MemoryCellSizeBytes * 2;

        private static async Task MainTask(Options configuration)
        {
            using (var hastlayer = await Hastlayer.Create(new HastlayerConfiguration { Flavor = HastlayerFlavor.Developer }))
            {
                var devices = await hastlayer.GetSupportedDevices();
                if (devices == null || devices.Count() == 0) throw new Exception("No devices are available!");

                if (configuration.ListDevices)
                {
                    foreach (var d in devices) Console.WriteLine(d.Name);
                    return;
                }


                switch (configuration.OutputFileType)
                {
                    case OutputFileType.None:
                        if (!string.IsNullOrEmpty(configuration.OutputFileName))
                            configuration.OutputFileType = OutputFileType.Hexdump;
                        break;
                    case OutputFileType.Hexdump:
                        if (string.IsNullOrEmpty(configuration.OutputFileName))
                            configuration.OutputFileName = DefaultHexdumpFileName;
                        break;
                    case OutputFileType.Binary:
                        if (string.IsNullOrEmpty(configuration.OutputFileName))
                            configuration.OutputFileName = DefaultBinaryFileName;
                        break;
                }


                if (string.IsNullOrEmpty(configuration.DeviceName)) configuration.DeviceName = devices.First().Name;
                var selectedDevice = devices.FirstOrDefault(device => device.Name == configuration.DeviceName);
                if (selectedDevice == null) throw new Exception($"Target device '{configuration.DeviceName}' not found!");
                var channelName = selectedDevice.DefaultCommunicationChannelName;


                Console.WriteLine("Generating memory.");
                var memory = new SimpleMemory(configuration.PayloadLengthCells);
                var accessor = new SimpleMemoryAccessor(memory);
                switch (configuration.PayloadType)
                {
                    case PayloadType.ConstantIntOne:
                        for (int i = 0; i < memory.CellCount; i++) memory.WriteInt32(i, 1);
                        break;
                    case PayloadType.Counter:
                        for (int i = 0; i < memory.CellCount; i++) memory.WriteInt32(i, i);
                        break;
                    case PayloadType.Random:
                        var random = new Random();
                        for (int i = 0; i < memory.CellCount; i++)
                            memory.WriteInt32(i, random.Next(int.MinValue, int.MaxValue));
                        break;
                    case PayloadType.BinaryFile:
                        using (var fileStream = File.OpenRead(configuration.InputFileName))
                        {
                            int prefixBytes = 3 * SimpleMemory.MemoryCellSizeBytes;
                            var data = new byte[fileStream.Length + prefixBytes];
                            fileStream.Read(data, prefixBytes, (int)fileStream.Length);
                            accessor.Set(data, 3);
                        }
                        break;
                }
                // Save input to file.
                switch (configuration.OutputFileType)
                {
                    case OutputFileType.None: break;
                    case OutputFileType.Hexdump:
                        Console.WriteLine("Saving input hexdump to '{0}'", configuration.InputFileName);
                        if (configuration.InputFileName == "-")
                            WriteHexdump(Console.Out, memory);
                        else
                            using (var streamWriter = new StreamWriter(configuration.InputFileName, false, Encoding.UTF8))
                                WriteHexdump(streamWriter, memory);
                        Console.WriteLine("File saved.");
                        break;
                    case OutputFileType.Binary:
                        if (configuration.PayloadType != PayloadType.BinaryFile)
                        {
                            Console.WriteLine("Saving input binary file to '{0}'", configuration.InputFileName);
                            using (var fileStream = File.OpenWrite(configuration.InputFileName))
                                fileStream.Write(accessor.Get().GetUnderlyingArray().Array, 0, memory.ByteCount);
                            Console.WriteLine("File saved.");
                        }
                        break;
                }

                // Create reference copy of input to compare against output.
                var referenceMemory = new SimpleMemory(memory.CellCount);
                var memoryBytes = new SimpleMemoryAccessor(memory).Get();
                memoryBytes.CopyTo(new SimpleMemoryAccessor(referenceMemory).Get());

                Console.WriteLine("Starting hardware execution.");
                var communicationService = await hastlayer.GetCommunicationService(channelName);
                communicationService.TesterOutput = Console.Out;
                var executionContext = new BasicExecutionContext(hastlayer, selectedDevice.Name,
                    selectedDevice.DefaultCommunicationChannelName);
                var info = await communicationService.Execute(memory, configuration.MemberId, executionContext);

                Console.WriteLine("Executing test on hardware took {0}ms (net) {1}ms (all together)",
                    info.HardwareExecutionTimeMilliseconds, info.FullExecutionTimeMilliseconds);

                // Verify results!
                var mismatches = new List<HardwareExecutionResultMismatchException.Mismatch>();
                for (int i = 0; i < memory.CellCount; i++)
                    if (!memory.Read4Bytes(i).SequenceEqual(referenceMemory.Read4Bytes(i)))
                        mismatches.Add(new HardwareExecutionResultMismatchException.Mismatch(
                            i, memory.Read4Bytes(i), referenceMemory.Read4Bytes(i)));
                if (mismatches.Any()) throw new HardwareExecutionResultMismatchException(mismatches);
                Console.WriteLine("Verification passed!");

                
                if (!string.IsNullOrWhiteSpace(configuration?.JsonOutputFileName))
                    File.WriteAllText(configuration.JsonOutputFileName, JsonConvert.SerializeObject(
                        new { Success = true, Result = info }));

                switch (configuration.OutputFileType)
                {
                    case OutputFileType.None: break;
                    case OutputFileType.Hexdump:
                        Console.WriteLine("Saving input hexdump to '{0}'", configuration.OutputFileName);
                        if (configuration.OutputFileName == "-")
                            WriteHexdump(Console.Out, memory);
                        else
                            using (var streamWriter = new StreamWriter(configuration.OutputFileName, false, Encoding.UTF8))
                                WriteHexdump(streamWriter, memory);
                        Console.WriteLine("File saved.");
                        break;
                    case OutputFileType.Binary:
                        Console.WriteLine("Saving input binary file to '{0}'", configuration.OutputFileName);
                        using (var fileStream = File.OpenWrite(configuration.OutputFileName))
                            fileStream.Write(accessor.Get().GetUnderlyingArray().Array, 0, memory.ByteCount);
                        Console.WriteLine("File saved.");
                        break;
                }
            }
        }

        public static void WriteHexdump(TextWriter writer, SimpleMemory memory)
        {
            for (int i = 0; i < memory.CellCount; i += HexDumpDigits)
                writer.WriteLine(string.Join(" ", memory.ReadUInt32(i, HexDumpDigits)
                    .Select(x => x.ToString("X").PadLeft(HexDumpDigits, '0'))));
        }

        private static void Main(string[] args)
        {
            Options configuration = null;
            try
            {
                Parser.Default.ParseArguments<Options>(args).WithParsed(o => { configuration = o; });
                if (configuration != null) MainTask(configuration).Wait();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                if (!string.IsNullOrWhiteSpace(configuration?.JsonOutputFileName))
                    File.WriteAllText(configuration.JsonOutputFileName, JsonConvert.SerializeObject(
                        new { Success = false, Exception = ex, }));
            }

            Console.ReadKey();
        }
    }
}
