# NutstoreAutoPatch

v0.3.0 增加三个独立选项，默认不勾选。选中后点击“应用所选”，撤销时选中同一项并点击“恢复所选”；取消勾选仅表示本次不处理，不会撤销已经应用的修改。

| 选项 | 作用与范围 |
|---|---|
| 关闭后台更新检查 | 精确补丁 `NutstoreClient.Legacy.dll` 的后台线程启动入口；同时暂停该线程中的 Outlook 插件更新及相关功能配置刷新。不保证阻止手动安装和其他更新入口，遇到未知代码版本会拒绝修改。 |
| 日志保存到程序目录 | 调整所选版本目录中可识别的组件日志配置为相对 `..\UserData\logs`。不迁移旧日志，不修改已安装到 ProgramData 的服务副本，不承诺所有临时文件或系统写入都被重定向。 |
| 关闭当前安装的开机启动 | 仅删除当前用户 Run 中指向所选安装目录 `Nutstore.exe` 的 `Nutstore` 值。其他安装或其他软件的启动项不会修改；不停止、卸载或安装服务。 |

“备份修改项”勾选后才保存此次修改的备份和恢复记录，包括 DLL、日志配置与启动项；取消后不能依靠本次操作自动恢复。后台更新和配置文件恢复会核对当前文件与备份；升级或外部修改后拒绝用旧内容覆盖。

```powershell
# 保持原资料目录，只关闭后台更新检查并调整组件日志
.\NutstoreAutoPatch.exe patch "D:\Nutstore" --no-userdata --disable-background-updates --portable-logs --close
# 恢复对应的两项，不恢复资料目录补丁
.\NutstoreAutoPatch.exe restore "D:\Nutstore" --no-userdata --disable-background-updates --portable-logs --close
# 仅取消当前用户、当前安装目录的开机启动
.\NutstoreAutoPatch.exe patch "D:\Nutstore" --no-userdata --disable-startup
```

普通文件同步能否完全不依赖 USN 文件监测服务，尚未完成运行验证。本工具不提供一键卸载所有后台服务的选项，也不改变 USN 或驱动服务。

将 Windows 坚果云的 `NutstoreLib.Utils.DirectoryUtils.APPDATA_NUTSTORE_DIR` 改为**相对于程序安装位置的 `UserData`**。基于[原文](https://czyt.tech/post/use-mono-cecil-to-auto-patch-dotnet-application/)完善，使用项目现有的 Mono.Cecil 0.11.5 和 .NET Framework 4.8。

例如，DLL 位于 `D:\Nutstore\bin-7.2.11\NutstoreLib.dll` 时，资料路径为 `D:\Nutstore\UserData`。将整个目录移到 `E:\Apps\Nutstore` 后，路径随程序变为 `E:\Apps\Nutstore\UserData`。补丁不写死盘符，也不依赖启动时的当前工作目录。

## 使用

将发布包完整解压，保持 `NutstoreAutoPatch.exe` 和 `Mono.Cecil.dll` 在同一目录，双击 EXE 打开中文图形界面。需要 Windows .NET Framework 4.8 运行时，使用时无需安装 SDK。

选择坚果云根目录、版本目录或 `NutstoreLib.dll`，勾选功能后点击“检查状态”“应用所选”或“恢复所选”。窗口下方显示操作记录。工具位于 `Nutstore.exe` 旁或版本目录内时，会自动识别附近的安装目录。

“操作前自动关闭坚果云相关进程”默认勾选：补丁或恢复前，先请求正常关闭属于所选安装目录的组件，最多等待 3 秒；仍运行的组件会被结束，再最多等待 3 秒。不会结束其他安装目录的同名组件、本工具或原生驱动服务。无法确认路径或无法结束时停止操作。**检查状态不会关闭进程**。取消勾选后，发现运行中的组件会提示先退出。

“备份修改项（用于恢复）”默认勾选，只有勾选时才为本次实际修改的 DLL、日志配置或启动项保存原件和恢复记录。取消后仍会验证并应用所选修改，但不新建备份和恢复记录，也不删除之前已有的文件；无法依靠本次操作自动恢复。此选项只影响“应用所选”，不影响检查或恢复已有备份。

也保留命令行接口：

```powershell
.\NutstoreAutoPatch.exe status "D:\Nutstore"
.\NutstoreAutoPatch.exe patch "D:\Nutstore"
.\NutstoreAutoPatch.exe patch "D:\Nutstore" --close
.\NutstoreAutoPatch.exe patch "D:\Nutstore" --no-backup --close
.\NutstoreAutoPatch.exe restore "D:\Nutstore"
```

路径也可以直接指定 `bin-*` 目录或 `NutstoreLib.dll`。指定根目录时，会处理其中每个 `bin-*\NutstoreLib.dll`，并逐个报告结果；某个版本失败时其他版本可能已成功，请根据输出逐个检查。成功退出码为 0，失败为 1。

命令行默认不结束进程；添加 `--close` 才启用上述自动关闭流程。无参数启动时打开图形界面。通过脚本调用并读取退出码时，请使用等待进程结束的方式，例如 `Start-Process -Wait -PassThru`；输出可以重定向，不会额外弹出控制台窗口。

命令行默认备份，`--no-backup` 只适用于 `patch`，表示不创建本次备份和恢复记录；用于 `status` 或 `restore` 时会拒绝操作。`--close` 和 `--no-backup` 可放在任意位置，不能重复。

## 备份、升级与恢复

- 勾选备份时，应用补丁前以 SHA256 命名保存原件：`NutstoreLib.dll.original.<原件哈希>.bak`。不会覆盖已有备份，已有备份损坏时停止操作。
- 先写临时 DLL，再重新读取验证 IL，最后通过 `File.Replace` 替换。写入或校验失败不会先删掉正在使用的 DLL。
- 同一 DLL 重复补丁不会再次改写字节；能识别原文的 7 条指令版本及本工具的版本。已补丁的 DLL 不会补造原件备份；如第一次未备份，之后再勾选也不能找回原件。
- 恢复记录为 `NutstoreLib.dll.portable.<补丁哈希>.state`。按当前 DLL 的哈希匹配原件；DLL 已更新或被其他工具改动时拒绝用旧版本恢复。
- 保留 DLL 旁的备份和恢复记录。`.portable.lock` 为并发锁文件，保留空文件不代表工具仍在运行。
- 后台更新和日志配置使用目标文件旁的 `.nsopt-*.state` 记录，原件为 `<目标文件名>.original.<哈希>.bak`。开机启动备份位于安装根目录 `.nutstore-autopatch/startup.xml`。这些记录和原件应一起保留；恢复会拒绝覆盖后来更新或修改的状态。
- 恢复仅处理勾选的功能，并要求每个目标都有有效记录；某配置原本已经便携、没有被本工具备份，或首次未勾选备份时，无法自动恢复该项。预检失败会先报告原因，不会开始其他所选修改。
- 坚果云更新可能替换 DLL。退出客户端后重新运行 `status` 和 `patch` 即可；旧版本备份不会覆盖新版本备份。
- 原文旧工具生成的 `NutstoreLib.origin.dll` 不会自动用于恢复，因为旧工具可能已覆盖它，无法验证它是否是原件。

## 资料与兼容边界

工具按勾选内容修改 DLL、组件日志配置或当前用户的指定启动项，**不会迁移、合并、删除或读取账号资料**。启用资料目录补丁后，客户端使用安装根目录的 `UserData`；系统 AppData 中的旧资料仍然保留。如果两处都有资料，请自行确认要使用的一套并备份。恢复只撤销对应功能的受管理修改，不会把 `UserData` 自动迁回 AppData。

资料目录选项只重定向该属性；日志选项另行调整可识别的配置，启动项选项只处理当前用户所属安装的 Run 值。它们不能保证所有临时文件、注册表、系统服务、资源管理器集成和其他组件都完全便携。跨电脑保留登录状态也可能受系统凭据保护、服务器校验等影响。原生驱动服务 `NutstoreDriverSvc` 不作为 DLL 占用组件阻止操作。

遇到程序集名、方法签名不匹配或强名称签名时拒绝修改，不尝试绕过签名。修改 DLL 也可能影响软件自身完整性校验；请保留原件，并在升级后重新检查兼容性。

## 构建与自动发布

在安装了 .NET Framework 4.8 开发工具的 Visual Studio/MSBuild 中构建解决方案，NuGet 依赖见 `packages.config`，不新增依赖。项目也支持使用 Windows Framework C# 编译器和已还原的包构建：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

构建脚本优先使用 MSBuild，本机没有 MSBuild 时使用已有的 Windows Framework C# 编译器。输出为 `artifacts/NutstoreAutoPatch-local-win-anycpu.zip` 及 `artifacts/SHA256SUMS.txt`。

GitHub Actions 在 push 和 pull request 时构建、运行测试并上传二进制产物；手动 `workflow_dispatch` 仅构建测试，不发布 Release。推送 `v*` 标签时自动生成 GitHub Release，附加包含 EXE、Mono.Cecil 和使用说明的 ZIP 及 SHA256 校验文件。含 `-` 的版本标签发布为预发行版本。工作流使用 GitHub 自动提供的 `GITHUB_TOKEN`，发布任务申请 `contents: write`，无需额外配置个人令牌。运行发布包仍需系统 .NET Framework 4.8 运行时，不包含 .NET SDK。

发布流程由 `.github/workflows/` 和 `scripts/` 中的脚本维护。发布前应先让构建和回归测试通过，再推送版本标签。
