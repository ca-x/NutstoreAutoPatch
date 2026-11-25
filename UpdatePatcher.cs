using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace NutstoreAutoPatch
{
    public static class UpdatePatcher
    {
        public const string Feature = "background-update";
        private const string TypeName = "NutstoreClient.Upgrade.UpgradeThread";

        private static DefaultAssemblyResolver Resolver(string path)
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            return resolver;
        }

        private static AssemblyDefinition Read(byte[] bytes, IAssemblyResolver resolver)
        {
            return AssemblyDefinition.ReadAssembly(new MemoryStream(bytes, false), new ReaderParameters { InMemory = true, ReadSymbols = false, AssemblyResolver = resolver });
        }

        private static MethodDefinition Method(AssemblyDefinition assembly)
        {
            var type = assembly.MainModule.GetType(TypeName);
            if (assembly.Name.Name != "NutstoreClient.Legacy" || type == null)
                throw new InvalidOperationException("不是受支持的 NutstoreClient.Legacy.dll。");
            if (assembly.Name.HasPublicKey || (assembly.MainModule.Attributes & ModuleAttributes.StrongNameSigned) != 0)
                throw new InvalidOperationException("更新程序集有强名称签名，拒绝修改。");
            var methods = type.Methods.Where(m => m.Name == "BeginCheckForUpgrade").ToArray();
            if (methods.Length != 1 || methods[0].IsStatic || methods[0].HasParameters || methods[0].HasGenericParameters ||
                methods[0].ReturnType.FullName != "System.Void" || !methods[0].HasBody)
                throw new InvalidOperationException("后台更新入口签名不兼容。");
            var method = methods[0];
            var instructions = method.Body.Instructions.Where(i => i.OpCode != OpCodes.Nop).ToArray();
            if (method.Body.ExceptionHandlers.Count != 0 || method.Body.Variables.Count != 0)
                throw new InvalidOperationException("后台更新入口含未知局部变量或异常处理，拒绝修改。");
            if (instructions.Length == 1 && instructions[0].OpCode == OpCodes.Ret) return method;
            var field = instructions.Length == 4 ? instructions[1].Operand as FieldReference : null;
            var start = instructions.Length == 4 ? instructions[2].Operand as MethodReference : null;
            if (instructions.Length != 4 || instructions[0].OpCode != OpCodes.Ldarg_0 || instructions[1].OpCode != OpCodes.Ldfld ||
                field == null || field.DeclaringType.FullName != TypeName || field.FieldType.FullName != "System.Threading.Thread" ||
                instructions[2].OpCode != OpCodes.Callvirt || start == null || !start.HasThis ||
                start.FullName != "System.Void System.Threading.Thread::Start()" || instructions[3].OpCode != OpCodes.Ret)
                throw new InvalidOperationException("后台更新入口实现与已验证版本不符，拒绝修改。");
            return method;
        }

        private static bool Disabled(MethodDefinition method)
        {
            var instructions = method.Body.Instructions.Where(i => i.OpCode != OpCodes.Nop).ToArray();
            return instructions.Length == 1 && instructions[0].OpCode == OpCodes.Ret;
        }

        public static bool IsDisabled(string path)
        {
            using (var resolver = Resolver(path))
            using (var assembly = Read(File.ReadAllBytes(path), resolver)) return Disabled(Method(assembly));
        }

        public static bool Disable(string path, bool createBackup)
        {
            byte[] original = File.ReadAllBytes(path);
            string originalHash = FileChanges.HashBytes(original);
            byte[] replacement;
            using (var resolver = Resolver(path))
            {
                using (var assembly = Read(original, resolver))
                {
                    var method = Method(assembly);
                    if (Disabled(method)) return false;
                    method.Body = new Mono.Cecil.Cil.MethodBody(method);
                    method.Body.GetILProcessor().Emit(OpCodes.Ret);
                    using (var stream = new MemoryStream()) { assembly.Write(stream); replacement = stream.ToArray(); }
                }
                using (var verification = Read(replacement, resolver))
                    if (!Disabled(Method(verification))) throw new InvalidOperationException("后台更新补丁验证失败。");
            }
            return FileChanges.Apply(path, replacement, createBackup, Feature, originalHash);
        }

        public static bool CanRestore(string path)
        {
            return IsDisabled(path) && FileChanges.CanRestore(path, Feature);
        }

        public static void Restore(string path)
        {
            if (!IsDisabled(path)) throw new InvalidOperationException("当前后台更新检查未被此类补丁禁用，拒绝恢复。");
            FileChanges.Restore(path, Feature);
        }
    }
}
