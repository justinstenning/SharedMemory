using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace SharedMemory.Tests
{

    [MemoryDiagnoser]
    public class RpcBufferBenchmark
    {
        RpcBuffer ipcServer;
        RpcBuffer ipcClient;

        RpcBuffer ipcServerNested;
        RpcBuffer ipcClientNested;
        readonly byte[] data;
        public RpcBufferBenchmark()
        {
            data = new byte[] { 3, 3 };
            var ipcName = "RpcBufferBenchmark" + Guid.NewGuid().ToString();
            var result = new byte[1];
            ipcServer = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                result[0] = (byte)(payload[0] * payload[1]);
                return result;
            }, bufferCapacity: 256);
            ipcClient = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                result[0] = (byte)(payload[0] * payload[1]);
                return result;
            });

            ipcName = "RpcBufferBenchmarkBidrectional" + Guid.NewGuid().ToString();
            ipcServerNested = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                result[0] = (byte)(payload[0] * payload[1]);
                return result;
            }, bufferCapacity: 256);
            ipcClientNested = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return ipcServerNested.RemoteRequest(payload).Data;
            });
        }

        [Benchmark]
        public byte[] ClientToServer() => ipcClient.RemoteRequest(data).Data;

        [Benchmark]
        public byte[] ServerToClient() => ipcServer.RemoteRequest(data).Data;

        [Benchmark]
        public byte[] ClientNested() => ipcClientNested.RemoteRequest(data).Data;
    }
}
