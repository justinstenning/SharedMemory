using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace SharedMemoryTests.ReadmeSamples
{
    /// <summary>
    /// Checks the README's Example Usage section by running it. Each sample there is two programs, which this builds from
    /// the README's own text and runs as two processes, comparing what they print with what the README says they print.
    /// </summary>
    /// <remarks>
    /// <code>
    /// dotnet run --project SharedMemory.Tests.ReadmeSamples -c Release -- README.md [--work {folder}] [--frameworks {first}[,{second}]]
    ///
    /// --work        where the programs are written, built and run; by default TestResults/readme-samples beside the README
    /// --frameworks  what the first and second program of each sample target; by default net10.0 for both, and net48 on Windows
    ///               builds them for .NET Framework
    /// </code>
    /// What the section must hold, which is all that is read of it:
    /// <code>
    /// ### SharedArray         the four samples, under these headings and in this order:
    ///                         SharedArray, BufferReadWrite, CircularBuffer, RpcBuffer
    /// ```csharp               the first program, a whole Program.cs. It creates what the sample shares, prints at least
    ///                         one line, then waits for Enter
    /// ```csharp               the second program, which is started once the first has printed its lines
    /// ```text                 what the first program prints
    /// ```text                 what the second program prints
    /// ```text                 optional: what the second program prints when the first is not running
    /// </code>
    /// The blocks may come in any order under a heading, with any prose between them, and the section may hold no other
    /// code. Prints a line for each check, and exits with 1 if any failed.
    /// </remarks>
    static class Program
    {
        const string Marker = ".readme-samples";

        static int checks;
        static int failures;

        static int Main(string[] args)
        {
            string readme = null;
            string work = null;
            string[] frameworks = { "net10.0", "net10.0" };
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--work" && i + 1 < args.Length)
                {
                    work = args[++i];
                }
                else if (args[i] == "--frameworks" && i + 1 < args.Length)
                {
                    string[] given = args[++i].Split(',').Select(f => f.Trim()).ToArray();
                    if (given.Length > 2 || given.Any(f => f.Length == 0))
                        return Usage();
                    frameworks = new[] { given[0], given[given.Length - 1] };
                }
                else if (readme == null && !args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    readme = args[i];
                }
                else
                {
                    return Usage();
                }
            }
            if (readme == null)
                return Usage();

            readme = Path.GetFullPath(readme);
            string root = Path.GetDirectoryName(readme);
            string library = Path.Combine(root, "SharedMemory", "SharedMemory.csproj");
            if (!File.Exists(readme) || !File.Exists(library))
            {
                Console.Error.WriteLine("Expected " + readme + " with the library's project at " + library);
                return 2;
            }
            if (frameworks.Any(f => f.StartsWith("net4", StringComparison.Ordinal)) && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.Error.WriteLine("A program built for .NET Framework runs on Windows only");
                return 2;
            }
            work = Path.GetFullPath(work ?? Path.Combine(root, "TestResults", "readme-samples"));

            Console.WriteLine("The README's " + Readme.Section + ", first programs built for " + frameworks[0] + " and second programs for " + frameworks[1]);

            var problems = new List<string>();
            List<Sample> samples = Readme.Parse(File.ReadAllLines(readme), problems);
            if (!Check("the section holds the four samples, each two programs with their output", problems))
                return Finish();

            Check("no call to the library passes a bare number after its first argument",
                samples.SelectMany(s => s.Programs.SelectMany((p, i) => Readme.BareNumbers(p).Select(found => s.Name + ", program " + (i + 1) + ": " + found))).ToList());

            if (!Empty(work))
                return 2;
            foreach (Sample sample in samples)
            {
                SampleProgram first = SampleRunner.Write(work, sample, 0, frameworks[0], library);
                SampleProgram second = SampleRunner.Write(work, sample, 1, frameworks[1], library);
                List<string> build = SampleRunner.Build(first);
                build.AddRange(SampleRunner.Build(second));
                if (!Check(sample.Name + ": both programs build, with no warning", build))
                    continue;

                Check(sample.Name + ": run as two processes, each program prints what the README shows", SampleRunner.RunPair(sample, first, second));
                if (sample.OutputAlone != null)
                    Check(sample.Name + ": started alone, the second program prints what the README shows", SampleRunner.RunAlone(sample, second));
            }
            return Finish();
        }

        static bool Check(string name, List<string> problems)
        {
            checks++;
            if (problems.Count == 0)
            {
                Console.WriteLine("ok    " + name);
                return true;
            }
            failures++;
            Console.WriteLine("FAIL  " + name);
            foreach (string problem in problems)
                Console.WriteLine("      " + problem);
            return false;
        }

        static int Finish()
        {
            Console.WriteLine(checks + " checks, " + failures + " failed");
            return failures == 0 ? 0 : 1;
        }

        // Starts the work folder afresh. It deletes only a folder this program made, which its marker file shows.
        static bool Empty(string work)
        {
            if (Directory.Exists(work) && Directory.EnumerateFileSystemEntries(work).Any())
            {
                if (!File.Exists(Path.Combine(work, Marker)))
                {
                    Console.Error.WriteLine(work + " holds files this program did not write. Give --work an empty folder or a new one");
                    return false;
                }
                Directory.Delete(work, true);
            }
            Directory.CreateDirectory(work);
            File.WriteAllText(Path.Combine(work, Marker), "Written by SharedMemory.Tests.ReadmeSamples, which deletes this folder at the start of each run.\n");
            return true;
        }

        static int Usage()
        {
            Console.Error.WriteLine("usage: SharedMemory.Tests.ReadmeSamples <README.md> [--work <folder>] [--frameworks <first>[,<second>]]");
            return 2;
        }
    }
}
