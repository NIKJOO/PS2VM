using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Ps2Vm
{
    internal sealed class Instr
    {
        public Op Op; public int A; public int B;
        public Instr(Op o, int a = 0, int b = 0) { Op = o; A = a; B = b; }
        public override string ToString() => $"{Op,-18} {A,-6} {B}";
    }

    internal sealed class FunctionDef
    {
        public string Name;
        public int ProgramIndex;
        public List<string> ParamNames = new List<string>();
    }

    internal sealed class BytecodeProgram
    {
        public readonly List<Instr> Code = new List<Instr>();
        public readonly List<object> Constants = new List<object>();
        public readonly List<BytecodeProgram> SubPrograms = new List<BytecodeProgram>();
        public readonly List<FunctionDef> Functions = new List<FunctionDef>();
    }

    internal static class BytecodeIo
    {
        private static readonly byte[] Magic = { 0x50, 0x53, 0x56, 0x4D };

        public static readonly byte[] BuildKey =
        {
            0x2B, 0x7E, 0x15, 0x16, 0x28, 0xAE, 0xD2, 0xA6,
            0xAB, 0xF7, 0x15, 0x88, 0x09, 0xCF, 0x4F, 0x3C
        };

        public static byte[] FreshKey() => (byte[])BuildKey.Clone();

        public static void Save(string path, BytecodeProgram p, byte[] key)
        {
            using (var fs = File.Create(path))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Magic);
                bw.Write((byte)1);

                SaveBody(bw, p, key);

                bw.Write(p.SubPrograms.Count);
                foreach (var sp in p.SubPrograms) SaveBody(bw, sp, key);

                bw.Write(p.Functions.Count);
                foreach (var f in p.Functions)
                {
                    WriteConst(bw, f.Name, key);
                    bw.Write(f.ProgramIndex);
                    bw.Write(f.ParamNames.Count);
                    foreach (var pn in f.ParamNames) WriteConst(bw, pn, key);
                }
            }
        }

        public static BytecodeProgram Load(string path, byte[] key)
        {
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs))
            {
                var m = br.ReadBytes(4);
                for (int i = 0; i < 4; i++)
                    if (m[i] != Magic[i]) throw new InvalidDataException("bad magic");
                if (br.ReadByte() != 1) throw new InvalidDataException("bad version");

                var p = new BytecodeProgram();
                LoadBody(br, p, key);

                int ns = br.ReadInt32();
                for (int i = 0; i < ns; i++)
                {
                    var sp = new BytecodeProgram();
                    LoadBody(br, sp, key);
                    p.SubPrograms.Add(sp);
                }

                int nf = br.ReadInt32();
                for (int i = 0; i < nf; i++)
                {
                    var f = new FunctionDef();
                    f.Name = (string)ReadConst(br, key);
                    f.ProgramIndex = br.ReadInt32();
                    int np = br.ReadInt32();
                    for (int j = 0; j < np; j++) f.ParamNames.Add((string)ReadConst(br, key));
                    p.Functions.Add(f);
                }
                return p;
            }
        }

        private static void SaveBody(BinaryWriter bw, BytecodeProgram p, byte[] key)
        {
            bw.Write(p.Constants.Count);
            foreach (var c in p.Constants) WriteConst(bw, c, key);

          //  bw.Write(p.Code.Count);
            var buf = new byte[p.Code.Count * 9];
            int o = 0;
            foreach (var i in p.Code)
            {
                buf[o++] = (byte)i.Op;
                Buffer.BlockCopy(BitConverter.GetBytes(i.A), 0, buf, o, 4); o += 4;
                Buffer.BlockCopy(BitConverter.GetBytes(i.B), 0, buf, o, 4); o += 4;
            }
            XorInPlace(buf, key);
            bw.Write(buf.Length);
            bw.Write(buf);
        }

        private static void LoadBody(BinaryReader br, BytecodeProgram p, byte[] key)
        {
            int nc = br.ReadInt32();
            for (int i = 0; i < nc; i++) p.Constants.Add(ReadConst(br, key));

            int len = br.ReadInt32();
            var buf = br.ReadBytes(len);
            XorInPlace(buf, key);
            int count = len / 9;
            for (int k = 0; k < count; k++)
            {
                int o = k * 9;
                var op = (Op)buf[o];
                int a = BitConverter.ToInt32(buf, o + 1);
                int b = BitConverter.ToInt32(buf, o + 5);
                p.Code.Add(new Instr(op, a, b));
            }
        }

        private static void XorInPlace(byte[] buf, byte[] key)
        {
            for (int i = 0; i < buf.Length; i++)
            {
                buf[i] ^= key[i % key.Length];
                key[i % key.Length] = (byte)(key[i % key.Length] + 0x5D + (i & 7));
            }
        }

        private static void WriteConst(BinaryWriter bw, object v, byte[] key)
        {
            if (v == null) { bw.Write((byte)0); return; }
            if (v is bool bl) { bw.Write((byte)1); bw.Write(bl); return; }
            if (v is int ii) { bw.Write((byte)2); bw.Write(ii); return; }
            if (v is long ll) { bw.Write((byte)3); bw.Write(ll); return; }
            if (v is double dd) { bw.Write((byte)4); bw.Write(dd); return; }

            bw.Write((byte)5);
            var enc = Encoding.UTF8.GetBytes(v.ToString());
            for (int i = 0; i < enc.Length; i++)
                enc[i] ^= (byte)(key[i % key.Length] ^ 0xA7);
            bw.Write(enc.Length);
            bw.Write(enc);
        }

        private static object ReadConst(BinaryReader br, byte[] key)
        {
            byte tag = br.ReadByte();
            switch (tag)
            {
                case 0: return null;
                case 1: return br.ReadBoolean();
                case 2: return br.ReadInt32();
                case 3: return br.ReadInt64();
                case 4: return br.ReadDouble();
                case 5:
                    int n = br.ReadInt32();
                    var enc = br.ReadBytes(n);
                    for (int i = 0; i < enc.Length; i++)
                        enc[i] ^= (byte)(key[i % key.Length] ^ 0xA7);
                    return Encoding.UTF8.GetString(enc);
            }
            throw new InvalidDataException("bad const tag");
        }
    }
}