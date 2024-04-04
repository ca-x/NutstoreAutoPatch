using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace NutstoreAutoPatch
{
    internal class Program
    {
        public static void Main(string[] args)
        {
           const string nutstoreLib = "NutstoreLib.dll"; 
           const string directoryutils = "NutstoreLib.Utils.DirectoryUtils"; 
           const string appdataNutstoreDir = "get_APPDATA_NUTSTORE_DIR"; 
           
           
            // Read the assembly
            var assemblyDefinition = AssemblyDefinition.ReadAssembly(nutstoreLib);
            var directoryUtil = assemblyDefinition.MainModule.Types.First(t => t.FullName == directoryutils);
            var appdataDirMethod = directoryUtil.Methods.First(m => m.Name == appdataNutstoreDir);
            
            // 清除当前指令
            appdataDirMethod.Body.Instructions.Clear();

            // 获取IL处理器
            var ilProcessor = appdataDirMethod.Body.GetILProcessor();

            // 构造调用NutstoreLib.Utils.DirectoryUtils.get_NUTSTORE_INSTALL_DIR的指令
            var getInstallDirMethod = appdataDirMethod.Module.ImportReference(
                appdataDirMethod.DeclaringType.Module.Types
                    .First(t => t.Name == "DirectoryUtils" && t.Namespace == "NutstoreLib.Utils")
                    .Methods.First(m => m.Name == "get_NUTSTORE_INSTALL_DIR")
            );
            ilProcessor.Append(ilProcessor.Create(OpCodes.Call, getInstallDirMethod));

            // 构造调用System.IO.DirectoryInfo构造函数的指令
            var directoryInfoConstructor = appdataDirMethod.Module.ImportReference(
                typeof(System.IO.DirectoryInfo).GetConstructor(new[] { typeof(string) })
            );
            ilProcessor.Append(ilProcessor.Create(OpCodes.Newobj, directoryInfoConstructor));

            // 构造获取DirectoryInfo.Parent属性的调用指令
            var getParentMethod = appdataDirMethod.Module.ImportReference(
                typeof(System.IO.DirectoryInfo).GetProperty("Parent").GetGetMethod()
            );
            ilProcessor.Append(ilProcessor.Create(OpCodes.Call, getParentMethod));

            // 构造调用System.IO.FileSystemInfo.FullName属性的指令
            var getFullNameMethod = appdataDirMethod.Module.ImportReference(
                typeof(System.IO.FileSystemInfo).GetProperty("FullName").GetGetMethod()
            );
            ilProcessor.Append(ilProcessor.Create(OpCodes.Callvirt, getFullNameMethod));

            // 加载字符串"UserData"
            ilProcessor.Append(ilProcessor.Create(OpCodes.Ldstr, "UserData"));

            // 构造调用Pri.LongPath.Path.Combine的指令
            var pathCombineMethod = appdataDirMethod.Module.ImportReference(
                    typeof(System.IO.Path).GetMethod("Combine", new[] { typeof(string), typeof(string) })
                );
            ilProcessor.Append(ilProcessor.Create(OpCodes.Call, pathCombineMethod));

          // 返回指令
            ilProcessor.Append(ilProcessor.Create(OpCodes.Ret));

            // Write the modified assembly
            assemblyDefinition.Write("NutstoreLib_patched.dll");
        }
    }
}