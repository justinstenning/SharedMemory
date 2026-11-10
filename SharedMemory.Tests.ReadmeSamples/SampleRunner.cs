using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;

namespace SharedMemoryTests.ReadmeSamples
{
    /// <summary>One of a sample's programs, written out as a console project of its own.</summary>
    sealed class SampleProgram
    {
        /// <summary>Holds Program.cs, the project, the build log and what each run printed.</summary>
        public string Folder;
        public string AssemblyName;
        public string Framework;

        public bool IsNetFramework
        {
            get { return Framework.StartsWith("net4", StringComparison.Ordinal); }
        }

        public string OutputFolder
        {
            get { return Path.Combine(Folder, "bin", "Release", Framework); }
        }
    }

    /// <summary>Writes, builds and runs the samples' programs.</summary>
    static class SampleRunner
    {
        /// <summary>How long a program gets to print its lines, and to end once it should. No sample's output depends on timing.</summary>
        static readonly TimeSpan limit = TimeSpan.FromSeconds(60);

        static readonly TimeSpan buildLimit = TimeSpan.FromMinutes(10);

        static readonly string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

        /// <summary>
        /// Writes one program as a console project: Program.cs is the README's block, unchanged, and the project is what
        /// a reader would have.
        /// </summary>
        public static SampleProgram Write(string work, Sample sample, int index, string framework, string libraryProject)
        {
            var program = new SampleProgram
            {
                Folder = Path.Combine(work, sample.Name, (index + 1).ToString()),
                AssemblyName = sample.Name + (index + 1),
                Framework = framework,
            };
            Directory.CreateDirectory(program.Folder);
            File.WriteAllText(Path.Combine(program.Folder, "Program.cs"), string.Join("\n", sample.Programs[index]) + "\n");

            // For .NET, the settings dotnet new console gives a project. For .NET Framework, whose projects default to
            // C# 7.3, the language version the README tells those readers to set.
            string language = program.IsNetFramework
                ? "    <LangVersion>9.0</LangVersion>\n"
                : "    <ImplicitUsings>enable</ImplicitUsings>\n    <Nullable>enable</Nullable>\n";
            File.WriteAllText(Path.Combine(program.Folder, program.AssemblyName + ".csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                "  <PropertyGroup>\n" +
                "    <OutputType>Exe</OutputType>\n" +
                "    <TargetFramework>" + framework + "</TargetFramework>\n" +
                language +
                "    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>\n" +
                "    <MSBuildTreatWarningsAsErrors>true</MSBuildTreatWarningsAsErrors>\n" +
                "  </PropertyGroup>\n" +
                "  <ItemGroup>\n" +
                "    <ProjectReference Include=\"" + SecurityElement.Escape(libraryProject) + "\" />\n" +
                "  </ItemGroup>\n" +
                "</Project>\n");
            return program;
        }

        /// <summary>Builds a program. Returns what went wrong, a warning included; nothing if it built.</summary>
        public static List<string> Build(SampleProgram program)
        {
            var problems = new List<string>();
            var start = new ProcessStartInfo(dotnet)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = program.Folder,
            };
            foreach (string argument in new[] { "build", program.AssemblyName + ".csproj", "-c", "Release", "-nologo", "-tl:off" })
                start.ArgumentList.Add(argument);

            var log = new StringBuilder();
            using (var build = new Process { StartInfo = start })
            {
                build.OutputDataReceived += (sender, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
                build.ErrorDataReceived += (sender, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
                build.Start();
                build.BeginOutputReadLine();
                build.BeginErrorReadLine();
                if (!build.WaitForExit((int)buildLimit.TotalMilliseconds))
                {
                    build.Kill(true);
                    problems.Add("program " + program.AssemblyName + "'s build was still running after " + buildLimit.TotalMinutes + " minutes");
                }
                build.WaitForExit();
                string logPath = Path.Combine(program.Folder, "build.log");
                File.WriteAllText(logPath, log.ToString());
                if (problems.Count == 0 && build.ExitCode != 0)
                {
                    problems.Add("program " + program.AssemblyName + " (" + program.Framework + ") does not build:");
                    string[] errors = log.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Contains(": error ")).Distinct().Take(10).ToArray();
                    problems.AddRange(errors.Select(e => "  " + e));
                    if (errors.Length == 0)
                        problems.Add("  dotnet build exited with code " + build.ExitCode + "; see " + logPath);
                }
            }
            return problems;
        }

        /// <summary>
        /// Runs a sample as its reader would, in two processes. The first program is started and left until it has printed
        /// its lines, which is when it waits for Enter. Then the second is started and left until it has printed its
        /// lines or ended. Then the first gets its Enter. Both must end with exit code 0, having printed exactly what the
        /// README shows, and nothing on stderr.
        /// </summary>
        public static List<string> RunPair(Sample sample, SampleProgram first, SampleProgram second)
        {
            var problems = new List<string>();
            string[] firstExpected = Readme.Lines(sample.Outputs[0]);
            string[] secondExpected = Readme.Lines(sample.Outputs[1]);
            using (var a = new RunningProgram(first))
            {
                if (!a.WaitForLines(firstExpected.Length, limit))
                {
                    problems.Add(a.HasExited
                        ? "the first program ended before the second was started; it should wait, since the buffer lasts only while a process has it open"
                        : "the first program had not printed its " + firstExpected.Length + " lines after " + limit.TotalSeconds + " s");
                    a.Save("");
                    Describe(problems, "first", a, firstExpected, a.HasExited);
                    return problems;
                }

                using (var b = new RunningProgram(second))
                {
                    b.CloseInput();
                    b.WaitForLines(secondExpected.Length, limit);
                    a.PressEnter();
                    bool aEnded = a.WaitForExit(limit);
                    bool bEnded = b.WaitForExit(limit);
                    a.Save("");
                    b.Save("");
                    Describe(problems, "first", a, firstExpected, aEnded);
                    Describe(problems, "second", b, secondExpected, bEnded);
                }
            }
            return problems;
        }

        /// <summary>Runs a sample's second program with no first program running: it must end by itself, as the README shows.</summary>
        public static List<string> RunAlone(Sample sample, SampleProgram second)
        {
            var problems = new List<string>();
            using (var b = new RunningProgram(second))
            {
                b.CloseInput();
                bool ended = b.WaitForExit(limit);
                b.Save("alone-");
                Describe(problems, "second", b, Readme.Lines(sample.OutputAlone), ended);
            }
            return problems;
        }

        static void Describe(List<string> problems, string which, RunningProgram program, string[] expected, bool ended)
        {
            if (!ended)
                problems.Add("the " + which + " program was still running after " + limit.TotalSeconds + " s");
            else if (program.ExitCode != 0)
                problems.Add("the " + which + " program ended with exit code " + program.ExitCode);

            string[] printed = Readme.Lines(program.Output);
            if (!printed.SequenceEqual(expected))
            {
                problems.Add("the " + which + " program printed:");
                problems.AddRange(printed.Select(l => "  | " + l));
                problems.Add("where the README says it prints:");
                problems.AddRange(expected.Select(l => "  | " + l));
            }

            string[] errors = Readme.Lines(program.Errors.Split('\n'));
            if (errors.Length > 0)
            {
                problems.Add("the " + which + " program wrote to stderr:");
                problems.AddRange(errors.Take(20).Select(l => "  | " + l));
            }
        }

        /// <summary>A program that has been started, and what it has printed so far.</summary>
        sealed class RunningProgram : IDisposable
        {
            readonly SampleProgram program;
            readonly Process process;
            readonly List<string> output = new List<string>();
            readonly StringBuilder errors = new StringBuilder();

            public RunningProgram(SampleProgram program)
            {
                this.program = program;
                var start = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = program.OutputFolder,
                };
                if (program.IsNetFramework)
                {
                    start.FileName = Path.Combine(program.OutputFolder, program.AssemblyName + ".exe");
                }
                else
                {
                    start.FileName = dotnet;
                    start.ArgumentList.Add(Path.Combine(program.OutputFolder, program.AssemblyName + ".dll"));
                }

                process = new Process { StartInfo = start };
                process.OutputDataReceived += (sender, e) => { if (e.Data != null) lock (output) output.Add(e.Data); };
                process.ErrorDataReceived += (sender, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            public bool HasExited
            {
                get { return process.HasExited; }
            }

            public int ExitCode
            {
                get { return process.ExitCode; }
            }

            public string[] Output
            {
                get { lock (output) return output.ToArray(); }
            }

            public string Errors
            {
                get { lock (errors) return errors.ToString(); }
            }

            /// <summary>Waits until the program has printed this many lines. False if it ended, or the time ran out, first.</summary>
            public bool WaitForLines(int count, TimeSpan time)
            {
                Stopwatch waited = Stopwatch.StartNew();
                for (;;)
                {
                    lock (output)
                    {
                        if (output.Count >= count)
                            return true;
                    }
                    if (process.HasExited)
                    {
                        process.WaitForExit();   // until everything it printed has been read
                        lock (output) return output.Count >= count;
                    }
                    if (waited.Elapsed > time)
                        return false;
                    Thread.Sleep(20);
                }
            }

            public bool WaitForExit(TimeSpan time)
            {
                if (!process.WaitForExit((int)time.TotalMilliseconds))
                    return false;
                process.WaitForExit();   // until everything it printed has been read
                return true;
            }

            /// <summary>A program that does not read its input must not be left waiting on it.</summary>
            public void CloseInput()
            {
                process.StandardInput.Close();
            }

            public void PressEnter()
            {
                try
                {
                    process.StandardInput.WriteLine();
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // It has ended already, which the checks after this report.
                }
            }

            /// <summary>Keeps what the program printed beside its source, for whoever reads a failed run.</summary>
            public void Save(string prefix)
            {
                File.WriteAllLines(Path.Combine(program.Folder, prefix + "stdout.txt"), Output);
                File.WriteAllText(Path.Combine(program.Folder, prefix + "stderr.txt"), Errors);
            }

            public void Dispose()
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                        process.WaitForExit();
                    }
                }
                catch (InvalidOperationException)
                {
                    // It ended between the look and the kill.
                }
                process.Dispose();
            }
        }
    }
}
