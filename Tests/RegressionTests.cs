using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using NutstoreAutoPatch;

internal static class RegressionTests
{
    static int count;
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAIL: " + message);
        count++;
        Console.WriteLine("PASS: " + message);
    }
    static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, message);
    }
    static string Fixture(string directory, bool valid)
    {
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "NutstoreLib.dll");
        using (var a = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("NutstoreLib", new Version(1, 0)), "NutstoreLib", ModuleKind.Dll))
        {
            var type = new TypeDefinition("NutstoreLib.Utils", "DirectoryUtils", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, a.MainModule.TypeSystem.Object);
            a.MainModule.Types.Add(type);
            foreach (string name in new[] { "get_NUTSTORE_INSTALL_DIR", "get_APPDATA_NUTSTORE_DIR" })
            {
                var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static, a.MainModule.TypeSystem.String);
                type.Methods.Add(method);
                var il = method.Body.GetILProcessor();
                if (name == "get_NUTSTORE_INSTALL_DIR")
                {
                    il.Emit(OpCodes.Call, a.MainModule.ImportReference(typeof(System.Reflection.Assembly).GetMethod("GetExecutingAssembly")));
                    il.Emit(OpCodes.Callvirt, a.MainModule.ImportReference(typeof(System.Reflection.Assembly).GetProperty("Location").GetGetMethod()));
                    il.Emit(OpCodes.Call, a.MainModule.ImportReference(typeof(Path).GetMethod("GetDirectoryName", new[] { typeof(string) })));
                }
                else il.Emit(OpCodes.Ldstr, "original-data");
                il.Emit(OpCodes.Ret);
                if (!valid && name == "get_APPDATA_NUTSTORE_DIR")
                    method.Parameters.Add(new ParameterDefinition(a.MainModule.TypeSystem.String));
            }
            a.Write(file);
        }
        return file;
    }
    public static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        string file = Fixture(Path.Combine(root, "normal", "bin-1.0"), true);
        string original = Patcher.Hash(file);
        Check(!Patcher.IsPatched(file), "original is recognized as unpatched");
        Check(Patcher.Patch(file), "first patch succeeds");
        string patched = Patcher.Hash(file);
        Check(Patcher.IsPatched(file), "relative patch recognized");
        Check(!Patcher.Patch(file) && Patcher.Hash(file) == patched, "repeat patch leaves bytes unchanged");
        string[] backups = Directory.GetFiles(Path.GetDirectoryName(file), "*.original.*.bak");
        Check(backups.Length == 1 && Patcher.Hash(backups[0]) == original, "backup preserves exact original");
        Patcher.Restore(file);
        Check(Patcher.Hash(file) == original, "restore reproduces exact original bytes");
        Check(!Patcher.IsPatched(file), "restored file is recognized as original");
        Check(Patcher.Patch(file), "repatch after restore succeeds");
        patched = Patcher.Hash(file);
        File.AppendAllText(file, "new-version");
        string changed = Patcher.Hash(file);
        Reject(delegate { Patcher.Restore(file); }, "restore refuses changed or updated DLL");
        Check(Patcher.Hash(file) == changed, "failed restore preserves changed DLL");
        string bad = Fixture(Path.Combine(root, "bad-signature"), false);
        string badHash = Patcher.Hash(bad);
        Reject(delegate { Patcher.Patch(bad); }, "incompatible getter signature rejected");
        Check(Patcher.Hash(bad) == badHash, "incompatible assembly stays unchanged");
        string signed = Fixture(Path.Combine(root, "signed"), true);
        using (var a = AssemblyDefinition.ReadAssembly(signed, new ReaderParameters { InMemory = true }))
        {
            a.MainModule.Attributes |= ModuleAttributes.StrongNameSigned;
            a.Write(signed + ".signed");
        }
        File.Copy(signed + ".signed", signed, true);
        string signedHash = Patcher.Hash(signed);
        Reject(delegate { Patcher.Patch(signed); }, "strong-name signed module rejected");
        Check(Patcher.Hash(signed) == signedHash, "signed module stays unchanged");
        string corrupt = Fixture(Path.Combine(root, "corrupt-backup"), true);
        Patcher.Patch(corrupt);
        string backup = Directory.GetFiles(Path.GetDirectoryName(corrupt), "*.original.*.bak").Single();
        File.AppendAllText(backup, "broken-backup");
        string before = Patcher.Hash(corrupt);
        Reject(delegate { Patcher.Restore(corrupt); }, "corrupt backup rejected");
        Check(Patcher.Hash(corrupt) == before, "corrupt backup does not damage patched DLL");
        string noBackup = Fixture(Path.Combine(root, "no-backup"), true);
        Check(Patcher.Patch(noBackup, false), "backup can be explicitly disabled");
        Check(Patcher.IsPatched(noBackup), "patch without backup still validates");
        string noBackupDirectory = Path.GetDirectoryName(noBackup);
        Check(!Directory.GetFiles(noBackupDirectory, "*.bak").Any(), "disabled backup creates no original backup");
        Check(!Directory.GetFiles(noBackupDirectory, "*.state").Any(), "disabled backup creates no restore record");
        string noBackupPatched = Patcher.Hash(noBackup);
        Reject(delegate { Patcher.Restore(noBackup); }, "restore without a backup is rejected");
        Check(Patcher.Hash(noBackup) == noBackupPatched, "rejected restore without backup preserves patched DLL");
        Check(!Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "temporary files cleaned");
        string longRoot = Path.Combine(Path.GetTempPath(), "NutstoreLong-" + Guid.NewGuid().ToString("N"));
        int padding = 150 - Path.Combine(longRoot, "NutstoreLib.dll").Length - 1;
        string longFile = Fixture(Path.Combine(longRoot, new string('x', padding)), true);
        Check(longFile.Length == 150, "long-path regression fixture has expected length");
        string longOriginal = Patcher.Hash(longFile);
        Check(Patcher.Patch(longFile), "long install path patches successfully");
        Patcher.Restore(longFile);
        Check(Patcher.Hash(longFile) == longOriginal, "long install path restores exactly");
        string move = Fixture(Path.Combine(root, "move-a", "bin-1.0"), true);
        Patcher.Patch(move);
        Console.WriteLine("TOTAL: " + count);
        return 0;
    }
}
