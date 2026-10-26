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

#if HAS_RPC_BUFFER
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
        RpcBuffer ipcOwner;
        RpcBuffer ipcOpener;

        [TestInitialize]
        public void Initialise()
        {
            ipcName = "OwnerOpenerTest" + Guid.NewGuid().ToString();
        }

        [TestCleanup]
        public void Cleanup()
        {
            ipcOwner?.Dispose();
            ipcOpener?.Dispose();
        }

        [TestMethod]
        public void Constructor_OwnerOpener_Create()
        {
            ipcOwner = new RpcBuffer(ipcName, (msgId, payload) =>
            {
            });
            ipcOpener = new RpcBuffer(ipcName, (msgId, payload) =>
            {
            });
        }

        [TestMethod]
        public void Constructor_BufferCapacityOutOfRange()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RpcBuffer(ipcName, 255));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RpcBuffer(ipcName, 1024*1024 + 1));
        }

        [TestMethod]
        public void RPC_OwnerCallsOpener()
        {
            ipcOwner = new RpcBuffer(ipcName);
            ipcOpener = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                Assert.IsTrue(payload != null);
                Assert.IsTrue(payload.Length == 2);
                // Add the two bytes together
                return BitConverter.GetBytes((payload[0] + payload[1]));
            });

            var result = ipcOwner.RemoteRequest(new byte[] { 123, 10 });

            Assert.IsTrue(result.Success);
            Assert.AreEqual(123 + 10, BitConverter.ToInt32(result.Data, 0));
        }

        [TestMethod]
        public async Task RPC_OwnerCallsOpener_Async_WithCancellationToken_WithActualCancellation()
        {
            ipcOwner = new RpcBuffer(ipcName);
            var openerBlockingTcs = new TaskCompletionSource<bool>();
            var openerBlockingTask = openerBlockingTcs.Task;
            ipcOpener = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
                await openerBlockingTask;
                return BitConverter.GetBytes((payload[0] + payload[1]));
            });

            using (var cts = new CancellationTokenSource())
            {
                var remoteRequestTask = ipcOwner.RemoteRequestAsync(new byte[] { 123, 10 }, cancellationToken: cts.Token);
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
        public async Task RPC_OwnerCallsOpener_Async_WithCancellationToken_WithoutCancellation()
        {
            ipcOwner = new RpcBuffer(ipcName);
            var openerBlockingTcs = new TaskCompletionSource<bool>();
            var openerBlockingTask = openerBlockingTcs.Task;
            ipcOpener = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
                await openerBlockingTask;
                return BitConverter.GetBytes((payload[0] + payload[1]));
            });

            using (var cts = new CancellationTokenSource())
            {
                var remoteRequestTask = ipcOwner.RemoteRequestAsync(new byte[] { 123, 10 }, cancellationToken: cts.Token);
                openerBlockingTcs.SetResult(false);

                var result = await remoteRequestTask;
                Assert.IsTrue(result.Success);
            }
        }

        [TestMethod]
        public async Task RPC_OwnerCallsOpener_Sync_WithCancellationToken_WithActualCancellation()
        {
            ipcOwner = new RpcBuffer(ipcName);
            var openerBlockingTcs = new TaskCompletionSource<bool>();
            var openerBlockingTask = openerBlockingTcs.Task;
            ipcOpener = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
                await openerBlockingTask;
                return BitConverter.GetBytes((payload[0] + payload[1]));
            });

            using (var cts = new CancellationTokenSource())
            {
                var remoteRequestTask = Task.Run(() => ipcOwner.RemoteRequest(new byte[] { 123, 10 }, cancellationToken: cts.Token));
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
        public async Task RPC_OwnerCallsOpener_Sync_WithCancellationToken_WithoutCancellation()
        {
            ipcOwner = new RpcBuffer(ipcName);
            var openerBlockingTcs = new TaskCompletionSource<bool>();
            var openerBlockingTask = openerBlockingTcs.Task;
            ipcOpener = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
                await openerBlockingTask;
                return BitConverter.GetBytes((payload[0] + payload[1]));
            });

            using (var cts = new CancellationTokenSource())
            {
                var remoteRequestTask = Task.Run(() => ipcOwner.RemoteRequest(new byte[] { 123, 10 }, cancellationToken: cts.Token));
                openerBlockingTcs.SetResult(false);

                var result = await remoteRequestTask;
                Assert.IsTrue(result.Success);
            }
        }

        [TestMethod]
        public void RPC_Statistics_Reset()
        {
            ipcOwner = new RpcBuffer(ipcName);
            ipcOpener = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                Assert.IsTrue(payload != null);
                Assert.IsTrue(payload.Length == 2);
                // Add the two bytes together
                return BitConverter.GetBytes((payload[0] + payload[1]));
            });

            var result = ipcOwner.RemoteRequest(new byte[] { 123, 10 });

            Assert.IsTrue(result.Success);
            Assert.AreEqual((ulong)1, ipcOwner.Statistics.RequestsSent);
            Assert.AreEqual((ulong)1, ipcOpener.Statistics.RequestsReceived);
            Assert.AreEqual((ulong)1, ipcOpener.Statistics.ResponsesSent);
            Assert.AreEqual((ulong)1, ipcOwner.Statistics.ResponsesReceived);

            ipcOwner.Statistics.Reset();

            var empty = new RpcStatistics();

            Assert.AreEqual(empty.RequestsSent, ipcOwner.Statistics.RequestsSent);
            Assert.AreEqual(empty.ResponsesReceived, ipcOwner.Statistics.ResponsesReceived);
            Assert.AreEqual(empty.ReadingLastMessageSize, ipcOwner.Statistics.ReadingLastMessageSize);
            Assert.AreEqual(empty.WritingLastMessageSize, ipcOwner.Statistics.WritingLastMessageSize);
        }

        [TestMethod]
        public void RPC_OwnerCallsOpener_Exception()
        {
            ipcOwner = new RpcBuffer(ipcName);
            ipcOpener = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
                throw new Exception("test exception");
            });

            var result = ipcOwner.RemoteRequest(null);

            Assert.IsFalse(result.Success);
        }

        [TestMethod]
        public void RPC_Bidirectional_Nested()
        {
            ipcOwner = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
                // Ask opener to multiply the two bytes
                return (await ipcOwner.RemoteRequestAsync(new byte[] { 3, 3 }).ConfigureAwait(false)).Data;
            });
            ipcOpener = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            // Send request to owner from opener
            var result = ipcOpener.RemoteRequest(null);
            Assert.IsTrue(result.Success);
            Assert.AreEqual((3 * 3), result.Data[0]);
        }


        [TestMethod]
        public void RPC_Timeout()
        {
            ipcOwner = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
            }, bufferCapacity: 256);
            ipcOpener = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                Task.Delay(1000).Wait();
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            var result = ipcOwner.RemoteRequest(new byte[] { 3, 3 }, 100);
            Assert.IsFalse(result.Success);

        }

        [TestMethod]
        public void RPC_Timeout_FireAndForget()
        {
            ipcOwner = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
            }, bufferCapacity: 256);
            ipcOpener = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                Task.Delay(1000).Wait();
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            var result = ipcOwner.RemoteRequest(new byte[] { 3, 3 }, 0);
            Assert.IsFalse(result.Success);

        }

        [TestMethod]
        [TestCategory("Load")]
        public void RPC_LoadTest_5k_Small()
        {
            ipcOwner = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
            }, bufferCapacity: 256);
            ipcOpener = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            Stopwatch watch = Stopwatch.StartNew();

            // Send request to opener from owner
            for (var i = 0; i < 5000; i++)
            {
                var result = ipcOwner.RemoteRequest(new byte[] { 3, 3 }, 100);
                Assert.IsTrue(result.Success);
                Assert.AreEqual((3 * 3), result.Data[0]);
            }
            watch.Stop();

            Assert.IsTrue(watch.ElapsedMilliseconds < 1000);
        }
        
        [TestMethod]
        [TestCategory("Load")]
        public void RPC_LoadTest_5k_Small_Multi_Thread()
        {
            // Warmup the Theadpool
            ThreadPool.SetMinThreads(15, 10);
            
            ipcOwner = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
            }, bufferCapacity: 256);
            ipcOpener = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            Stopwatch watch = Stopwatch.StartNew();

            List<Task> tasks = new List<Task>();
            
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Task.Run(() =>
                {
                    // Send request to opener from owner
                    for (var j = 0; j < 5000; j++)
                    {
                        var result = ipcOwner.RemoteRequest(new byte[] { 3, 3 });
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
        [TestCategory("Load")]
        public void RPC_LoadTest_1k_Large()
        {
            ipcOwner = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
            }, bufferCapacity: 1025 * 512);
            ipcOpener = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            var buf = new byte[1025 * 512];
            buf[0] = 3;
            buf[1] = 3;

            Stopwatch watch = Stopwatch.StartNew();

            // Send request to opener from owner
            for (var i = 0; i < 1000; i++)
            {
                var result = ipcOwner.RemoteRequest(buf, 100);
                Assert.IsTrue(result.Success);
                Assert.AreEqual((3 * 3), result.Data[0]);
            }
            watch.Stop();

            Assert.IsTrue(watch.ElapsedMilliseconds < 2000);
        }

        [TestMethod]
        [TestCategory("Load")]
        public void RPC_LoadTest_NestedCalls()
        {
            ipcOwner = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
                // Ask opener to multiply the two bytes
                return (await ipcOwner.RemoteRequestAsync(new byte[] { 3, 3 }).ConfigureAwait(false)).Data;
            });
            ipcOpener = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            Stopwatch watch = Stopwatch.StartNew();

            // Send request to owner from opener
            for (var i = 0; i < 10; i++)
            {
                var result = ipcOpener.RemoteRequest(null, 30000);
                Assert.IsTrue(result.Success);
                Assert.AreEqual((3 * 3), result.Data[0]);
            }
            watch.Stop();

            Assert.IsTrue(watch.ElapsedMilliseconds < 1000);
        }

        [TestMethod]
        public void RPC_OpenerCallsOwnerAfterClosed_Exception()
        {
            ipcOwner = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
            });

            ipcOpener = new RpcBuffer(ipcName);

            ipcOpener.RemoteRequest(null);

            ipcOwner.Dispose();
            while (!ipcOwner.DisposeFinished)
            {
                Task.Delay(125).Wait();
            }

            Assert.ThrowsExactly<InvalidOperationException>(() => ipcOpener.RemoteRequest(null));
        }
        
        [TestMethod]
        public void RPC_Dispose()
        {
            // Warmup the Theadpool
            ThreadPool.SetMinThreads(15, 10);

            for (int i = 0; i < 10; i++)
            {
                ipcOwner = new RpcBuffer(ipcName, async (msgId, payload) =>
                {
                }, bufferCapacity: 256);

                ipcOpener = new RpcBuffer(ipcName, (msgId, payload) =>
                {
                    ipcOpener.Dispose();
                    return new byte[] { (byte)(payload[0] * payload[1]) };
                    
                });

                Stopwatch watch = Stopwatch.StartNew();

                ipcOwner.RemoteRequestAsync(new byte[] { 3, 3 });
                Task.Delay(125).Wait();
                ipcOpener.Dispose();
                watch.Stop();

                ipcOwner.Dispose();

                while (!ipcOwner.DisposeFinished || !ipcOpener.DisposeFinished)
                {
                    Task.Delay(125).Wait();
                }

                
            }


            

         
        }
        
    }
}
#endif
