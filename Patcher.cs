using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace NutstoreAutoPatch
{
    public static class Patcher
    {
        private const string TypeName = "NutstoreLib.Utils.DirectoryUtils";

        public static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static AssemblyDefinition Read(string path)
        {
            return AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true, ReadSymbols = false });
        }

        private static MethodDefinition Getter(AssemblyDefinition assembly, string name)
        {
            var type = assembly.MainModule.GetType(TypeName);
            if (assembly.Name.Name != "NutstoreLib" || type == null)
                throw new InvalidOperationException("不是支持的 NutstoreLib.dll。");
            var found = type.Methods.Where(m => m.Name == name).ToArray();
            if (found.Length != 1 || !found[0].IsStatic || found[0].HasParameters ||
                found[0].ReturnType.FullName != "System.String" || !found[0].HasBody)
                throw new InvalidOperationException("方法签名不兼容，未修改文件：" + name);
            return found[0];
        }

        private static bool Calls(Instruction instruction, OpCode opcode, string signature)
        {
            var method = instruction.Operand as MethodReference;
            return instruction.OpCode == opcode && method != null && method.FullName == signature;
        }

        private static bool Matches(AssemblyDefinition assembly)
        {
            var body = Getter(assembly, "get_APPDATA_NUTSTORE_DIR").Body;
            var install = Getter(assembly, "get_NUTSTORE_INSTALL_DIR");
            var i = body.Instructions;
            if (body.ExceptionHandlers.Count != 0 || (i.Count != 5 && i.Count != 7) ||
                !Calls(i[0], OpCodes.Call, install.FullName) ||
                i[i.Count - 3].OpCode != OpCodes.Ldstr || !object.Equals(i[i.Count - 3].Operand, "UserData") ||
                i[i.Count - 1].OpCode != OpCodes.Ret) return false;
            string combine = "System.String System.IO.Path::Combine(System.String,System.String)";
            string longCombine = "System.String Pri.LongPath.Path::Combine(System.String,System.String)";
            if (!Calls(i[i.Count - 2], OpCodes.Call, combine) && !Calls(i[i.Count - 2], OpCodes.Call, longCombine)) return false;
            if (i.Count == 5) return Calls(i[1], OpCodes.Call, "System.String System.IO.Path::GetDirectoryName(System.String)");
            return Calls(i[1], OpCodes.Newobj, "System.Void System.IO.DirectoryInfo::.ctor(System.String)") &&
                (Calls(i[2], OpCodes.Call, "System.IO.DirectoryInfo System.IO.DirectoryInfo::get_Parent()") ||
                 Calls(i[2], OpCodes.Callvirt, "System.IO.DirectoryInfo System.IO.DirectoryInfo::get_Parent()")) &&
                Calls(i[3], OpCodes.Callvirt, "System.String System.IO.FileSystemInfo::get_FullName()");
        }

        public static bool IsPatched(string path)
        {
            using (var assembly = Read(path)) return Matches(assembly);
        }

        public static void Validate(string path)
        {
            using (var assembly = Read(path))
                if (!Matches(assembly) && (assembly.Name.HasPublicKey || (assembly.MainModule.Attributes & ModuleAttributes.StrongNameSigned) != 0))
                    throw new InvalidOperationException("此程序集有强名称签名，没有原始密钥，拒绝修改。");
        }

        public static bool CanRestore(string path)
        {
            path = Path.GetFullPath(path);
            string hash = Hash(path);
            if (!File.Exists(RecordPath(path, hash))) return false;
            string originalHash;
            ValidatedBackup(path, hash, out originalHash);
            return true;
        }

        private static string ValidatedBackup(string path, string patchedHash, out string originalHash)
        {
            string record = RecordPath(path, patchedHash);
            if (!File.Exists(record)) throw new InvalidOperationException("当前 DLL 没有可用的恢复记录；可能未勾选备份，或文件已更新、由其他工具修改，无法自动恢复。");
            string[] state = File.ReadAllLines(record);
            if (state.Length != 2 || state[1].Length != 64 || state[1].Any(c => !"0123456789abcdef".Contains(c)) ||
                state[0] != Path.GetFileName(path) + ".original." + state[1] + ".bak")
                throw new InvalidOperationException("恢复记录无效。");
            string backup = Path.Combine(Path.GetDirectoryName(path), state[0]);
            originalHash = state[1];
            if (Hash(backup) != originalHash) throw new InvalidOperationException("备份哈希不一致，拒绝恢复。");
            if (!IsPatched(path)) throw new InvalidOperationException("当前 DLL 不是受支持的补丁，拒绝恢复。");
            return backup;
        }

        private static string RecordPath(string path, string patchedHash)
        {
            return path + ".portable." + patchedHash + ".state";
        }

        private static void SaveRecord(string path, string contents)
        {
            if (File.Exists(path))
            {
                if (File.ReadAllText(path) != contents) throw new InvalidOperationException("已有恢复记录与当前备份不一致。");
                return;
            }
            string temp = Path.Combine(Path.GetDirectoryName(path), ".nutstore-" + Guid.NewGuid().ToString("N") + ".tmp");
            try { File.WriteAllText(temp, contents); File.Move(temp, path); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public static bool Patch(string path)
        {
            return Patch(path, true);
        }

        public static bool Patch(string path, bool createBackup)
        {
            path = Path.GetFullPath(path);
            using (var operationLock = new FileStream(path + ".portable.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                string originalHash = Hash(path);
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var assembly = Read(path))
                    {
                        if (Matches(assembly)) return false;
                        if (assembly.Name.HasPublicKey || (assembly.MainModule.Attributes & ModuleAttributes.StrongNameSigned) != 0)
                            throw new InvalidOperationException("此程序集有强名称签名，没有原始密钥，拒绝修改。");
                        var method = Getter(assembly, "get_APPDATA_NUTSTORE_DIR");
                        method.Body = new Mono.Cecil.Cil.MethodBody(method);
                        var il = method.Body.GetILProcessor();
                        il.Emit(OpCodes.Call, Getter(assembly, "get_NUTSTORE_INSTALL_DIR"));
                        il.Emit(OpCodes.Newobj, assembly.MainModule.ImportReference(typeof(DirectoryInfo).GetConstructor(new[] { typeof(string) })));
                        il.Emit(OpCodes.Call, assembly.MainModule.ImportReference(typeof(DirectoryInfo).GetProperty("Parent").GetGetMethod()));
                        il.Emit(OpCodes.Callvirt, assembly.MainModule.ImportReference(typeof(FileSystemInfo).GetProperty("FullName").GetGetMethod()));
                        il.Emit(OpCodes.Ldstr, "UserData");
                        il.Emit(OpCodes.Call, assembly.MainModule.ImportReference(typeof(Path).GetMethod("Combine", new[] { typeof(string), typeof(string) })));
                        il.Emit(OpCodes.Ret);
                        assembly.Write(temp);
                    }
                    if (!IsPatched(temp)) throw new InvalidOperationException("生成的补丁未通过校验。");
                    if (createBackup)
                    {
                        string backup = path + ".original." + originalHash + ".bak";
                        if (!File.Exists(backup)) File.Copy(path, backup, false);
                        if (Hash(backup) != originalHash)
                            throw new InvalidOperationException("备份已变化，停止替换。");
                        SaveRecord(RecordPath(path, Hash(temp)), Path.GetFileName(backup) + "\n" + originalHash + "\n");
                    }
                    if (Hash(path) != originalHash)
                        throw new InvalidOperationException("原文件已变化，停止替换。");
                    File.Replace(temp, path, null);
                    return true;
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }

        public static void Restore(string path)
        {
            path = Path.GetFullPath(path);
            using (var operationLock = new FileStream(path + ".portable.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                string patchedHash = Hash(path);
                string originalHash;
                string backup = ValidatedBackup(path, patchedHash, out originalHash);
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(backup, temp, false);
                    if (Hash(temp) != originalHash || Hash(path) != patchedHash)
                        throw new InvalidOperationException("恢复期间文件已变化，停止替换。");
                    File.Replace(temp, path, null);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
    }
}
