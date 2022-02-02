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
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Threading;
using SharedMemory;
using System.Diagnostics;

namespace SharedMemoryTests
{
    [TestClass]
    public class RpcBufferTests
    {
        string ipcName;
        RpcBuffer ipcMaster;
        RpcBuffer ipcSlave;

        [TestInitialize]
        public void Initialise()
        {
            ipcName = "MasterSlaveTest" + Guid.NewGuid().ToString();
        }

        [TestCleanup]
        public void Cleanup()
        {
            ipcMaster?.Dispose();
            ipcSlave?.Dispose();
        }

        [TestMethod]
        public void Constructor_MasterSlave_Create()
        {
            ipcMaster = new RpcBuffer(ipcName, (msgId, payload) =>
            {
            });
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) =>
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
        public void RPC_MasterCallsSlave()
        {
            ipcMaster = new RpcBuffer(ipcName);
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                Assert.IsTrue(payload != null);
                Assert.IsTrue(payload.Length == 2);
                // Add the two bytes together
                return BitConverter.GetBytes((payload[0] + payload[1]));
            });

            var result = ipcMaster.RemoteRequest(new byte[] { 123, 10 });

            Assert.IsTrue(result.Success);
            Assert.AreEqual(123 + 10, BitConverter.ToInt32(result.Data, 0));
        }

        [TestMethod]
        public async Task RPC_MasterCallsSlave_Async_WithCancellationToken_WithActualCancellation()
        {
            ipcMaster = new RpcBuffer(ipcName);
            var slaveBlockingTcs = new TaskCompletionSource<bool>();
            var slaveBlockingTask = slaveBlockingTcs.Task;
            ipcSlave = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
                await slaveBlockingTask;
                return BitConverter.GetBytes((payload[0] + payload[1]));
            });

            using (var cts = new CancellationTokenSource())
            {
                var remoteRequestTask = ipcMaster.RemoteRequestAsync(new byte[] { 123, 10 }, cancellationToken: cts.Token);
                cts.Cancel();

                if (await Task.WhenAny(remoteRequestTask, Task.Delay(TimeSpan.FromMilliseconds(50))) == remoteRequestTask)
                {
                    var result = await remoteRequestTask;
                    Assert.IsFalse(result.Success);
                }
                else
                {
                    Assert.Fail("cancellation seems not to have worked");
                }
            }
        }

        [TestMethod]
        public async Task RPC_MasterCallsSlave_Async_WithCancellationToken_WithoutCancellation()
        {
            ipcMaster = new RpcBuffer(ipcName);
            var slaveBlockingTcs = new TaskCompletionSource<bool>();
            var slaveBlockingTask = slaveBlockingTcs.Task;
            ipcSlave = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
                await slaveBlockingTask;
                return BitConverter.GetBytes((payload[0] + payload[1]));
            });

            using (var cts = new CancellationTokenSource())
            {
                var remoteRequestTask = ipcMaster.RemoteRequestAsync(new byte[] { 123, 10 }, cancellationToken: cts.Token);
                slaveBlockingTcs.SetResult(false);

                var result = await remoteRequestTask;
                Assert.IsTrue(result.Success);
            }
        }

        [TestMethod]
        public async Task RPC_MasterCallsSlave_Sync_WithCancellationToken_WithActualCancellation()
        {
            ipcMaster = new RpcBuffer(ipcName);
            var slaveBlockingTcs = new TaskCompletionSource<bool>();
            var slaveBlockingTask = slaveBlockingTcs.Task;
            ipcSlave = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
                await slaveBlockingTask;
                return BitConverter.GetBytes((payload[0] + payload[1]));
            });

            using (var cts = new CancellationTokenSource())
            {
                var remoteRequestTask = Task.Run(() => ipcMaster.RemoteRequest(new byte[] { 123, 10 }, cancellationToken: cts.Token));
                cts.Cancel();

                if (await Task.WhenAny(remoteRequestTask, Task.Delay(TimeSpan.FromMilliseconds(50))) == remoteRequestTask)
                {
                    var result = await remoteRequestTask;
                    Assert.IsFalse(result.Success);
                }
                else
                {
                    Assert.Fail("cancellation seems not to have worked");
                }
            }
        }

        [TestMethod]
        public async Task RPC_MasterCallsSlave_Sync_WithCancellationToken_WithoutCancellation()
        {
            ipcMaster = new RpcBuffer(ipcName);
            var slaveBlockingTcs = new TaskCompletionSource<bool>();
            var slaveBlockingTask = slaveBlockingTcs.Task;
            ipcSlave = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
                await slaveBlockingTask;
                return BitConverter.GetBytes((payload[0] + payload[1]));
            });

            using (var cts = new CancellationTokenSource())
            {
                var remoteRequestTask = Task.Run(() => ipcMaster.RemoteRequest(new byte[] { 123, 10 }, cancellationToken: cts.Token));
                slaveBlockingTcs.SetResult(false);

                var result = await remoteRequestTask;
                Assert.IsTrue(result.Success);
            }
        }

        [TestMethod]
        public void RPC_Statistics_Reset()
        {
            ipcMaster = new RpcBuffer(ipcName);
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                Assert.IsTrue(payload != null);
                Assert.IsTrue(payload.Length == 2);
                // Add the two bytes together
                return BitConverter.GetBytes((payload[0] + payload[1]));
            });

            var result = ipcMaster.RemoteRequest(new byte[] { 123, 10 });

            Assert.IsTrue(result.Success);
            Assert.AreEqual((ulong)1, ipcMaster.Statistics.RequestsSent);
            Assert.AreEqual((ulong)1, ipcSlave.Statistics.RequestsReceived);
            Assert.AreEqual((ulong)1, ipcSlave.Statistics.ResponsesSent);
            Assert.AreEqual((ulong)1, ipcMaster.Statistics.ResponsesReceived);

            ipcMaster.Statistics.Reset();

            var empty = new RpcStatistics();

            Assert.AreEqual(empty.RequestsSent, ipcMaster.Statistics.RequestsSent);
            Assert.AreEqual(empty.ResponsesReceived, ipcMaster.Statistics.ResponsesReceived);
            Assert.AreEqual(empty.ReadingLastMessageSize, ipcMaster.Statistics.ReadingLastMessageSize);
            Assert.AreEqual(empty.WritingLastMessageSize, ipcMaster.Statistics.WritingLastMessageSize);
        }

        [TestMethod]
        public void RPC_MasterCallsSlave_Exception()
        {
            ipcMaster = new RpcBuffer(ipcName);
            ipcSlave = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
                throw new Exception("test exception");
            });

            var result = ipcMaster.RemoteRequest(null);

            Assert.IsFalse(result.Success);
        }

        [TestMethod]
        public void RPC_Bidirectional_Nested()
        {
            ipcMaster = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
                // Ask slave to multiply the two bytes
                return (await ipcMaster.RemoteRequestAsync(new byte[] { 3, 3 }).ConfigureAwait(false)).Data;
            });
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            // Send request to master from slave
            var result = ipcSlave.RemoteRequest(null);
            Assert.IsTrue(result.Success);
            Assert.AreEqual((3 * 3), result.Data[0]);
        }


        [TestMethod]
        public void RPC_Timeout()
        {
            ipcMaster = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
            }, bufferCapacity: 256);
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                Task.Delay(1000).Wait();
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            var result = ipcMaster.RemoteRequest(new byte[] { 3, 3 }, 100);
            Assert.IsFalse(result.Success);

        }

        [TestMethod]
        public void RPC_Timeout_FireAndForget()
        {
            ipcMaster = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
            }, bufferCapacity: 256);
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                Task.Delay(1000).Wait();
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            var result = ipcMaster.RemoteRequest(new byte[] { 3, 3 }, 0);
            Assert.IsFalse(result.Success);

        }

#if DEBUG
        [TestMethod]
        public void RPC_LoadTest_5k_Small()
        {
            ipcMaster = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
            }, bufferCapacity: 256);
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            Stopwatch watch = Stopwatch.StartNew();

            // Send request to slave from master
            for (var i = 0; i < 5000; i++)
            {
                var result = ipcMaster.RemoteRequest(new byte[] { 3, 3 }, 100);
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
            
            ipcMaster = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
            }, bufferCapacity: 256);
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            Stopwatch watch = Stopwatch.StartNew();

            List<Task> tasks = new List<Task>();
            
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Task.Run(() =>
                {
                    // Send request to slave from master
                    for (var j = 0; j < 5000; j++)
                    {
                        var result = ipcMaster.RemoteRequest(new byte[] { 3, 3 });
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
            ipcMaster = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
            }, bufferCapacity: 1025 * 512);
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            var buf = new byte[1025 * 512];
            buf[0] = 3;
            buf[1] = 3;

            Stopwatch watch = Stopwatch.StartNew();

            // Send request to slave from master
            for (var i = 0; i < 1000; i++)
            {
                var result = ipcMaster.RemoteRequest(buf, 100);
                Assert.IsTrue(result.Success);
                Assert.AreEqual((3 * 3), result.Data[0]);
            }
            watch.Stop();

            Assert.IsTrue(watch.ElapsedMilliseconds < 2000);
        }

        [TestMethod]
        public void RPC_LoadTest_NestedCalls()
        {
            ipcMaster = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
                // Ask slave to multiply the two bytes
                return (await ipcMaster.RemoteRequestAsync(new byte[] { 3, 3 }).ConfigureAwait(false)).Data;
            });
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            Stopwatch watch = Stopwatch.StartNew();

            // Send request to master from slave
            for (var i = 0; i < 10; i++)
            {
                var result = ipcSlave.RemoteRequest(null, 30000);
                Assert.IsTrue(result.Success);
                Assert.AreEqual((3 * 3), result.Data[0]);
            }
            watch.Stop();

            Assert.IsTrue(watch.ElapsedMilliseconds < 1000);
        }
#endif

        [TestMethod]
        public void RPC_SlaveCallsMasterAfterClosed_Exception()
        {
            ipcMaster = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
            });

            ipcSlave = new RpcBuffer(ipcName);

            ipcSlave.RemoteRequest(null);

            ipcMaster.Dispose();
            while (!ipcMaster.DisposeFinished)
            {
                Task.Delay(125).Wait();
            }

            Assert.ThrowsException<InvalidOperationException>(() => ipcSlave.RemoteRequest(null));
        }
        
        [TestMethod]
        public void RPC_Dispose()
        {
            // Warmup the Theadpool
            ThreadPool.SetMinThreads(15, 10);

            for (int i = 0; i < 10; i++)
            {
                ipcMaster = new RpcBuffer(ipcName, async (msgId, payload) =>
                {
                }, bufferCapacity: 256);

                ipcSlave = new RpcBuffer(ipcName, (msgId, payload) =>
                {
                    ipcSlave.Dispose();
                    return new byte[] { (byte)(payload[0] * payload[1]) };
                    
                });

                Stopwatch watch = Stopwatch.StartNew();

                ipcMaster.RemoteRequestAsync(new byte[] { 3, 3 });
                Task.Delay(125).Wait();
                ipcSlave.Dispose();
                watch.Stop();

                ipcMaster.Dispose();

                while (!ipcMaster.DisposeFinished || !ipcSlave.DisposeFinished)
                {
                    Task.Delay(125).Wait();
                }

                
            }


            

         
        }
        
    }
}
