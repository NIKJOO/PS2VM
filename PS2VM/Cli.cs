using System;
using System.IO;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;

namespace Ps2Vm
{
    internal static class Cli
    {
        private static int Main(string[] args)
        {
            if (args.Length < 1) return Usage();

            switch (args[0])
            {
                case "c": case "compile": return CmdCompile(args);
                case "r": case "run": return CmdRun(args);
                case "d": case "dump": return CmdDump(args);
                default: return Usage();
            }
        }

        private static int Usage()
        {
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  Ps2Vm.exe c <input.ps1> <output.pbc>   # compile");
            Console.Error.WriteLine("  Ps2Vm.exe r <input.pbc>                # run in obfuscated VM");
            Console.Error.WriteLine("  Ps2Vm.exe d <input.pbc>                # disassemble");
            return 1;
        }

        private static int CmdCompile(string[] a)
        {
            if (a.Length < 3) return Usage();
            string src = File.ReadAllText(a[1]);

            Token[] tk; ParseError[] errs;
            var ast = Parser.ParseInput(src, out tk, out errs);
            if (errs != null && errs.Length > 0)
                Console.Error.WriteLine($"[!] {errs.Length} parse error(s); first: {errs[0].Message}");

            var prog = new Compiler().Compile(ast);
            BytecodeIo.Save(a[2], prog, BytecodeIo.FreshKey());

            Console.WriteLine($"[+] {a[2]}  insns={prog.Code.Count}  consts={prog.Constants.Count}  subs={prog.SubPrograms.Count}  funcs={prog.Functions.Count}");
            return 0;
        }

        private static int CmdRun(string[] a)
        {
            if (a.Length < 2) return Usage();

            var prog = BytecodeIo.Load(a[1], BytecodeIo.FreshKey());

            // No ExecutionPolicy property on InitialSessionState — just create the runspace.
            var iss = InitialSessionState.CreateDefault();

            using (var rs = RunspaceFactory.CreateRunspace(iss))
            {
                rs.Open();
                try
                {
                    var vm = new Vm(rs) { Output = v => Console.WriteLine(v) };
                    vm.Run(prog);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[!] " + ex.Message);
                    return 2;
                }
            }
            return 0;
        }

        private static int CmdDump(string[] a)
        {
            if (a.Length < 2) return Usage();
            var prog = BytecodeIo.Load(a[1], BytecodeIo.FreshKey());

            Console.WriteLine($"== Root  ({prog.Code.Count} insns, {prog.Constants.Count} consts) ==");
            DumpBody(prog);

            for (int i = 0; i < prog.SubPrograms.Count; i++)
            {
                Console.WriteLine();
                Console.WriteLine($"== Sub[{i}]  ({prog.SubPrograms[i].Code.Count} insns) ==");
                DumpBody(prog.SubPrograms[i]);
            }

            if (prog.Functions.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("== Functions ==");
                foreach (var f in prog.Functions)
                    Console.WriteLine($"  {f.Name}  -> sub[{f.ProgramIndex}]  args={string.Join(",", f.ParamNames)}");
            }
            return 0;
        }

        private static void DumpBody(BytecodeProgram p)
        {
            for (int i = 0; i < p.Code.Count; i++)
                Console.WriteLine($"  {i,5}: {p.Code[i]}");
        }
    }
}