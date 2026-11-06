using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// An opener racing its owner: one thread creates and disposes buffers under new names as fast as it can, with no sleeps, and
    /// whatever their DoOpen writes, another opens each name as soon as it is announced. An opener that opens reads what DoOpen
    /// wrote, at once, because it waited for the owner's ready flag. Every open that fails is FileNotFoundException (the name is
    /// not made yet, or gone) or "is not ready".
    /// </summary>
    [TestClass]
    public class LayoutMarkerOrderingTests
    {
        static readonly byte[] Pattern = Enumerable.Range(0, 64).Select(i => (byte)(255 - i * 3)).ToArray();

        [TestMethod]
        public void CreateWhileOpening_OpenerSeesTheWholeBuffer()
        {
            var buffers = new Race("buffer", TimeSpan.FromSeconds(2), name => new PatternBuffer(name), ReadPattern);
            buffers.Run();
            buffers.AssertNoProblem();
        }

        [TestMethod]
        [TestCategory("Stress")]
        public void CreateWhileOpening_OpenerSeesTheWholeBuffer_ForAMinute()
        {
            var buffers = new Race("buffer", TimeSpan.FromSeconds(60), name => new PatternBuffer(name), ReadPattern);
            var rings = new Race("ring", TimeSpan.FromSeconds(60), name => new PatternRing(name), ReadFirstItem);
            var racing = new Thread(buffers.Run) { IsBackground = true };
            racing.Start();
            rings.Run();
            Assert.IsTrue(racing.Join(TimeSpan.FromSeconds(30)), "The buffers' race did not finish.");
            buffers.AssertNoProblem();
            rings.AssertNoProblem();
        }

        /// <summary>
        /// Opens the buffer as an event-free SharedBuffer and returns its first 64 bytes.
        /// </summary>
        static byte[] ReadPattern(string name)
        {
            using (var opener = new EventFreeBuffer(name))
                return opener.ReadBytes(Pattern.Length);
        }

        /// <summary>
        /// Opens the ring and returns its first item, or no bytes if it has none.
        /// </summary>
        static byte[] ReadFirstItem(string name)
        {
            using (var opener = new CircularBuffer(name))
            {
                byte[] item = new byte[Pattern.Length];
                int read = opener.Read(item, 0, 0);
                Array.Resize(ref item, read);
                return item;
            }
        }

        sealed class Race
        {
            readonly string _what;
            readonly TimeSpan _duration;
            readonly Func<string, IDisposable> _create;
            readonly Func<string, byte[]> _openAndRead;
            readonly string _prefix = Guid.NewGuid().ToString("N") + "-";
            readonly List<string> _problems = new List<string>();
            int _problemCount;
            string _current;
            volatile bool _stop;
            int _created, _opened, _missing, _notReady;

            public Race(string what, TimeSpan duration, Func<string, IDisposable> create, Func<string, byte[]> openAndRead)
            {
                _what = what;
                _duration = duration;
                _create = create;
                _openAndRead = openAndRead;
            }

            public void Run()
            {
                var opener = new Thread(Open) { IsBackground = true };
                opener.Start();
                var watch = Stopwatch.StartNew();
                for (int i = 0; !_stop && watch.Elapsed < _duration; i++)
                {
                    string name = _prefix + i;
                    Volatile.Write(ref _current, name);
                    try
                    {
                        using (_create(name))
                            _created++;
                    }
                    catch (Exception e)
                    {
                        Problem("creating " + name + ": " + e.GetType().FullName + ": " + e.Message);
                        break;
                    }
                }
                _stop = true;
                if (!opener.Join(TimeSpan.FromSeconds(30)))
                    Problem("the opener did not stop");
            }

            void Open()
            {
                string last = null;
                while (!_stop)
                {
                    string name = Volatile.Read(ref _current);
                    if (name == null || name == last)
                    {
                        Thread.Yield();
                        continue;
                    }
                    try
                    {
                        byte[] read = _openAndRead(name);
                        last = name;
                        if (read.SequenceEqual(Pattern))
                            Interlocked.Increment(ref _opened);
                        else
                            Problem("opened " + name + " before its owner's DoOpen returned: read [" + BitConverter.ToString(read) + "]");
                    }
                    catch (FileNotFoundException)
                    {
                        Interlocked.Increment(ref _missing);
                    }
                    catch (InvalidOperationException e) when (e.Message.Contains("is not ready"))
                    {
                        last = name;
                        Interlocked.Increment(ref _notReady);
                    }
                    catch (Exception e)
                    {
                        last = name;
                        Problem("opening " + name + ": " + e.GetType().FullName + ": " + e.Message);
                    }
                }
            }

            void Problem(string problem)
            {
                lock (_problems)
                {
                    _problemCount++;
                    if (_problems.Count < 10)
                        _problems.Add(problem);
                }
            }

            public void AssertNoProblem()
            {
                string counts = _what + "s: " + _created + " created, " + _opened + " opened, " + _missing + " not found, " + _notReady + " not ready, "
                    + _problemCount + " problems";
                Assert.AreEqual(0, _problemCount, counts + ". The first: " + string.Join(" | ", _problems.ToArray()));
                Assert.IsTrue(_opened > 0, "No opener opened a " + _what + " in " + _duration.TotalSeconds + " s; " + counts);
            }
        }

        /// <summary>
        /// A SharedBuffer subclass whose DoOpen writes <see cref="Pattern"/> at the start of its buffer.
        /// </summary>
        sealed class PatternBuffer : SharedBuffer
        {
            public PatternBuffer(string name)
                : base(name, 1024, true)
            {
                Open();
            }

            protected override bool DoOpen()
            {
                Write(Pattern);
                return true;
            }
        }

        /// <summary>
        /// A ring whose DoOpen writes <see cref="Pattern"/> as its first item.
        /// </summary>
        sealed class PatternRing : CircularBuffer
        {
            public PatternRing(string name)
                : base(name, 4, Pattern.Length)
            {
            }

            protected override bool DoOpen()
            {
                bool opened = base.DoOpen();
                if (Write(Pattern, 0, 0) != Pattern.Length)
                    throw new InvalidOperationException("The owner could not write its first item.");
                return opened;
            }
        }
    }
}
