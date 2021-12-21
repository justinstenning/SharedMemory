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

        RpcBuffer ipcServerBidrectional;
        RpcBuffer ipcClientBidrectional;
        readonly byte[] data;
        public RpcBufferBenchmark()
        {
            data = new byte[] { 3, 3 };
            var ipcName = "RpcBufferBenchmark" + Guid.NewGuid().ToString();
            ipcServer = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            }, bufferCapacity: 256);
            ipcClient = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return Task.FromResult(new byte[] { (byte)(payload[0] * payload[1]) });
            });

            ipcName = "RpcBufferBenchmarkBidrectional" + Guid.NewGuid().ToString();
            ipcServerBidrectional = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return Task.FromResult(new byte[] { (byte)(payload[0] * payload[1]) });
            }, bufferCapacity: 256);
            ipcClientBidrectional = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return ipcServerBidrectional.RemoteRequest(payload).Data;
            });
        }

        [Benchmark]
        public byte[] ClientToServer() => ipcClient.RemoteRequest(data).Data;

        [Benchmark]
        public byte[] ServerToClientAsync() => ipcServer.RemoteRequest(data).Data;

        [Benchmark]
        public byte[] ClientBiderectionalAsync() => ipcClientBidrectional.RemoteRequest(data).Data;
    }
}
