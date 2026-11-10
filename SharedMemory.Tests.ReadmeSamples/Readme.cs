using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SharedMemoryTests.ReadmeSamples
{
    /// <summary>
    /// One sample from the README's Example Usage: its programs, in the order they are started, and the output the README
    /// says each prints.
    /// </summary>
    sealed class Sample
    {
        public readonly string Name;

        /// <summary>The lines of each <c>csharp</c> block: the first program, then the second.</summary>
        public readonly List<string[]> Programs = new List<string[]>();

        /// <summary>The lines of each <c>text</c> block: the first program's output, the second's, then <see cref="OutputAlone"/>.</summary>
        public readonly List<string[]> Outputs = new List<string[]>();

        public Sample(string name)
        {
            Name = name;
        }

        /// <summary>
        /// What the second program prints when it is started with no first program running, or null where the README does
        /// not say.
        /// </summary>
        public string[] OutputAlone
        {
            get { return Outputs.Count > 2 ? Outputs[2] : null; }
        }
    }

    /// <summary>Reads the samples out of the README, from its headings and fenced blocks alone, never its prose.</summary>
    static class Readme
    {
        public const string Section = "Example Usage";

        /// <summary>The section's <c>###</c> headings, in order. A sample that goes missing or is renamed fails the check.</summary>
        public static readonly string[] SampleNames = { "SharedArray", "BufferReadWrite", "CircularBuffer", "RpcBuffer" };

        // A call to the library: a read, a write or a request, or one of the four classes' constructors.
        static readonly Regex libraryCall = new Regex(
            @"(?:\.\s*(?:Read|Write|RemoteRequest|RemoteRequestAsync)\b\s*(?:<[^<>()]*>)?" +
            @"|\bnew\s+(?:SharedArray\s*<[^<>()]*>|BufferReadWrite|CircularBuffer|RpcBuffer))\s*\(");

        static readonly Regex number = new Regex(
            @"^[-+]?\s*(?:0[xX][0-9a-fA-F_]+|[0-9][0-9_]*(?:\.[0-9_]+)?(?:[eE][-+]?[0-9]+)?)[uUlLfFdDmM]{0,2}$");

        /// <summary>
        /// Reads the section's samples, and adds to <paramref name="problems"/> everything about the section that is not
        /// as the samples need it: four <c>###</c> headings, and under each two <c>csharp</c> blocks and two or three
        /// <c>text</c> blocks, with no other code in the section.
        /// </summary>
        public static List<Sample> Parse(string[] lines, List<string> problems)
        {
            var samples = new List<Sample>();
            int start = -1;
            for (int i = 0; i + 1 < lines.Length && start < 0; i++)
            {
                if (lines[i].Trim() == Section && IsUnderline(lines[i + 1]))
                    start = i + 2;
            }
            if (start < 0)
            {
                problems.Add("the README has no \"" + Section + "\" section");
                return samples;
            }

            Sample sample = null;
            string language = null;   // of the block being read, or null between blocks
            var block = new List<string>();
            bool indentedSeen = false;
            for (int i = start; i < lines.Length; i++)
            {
                string line = lines[i];
                if (language != null)
                {
                    if (line.Trim() != "```")
                    {
                        block.Add(line);
                        continue;
                    }
                    if (sample == null)
                        problems.Add("line " + (i - block.Count) + ": a code block before the first sample's heading");
                    else if (language == "csharp")
                        sample.Programs.Add(block.ToArray());
                    else if (language == "text")
                        sample.Outputs.Add(block.ToArray());
                    else
                        problems.Add(sample.Name + " has a block marked \"" + language + "\": a sample holds only csharp blocks, its programs, and text blocks, their output");
                    language = null;
                    continue;
                }

                // The section ends at the next heading of its own kind.
                if (line.Trim().Length > 0 && i + 1 < lines.Length && IsUnderline(lines[i + 1]))
                    break;

                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    language = line.Substring(3).Trim();
                    block.Clear();
                }
                else if (line.StartsWith("### ", StringComparison.Ordinal))
                {
                    sample = new Sample(line.Substring(4).Trim());
                    samples.Add(sample);
                }
                else if (!indentedSeen && line.Trim().Length > 0 && (line.StartsWith("    ", StringComparison.Ordinal) || line[0] == '\t'))
                {
                    indentedSeen = true;
                    problems.Add("line " + (i + 1) + ": an indented code block, which cannot be run: a program goes in a fenced csharp block and its output in a fenced text block");
                }
            }
            if (language != null)
                problems.Add("the section ends inside a fenced block");

            string[] found = samples.Select(s => s.Name).ToArray();
            if (!found.SequenceEqual(SampleNames))
            {
                problems.Add("the samples should be " + string.Join(", ", SampleNames) + ", each under a ### heading, in that order; found "
                    + (found.Length == 0 ? "none" : string.Join(", ", found)));
            }
            foreach (Sample s in samples)
            {
                if (s.Programs.Count != 2)
                    problems.Add(s.Name + " has " + s.Programs.Count + " csharp blocks; a sample is two programs");
                if (s.Outputs.Count < 2 || s.Outputs.Count > 3)
                    problems.Add(s.Name + " has " + s.Outputs.Count + " text blocks; a sample shows what each of its two programs prints, and may show what the second prints when started alone");
                else if (Lines(s.Outputs[0]).Length == 0)
                    problems.Add(s.Name + "'s first program prints nothing; it must print a line once it is ready, so that the second program is started after that");
            }
            if (samples.Count > 0 && samples.All(s => s.OutputAlone == null))
                problems.Add("no sample has a third text block, showing what its second program prints when started alone");
            return samples;
        }

        /// <summary>
        /// The calls in a program that pass a bare number after their first argument, where a reader cannot tell a timeout
        /// from a start index or a buffer position. Covers the library's reads, writes and requests and its constructors.
        /// </summary>
        public static List<string> BareNumbers(string[] program)
        {
            var found = new List<string>();
            string code = WithoutComments(string.Join("\n", program));
            foreach (Match call in libraryCall.Matches(code))
            {
                int open = call.Index + call.Length - 1;
                int close;
                List<string> arguments = Arguments(code, open, out close);
                for (int i = 1; i < arguments.Count; i++)
                {
                    if (!number.IsMatch(arguments[i]))
                        continue;
                    int from = call.Index;
                    while (from > 0 && (char.IsLetterOrDigit(code[from - 1]) || code[from - 1] == '_' || code[from - 1] == '.'))
                        from--;
                    string text = Regex.Replace(code.Substring(from, close - from + 1), @"\s+", " ");
                    found.Add(text + " passes the bare number " + arguments[i] + ". Name the argument, for example timeout: " + arguments[i]
                        + " or bufferPosition: " + arguments[i]);
                }
            }
            return found;
        }

        /// <summary>The lines of a block or of a program's output, without trailing white space and trailing empty lines.</summary>
        public static string[] Lines(IEnumerable<string> lines)
        {
            List<string> trimmed = lines.Select(l => l.TrimEnd()).ToList();
            while (trimmed.Count > 0 && trimmed[trimmed.Count - 1].Length == 0)
                trimmed.RemoveAt(trimmed.Count - 1);
            return trimmed.ToArray();
        }

        static bool IsUnderline(string line)
        {
            string trimmed = line.Trim();
            return trimmed.Length >= 2 && (trimmed.All(c => c == '-') || trimmed.All(c => c == '='));
        }

        // The arguments of the call whose opening bracket is at open, split at its own commas.
        static List<string> Arguments(string code, int open, out int close)
        {
            var arguments = new List<string>();
            var argument = new StringBuilder();
            int depth = 0;
            int i = open;
            for (; i < code.Length; i++)
            {
                char c = code[i];
                if (c == '"' || c == '\'')
                {
                    int end = EndOfLiteral(code, i);
                    argument.Append(code, i, end - i + 1);
                    i = end;
                    continue;
                }
                if (c == '(' || c == '[' || c == '{')
                {
                    if (depth++ == 0)
                        continue;
                }
                else if (c == ')' || c == ']' || c == '}')
                {
                    if (--depth == 0)
                        break;
                }
                else if (c == ',' && depth == 1)
                {
                    arguments.Add(argument.ToString().Trim());
                    argument.Clear();
                    continue;
                }
                argument.Append(c);
            }
            if (argument.ToString().Trim().Length > 0)
                arguments.Add(argument.ToString().Trim());
            close = Math.Min(i, code.Length - 1);
            return arguments;
        }

        // The program with each comment replaced by a space, so that a call a comment talks about is not read as one.
        static string WithoutComments(string code)
        {
            var kept = new StringBuilder();
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (c == '"' || c == '\'')
                {
                    int end = EndOfLiteral(code, i);
                    kept.Append(code, i, end - i + 1);
                    i = end;
                }
                else if (c == '/' && i + 1 < code.Length && code[i + 1] == '/')
                {
                    while (i < code.Length && code[i] != '\n')
                        i++;
                    kept.Append('\n');
                }
                else if (c == '/' && i + 1 < code.Length && code[i + 1] == '*')
                {
                    int end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? code.Length : end + 1;
                    kept.Append(' ');
                }
                else
                {
                    kept.Append(c);
                }
            }
            return kept.ToString();
        }

        // The index of the quote that closes the string or character literal opening at start.
        static int EndOfLiteral(string code, int start)
        {
            char quote = code[start];
            bool verbatim = quote == '"' && start > 0 && (code[start - 1] == '@' || (code[start - 1] == '$' && start > 1 && code[start - 2] == '@'));
            for (int i = start + 1; i < code.Length; i++)
            {
                if (code[i] == '\n' && !verbatim)
                    return i - 1;
                if (code[i] == '\\' && !verbatim)
                    i++;
                else if (code[i] == quote && verbatim && i + 1 < code.Length && code[i + 1] == quote)
                    i++;
                else if (code[i] == quote)
                    return i;
            }
            return code.Length - 1;
        }
    }
}
