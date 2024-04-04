auto patch NutstoreLib.dll 
```
0    0000    call    string NutstoreLib.Utils.DirectoryUtils::get_NUTSTORE_INSTALL_DIR()
1    0005    newobj    instance void [mscorlib]System.IO.DirectoryInfo::.ctor(string)
2    000A    call    instance class [mscorlib]System.IO.DirectoryInfo [mscorlib]System.IO.DirectoryInfo::get_Parent()
3    000F    callvirt    instance string [mscorlib]System.IO.FileSystemInfo::get_FullName()
4    0014    ldstr    "UserData"
5    0019    call    string [Pri.LongPath]Pri.LongPath.Path::Combine(string, string)
6    001E    ret
```
detail,see my blog post about this [url](https://czyt.tech/post/use-mono-cecil-to-auto-patch-dotnet-application/)
