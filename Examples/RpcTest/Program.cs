// SharedMemory (File: RpcTest\Program.cs)
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

using SharedMemory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RpcTest
{
    class Program
    {
        static void Main(string[] args)
        {
            long completed = 0;
            long count = 0;
            byte[][] dataList;
            int loopCount = 2000;
            int bufSize = 1024 * 500;
            int bufferCapacity = bufSize + 64; // buf size + enough room for protocol header
            int threadCount = 1;
            int dataListCount = 256;
            
            // Generate random data to be written
            Random random = new Random();
            dataList = new byte[dataListCount][];
            for (var j = 0; j < dataListCount; j++)
            {
                var data = new byte[bufSize];
                random.NextBytes(data);
                dataList[j] = data;
            }

            Console.WriteLine($"Thread count: {threadCount}");
            Console.WriteLine($"Buffer size: {bufferCapacity}");
            Console.WriteLine($"Message size: {bufSize}");

            Console.WriteLine("Running...");

            Stopwatch watch = Stopwatch.StartNew();

            for (var i = 0; i < threadCount; i++)
            {
                new Task(async () =>
                    {
                        RpcBuffer ipcMaster = null;
                        RpcBuffer ipcSlave = null;
                        var name = $"MasterSlaveTest{Guid.NewGuid()}";
                        ipcMaster = new RpcBuffer(name, bufferCapacity: bufferCapacity);
                        ipcSlave = new RpcBuffer(name, (msgId, payload) =>
                        {
                            Interlocked.Increment(ref count);
                            return (byte[])null;
                            //return new byte[] { (byte)(payload[0] * payload[1]) };
                        });
                        var rnd = new Random();
                        var watchLine = Stopwatch.StartNew();
                        for (var j = 0; j < loopCount; j++)
                        {
                            var result = await ipcMaster.RemoteRequestAsync(dataList[rnd.Next(0, dataList.Length)]);
                            if (!result.Success)
                            {
                                Console.WriteLine("Failed");
                                return;
                            }
                        }
                        Interlocked.Increment(ref completed);
                    }).Start();
            }

            while(Interlocked.Read(ref completed) < threadCount)
            {
                Thread.Sleep(0);
            }

            watch.Stop();
            Console.WriteLine($"{count} in {watch.Elapsed}, {(int)(count / watch.Elapsed.TotalSeconds)} requests / sec");

            Console.ReadLine();
        }
    }
}
