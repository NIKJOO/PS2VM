using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.RegularExpressions;

namespace Ps2Vm
{
    internal sealed class SubProgramRef
    {
        public readonly int Index;
        public SubProgramRef(int idx) { Index = idx; }
    }

    internal sealed class Vm
    {
        private readonly Runspace _rs;

        private object[] _stack;
        private int _sp;
        private int _pc;
        private Instr[] _code;
        private object[] _consts;
        private BytecodeProgram _prog;
        private BytecodeProgram _root;
        private object _last;

        private readonly Stack<KeyValuePair<IEnumerator, string>> _iters
            = new Stack<KeyValuePair<IEnumerator, string>>();

        private Action<Instr>[] _h;

        // Diagnostic — set env var VM_TRACE=1 to print every instruction
        internal static readonly bool Trace =
            Environment.GetEnvironmentVariable("VM_TRACE") == "1";

        public Action<object> Output = v => Console.WriteLine(v);

        public Vm(Runspace rs) { _rs = rs; Init(); }

        private void Init()
        {
            _h = new Action<Instr>[256];
            for (int i = 0; i < 256; i++) _h[i] = H_Nop;

            B(Op.PushConst, H_PushConst);
            B(Op.LoadVar, H_LoadVar);
            B(Op.StoreVar, H_StoreVar);
            B(Op.Pop, H_Pop);
            B(Op.Dup, H_Dup);
            B(Op.Swap, H_Swap);

            B(Op.Add, H_Add); B(Op.Sub, H_Sub); B(Op.Mul, H_Mul);
            B(Op.Div, H_Div); B(Op.Mod, H_Mod); B(Op.Neg, H_Neg);

            B(Op.Eq, Cmp("-eq")); B(Op.Ne, Cmp("-ne"));
            B(Op.Lt, Cmp("-lt")); B(Op.Gt, Cmp("-gt"));
            B(Op.Le, Cmp("-le")); B(Op.Ge, Cmp("-ge"));

            B(Op.And, H_And); B(Op.Or, H_Or); B(Op.Not, H_Not);

            B(Op.Jmp, H_Jmp);
            B(Op.Jz, H_Jz);
            B(Op.Jnz, H_Jnz);

            B(Op.Print, H_Print);
            B(Op.Halt, H_Halt);

            B(Op.NewArray, H_NewArray);
            B(Op.NewHashtable, H_NewHashtable);

            B(Op.IndexGet, H_IndexGet);
            B(Op.IndexSet, H_IndexSet);
            B(Op.PropGet, H_PropGet);

            B(Op.InvokeCmdlet, H_InvokeCmdlet);
            B(Op.InvokeMethod, H_InvokeMethod);
            B(Op.BinOpStr, H_BinOpStr);
            B(Op.Return, H_Return);
            B(Op.Eval, H_Eval);
            B(Op.ConvertTo, H_ConvertTo);
            B(Op.Throw, H_Throw);

            B(Op.PushNull, i => Push(null));
            B(Op.PushTrue, i => Push(true));
            B(Op.PushFalse, i => Push(false));

            B(Op.Incr, H_Incr);
            B(Op.Decr, H_Decr);
            B(Op.PostIncr, H_PostIncr);
            B(Op.PostDecr, H_PostDecr);

            B(Op.Concat, H_Concat);
            B(Op.Range, H_Range);

            B(Op.PushScriptBlock, H_PushScriptBlock);
            B(Op.PushSubProgram, i => Push(new SubProgramRef(i.A)));
            B(Op.InvokeSubProgram, H_InvokeSubProgram);

            B(Op.PipeForEach, H_PipeForEach);
            B(Op.PipeWhere, H_PipeWhere);
            B(Op.PipeSelect, H_PipeSelect);

            B(Op.ForEachInit, H_ForEachInit);
            B(Op.ForEachNext, H_ForEachNext);

            B(Op.Break, H_Jmp);
            B(Op.Continue, H_Jmp);

            B(Op.SwitchCaseEq, i => { var b = Pop(); var a = Peek(); Push(Compare(a, b, "-eq")); });
            B(Op.SwitchCaseLike, i =>
            {
                var pat = Pop(); var val = Peek();
                var src = Regex.Escape(pat?.ToString() ?? "")
                               .Replace("\\*", ".*")
                               .Replace("\\?", ".");
                Push(Regex.IsMatch(val?.ToString() ?? "", "^" + src + "$",
                                   RegexOptions.IgnoreCase));
            });

            B(Op.MakeFunc, i => { });
            B(Op.CallFunc, H_CallFunc);
        }

        private void B(Op op, Action<Instr> h) => _h[(byte)op] = h;

        private void Push(object v) => _stack[_sp++] = v;
        private object Pop() => _stack[--_sp];
        private object Peek() => _stack[_sp - 1];

        public object Run(BytecodeProgram p)
        {
            _root = p;
            _prog = p;
            _code = p.Code.ToArray();
            _consts = p.Constants.ToArray();
            _stack = new object[8192];
            _sp = 0; _pc = 0; _last = null;

            while (_pc < _code.Length)
            {
                int pcBefore = _pc;
                var ins = _code[_pc++];

                if (Trace)
                    Console.Error.WriteLine($"[vm  pc={pcBefore,4}] {ins}");

                try
                {
                    _h[(byte)ins.Op](ins);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[vm] FAIL at pc={pcBefore}: {ins}");
                    Console.Error.WriteLine($"[vm] exc: {ex.GetType().Name}: {ex.Message}");
                    Console.Error.WriteLine($"[vm] stack depth: {_sp}");
                    for (int k = _sp - 1; k >= 0 && k >= _sp - 5; k--)
                        Console.Error.WriteLine($"[vm]   stack[{k}] = {Describe(_stack[k])}");
                    throw;
                }
            }
            return _last;
        }

        private static string Describe(object v)
        {
            if (v == null) return "null";
            return v.GetType().Name + ": " + SafeToString(v);
        }

        private static string SafeToString(object v)
        {
            try { return v.ToString(); } catch { return "<tostring threw>"; }
        }

        // ---------- handlers ----------
        private void H_Nop(Instr i) { }

        private void H_PushConst(Instr i) => Push(_consts[i.A]);

        private void H_LoadVar(Instr i)
        {
            var v = _rs.SessionStateProxy.GetVariable((string)_consts[i.A]);
            Push(v);
        }

        private void H_StoreVar(Instr i)
        {
            object v = _sp > 0 ? Pop() : null;
            _rs.SessionStateProxy.SetVariable((string)_consts[i.A], v);
        }

        private void H_Pop(Instr i) { _last = _sp > 0 ? Pop() : null; }
        private void H_Dup(Instr i) { var v = Pop(); Push(v); Push(v); }
        private void H_Swap(Instr i) { var a = Pop(); var b = Pop(); Push(a); Push(b); }

        private void H_Add(Instr i) { var b = Pop(); var a = Pop(); Push(Add(a, b)); }
        private void H_Sub(Instr i) { var b = Pop(); var a = Pop(); Push(Sub(a, b)); }
        private void H_Mul(Instr i) { var b = Pop(); var a = Pop(); Push(Mul(a, b)); }
        private void H_Div(Instr i) { var b = Pop(); var a = Pop(); Push(Div(a, b)); }
        private void H_Mod(Instr i) { var b = Pop(); var a = Pop(); Push(Mod(a, b)); }
        private void H_Neg(Instr i) { var a = Pop(); Push(Neg(a)); }

        private Action<Instr> Cmp(string op) => i =>
        {
            var b = Pop(); var a = Pop();
            Push(Compare(a, b, op));
        };

        private void H_And(Instr i) { var b = Pop(); var a = Pop(); Push(Truthy(a) && Truthy(b)); }
        private void H_Or(Instr i) { var b = Pop(); var a = Pop(); Push(Truthy(a) || Truthy(b)); }
        private void H_Not(Instr i) { Push(!Truthy(Pop())); }

        private void H_Jmp(Instr i) => _pc = i.A;
        private void H_Jz(Instr i) { if (!Truthy(Pop())) _pc = i.A; }
        private void H_Jnz(Instr i) { if (Truthy(Pop())) _pc = i.A; }

        private void H_Print(Instr i)
        {
            var v = _sp > 0 ? Pop() : null;
            _last = v;
            if (v == null) return;

            if (v is object[] arr)
            {
                foreach (var x in arr) if (x != null) Output(x);
            }
            else Output(v);
        }

        private void H_Halt(Instr i) => _pc = _code.Length;
        private void H_Return(Instr i) { _last = _sp > 0 ? Pop() : null; _pc = _code.Length; }

        private void H_NewArray(Instr i)
        {
            int n = i.A;
            var arr = new object[n];
            for (int k = n - 1; k >= 0; k--) arr[k] = Pop();
            Push(arr);
        }

        private void H_NewHashtable(Instr i)
        {
            int n = i.A;
            var ht = new Hashtable();
            for (int k = 0; k < n; k++)
            {
                var v = Pop(); var key = Pop();
                ht[key] = v;
            }
            Push(ht);
        }

        private void H_IndexGet(Instr i) { var idx = Pop(); var tgt = Pop(); Push(Index(tgt, idx)); }
        private void H_IndexSet(Instr i)
        {
            var v = Pop(); var idx = Pop(); var tgt = Pop();
            SetIndex(tgt, idx, v);
        }
        private void H_PropGet(Instr i)
        {
            var tgt = Pop();
            Push(GetProp(tgt, (string)_consts[i.A]));
        }

        private void H_InvokeCmdlet(Instr i)
        {
            string name = (string)_consts[i.A];
            int argc = i.B;

            var splat = Pop();
            var args = new object[argc];
            for (int k = argc - 1; k >= 0; k--) args[k] = Pop();

            if (name.Equals("Write-Host", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var a in args) if (a != null) Output(a);
                Push(null);
                return;
            }

            using (var ps = PowerShell.Create())
            {
                ps.Runspace = _rs;
                ps.AddCommand(name);
                foreach (var a in args) ps.AddArgument(a);
                if (splat is IDictionary d)
                    foreach (DictionaryEntry e in d)
                        ps.AddParameter(e.Key.ToString(), e.Value);

                var res = ps.Invoke();
                if (ps.HadErrors) throw ps.Streams.Error[0].Exception;

                Push(res.Count == 0 ? null :
                     res.Count == 1 ? (object)res[0] : res.ToArray());
            }
        }

        private void H_InvokeMethod(Instr i)
        {
            string mname = (string)_consts[i.A];
            int argc = i.B;
            var args = new object[argc];
            for (int k = argc - 1; k >= 0; k--) args[k] = Pop();
            var target = Pop();

            if (target == null) { Push(null); return; }
            if (target is PSObject pso) target = pso.BaseObject;

            var result = target.GetType().InvokeMember(
                mname,
                System.Reflection.BindingFlags.InvokeMethod |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance,
                null, target, args);
            Push(result);
        }

        private void H_BinOpStr(Instr i)
        {
            var b = Pop(); var a = Pop();
            string opName = (string)_consts[i.A];

            switch (opName)
            {
                case "Imatch":
                case "Iimatch":
                case "Cmatch":
                case "Cimatch":
                    Push(Regex.IsMatch(a?.ToString() ?? "", b?.ToString() ?? "",
                                       RegexOptions.IgnoreCase));
                    return;
                case "Inotmatch":
                case "Cnotmatch":
                    Push(!Regex.IsMatch(a?.ToString() ?? "", b?.ToString() ?? "",
                                        RegexOptions.IgnoreCase));
                    return;
                case "Ilike":
                case "Like":
                    {
                        var pat = "^" + Regex.Escape(b?.ToString() ?? "")
                                            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
                        Push(Regex.IsMatch(a?.ToString() ?? "", pat, RegexOptions.IgnoreCase));
                        return;
                    }
                case "Inotlike":
                case "Notlike":
                    {
                        var pat = "^" + Regex.Escape(b?.ToString() ?? "")
                                            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
                        Push(!Regex.IsMatch(a?.ToString() ?? "", pat, RegexOptions.IgnoreCase));
                        return;
                    }
            }

            using (var ps = PowerShell.Create())
            {
                ps.Runspace = _rs;
                ps.AddScript("param($x,$y,$op) & ([scriptblock]::Create(\"param(`$x,`$y) `$x $op `$y\")) $x $y")
                  .AddArgument(a).AddArgument(b).AddArgument(StripOpPrefix(opName));
                var res = ps.Invoke();
                Push(res.Count > 0 ? res[0] : null);
            }
        }

        private static string StripOpPrefix(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.StartsWith("I") || s.StartsWith("C")) return "-" + s.Substring(1).ToLowerInvariant();
            return "-" + s.ToLowerInvariant();
        }

        private void H_Eval(Instr i)
        {
            string text = (string)_consts[i.A];
            using (var ps = PowerShell.Create())
            {
                ps.Runspace = _rs;
                ps.AddScript(text);
                var res = ps.Invoke();
                if (ps.HadErrors) throw ps.Streams.Error[0].Exception;
                Push(res.Count == 0 ? null :
                     res.Count == 1 ? (object)res[0] : res.ToArray());
            }
        }

        private void H_ConvertTo(Instr i)
        {
            var v = Pop();
            string tn = (string)_consts[i.A];
            var t = Type.GetType(tn) ??
                    AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType(tn)).FirstOrDefault(x => x != null);
            Push(t != null && v != null ? Convert.ChangeType(v, t) : v);
        }

        private void H_Throw(Instr i)
        {
            var v = Pop();
            throw v as Exception ?? new Exception(v?.ToString() ?? "VM throw");
        }

        private void H_Incr(Instr i)
        {
            string n = (string)_consts[i.A];
            var v = ToD(_rs.SessionStateProxy.GetVariable(n)) + 1;
            _rs.SessionStateProxy.SetVariable(n, v);
            Push(v);
        }
        private void H_Decr(Instr i)
        {
            string n = (string)_consts[i.A];
            var v = ToD(_rs.SessionStateProxy.GetVariable(n)) - 1;
            _rs.SessionStateProxy.SetVariable(n, v);
            Push(v);
        }
        private void H_PostIncr(Instr i)
        {
            string n = (string)_consts[i.A];
            var v = ToD(_rs.SessionStateProxy.GetVariable(n));
            _rs.SessionStateProxy.SetVariable(n, v + 1);
            Push(v);
        }
        private void H_PostDecr(Instr i)
        {
            string n = (string)_consts[i.A];
            var v = ToD(_rs.SessionStateProxy.GetVariable(n));
            _rs.SessionStateProxy.SetVariable(n, v - 1);
            Push(v);
        }

        private void H_Concat(Instr i)
        {
            int n = i.A;
            var parts = new string[n];
            for (int k = n - 1; k >= 0; k--)
            {
                var v = Pop();
                if (v is object[] arr) parts[k] = string.Join(" ", arr.Select(x => x?.ToString() ?? ""));
                else parts[k] = v?.ToString() ?? "";
            }
            Push(string.Concat(parts));
        }

        private void H_Range(Instr i)
        {
            var hiObj = Pop();
            var loObj = Pop();
            int lo = 0, hi = 0;
            try
            {
                var lv = loObj is PSObject p1 ? p1.BaseObject : loObj;
                var hv = hiObj is PSObject p2 ? p2.BaseObject : hiObj;
                lo = Convert.ToInt32(lv);
                hi = Convert.ToInt32(hv);
            }
            catch { /* leave 0,0 */ }

            if (lo <= hi)
            {
                var arr = new object[hi - lo + 1];
                for (int k = 0; k < arr.Length; k++) arr[k] = lo + k;
                Push(arr);
            }
            else
            {
                var arr = new object[lo - hi + 1];
                for (int k = 0; k < arr.Length; k++) arr[k] = lo - k;
                Push(arr);
            }
        }

        private void H_PushScriptBlock(Instr i)
        {
            Push(ScriptBlock.Create((string)_consts[i.A]));
        }

        private void H_InvokeSubProgram(Instr i)
        {
            int argc = i.B;
            var args = new object[argc];
            for (int k = argc - 1; k >= 0; k--) args[k] = Pop();
            var spr = Pop() as SubProgramRef;
            if (spr == null) { Push(null); return; }
            var sub = _prog.SubPrograms[spr.Index];
            Push(InvokeProgram(sub, args));
        }

        private void H_PipeForEach(Instr i)
        {
            var input = Pop();
            var sub = _prog.SubPrograms[i.A];
            var results = new List<object>();
            foreach (var item in Enumerate(input))
            {
                _rs.SessionStateProxy.SetVariable("_", item);
                _rs.SessionStateProxy.SetVariable("PSItem", item);
                var r = InvokeProgram(sub, new object[0]);
                if (r != null) results.Add(r);
            }
            Push(results.Count == 1 ? results[0] : (object)results.ToArray());
        }

        private void H_PipeWhere(Instr i)
        {
            var input = Pop();
            var sub = _prog.SubPrograms[i.A];
            var results = new List<object>();
            foreach (var item in Enumerate(input))
            {
                _rs.SessionStateProxy.SetVariable("_", item);
                _rs.SessionStateProxy.SetVariable("PSItem", item);
                if (Truthy(InvokeProgram(sub, new object[0]))) results.Add(item);
            }
            Push(results.Count == 1 ? results[0] : (object)results.ToArray());
        }

        private void H_PipeSelect(Instr i)
        {
            int count = ToIndex(Pop());
            var input = Pop();
            var results = new List<object>();
            foreach (var item in Enumerate(input))
            {
                results.Add(item);
                if (count >= 0 && results.Count >= count) break;
            }
            Push(results.Count == 1 ? results[0] : (object)results.ToArray());
        }

        private void H_ForEachInit(Instr i)
        {
            string varName = (string)_consts[i.A];
            var coll = Pop();
            _iters.Push(new KeyValuePair<IEnumerator, string>(
                Enumerate(coll).GetEnumerator(), varName));
        }

        private void H_ForEachNext(Instr i)
        {
            if (_iters.Count == 0) { _pc = i.A; return; }
            var top = _iters.Peek();
            if (top.Key.MoveNext())
            {
                _rs.SessionStateProxy.SetVariable(top.Value, top.Key.Current);
            }
            else
            {
                _iters.Pop();
                _pc = i.A;
            }
        }

        private void H_CallFunc(Instr i)
        {
            string name = (string)_consts[i.A];
            int argc = i.B;
            var args = new object[argc];
            for (int k = argc - 1; k >= 0; k--) args[k] = Pop();

            FunctionDef fd = null;
            foreach (var f in _root.Functions)
                if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { fd = f; break; }
            if (fd == null) { Push(null); return; }

            var sub = _root.SubPrograms[fd.ProgramIndex];
            Push(InvokeProgram(sub, args));
        }

        private object InvokeProgram(BytecodeProgram p, object[] args)
        {
            var saveProg = _prog;
            var saveCode = _code;
            var saveConsts = _consts;
            var saveStack = _stack;
            var savePc = _pc;
            var saveSp = _sp;
            var saveLast = _last;
            var saveIters = _iters.ToArray();

            _prog = p;
            _code = p.Code.ToArray();
            _consts = p.Constants.ToArray();
            _stack = new object[4096];
            _sp = 0; _pc = 0; _last = null;
            _iters.Clear();

            foreach (var a in args) Push(a);

            while (_pc < _code.Length)
            {
                int pcBefore = _pc;
                var ins = _code[_pc++];
                if (Trace)
                    Console.Error.WriteLine($"[vm sub pc={pcBefore,4}] {ins}");
                _h[(byte)ins.Op](ins);
            }

            object ret = _last;

            _prog = saveProg;
            _code = saveCode;
            _consts = saveConsts;
            _stack = saveStack;
            _pc = savePc;
            _sp = saveSp;
            _last = saveLast;

            _iters.Clear();
            for (int i = saveIters.Length - 1; i >= 0; i--) _iters.Push(saveIters[i]);

            return ret;
        }

        // ---------- runtime helpers ----------
        private static bool Truthy(object o)
        {
            if (o == null) return false;
            if (o is bool b) return b;
            if (o is string s) return s.Length > 0;
            if (o is ICollection c) return c.Count > 0;
            try { return LanguagePrimitives.ConvertTo<bool>(o); }
            catch { return true; }
        }

        private static double ToD(object o)
        {
            if (o == null) return 0;
            var v = o is PSObject pso ? pso.BaseObject : o;
            if (v is double d) return d;
            if (v is float f) return f;
            if (v is int i) return i;
            if (v is long l) return l;
            if (v is short sh) return sh;
            if (v is byte by) return by;
            if (v is decimal de) return (double)de;
            if (v is string str)
            {
                if (double.TryParse(str, out var pd)) return pd;
                return 0;
            }
            try { return LanguagePrimitives.ConvertTo<double>(v); }
            catch { return 0; }
        }

        private static object Add(object a, object b)
        {
            // Unwrap PSObject wrappers first.
            var av = a is PSObject p1 ? p1.BaseObject : a;
            var bv = b is PSObject p2 ? p2.BaseObject : b;

            // String concatenation on either side wins.
            if (av is string || bv is string)
                return (av?.ToString() ?? "") + (bv?.ToString() ?? "");

            // Both numeric → add as doubles.
            if (IsNumeric(av) && IsNumeric(bv))
                return ToD(av) + ToD(bv);

            // Arrays → concatenate element-wise (PowerShell behaviour).
            var aArr = av as object[];
            var bArr = bv as object[];
            if (aArr != null || bArr != null)
            {
                var list = new List<object>();
                if (aArr != null) list.AddRange(aArr);
                else if (av != null) list.Add(av);
                if (bArr != null) list.AddRange(bArr);
                else if (bv != null) list.Add(bv);
                return list.ToArray();
            }

            // Fallback: coerce both to string and concatenate.
            return (av?.ToString() ?? "") + (bv?.ToString() ?? "");
        }

        private static bool IsNumeric(object v)
        {
            if (v == null) return false;
            return v is double || v is float || v is int || v is long
                || v is short || v is byte || v is decimal
                || (v is string s && double.TryParse(s, out _));
        }

        private static object Sub(object a, object b) => ToD(a) - ToD(b);
        private static object Mul(object a, object b) => ToD(a) * ToD(b);
        private static object Div(object a, object b) => ToD(a) / ToD(b);
        private static object Mod(object a, object b) => ToD(a) % ToD(b);
        private static object Neg(object a) => -ToD(a);

        private static object Compare(object a, object b, string op)
        {
            int c;
            var av = a is PSObject p1 ? p1.BaseObject : a;
            var bv = b is PSObject p2 ? p2.BaseObject : b;

            if (av is IComparable ca && bv != null && av.GetType() == bv.GetType())
                c = ca.CompareTo(bv);
            else if (av is string || bv is string)
                c = string.Compare(av?.ToString() ?? "", bv?.ToString() ?? "",
                                   StringComparison.OrdinalIgnoreCase);
            else if (IsNumeric(av) && IsNumeric(bv))
            {
                var x = ToD(av); var y = ToD(bv);
                c = x < y ? -1 : x > y ? 1 : 0;
            }
            else
                c = Comparer.Default.Compare(av, bv);

            switch (op)
            {
                case "-eq": return c == 0;
                case "-ne": return c != 0;
                case "-lt": return c < 0;
                case "-gt": return c > 0;
                case "-le": return c <= 0;
                case "-ge": return c >= 0;
            }
            return false;
        }

        private static object Unwrap(object o)
        {
            return o is PSObject pso ? pso.BaseObject : o;
        }

        private static int ToIndex(object idx)
        {
            var v = Unwrap(idx);
            if (v == null) return 0;
            try { return Convert.ToInt32(v); }
            catch { return 0; }
        }

        private static object Index(object tgt, object idx)
        {
            if (tgt == null) return null;
            tgt = Unwrap(tgt);
            var key = Unwrap(idx) ?? idx;
            if (tgt is IDictionary d) return d[key];
            if (tgt is Array arr) return arr.GetValue(ToIndex(idx));
            if (tgt is IList list) return list[ToIndex(idx)];
            if (tgt is string s)
            {
                int i = ToIndex(idx);
                return (i >= 0 && i < s.Length) ? (object)s[i] : null;
            }
            // Arrays coming from Eval / pipeline are often object[] of PSObject
            if (tgt is object[] oa)
            {
                int i = ToIndex(idx);
                return (i >= 0 && i < oa.Length) ? oa[i] : null;
            }
            return tgt.GetType().GetProperty("Item")?.GetValue(tgt, new[] { key });
        }

        private static void SetIndex(object tgt, object idx, object v)
        {
            if (tgt == null) return;
            tgt = Unwrap(tgt);
            var key = Unwrap(idx) ?? idx;
            if (tgt is IDictionary d) { d[key] = v; return; }
            if (tgt is Array arr) { arr.SetValue(v, ToIndex(idx)); return; }
            if (tgt is IList list) { list[ToIndex(idx)] = v; return; }
            if (tgt is object[] oa)
            {
                int i = ToIndex(idx);
                if (i >= 0 && i < oa.Length) oa[i] = v;
                return;
            }
            tgt.GetType().GetProperty("Item")?.SetValue(tgt, v, new[] { key });
        }

        private static object GetProp(object tgt, string name)
        {
            if (tgt == null) return null;
            if (tgt is PSObject pso) tgt = pso.BaseObject;
            var p = tgt.GetType().GetProperty(name);
            if (p != null) return p.GetValue(tgt);
            var f = tgt.GetType().GetField(name);
            if (f != null) return f.GetValue(tgt);
            return null;
        }

        private static IEnumerable<object> Enumerate(object o)
        {
            if (o == null) yield break;
            if (o is PSObject pso) o = pso.BaseObject;
            if (o == null) yield break;
            if (o is string s) { yield return s; yield break; }
            if (o is IEnumerable en) foreach (var x in en) yield return x;
            else yield return o;
        }
    }
}