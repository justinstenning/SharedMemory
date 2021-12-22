// SharedMemory (File: SharedMemoryTests\RpcBufferTests.cs)
// Copyright (c) 2020 Justin Stenning
// http://spazzarama.com
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Threading;
using SharedMemory;
using System.Diagnostics;
using System.Collections.Generic;

namespace SharedMemoryTests
{
    [TestClass]
    public class RpcBufferTests
    {
        string ipcName;
        RpcBuffer ipcServer;
        RpcBuffer ipcClient;

        [TestInitialize]
        public void Initialise()
        {
            ipcName = "ClientServerTest" + Guid.NewGuid().ToString();
        }

        [TestCleanup]
        public void Cleanup()
        {
            ipcServer?.Dispose();
            ipcClient?.Dispose();
        }

        [TestMethod]
        public void Constructor_ClientServer_Create()
        {
            ipcServer = new RpcBuffer(ipcName, (msgId, payload) =>
            {
            });
            ipcClient = new RpcBuffer(ipcName, (msgId, payload) =>
            {
            });
        }

        [TestMethod]
        public void Constructor_BufferCapacityOutOfRange()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new RpcBuffer(ipcName, 255));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new RpcBuffer(ipcName, 1024*1024 + 1));
        }

        [TestMethod]
        public void RPC_ServerCallsClient()
        {
            ipcServer = new RpcBuffer(ipcName);
            ipcClient = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                Assert.IsTrue(payload != null);
                Assert.IsTrue(payload.Length == 2);
                // Add the two bytes together
                return BitConverter.GetBytes((payload[0] + payload[1]));
            });

            var result = ipcServer.RemoteRequest(new byte[] { 123, 10 });

            Assert.IsTrue(result.Success);
            Assert.AreEqual(123 + 10, BitConverter.ToInt32(result.Data, 0));
        }

        [TestMethod]
        public void RPC_Statistics_Reset()
        {
            ipcServer = new RpcBuffer(ipcName);
            ipcClient = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                Assert.IsTrue(payload != null);
                Assert.IsTrue(payload.Length == 2);
                // Add the two bytes together
                return BitConverter.GetBytes((payload[0] + payload[1]));
            });

            var result = ipcServer.RemoteRequest(new byte[] { 123, 10 });

            Assert.IsTrue(result.Success);
            Assert.AreEqual((ulong)1, ipcServer.Statistics.RequestsSent);
            Assert.AreEqual((ulong)1, ipcClient.Statistics.RequestsReceived);
            Assert.AreEqual((ulong)1, ipcClient.Statistics.ResponsesSent);
            Assert.AreEqual((ulong)1, ipcServer.Statistics.ResponsesReceived);

            ipcServer.Statistics.Reset();

            var empty = new RpcStatistics();

            Assert.AreEqual(empty.RequestsSent, ipcServer.Statistics.RequestsSent);
            Assert.AreEqual(empty.ResponsesReceived, ipcServer.Statistics.ResponsesReceived);
            Assert.AreEqual(empty.ReadingLastMessageSize, ipcServer.Statistics.ReadingLastMessageSize);
            Assert.AreEqual(empty.WritingLastMessageSize, ipcServer.Statistics.WritingLastMessageSize);
        }

        [TestMethod]
        public void RPC_ServerCallsClient_Exception()
        {
            ipcServer = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                if (payload == null)
                {
                    throw new Exception("test exception");
                }
                return (byte[])null;
            });
            ipcClient = new RpcBuffer(ipcName);

            var result = ipcClient.RemoteRequest(null);

            Assert.IsFalse(result.Success);
        }

        [TestMethod]
        public void RPC_Bidirectional_Nested()
        {
            // Must use a minimum of 2 receiveThreads so that the ipcServer can process a request from client
            // and while processing send a remote request back to client and then receive the result
            ipcServer = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                // Ask client to multiply the two bytes
                return ipcServer.RemoteRequest(payload).Data;
            }, receiveThreads: 2);
            ipcClient = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            }, receiveThreads: 1);

            // Send request to server from client (which makes request back to client from server)
            RpcResponse result = ipcClient.RemoteRequest(new byte[] { 3, 3 });
            Assert.IsTrue(result.Success);
            Assert.AreEqual((3 * 3), result.Data[0]);
        }

        [TestMethod]
        public void RPC_LoadTest_5k_Small()
        {
            ipcServer = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            }, bufferCapacity: 256);
            ipcClient = new RpcBuffer(ipcName);

            Stopwatch watch = Stopwatch.StartNew();

            // Send request to client from server
            for (var i = 0; i < 5000; i++)
            {
                var result = ipcClient.RemoteRequest(new byte[] { 3, 3 }, 100);
                Assert.IsTrue(result.Success);
                Assert.AreEqual((3 * 3), result.Data[0]);
            }
            watch.Stop();

            Assert.IsTrue(watch.ElapsedMilliseconds < 1000);
        }

        [TestMethod]
        public void RPC_LoadTest_5k_Small_Multi_Thread()
        {
            // Warmup the Theadpool
            ThreadPool.SetMinThreads(15, 10);

            ipcServer = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            }, bufferCapacity: 256, receiveThreads: 2);
            ipcClient = new RpcBuffer(ipcName);

            Stopwatch watch = Stopwatch.StartNew();

            List<Task> tasks = new List<Task>();

            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Task.Run(() =>
                {
                    // Send request to slave from master
                    for (var j = 0; j < 5000; j++)
                    {
                        var result = ipcClient.RemoteRequest(new byte[] { 3, 3 });
                        Assert.IsTrue(result.Success);
                        Assert.AreEqual((3 * 3), result.Data[0]);
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());
            watch.Stop();

            Assert.IsTrue(watch.ElapsedMilliseconds < 1000);
        }

        [TestMethod]
        public void RPC_LoadTest_1k_Large()
        {
            ipcServer = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            }, bufferCapacity: 1025 * 512);
            ipcClient = new RpcBuffer(ipcName);

            var buf = new byte[1025 * 512];
            buf[0] = 3;
            buf[1] = 3;

            Stopwatch watch = Stopwatch.StartNew();

            // Send request to client from server
            uint iterations = 1000;
            for (var i = 0; i < iterations; i++)
            {
                var result = ipcClient.RemoteRequest(buf, 100000);
                Assert.IsTrue(result.Success);
                Assert.AreEqual((3 * 3), result.Data[0]);
            }
            watch.Stop();

            Assert.AreEqual(iterations, ipcClient.Statistics.PacketsRead);
            Assert.AreEqual(iterations * 2, ipcClient.Statistics.PacketsWritten);
            Assert.AreEqual(ipcClient.Statistics.RequestsSent, ipcServer.Statistics.RequestsReceived);

            Assert.IsTrue(watch.ElapsedMilliseconds < 2000);
        }

        [TestMethod]
        public void RPC_LoadTest_NestedCalls()
        {
            // Must use a minimum of 2 receiveThreads so that the ipcServer can process a request from client
            // and while processing send a remote request back to client and then receive the result
            ipcServer = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                // Ask client to multiply the two bytes
                return ipcServer.RemoteRequest(new byte[] { 3, 3 }).Data;
            }, receiveThreads: 2);
            ipcClient = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            }, receiveThreads: 1);

            Stopwatch watch = Stopwatch.StartNew();

            // Send request to server from client
            for (var i = 0; i < 10000; i++)
            {
                var result = ipcClient.RemoteRequest(null, 30000);
                Assert.IsTrue(result.Success);
                Assert.AreEqual((3 * 3), result.Data[0]);
            }
            watch.Stop();

            Assert.IsTrue(watch.ElapsedMilliseconds < 1000);
        }

        [TestMethod]
        public void RPC_ClientCallsServerAfterClosed_Exception()
        {
            ipcServer = new RpcBuffer(ipcName, (msgId, payload) =>
            {
            });

            ipcClient = new RpcBuffer(ipcName);

            ipcClient.RemoteRequest(null);

            ipcServer.Dispose();

            Assert.ThrowsException<InvalidOperationException>(() => ipcClient.RemoteRequest(null));
        }
    }
}
