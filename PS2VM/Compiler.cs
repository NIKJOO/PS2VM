using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation.Language;

namespace Ps2Vm
{
    internal sealed class Compiler
    {
        private readonly BytecodeProgram _root = new BytecodeProgram();
        private BytecodeProgram _cur;
        private int _switchSeq;

        private sealed class LoopCtx
        {
            public int ContinueTarget;
            public readonly List<int> BreakJumps = new List<int>();
            public readonly List<int> ContinueJumps = new List<int>();
            public bool IsSwitch;
        }
        private readonly Stack<LoopCtx> _loops = new Stack<LoopCtx>();

        public Compiler() { _cur = _root; }

        public BytecodeProgram Compile(ScriptBlockAst ast)
        {
            CompileScriptBlockBody(ast);
            Emit(Op.Halt);
            return _root;
        }

        // ---------- low-level helpers ----------
        private int Emit(Op op, int a = 0, int b = 0)
        {
            _cur.Code.Add(new Instr(op, a, b));
            return _cur.Code.Count - 1;
        }

        private int C(object v)
        {
            for (int i = 0; i < _cur.Constants.Count; i++)
                if (Equals(_cur.Constants[i], v)) return i;
            _cur.Constants.Add(v);
            return _cur.Constants.Count - 1;
        }

        private int Here => _cur.Code.Count;
        private void Patch(int at, int target) => _cur.Code[at].A = target;
        private bool AtTopLevel => ReferenceEquals(_cur, _root);

        private BytecodeProgram NewSub()
        {
            var sp = new BytecodeProgram();
            _root.SubPrograms.Add(sp);
            return sp;
        }

        private int SubIndex(BytecodeProgram p) => _root.SubPrograms.IndexOf(p);

        private void WithProgram(BytecodeProgram p, Action body)
        {
            var prev = _cur;
            _cur = p;
            try { body(); } finally { _cur = prev; }
        }

        // Command arguments are CommandElementAst; the ones we care about
        // (constants, variables, parens, method calls, etc.) all derive from
        // ExpressionAst. CommandParameterAst is handled separately by callers.
        private static ExpressionAst CmdArgToExpr(CommandElementAst el)
        {
            return el as ExpressionAst;
        }

        private static bool HasParallelParameter(CommandAst ca)
        {
            if (ca == null || ca.CommandElements == null) return false;
            foreach (var el in ca.CommandElements)
            {
                if (el is CommandParameterAst p && p.ParameterName != null &&
                    p.ParameterName.Equals("Parallel", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // ---------- script blocks ----------
        private void CompileScriptBlockBody(ScriptBlockAst sb)
        {
            if (sb == null) return;

            if (sb.ParamBlock != null && sb.ParamBlock.Parameters != null)
            {
                var names = sb.ParamBlock.Parameters
                    .Where(p => p != null && p.Name != null && p.Name.VariablePath != null)
                    .Select(p => p.Name.VariablePath.UserPath)
                    .ToList();
                for (int i = names.Count - 1; i >= 0; i--)
                    Emit(Op.StoreVar, C(names[i]));
            }
            if (sb.BeginBlock != null && sb.BeginBlock.Statements != null) CompileStatements(sb.BeginBlock.Statements);
            if (sb.ProcessBlock != null && sb.ProcessBlock.Statements != null) CompileStatements(sb.ProcessBlock.Statements);
            if (sb.EndBlock != null && sb.EndBlock.Statements != null) CompileStatements(sb.EndBlock.Statements);
        }

        private int CompileScriptBlockLit(ScriptBlockAst sb)
        {
            if (sb == null)
            {
                var empty = NewSub();
                WithProgram(empty, () => Emit(Op.PushNull));
                return SubIndex(empty);
            }

            var sub = NewSub();
            int codeStart = sub.Code.Count;
            int constStart = sub.Constants.Count;

            try
            {
                WithProgram(sub, () => CompileScriptBlockBody(sb));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[compile] scriptblock fallback to Eval: " + ex.Message);

                while (sub.Code.Count > codeStart) sub.Code.RemoveAt(sub.Code.Count - 1);
                while (sub.Constants.Count > constStart) sub.Constants.RemoveAt(sub.Constants.Count - 1);

                WithProgram(sub, () =>
                {
                    string bodyText = sb.EndBlock != null && sb.EndBlock.Extent != null
                        ? sb.EndBlock.Extent.Text
                        : "$null";
                    Emit(Op.Eval, C(bodyText));
                });
            }

            return SubIndex(sub);
        }

        private void CompileStatements(IEnumerable<StatementAst> s)
        {
            if (s == null) return;
            foreach (var x in s) CompileStatement(x);
        }

        private void CompileStatement(StatementAst s)
        {
            if (s == null) { Emit(Op.PushNull); return; }

            switch (s)
            {
                case PipelineAst pl:
                    CompilePipeline(pl);
                    Emit(AtTopLevel ? Op.Print : Op.Pop);
                    break;

                case AssignmentStatementAst a: CompileAssignment(a); break;
                case IfStatementAst i: CompileIf(i); break;
                case WhileStatementAst w: CompileWhile(w); break;
                case DoWhileStatementAst dw: CompileDoWhile(dw); break;
                case DoUntilStatementAst du: CompileDoUntil(du); break;
                case ForStatementAst f: CompileFor(f); break;
                case ForEachStatementAst fe: CompileForEach(fe); break;
                case SwitchStatementAst sw: CompileSwitch(sw); break;
                case FunctionDefinitionAst fn: CompileFunction(fn); break;
                case ReturnStatementAst r: CompileReturn(r); break;
                case ThrowStatementAst t: CompileThrow(t); break;
                case BreakStatementAst _: CompileBreak(); break;
                case ContinueStatementAst _: CompileContinue(); break;

                case ExitStatementAst es:
                    if (es.Pipeline != null) CompilePipeline(es.Pipeline);
                    else Emit(Op.PushNull);
                    Emit(Op.Halt);
                    break;

                // ---- Host-engine fallbacks (full PowerShell semantics) ----
                // try/catch/finally, trap, data, using, class/enum, configuration (DSC),
                // dynamic keywords, and any unrecognized statement are executed by
                // the real PowerShell engine via Eval so behavior matches native PS.
                case TryStatementAst _:
                case TrapStatementAst _:
                case DataStatementAst _:
                case UsingStatementAst _:
                case TypeDefinitionAst _:           // class / enum
                case ConfigurationDefinitionAst _:  // DSC configuration
                case DynamicKeywordStatementAst _:
                    Emit(Op.Eval, C(s.Extent != null ? s.Extent.Text : "$null"));
                    break;

                default:
                    // foreach -Parallel and anything else → host engine
                    if (s is ForEachStatementAst fePar &&
                        (fePar.Flags & ForEachFlags.Parallel) != 0)
                    {
                        Emit(Op.Eval, C(s.Extent != null ? s.Extent.Text : "$null"));
                        break;
                    }
                    Emit(Op.Eval, C(s.Extent != null ? s.Extent.Text : "$null"));
                    break;
            }
        }

        // ---------- loops ----------
        private void CompileWhile(WhileStatementAst ws)
        {
            if (ws == null || ws.Condition == null || ws.Body == null) { Emit(Op.PushNull); return; }

            var ctx = new LoopCtx();
            _loops.Push(ctx);

            ctx.ContinueTarget = Here;
            CompilePipeline(ws.Condition);
            int jz = Emit(Op.Jz, 0);
            CompileStatements(ws.Body.Statements);
            Emit(Op.Jmp, ctx.ContinueTarget);
            Patch(jz, Here);
            ResolveLoop(ctx, Here);
        }

        private void CompileDoWhile(DoWhileStatementAst dw)
        {
            if (dw == null || dw.Condition == null || dw.Body == null) { Emit(Op.PushNull); return; }

            var ctx = new LoopCtx();
            _loops.Push(ctx);

            int top = Here;
            ctx.ContinueTarget = Here;
            CompileStatements(dw.Body.Statements);
            CompilePipeline(dw.Condition);
            Emit(Op.Jnz, top);
            ResolveLoop(ctx, Here);
        }

        private void CompileDoUntil(DoUntilStatementAst du)
        {
            if (du == null || du.Condition == null || du.Body == null) { Emit(Op.PushNull); return; }

            var ctx = new LoopCtx();
            _loops.Push(ctx);

            int top = Here;
            ctx.ContinueTarget = Here;
            CompileStatements(du.Body.Statements);
            CompilePipeline(du.Condition);
            Emit(Op.Jz, top);
            ResolveLoop(ctx, Here);
        }

        private void CompileFor(ForStatementAst f)
        {
            if (f == null || f.Body == null) { Emit(Op.PushNull); return; }

            var ctx = new LoopCtx();
            _loops.Push(ctx);

            if (f.Initializer != null) CompileStatement(f.Initializer);

            int top = Here;
            int jz = -1;
            if (f.Condition != null)
            {
                CompilePipeline(f.Condition);
                jz = Emit(Op.Jz, 0);
            }

            CompileStatements(f.Body.Statements);

            ctx.ContinueTarget = Here;
            if (f.Iterator != null) CompileStatement(f.Iterator);

            Emit(Op.Jmp, top);
            if (jz >= 0) Patch(jz, Here);
            ResolveLoop(ctx, Here);
        }

        private void CompileForEach(ForEachStatementAst fe)
        {
            if (fe == null || fe.Body == null || fe.Variable == null || fe.Variable.VariablePath == null)
            {
                Emit(Op.PushNull);
                return;
            }

            var ctx = new LoopCtx();
            _loops.Push(ctx);

            string varName = fe.Variable.VariablePath.UserPath;
            if (fe.Condition != null) CompilePipeline(fe.Condition);
            else Emit(Op.NewArray, 0);

            Emit(Op.ForEachInit, C(varName));

            int top = Here;
            int exhausted = Emit(Op.ForEachNext, 0);

            ctx.ContinueTarget = Here;
            CompileStatements(fe.Body.Statements);
            Emit(Op.Jmp, top);

            Patch(exhausted, Here);
            ResolveLoop(ctx, Here);
        }

        private void ResolveLoop(LoopCtx ctx, int breakTarget)
        {
            _loops.Pop();
            foreach (var j in ctx.BreakJumps) Patch(j, breakTarget);
            foreach (var j in ctx.ContinueJumps) Patch(j, ctx.ContinueTarget);
        }

        private void CompileBreak()
        {
            foreach (var ctx in _loops)
            {
                ctx.BreakJumps.Add(Emit(Op.Break, 0));
                return;
            }
            Emit(Op.Eval, C("break"));
        }

        private void CompileContinue()
        {
            foreach (var ctx in _loops)
            {
                if (ctx.IsSwitch) continue;
                ctx.ContinueJumps.Add(Emit(Op.Continue, 0));
                return;
            }
            Emit(Op.Eval, C("continue"));
        }

        // ---------- switch ----------
        private void CompileSwitch(SwitchStatementAst sw)
        {
            if (sw == null) { Emit(Op.PushNull); return; }

            bool isWild = (sw.Flags & SwitchFlags.Wildcard) != 0;
            bool isRegex = (sw.Flags & SwitchFlags.Regex) != 0;

            string flagVar = "__sw_matched_" + (_switchSeq++).ToString();

            var ctx = new LoopCtx { IsSwitch = true };
            _loops.Push(ctx);

            // $__sw_matched_N = $false
            Emit(Op.PushFalse);
            Emit(Op.StoreVar, C(flagVar));

            // push the switch value
            if (sw.Condition != null) CompilePipeline(sw.Condition);
            else Emit(Op.PushNull);

            if (sw.Clauses != null)
            {
                foreach (var clause in sw.Clauses)
                {
                    if (clause == null) continue;

                    var labels = ExtractSwitchLabels(clause.Item1);
                    var matchJumps = new List<int>();

                    foreach (var label in labels)
                    {
                        if (label == null) continue;

                        if (label is ScriptBlockExpressionAst sbe && sbe.ScriptBlock != null)
                        {
                            Emit(Op.Dup);
                            Emit(Op.StoreVar, C("_"));
                            Emit(Op.Dup);
                            Emit(Op.StoreVar, C("PSItem"));

                            Emit(Op.PushSubProgram, CompileScriptBlockLit(sbe.ScriptBlock));
                            Emit(Op.InvokeSubProgram, 0, 0);
                            matchJumps.Add(Emit(Op.Jnz, 0));
                        }
                        else
                        {
                            Emit(Op.Dup);
                            CompileExpression(label);
                            if (isWild) Emit(Op.SwitchCaseLike);
                            else if (isRegex) Emit(Op.BinOpStr, C("Imatch"));
                            else Emit(Op.SwitchCaseEq);
                            matchJumps.Add(Emit(Op.Jnz, 0));
                        }
                    }

                    int skipBody = Emit(Op.Jmp, 0);

                    foreach (var j in matchJumps) Patch(j, Here);

                    Emit(Op.PushTrue);
                    Emit(Op.StoreVar, C(flagVar));

                    var body = clause.Item2 as StatementBlockAst;
                    if (body != null && body.Statements != null) CompileStatements(body.Statements);

                    Patch(skipBody, Here);
                }
            }

            // default: only when no clause matched
            Emit(Op.LoadVar, C(flagVar));
            int skipDefault = Emit(Op.Jnz, 0);
            if (sw.Default != null && sw.Default.Statements != null)
                CompileStatements(sw.Default.Statements);
            Patch(skipDefault, Here);

            Emit(Op.Pop);
            ResolveLoop(ctx, Here);
        }

        private static List<ExpressionAst> ExtractSwitchLabels(ExpressionAst cond)
        {
            if (cond == null) return new List<ExpressionAst>();
            if (cond is ArrayLiteralAst al) return al.Elements.ToList();
            return new List<ExpressionAst> { cond };
        }

        // ---------- functions ----------
        private void CompileFunction(FunctionDefinitionAst fn)
        {
            if (fn == null || fn.Body == null) { Emit(Op.PushNull); return; }

            var sub = NewSub();
            int idx = SubIndex(sub);

            var def = new FunctionDef { Name = fn.Name ?? "", ProgramIndex = idx };

            WithProgram(sub, () =>
            {
                // Parameters may be on fn.Parameters (function f($a) {}) OR
                // on fn.Body.ParamBlock (function f { param($a) }).
                // Bind in reverse order because CallFunc pushes args left-to-right.
                // CompileScriptBlockBody also emits Stores for ParamBlock; skip
                // duplicate emission when we already bound from fn.Parameters.
                List<string> names = null;
                if (fn.Parameters != null && fn.Parameters.Count > 0)
                {
                    names = fn.Parameters
                        .Where(p => p != null && p.Name != null && p.Name.VariablePath != null)
                        .Select(p => p.Name.VariablePath.UserPath)
                        .ToList();
                }
                else if (fn.Body.ParamBlock != null && fn.Body.ParamBlock.Parameters != null)
                {
                    names = fn.Body.ParamBlock.Parameters
                        .Where(p => p != null && p.Name != null && p.Name.VariablePath != null)
                        .Select(p => p.Name.VariablePath.UserPath)
                        .ToList();
                }

                if (names != null && names.Count > 0)
                {
                    for (int i = names.Count - 1; i >= 0; i--)
                        Emit(Op.StoreVar, C(names[i]));
                    def.ParamNames.AddRange(names);
                }

                // When parameters came from Body.ParamBlock we already emitted the
                // StoreVar ops above; skip CompileScriptBlockBody's ParamBlock
                // handling to avoid double-binding.
                if (names != null && names.Count > 0 &&
                    (fn.Parameters == null || fn.Parameters.Count == 0))
                {
                    if (fn.Body.BeginBlock != null && fn.Body.BeginBlock.Statements != null)
                        CompileStatements(fn.Body.BeginBlock.Statements);
                    if (fn.Body.ProcessBlock != null && fn.Body.ProcessBlock.Statements != null)
                        CompileStatements(fn.Body.ProcessBlock.Statements);
                    if (fn.Body.EndBlock != null && fn.Body.EndBlock.Statements != null)
                        CompileStatements(fn.Body.EndBlock.Statements);
                }
                else
                {
                    CompileScriptBlockBody(fn.Body);
                }
            });

            _root.Functions.Add(def);
            Emit(Op.MakeFunc, idx, C(fn.Name ?? ""));
        }

        // ---------- if / return / throw ----------
        private void CompileIf(IfStatementAst ifs)
        {
            if (ifs == null) { Emit(Op.PushNull); return; }

            var ends = new List<int>();

            if (ifs.Clauses != null)
            {
                foreach (var cl in ifs.Clauses)
                {
                    if (cl == null) continue;
                    if (cl.Item1 != null) CompilePipeline(cl.Item1);
                    else Emit(Op.PushFalse);

                    int jz = Emit(Op.Jz, 0);
                    if (cl.Item2 != null && cl.Item2.Statements != null)
                        CompileStatements(cl.Item2.Statements);
                    ends.Add(Emit(Op.Jmp, 0));
                    Patch(jz, Here);
                }
            }

            if (ifs.ElseClause != null && ifs.ElseClause.Statements != null)
                CompileStatements(ifs.ElseClause.Statements);

            foreach (var j in ends) Patch(j, Here);
        }

        private void CompileReturn(ReturnStatementAst rs)
        {
            if (rs == null) { Emit(Op.PushNull); Emit(Op.Return); return; }
            if (rs.Pipeline != null) CompilePipeline(rs.Pipeline);
            else Emit(Op.PushNull);
            Emit(Op.Return);
        }

        private void CompileThrow(ThrowStatementAst ts)
        {
            if (ts == null) { Emit(Op.PushNull); Emit(Op.Throw); return; }
            if (ts.Pipeline != null) CompilePipeline(ts.Pipeline);
            else Emit(Op.PushNull);
            Emit(Op.Throw);
        }

        // ---------- assignment ----------
        private void CompileAssignment(AssignmentStatementAst a)
        {
            if (a == null || a.Left == null) { Emit(Op.PushNull); return; }

            if (a.Left is VariableExpressionAst v && v.VariablePath != null)
            {
                string n = v.VariablePath.UserPath;

                if (a.Operator == TokenKind.Equals)
                {
                    CompileStatementAsValue(a.Right);
                    Emit(Op.StoreVar, C(n));
                }
                else
                {
                    // Compound assignment: $x OP= $y  ==>  $x = $x OP $y
                    Emit(Op.LoadVar, C(n));
                    CompileStatementAsValue(a.Right);
                    EmitBinOp(a.Operator);
                    Emit(Op.StoreVar, C(n));
                }
            }
            else if (a.Left is IndexExpressionAst idx)
            {
                if (idx.Target != null) CompileExpression(idx.Target); else Emit(Op.PushNull);
                if (idx.Index != null) CompileExpression(idx.Index); else Emit(Op.PushNull);
                CompileStatementAsValue(a.Right);
                Emit(Op.IndexSet);
            }
            else
            {
                Emit(Op.Eval, C(a.Extent != null ? a.Extent.Text : "$null"));
            }
        }

        // ---------- pipeline / statement value ----------
        private void CompileStatementAsValue(StatementAst s)
        {
            if (s == null) { Emit(Op.PushNull); return; }

            if (s is PipelineAst pl && pl.PipelineElements != null && pl.PipelineElements.Count == 1
                && pl.PipelineElements[0] is CommandExpressionAst ce
                && ce.Expression != null)
            {
                CompileExpression(ce.Expression);
                return;
            }
            if (s is PipelineBaseAst pb)
            {
                CompilePipeline(pb);
                return;
            }
            Emit(Op.Eval, C(s.Extent != null ? s.Extent.Text : "$null"));
        }

        private void CompilePipeline(PipelineBaseAst pl)
        {
            if (pl == null) { Emit(Op.PushNull); return; }

            var past = pl as PipelineAst;
            if (past == null || past.PipelineElements == null)
            {
                Emit(Op.Eval, C(pl.Extent != null ? pl.Extent.Text : "$null"));
                return;
            }

            // Redirections (>, >>, 2>&1, etc.) and file redirections are not
            // modeled in the VM — hand the entire pipeline to the host engine.
            if (PipelineHasRedirection(past))
            {
                Emit(Op.Eval, C(past.Extent != null ? past.Extent.Text : "$null"));
                return;
            }

            if (past.PipelineElements.Count == 1)
            {
                CompileCommandOrExpr(past.PipelineElements[0]);
                return;
            }
            if (!TryCompileSimplePipeline(past))
                Emit(Op.Eval, C(past.Extent != null ? past.Extent.Text : "$null"));
        }

        private static bool PipelineHasRedirection(PipelineAst pl)
        {
            if (pl == null || pl.PipelineElements == null) return false;
            foreach (var el in pl.PipelineElements)
            {
                if (el is CommandBaseAst cba && cba.Redirections != null && cba.Redirections.Count > 0)
                    return true;
            }
            return false;
        }

        // Two-pass: verify EVERY element first, then emit. Nothing leaks
        // onto the stack if any element isn't supported.
        private bool TryCompileSimplePipeline(PipelineAst pl)
        {
            if (pl == null || pl.PipelineElements == null || pl.PipelineElements.Count == 0)
                return false;

            // ---- Pass 1: verify ----
            for (int i = 1; i < pl.PipelineElements.Count; i++)
            {
                var ca = pl.PipelineElements[i] as CommandAst;
                if (ca == null) return false;

                string cn = null;
                try { cn = ca.GetCommandName(); } catch { /* ignore */ }
                if (string.IsNullOrEmpty(cn)) return false;
                cn = cn.ToLowerInvariant();

                switch (cn)
                {
                    case "foreach-object":
                        if (ca.CommandElements == null ||
                            !ca.CommandElements.OfType<ScriptBlockExpressionAst>().Any())
                            return false;
                        break;

                    case "where-object":
                        if (ca.CommandElements == null ||
                            !ca.CommandElements.OfType<ScriptBlockExpressionAst>().Any())
                            return false;
                        break;

                    case "select-object":
                        {
                            bool hasFirst = false;
                            var els = ca.CommandElements;
                            if (els != null)
                            {
                                for (int k = 0; k < els.Count - 1; k++)
                                {
                                    if (els[k] is CommandParameterAst p && p.ParameterName != null &&
                                        p.ParameterName.Equals("First", StringComparison.OrdinalIgnoreCase))
                                    { hasFirst = true; break; }
                                }
                            }
                            if (!hasFirst) return false;
                            break;
                        }

                    default:
                        return false;
                }
            }

            // ---- Pass 2: emit ----
            CompileCommandOrExpr(pl.PipelineElements[0]);

            for (int i = 1; i < pl.PipelineElements.Count; i++)
            {
                var ca = (CommandAst)pl.PipelineElements[i];
                string cn = ca.GetCommandName().ToLowerInvariant();

                switch (cn)
                {
                    case "foreach-object":
                        {
                            var sb = ca.CommandElements.OfType<ScriptBlockExpressionAst>().First();
                            Emit(Op.PipeForEach, CompileScriptBlockLit(sb.ScriptBlock));
                            break;
                        }
                    case "where-object":
                        {
                            var sb = ca.CommandElements.OfType<ScriptBlockExpressionAst>().First();
                            Emit(Op.PipeWhere, CompileScriptBlockLit(sb.ScriptBlock));
                            break;
                        }
                    case "select-object":
                        {
                            int firstCount = -1;
                            var els = ca.CommandElements;
                            for (int k = 0; k < els.Count - 1; k++)
                            {
                                var prm = els[k] as CommandParameterAst;
                                var val = els[k + 1] as ConstantExpressionAst;
                                if (prm != null && val != null &&
                                    prm.ParameterName != null &&
                                    prm.ParameterName.Equals("First", StringComparison.OrdinalIgnoreCase))
                                    firstCount = Convert.ToInt32(val.Value);
                            }
                            Emit(Op.PushConst, C(firstCount));
                            Emit(Op.PipeSelect);
                            break;
                        }
                }
            }
            return true;
        }

        private void CompileCommandOrExpr(CommandBaseAst el)
        {
            if (el == null) { Emit(Op.PushNull); return; }

            switch (el)
            {
                case CommandExpressionAst ce:
                    if (ce.Expression == null) Emit(Op.PushNull);
                    else CompileExpression(ce.Expression);
                    break;

                case CommandAst ca:
                    CompileCommand(ca);
                    break;

                default:
                    Emit(Op.Eval, C(el.Extent != null ? el.Extent.Text : "$null"));
                    break;
            }
        }

        private void CompileCommand(CommandAst ca)
        {
            if (ca == null) { Emit(Op.PushNull); return; }

            // Dot-sourcing (. script.ps1) and call operator (&) with complex
            // targets are handed to the host engine for correct scope/module
            // semantics.
            if (ca.InvocationOperator == TokenKind.Dot ||
                ca.InvocationOperator == TokenKind.Ampersand)
            {
                Emit(Op.Eval, C(ca.Extent != null ? ca.Extent.Text : "$null"));
                return;
            }

            string name = null;
            try { name = ca.GetCommandName(); } catch { /* ignore */ }
            if (string.IsNullOrEmpty(name))
            {
                Emit(Op.Eval, C(ca.Extent != null ? ca.Extent.Text : "$null"));
                return;
            }

            // Module / job / workflow / remoting cmdlets need full host engine
            // (session state, runspace, module table). Prefer Eval for these.
            string nl = name.ToLowerInvariant();
            if (nl == "import-module" || nl == "ipmo" ||
                nl == "remove-module" || nl == "rmo" ||
                nl == "start-job" || nl == "sajb" ||
                nl == "start-workflow" ||
                nl == "invoke-command" || nl == "icm" ||
                nl == "new-pssession" || nl == "nsn" ||
                nl == "enter-pssession" || nl == "etsn" ||
                nl == "register-pssessionconfiguration" ||
                nl == "foreach-object" && HasParallelParameter(ca))
            {
                Emit(Op.Eval, C(ca.Extent != null ? ca.Extent.Text : "$null"));
                return;
            }

            // user-defined VM function?
            var fn = _root.Functions.FirstOrDefault(f =>
                f.Name != null && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (fn != null)
            {
                int argc = 0;
                if (ca.CommandElements != null)
                {
                    foreach (var el in ca.CommandElements.Skip(1))
                    {
                        var ex = CmdArgToExpr(el);
                        if (ex != null) { CompileExpression(ex); argc++; }
                        else { Emit(Op.Eval, C(ca.Extent != null ? ca.Extent.Text : "$null")); return; }
                    }
                }
                Emit(Op.CallFunc, C(fn.Name), argc);
                return;
            }

            // positional + named split
            var positional = new List<ExpressionAst>();
            var named = new List<KeyValuePair<string, ExpressionAst>>();
            var list = ca.CommandElements != null
                ? ca.CommandElements.Skip(1).ToList()
                : new List<CommandElementAst>();

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is CommandParameterAst p)
                {
                    var av = i + 1 < list.Count ? CmdArgToExpr(list[i + 1]) : null;
                    if (av != null && p.ParameterName != null)
                    {
                        named.Add(new KeyValuePair<string, ExpressionAst>(p.ParameterName, av));
                        i++;
                    }
                    else
                    {
                        Emit(Op.Eval, C(ca.Extent != null ? ca.Extent.Text : "$null"));
                        return;
                    }
                }
                else
                {
                    var ex = CmdArgToExpr(list[i]);
                    if (ex != null) positional.Add(ex);
                    else
                    {
                        Emit(Op.Eval, C(ca.Extent != null ? ca.Extent.Text : "$null"));
                        return;
                    }
                }
            }

            foreach (var ex in positional) CompileExpression(ex);

            if (named.Count > 0)
            {
                foreach (var kv in named)
                {
                    Emit(Op.PushConst, C(kv.Key));
                    CompileExpression(kv.Value);
                }
                Emit(Op.NewHashtable, named.Count);
            }
            else Emit(Op.PushNull);

            Emit(Op.InvokeCmdlet, C(name), positional.Count);
        }

        // ---------- expressions ----------
        private void CompileExpression(ExpressionAst e)
        {
            if (e == null) { Emit(Op.PushNull); return; }

            switch (e)
            {
                case StringConstantExpressionAst sc:
                    Emit(Op.PushConst, C(sc.Value ?? string.Empty));
                    break;

                case ConstantExpressionAst c:
                    Emit(Op.PushConst, C(c.Value));
                    break;

                case VariableExpressionAst v:
                    {
                        string n = v.VariablePath != null ? v.VariablePath.UserPath : "_";
                        if (n.Equals("true", StringComparison.OrdinalIgnoreCase)) Emit(Op.PushTrue);
                        else if (n.Equals("false", StringComparison.OrdinalIgnoreCase)) Emit(Op.PushFalse);
                        else if (n.Equals("null", StringComparison.OrdinalIgnoreCase)) Emit(Op.PushNull);
                        else Emit(Op.LoadVar, C(n));
                        break;
                    }

                case ParenExpressionAst pe:
                    if (pe.Pipeline != null) CompilePipeline(pe.Pipeline);
                    else Emit(Op.PushNull);
                    break;

                case SubExpressionAst se:
                    {
                        if (se.SubExpression == null || se.SubExpression.Statements == null)
                        {
                            Emit(Op.PushNull);
                            break;
                        }

                        var stmts = se.SubExpression.Statements.ToList();
                        if (stmts.Count == 0) { Emit(Op.PushNull); break; }

                        for (int i = 0; i < stmts.Count - 1; i++)
                            CompileStatement(stmts[i]);

                        CompileStatementAsValue(stmts[stmts.Count - 1]);
                        break;
                    }

                case ArrayLiteralAst al:
                    foreach (var x in al.Elements) CompileExpression(x);
                    Emit(Op.NewArray, al.Elements.Count);
                    break;

                case ArrayExpressionAst ae:
                    // @( ... ) must collect pipeline output into an array.
                    // The previous implementation only ran the statements (discarding
                    // their values), leaving nothing on the stack. Fall back to
                    // the host PowerShell evaluator which implements the correct
                    // collection semantics.
                    Emit(Op.Eval, C(ae.Extent != null ? ae.Extent.Text : "@()"));
                    break;

                case HashtableAst ht:
                    foreach (var kv in ht.KeyValuePairs)
                    {
                        CompileExpression(kv.Item1);
                        CompileStatementAsValue(kv.Item2);
                    }
                    Emit(Op.NewHashtable, ht.KeyValuePairs.Count);
                    break;

                case BinaryExpressionAst be:
                    CompileBinary(be);
                    break;

                case UnaryExpressionAst ue:
                    CompileUnary(ue);
                    break;

                case MemberExpressionAst me:
                    {
                        if (me is InvokeMemberExpressionAst ime)
                        {
                            if (ime.Expression != null) CompileExpression(ime.Expression);
                            else Emit(Op.PushNull);

                            string mname = "";
                            if (ime.Member is StringConstantExpressionAst scm && scm.Value != null)
                                mname = scm.Value;
                            else if (ime.Member is VariableExpressionAst mvm && mvm.VariablePath != null)
                                mname = mvm.VariablePath.UserPath;

                            int argc = 0;
                            if (ime.Arguments != null)
                            {
                                foreach (var a in ime.Arguments) { CompileExpression(a); argc++; }
                            }
                            Emit(Op.InvokeMethod, C(mname), argc);
                        }
                        else
                        {
                            if (me.Expression != null) CompileExpression(me.Expression);
                            else Emit(Op.PushNull);

                            if (me.Member is StringConstantExpressionAst ms && ms.Value != null)
                                Emit(Op.PropGet, C(ms.Value));
                            else if (me.Member is VariableExpressionAst mv && mv.VariablePath != null)
                                Emit(Op.PropGet, C(mv.VariablePath.UserPath));
                            else
                                Emit(Op.Eval, C(me.Extent != null ? me.Extent.Text : "$null"));
                        }
                        break;
                    }

                case IndexExpressionAst ie:
                    if (ie.Target != null) CompileExpression(ie.Target); else Emit(Op.PushNull);
                    if (ie.Index != null) CompileExpression(ie.Index); else Emit(Op.PushNull);
                    Emit(Op.IndexGet);
                    break;

                case ConvertExpressionAst ce2:
                    if (ce2.Child != null) CompileExpression(ce2.Child); else Emit(Op.PushNull);
                    Emit(Op.ConvertTo, C(ce2.Type != null && ce2.Type.TypeName != null
                                         ? ce2.Type.TypeName.FullName
                                         : "System.Object"));
                    break;

                case ExpandableStringExpressionAst es:
                    CompileInterpolation(es);
                    break;

                case ScriptBlockExpressionAst sbe:
                    if (sbe.ScriptBlock != null && sbe.ScriptBlock.Extent != null)
                        Emit(Op.PushScriptBlock, C(sbe.ScriptBlock.Extent.Text));
                    else
                        Emit(Op.PushNull);
                    break;

                default:
                    Emit(Op.Eval, C(e.Extent != null ? e.Extent.Text : "$null"));
                    break;
            }
        }

        private void CompileUnary(UnaryExpressionAst ue)
        {
            if (ue == null) { Emit(Op.PushNull); return; }

            var k = ue.TokenKind;
            string extText = ue.Extent != null ? (ue.Extent.Text ?? "").Trim() : "";

            if (k == TokenKind.Minus)
            { if (ue.Child != null) CompileExpression(ue.Child); Emit(Op.Neg); return; }

            if (k == TokenKind.Not)
            { if (ue.Child != null) CompileExpression(ue.Child); Emit(Op.Not); return; }

            if (k == TokenKind.Plus)
            { if (ue.Child != null) CompileExpression(ue.Child); return; }

            // Increment / decrement — we distinguish pre vs post textually,
            // because the AST uses the same TokenKind for both forms.
            if (extText.StartsWith("++")) { CompileIncrDecr(ue.Child, true, true); return; }
            if (extText.StartsWith("--")) { CompileIncrDecr(ue.Child, true, false); return; }
            if (extText.EndsWith("++")) { CompileIncrDecr(ue.Child, false, true); return; }
            if (extText.EndsWith("--")) { CompileIncrDecr(ue.Child, false, false); return; }

            // Belt-and-braces: catch the raw TokenKind values too.
            if (k == TokenKind.PlusPlus) { CompileIncrDecr(ue.Child, true, true); return; }
            if (k == TokenKind.MinusMinus) { CompileIncrDecr(ue.Child, true, false); return; }

            if (ue.Child != null) CompileExpression(ue.Child);
            Emit(Op.Eval, C(ue.Extent != null ? ue.Extent.Text : "$null"));
        }

        private void CompileBinary(BinaryExpressionAst be)
        {
            if (be == null) { Emit(Op.PushNull); return; }

            if (be.Operator == TokenKind.DotDot)
            {
                if (be.Left != null) CompileExpression(be.Left);
                if (be.Right != null) CompileExpression(be.Right);
                Emit(Op.Range);
                return;
            }
            if (be.Operator == TokenKind.And)
            {
                if (be.Left != null) CompileExpression(be.Left);
                Emit(Op.Dup);
                int jz = Emit(Op.Jz, 0);
                Emit(Op.Pop);
                if (be.Right != null) CompileExpression(be.Right);
                Patch(jz, Here);
                return;
            }
            if (be.Operator == TokenKind.Or)
            {
                if (be.Left != null) CompileExpression(be.Left);
                Emit(Op.Dup);
                int jnz = Emit(Op.Jnz, 0);
                Emit(Op.Pop);
                if (be.Right != null) CompileExpression(be.Right);
                Patch(jnz, Here);
                return;
            }

            if (be.Left != null) CompileExpression(be.Left);
            if (be.Right != null) CompileExpression(be.Right);
            EmitBinOp(be.Operator);
        }

        private void CompileInterpolation(ExpandableStringExpressionAst es)
        {
            if (es == null || es.Extent == null) { Emit(Op.PushNull); return; }

            string src = es.Extent.Text ?? "";
            if (src.Length < 2) { Emit(Op.PushConst, C(src)); return; }

            string inner = src.Substring(1, src.Length - 2);

            var pieces = new List<object>();
            int cursor = 0;
            if (es.NestedExpressions != null)
            {
                foreach (var nested in es.NestedExpressions.OrderBy(n => n.Extent.StartOffset))
                {
                    if (nested == null || nested.Extent == null) continue;
                    int localStart = nested.Extent.StartOffset - es.Extent.StartOffset - 1;
                    int localEnd = nested.Extent.EndOffset - es.Extent.StartOffset - 1;
                    if (localStart < 0) localStart = 0;
                    if (localEnd > inner.Length) localEnd = inner.Length;
                    if (localStart > cursor)
                        pieces.Add(inner.Substring(cursor, localStart - cursor));
                    pieces.Add(nested);
                    cursor = localEnd;
                }
            }
            if (cursor < inner.Length)
                pieces.Add(inner.Substring(cursor));

            foreach (var p in pieces)
            {
                if (p is string s) Emit(Op.PushConst, C(s));
                else CompileExpression((ExpressionAst)p);
            }
            Emit(Op.Concat, pieces.Count);
        }

        private void CompileIncrDecr(ExpressionAst child, bool pre, bool inc)
        {
            if (child is VariableExpressionAst v && v.VariablePath != null)
            {
                int vi = C(v.VariablePath.UserPath);
                if (pre) Emit(inc ? Op.Incr : Op.Decr, vi);
                else Emit(inc ? Op.PostIncr : Op.PostDecr, vi);
            }
            else if (child != null)
            {
                Emit(Op.Eval, C(child.Extent != null ? child.Extent.Text : "$null"));
            }
            else Emit(Op.PushNull);
        }

        // ---------- binary operator dispatch ----------
        private void EmitBinOp(TokenKind k)
        {
            if (k == TokenKind.Plus) { Emit(Op.Add); return; }
            if (k == TokenKind.Minus) { Emit(Op.Sub); return; }
            if (k == TokenKind.Multiply) { Emit(Op.Mul); return; }
            if (k == TokenKind.Divide) { Emit(Op.Div); return; }
            if (k == TokenKind.Rem) { Emit(Op.Mod); return; }

            // Compound-assignment operators reduce to their base operation:
            //   $x += $y  ==>  $x = $x + $y
            if (k == TokenKind.PlusEquals) { Emit(Op.Add); return; }
            if (k == TokenKind.MinusEquals) { Emit(Op.Sub); return; }
            if (k == TokenKind.MultiplyEquals) { Emit(Op.Mul); return; }
            if (k == TokenKind.DivideEquals) { Emit(Op.Div); return; }
            if (k == TokenKind.RemainderEquals) { Emit(Op.Mod); return; }

            if (k == TokenKind.Ieq || k == TokenKind.Ceq) { Emit(Op.Eq); return; }
            if (k == TokenKind.Ine || k == TokenKind.Cne) { Emit(Op.Ne); return; }
            if (k == TokenKind.Ilt || k == TokenKind.Clt) { Emit(Op.Lt); return; }
            if (k == TokenKind.Igt || k == TokenKind.Cgt) { Emit(Op.Gt); return; }
            if (k == TokenKind.Ile || k == TokenKind.Cle) { Emit(Op.Le); return; }
            if (k == TokenKind.Ige || k == TokenKind.Cge) { Emit(Op.Ge); return; }

            if (k == TokenKind.And) { Emit(Op.And); return; }
            if (k == TokenKind.Or) { Emit(Op.Or); return; }

            Emit(Op.BinOpStr, C(k.ToString()));
        }
    }
}