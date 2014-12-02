// SharedMemory (File: SingleProcess\Program.cs)
// Copyright (c) 2014 Justin Stenning
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
//
// The SharedMemory library is inspired by the following Code Project article:
//   "Fast IPC Communication Using Shared Memory and InterlockedCompareExchange"
//   http://www.codeproject.com/Articles/14740/Fast-IPC-Communication-Using-Shared-Memory-and-Int

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
#if NET40Plus
using System.Threading.Tasks;
#endif

namespace SingleProcess
{
    class Program
    {
        static void Main(string[] args)
        {
            int writeCount = 0;
            int clientWaitCount = 0;
            int readCount = 0;
            int serverWaitCount = 0;
            long lastTick = 0;
            byte[] readData = new byte[1048576];
            int size = sizeof(byte) * 1048576;
            int count = 2; // node count within buffer

            long bytesWritten = 0;
            long bytesRead = 0;
            string name = Guid.NewGuid().ToString();
            var server = new SharedMemory.CircularBuffer(name, count, size);
            
            Stopwatch sw = Stopwatch.StartNew();

            Action clientAction = () =>
            {
                byte[] testData = new byte[size];

                var client = new SharedMemory.CircularBuffer(name);

                Stopwatch clientTime = new Stopwatch();
                clientTime.Start();
                long startTick = 0;
                long stopTick = 0;

                for (; ; )
                {
                    startTick = clientTime.ElapsedTicks;
                    int amount = client.Read(testData, 100);
                    bytesRead += amount;
                    if (amount == 0)
                        Interlocked.Increment(ref clientWaitCount);
                    else
                        Interlocked.Increment(ref readCount);
                    stopTick = clientTime.ElapsedTicks;

                    if (writeCount > 100000 && writeCount - readCount == 0)
                        break;
                }
            };
#if NET40Plus
            Task c1 = Task.Factory.StartNew(clientAction);
            Task c2 = Task.Factory.StartNew(clientAction);
            Task c3 = Task.Factory.StartNew(clientAction);
            Task c4 = Task.Factory.StartNew(clientAction);
            //Task c5 = Task.Factory.StartNew(clientAction);
            //Task c6 = Task.Factory.StartNew(clientAction);
            //Task c7 = Task.Factory.StartNew(clientAction);
#else
            ThreadPool.QueueUserWorkItem((o) => { clientAction(); });
            ThreadPool.QueueUserWorkItem((o) => { clientAction(); });
            ThreadPool.QueueUserWorkItem((o) => { clientAction(); });
            ThreadPool.QueueUserWorkItem((o) => { clientAction(); });
#endif
            int index = 0;

            Action serverWrite = () =>
            {
                int serverIndex = Interlocked.Increment(ref index);

                var writer = (serverIndex == 1 ? server : new SharedMemory.CircularBuffer(name));

                for (; ; )
                {
                    if (writeCount <= 100000)
                    {
                        int amount = writer.Write(readData, 100);
                        bytesWritten += amount;
                        if (amount == 0)
                            Interlocked.Increment(ref serverWaitCount);
                        else
                            Interlocked.Increment(ref writeCount);
                    }

                    if (serverIndex == 1 && sw.ElapsedTicks - lastTick > 1000000)
                    {
                        lastTick = sw.ElapsedTicks;
                        Console.WriteLine("Write: {0}, Read: {1}, Diff: {5}, Wait(cli/svr): {3}/{2}, {4}MB/s", writeCount, readCount, serverWaitCount, clientWaitCount, (int)((((bytesWritten + bytesRead) / 1048576.0) / sw.ElapsedMilliseconds) * 1000), writeCount - readCount);

                        if (writeCount > 100000 && writeCount - readCount == 0)
                            break;
                    }
                }
            };

#if NET40Plus
            Task s1 = Task.Factory.StartNew(serverWrite);
            //Task s2 = Task.Factory.StartNew(serverWrite);
            //Task s3 = Task.Factory.StartNew(serverWrite);
            //Task s4 = Task.Factory.StartNew(serverWrite);
            //Task s5 = Task.Factory.StartNew(serverWrite);
            //Task s6 = Task.Factory.StartNew(serverWrite);
            //Task s7 = Task.Factory.StartNew(serverWrite);
#else
            ThreadPool.QueueUserWorkItem((o) => { serverWrite(); });
#endif
            Console.ReadLine();
        }
    }
}
