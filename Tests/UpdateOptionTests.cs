using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;
using NutstoreAutoPatch;

internal static class UpdateOptionTests
{
    static int count;
    static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
        count++;
        Console.WriteLine("PASS: " + name);
    }
    static void Reject(Action action, string name)
    {
        bool rejected = false;
        try { action(); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, name);
    }
    static void AddDependency(AssemblyDefinition assembly, string directory, TypeDefinition type)
    {
        using (var dependency = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Fixture.Dependency", new Version(1, 0)), "Fixture.Dependency", ModuleKind.Dll))
        {
            var choice = new TypeDefinition("Fixture", "Choice", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Sealed, dependency.MainModule.ImportReference(typeof(Enum)));
            choice.Fields.Add(new FieldDefinition("value__", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.SpecialName | Mono.Cecil.FieldAttributes.RTSpecialName, dependency.MainModule.TypeSystem.Int32));
            dependency.MainModule.Types.Add(choice);
            dependency.Write(Path.Combine(directory, "Fixture.Dependency.dll"));
        }
        var reference = new AssemblyNameReference("Fixture.Dependency", new Version(1, 0));
        assembly.MainModule.AssemblyReferences.Add(reference);
        var enumType = new TypeReference("Fixture", "Choice", assembly.MainModule, reference, true);
        var method = new MethodDefinition("Unrelated", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, assembly.MainModule.TypeSystem.Void);
        method.Parameters.Add(new ParameterDefinition("choice", Mono.Cecil.ParameterAttributes.Optional | Mono.Cecil.ParameterAttributes.HasDefault, enumType) { Constant = 0 });
        method.Body.GetILProcessor().Emit(OpCodes.Ret);
        type.Methods.Add(method);
        ((DefaultAssemblyResolver)assembly.MainModule.AssemblyResolver).AddSearchDirectory(directory);
    }
    static string Fixture(string directory, string variant)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "NutstoreClient.Legacy.dll");
        using (var a = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("NutstoreClient.Legacy", new Version(7, 2, 12)), "NutstoreClient.Legacy", ModuleKind.Dll))
        {
            var module = a.MainModule;
            var type = new TypeDefinition("NutstoreClient.Upgrade", "UpgradeThread", Mono.Cecil.TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(type);
            var threadField = new FieldDefinition("thread", Mono.Cecil.FieldAttributes.Private, module.ImportReference(typeof(Thread)));
            var countField = new FieldDefinition("Count", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, module.TypeSystem.Int32);
            type.Fields.Add(threadField); type.Fields.Add(countField);
            var worker = new MethodDefinition("Work", Mono.Cecil.MethodAttributes.Private | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void);
            type.Methods.Add(worker);
            var il = worker.Body.GetILProcessor();
            il.Emit(OpCodes.Ldsfld, countField); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stsfld, countField); il.Emit(OpCodes.Ret);
            var ctor = new MethodDefinition(".ctor", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            type.Methods.Add(ctor);
            il = ctor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)));
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ldftn, worker);
            il.Emit(OpCodes.Newobj, module.ImportReference(typeof(ThreadStart).GetConstructors().Single()));
            il.Emit(OpCodes.Newobj, module.ImportReference(typeof(Thread).GetConstructor(new[] { typeof(ThreadStart) })));
            il.Emit(OpCodes.Stfld, threadField); il.Emit(OpCodes.Ret);
            var begin = new MethodDefinition("BeginCheckForUpgrade", Mono.Cecil.MethodAttributes.Public, module.TypeSystem.Void);
            type.Methods.Add(begin);
            il = begin.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, threadField);
            il.Emit(OpCodes.Callvirt, module.ImportReference(typeof(Thread).GetMethod("Start", Type.EmptyTypes))); il.Emit(OpCodes.Ret);
            if (variant == "signature") begin.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
            if (variant == "unknown") { il.InsertBefore(begin.Body.Instructions[0], il.Create(OpCodes.Ldstr, "unknown side effect")); il.InsertBefore(begin.Body.Instructions[1], il.Create(OpCodes.Pop)); }
            if (variant == "signed") module.Attributes |= ModuleAttributes.StrongNameSigned;
            if (variant == "dependency") AddDependency(a, directory, type);
            a.Write(path);
        }
        return path;
    }
    static int Execute(string file)
    {
        var type = Assembly.Load(File.ReadAllBytes(file)).GetType("NutstoreClient.Upgrade.UpgradeThread", true);
        object instance = Activator.CreateInstance(type);
        type.GetMethod("BeginCheckForUpgrade").Invoke(instance, null);
        var thread = (Thread)type.GetField("thread", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
        if (thread.ThreadState != ThreadState.Unstarted) thread.Join(5000);
        return (int)type.GetField("Count").GetValue(null);
    }
    public static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().FullName + ": " + error.Message);
            Console.Error.WriteLine(error.StackTrace);
            return 1;
        }
    }
    static int Run(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        string path = Fixture(Path.Combine(root, "normal"), "normal");
        string original = Patcher.Hash(path);
        Check(!UpdatePatcher.IsDisabled(path), "original background update is recognized");
        Check(!UpdatePatcher.CanRestore(path), "status without backup does not invent restore state");
        Check(UpdatePatcher.Disable(path, true), "disable background update succeeds");
        string modified = Patcher.Hash(path);
        Check(UpdatePatcher.IsDisabled(path), "disabled background update recognized");
        Check(!UpdatePatcher.Disable(path, true) && Patcher.Hash(path) == modified, "repeat disable leaves bytes unchanged");
        Check(Execute(path) == 0, "disabled method does not start real fixture thread");
        Check(UpdatePatcher.CanRestore(path), "valid original can be restored");
        UpdatePatcher.Restore(path);
        Check(Patcher.Hash(path) == original, "restore matches original bytes");
        string restoredCopy = Path.Combine(root, "restored-runtime", Path.GetFileName(path));
        Directory.CreateDirectory(Path.GetDirectoryName(restoredCopy)); File.Copy(path, restoredCopy);
        Check(Execute(restoredCopy) == 1, "restored method starts real fixture thread");
        string dependency = Fixture(Path.Combine(root, "dependency"), "dependency");
        string dependencyHash = Patcher.Hash(dependency);
        Check(UpdatePatcher.Disable(dependency, true), "writer resolves adjacent metadata-only enum dependency");
        UpdatePatcher.Restore(dependency);
        Check(Patcher.Hash(dependency) == dependencyHash, "metadata dependency fixture restores exact original");
        string noBackup = Fixture(Path.Combine(root, "no-backup"), "normal");
        Check(UpdatePatcher.Disable(noBackup, false), "backup can be disabled for update option");
        Check(!Directory.GetFiles(Path.GetDirectoryName(noBackup), "*.bak").Any() && !Directory.GetFiles(Path.GetDirectoryName(noBackup), "*.state").Any(), "backup off creates no backup or record");
        string noBackupHash = Patcher.Hash(noBackup);
        Reject(delegate { UpdatePatcher.Restore(noBackup); }, "no-backup restore is rejected");
        Check(Patcher.Hash(noBackup) == noBackupHash, "rejected restore keeps disabled DLL unchanged");
        foreach (string variant in new[] { "signature", "unknown", "signed" })
        {
            string file = Fixture(Path.Combine(root, variant), variant);
            string before = Patcher.Hash(file);
            Reject(delegate { UpdatePatcher.Disable(file, true); }, "reject " + variant + " update implementation");
            Check(Patcher.Hash(file) == before, "rejected " + variant + " stays byte-identical");
        }
        string corrupt = Fixture(Path.Combine(root, "corrupt"), "normal");
        UpdatePatcher.Disable(corrupt, true);
        string backup = Directory.GetFiles(Path.GetDirectoryName(corrupt), "*.bak").Single();
        File.AppendAllText(backup, "corrupted");
        Reject(delegate { UpdatePatcher.Restore(corrupt); }, "corrupted update backup is rejected");
        string tampered = Fixture(Path.Combine(root, "tampered-record"), "normal");
        UpdatePatcher.Disable(tampered, true);
        string tamperedHash = Patcher.Hash(tampered);
        string record = Directory.GetFiles(Path.GetDirectoryName(tampered), "*.state").Single();
        string[] state = File.ReadAllLines(record); state[0] = "..\\outside.dll"; File.WriteAllLines(record, state);
        Reject(delegate { UpdatePatcher.CanRestore(tampered); }, "tampered backup path rejected during restore preflight");
        Reject(delegate { UpdatePatcher.Restore(tampered); }, "tampered backup path rejected during restore");
        Check(Patcher.Hash(tampered) == tamperedHash, "tampered restore record cannot modify target");
        string external = Fixture(Path.Combine(root, "external"), "normal");
        UpdatePatcher.Disable(external, true); File.AppendAllText(external, "updated");
        string externalHash = Patcher.Hash(external);
        Reject(delegate { UpdatePatcher.Restore(external); }, "externally updated DLL refuses old restore");
        Check(Patcher.Hash(external) == externalHash, "external update remains unchanged");
        string snapshot = Path.Combine(root, "snapshot.txt"); File.WriteAllText(snapshot, "original");
        string snapshotHash = Patcher.Hash(snapshot); File.AppendAllText(snapshot, "changed");
        Reject(delegate { FileChanges.Apply(snapshot, new byte[] { 1, 2 }, true, "test", snapshotHash); }, "stale transformation source is rejected");
        Check(File.ReadAllText(snapshot) == "originalchanged", "stale transformation preserves latest bytes");
        Fixture(Path.Combine(root, "cli-fixture"), "normal");
        Check(!Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "option temporary files cleaned");
        Console.WriteLine("TOTAL: " + count);
        return 0;
    }
}
