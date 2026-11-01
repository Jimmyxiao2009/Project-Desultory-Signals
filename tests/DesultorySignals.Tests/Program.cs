using System;

namespace DesultorySignals.Tests
{
    /// <summary>
    /// Desktop test runner for the C# core.
    /// </summary>
    /// <remarks>
    /// A plain console runner rather than a test framework: the project targets the
    /// oldest toolchain in the workspace on purpose, and a runner with no
    /// dependencies keeps the whole suite buildable with a single `dotnet run`.
    /// Exit code is the number of failures, so a script can gate on it.
    /// </remarks>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            Console.WriteLine(new string('=', 72));
            Console.WriteLine("Desultory Signals - C# core tests");
            Console.WriteLine("runtime: " + Environment.Version + "  os: " + Environment.OSVersion);
            Console.WriteLine(new string('=', 72));

            var runner = new TestRunner();
            int failures = runner.Run();

            if (args != null && Array.IndexOf(args, "--pause") >= 0)
            {
                Console.WriteLine("");
                Console.WriteLine("press enter to exit");
                Console.ReadLine();
            }
            return failures;
        }
    }
}
