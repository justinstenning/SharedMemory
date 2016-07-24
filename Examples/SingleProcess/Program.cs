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
using CommandLine;
#if NET40Plus
using System.Threading.Tasks;
#endif

namespace SingleProcess
{
    class Program
    {
        class AppArguments
        {
            [Argument(ArgumentType.AtMostOnce, ShortName = "b", DefaultValue = 1048576, HelpText = "The buffer size.")]
            public int bufferSize = 0;
            [Argument(ArgumentType.AtMostOnce, ShortName = "n", DefaultValue = 50, HelpText = "The number of nodes.")]
            public int nodeCount = 0;
            [Argument(ArgumentType.Required, ShortName = "w", HelpText = "The number of writers.")]
            public int writers = 0;
            [Argument(ArgumentType.Required, ShortName = "r", HelpText = "The number of readers.")]
            public int readers = 0;
            [Argument(ArgumentType.AtMostOnce, ShortName = "e", DefaultValue = 100000, HelpText = "The number of elements to process.")]
            public int elements = 0;
        }

        static void Main(string[] args)
        {
            int elements = 100000;
            int writeCount = 0;
            int clientWaitCount = 0;
            int readCount = 0;
            int serverWaitCount = 0;
            long lastTick = 0;
            int bufferSize = 1048576;
            int size = sizeof(byte) * bufferSize;
            int count = 50; // node count within buffer

            int serverCount = 0;
            int clientCount = 0;

            // Process command line
            AppArguments parsedArgs = new AppArguments();
            var validArgs = Parser.ParseArgumentsWithUsage(args, parsedArgs);

            if (!validArgs)
            {
                return;
            }
            else
            {
                elements = parsedArgs.elements;
                bufferSize = parsedArgs.bufferSize;
                serverCount = parsedArgs.writers;
                clientCount = parsedArgs.readers;
                size = sizeof(byte) * bufferSize;
                count = parsedArgs.nodeCount;
            }

            
            Console.WriteLine("Node buffer size: {0}, count: {1}, writers: {2}, readers {3}, elements: {4}", size, count, serverCount, clientCount, elements);

            int dataListCount = 256;
            // Generate random data to be written
            Random random = new Random();
            byte[][] dataList = new byte[dataListCount][];
            for (var j = 0; j < dataListCount; j++)
            {
                var data = new byte[size];
                random.NextBytes(data);
                dataList[j] = data;
            }

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

                    if (writeCount > elements && writeCount - readCount == 0)
                        break;
                }
            };
            for (int c = 0; c < clientCount; c++)
            {
#if NET40Plus
                Task c1 = Task.Factory.StartNew(clientAction);
#else
                ThreadPool.QueueUserWorkItem((o) => { clientAction(); });
#endif
            }
            bool wait = true;
            int index = 0;
            Action serverWrite = () =>
            {
                int serverIndex = Interlocked.Increment(ref index);

                var writer = (serverIndex == 1 ? server : new SharedMemory.CircularBuffer(name));
                bool done = false;
                TimeSpan doneTime = TimeSpan.MinValue;
                for (; ; )
                {
                    if (writeCount <= elements)
                    {
                        int amount = writer.Write(dataList[random.Next(0, dataListCount)], 100);
                        bytesWritten += amount;
                        if (amount == 0)
                            Interlocked.Increment(ref serverWaitCount);
                        else
                            Interlocked.Increment(ref writeCount);
                    }
                    else
                    {
                        if (!done && serverIndex == 1)
                        {
                            doneTime = sw.Elapsed;
                            done = true;
                        }
                    }

                    if (serverIndex == 1 && sw.ElapsedTicks - lastTick > 1000000)
                    {
                        Console.WriteLine("Write: {0}, Read: {1}, Diff: {5}, Wait(cli/svr): {3}/{2}, {4}MB/s", writeCount, readCount, serverWaitCount, clientWaitCount, (int)((((bytesWritten + bytesRead) / 1048576.0) / sw.ElapsedMilliseconds) * 1000), writeCount - readCount);
                        lastTick = sw.ElapsedTicks;
                        if (writeCount > elements && writeCount - readCount == 0)
                        {
                            Console.WriteLine("Total Time: " + doneTime);
                            wait = false;
                            break;
                        }
                    }
                }
            };

            for (int s = 0; s < serverCount; s++)
            {
#if NET40Plus
                Task s1 = Task.Factory.StartNew(serverWrite);
#else
                ThreadPool.QueueUserWorkItem((o) => { serverWrite(); });
#endif
            }
            while (wait)
                Thread.Sleep(100);
        }
    }
}
