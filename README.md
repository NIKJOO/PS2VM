# PS2VM

> **PowerShell → convert powershell scripts to bytecodes and run inside virtual machine**.

---

## Disclaimer

**This software is provided for legitimate research, education, interoperability testing, and authorized automation only.**

By downloading, building, or using this project you acknowledge and agree that:

1. **You are solely responsible** for how you use this software and for ensuring that your use complies with all applicable laws, regulations, organizational policies, and third-party terms of service.
2. The authors and contributors **do not condone or support** any use of this software for malware, unauthorized access, evasion of security controls, fraud, harassment, or any other unlawful or harmful activity.
3. Converting or executing scripts through a custom VM does **not** make the underlying actions legal or authorized. Obfuscation or alternate execution engines must not be used to bypass security products, audit requirements, or consent requirements.
4. **No warranty** is provided. The software is offered **“as is”**, without warranties of any kind, express or implied, including but not limited to merchantability, fitness for a particular purpose, and non-infringement.
5. In no event shall the authors or contributors be liable for any claim, damages, or other liability arising from the use of or inability to use this software, including misuse by third parties.
6. If you are evaluating this tool in a corporate or production environment, obtain appropriate approval from your security and legal teams before deployment.

If you cannot accept these terms, do not use this software.

---

## Overview

PS2VM compiles a subset of Windows PowerShell into a compact, XOR-obfuscated bytecode program (`.pbc`) and runs it on a small stack-based virtual machine. Unsupported or complex constructs are delegated to the real PowerShell engine on the **same runspace**, so behavior stays aligned with native PowerShell where the VM does not implement a feature itself.

| Component | Role |
|-----------|------|
| **Compiler** | Walks the PowerShell AST and emits VM opcodes |
| **Bytecode I/O** | Serializes/deserializes `.pbc` with a simple stream XOR |

## Requirements

- Windows (recommended for full cmdlet coverage)
- Visual Studio / MSBuild, or an equivalent .NET Framework build toolchain

## Build

1. Restore the PowerShell 5 reference assemblies (NuGet).
2. Open `PS2VM.sln` or build `PS2VM.csproj` (e.g. **x64 | Release**).
3. Output: `bin\x64\Release\PS2VM.exe` (path may vary by configuration).

```text
MSBuild PS2VM.csproj /p:Configuration=Release /p:Platform=x64
```

## Usage

```text
PS2VM.exe c <input.ps1> <output.pbc>   # compile PowerShell → bytecode
PS2VM.exe r <input.pbc>                # run bytecode in the VM
PS2VM.exe d <input.pbc>                # disassemble bytecode
```

### Examples

```powershell
.\PS2VM.exe c .\script.ps1 .\script.pbc // Compile powershell script to bytecodes
.\PS2VM.exe r .\script.pbc // Run bytecodes inside VM
```
## What is compiled natively (typical)

- Functions (simple parameter lists; advanced attributes may register via the host engine)
- Variables and scoped names (`global:`, `script:`, `local:`, `private:`)
- Assignments (including compound forms where supported)
- Arrays, hashtables, indexing, property access, method invocation
- Arithmetic / comparison / logical operators, range (`..`)
- `if`, `switch`, `while`, `do`, `for`, `foreach`
- Simple pipelines: `ForEach-Object`, `Where-Object`, `Select-Object -First`
- Common cmdlet calls via `InvokeCmdlet`

## What is delegated to the host engine

Complex or high-surface features are executed with the real PowerShell engine (`Eval` / related paths) on the shared runspace, including for example:

- `try` / `catch` / `finally`, `trap`, `data`
- `using`, `class` / `enum`, DSC-style configuration
- Redirections, dot-sourcing, many module / job / remoting cmdlets
- Advanced parameter attributes and validation (function definition may be registered by the host)
- Constructs the compiler does not map to dedicated opcodes

This maximizes script compatibility without reimplementing the entire PowerShell runtime.

## Limitations

- Not a drop-in replacement for `powershell.exe` or full language parity.
- Depends on Windows PowerShell 5.x assemblies; not the PowerShell 7 runtime.
- Bytecode obfuscation is lightweight (not a substitute for proper application security).
- Pipeline coverage is partial; unsupported shapes fall back to the host.
- No full interactive debugger, AppLocker integration, or system-wide script logging.

## Project layout

```text
PS2VM/
  Cli.cs          # entry point: compile / run / dump
  Compiler.cs     # AST → bytecode
  Vm.cs           # stack machine + host interop
  Bytecode.cs     # program model + encrypted I/O
  Os.cs           # opcode enumeration
  PS2VM.csproj
  packages.config
```

## Responsible use

Use this project only on systems and scripts you are authorized to analyze or run. Report vulnerabilities in this repository responsibly. Do not use PS2VM to develop or distribute malware or to circumvent security controls.

## License

This project is provided for educational and research purposes.



