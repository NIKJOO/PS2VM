namespace Ps2Vm
{
    internal enum Op : byte
    {
        Nop = 0, PushConst, LoadVar, StoreVar, Pop, Dup, Swap,
        Add, Sub, Mul, Div, Mod, Neg,
        Eq, Ne, Lt, Gt, Le, Ge, And, Or, Not,
        Jmp, Jz, Jnz, Print, Halt,
        NewArray, NewHashtable,
        IndexGet, IndexSet, PropGet,
        InvokeCmdlet, InvokeMethod, BinOpStr,
        Return, Eval, ConvertTo, Throw,

        PushNull, PushTrue, PushFalse,
        Incr, Decr, PostIncr, PostDecr,
        Concat, Range,
        PushScriptBlock, PushSubProgram, InvokeSubProgram,
        PipeForEach, PipeWhere, PipeSelect,
        ForEachInit, ForEachNext,
        Break, Continue,
        SwitchCaseEq, SwitchCaseLike,
        MakeFunc, CallFunc
    }
}