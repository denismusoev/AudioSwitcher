using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;
using AudioSwitcher.Core;

namespace AudioSwitcher.Platform;

public sealed class ProgramProcessService
{
    public bool UsesNormalClose(ProcessIdentity identity)
    {
        using var handle = ProgramNative.OpenProcess(ProgramNative.Query, false, identity.Pid);
        if (handle.IsInvalid) return false;
        try { return ProgramNative.Identity(handle, identity.Pid) == identity && IsExplorer(handle); }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
    private static bool IsExplorer(SafeProcessHandle handle) => string.Equals(ProgramNative.Image(handle),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase);

    private static async Task<ProgramResult> CloseExplorerWindowsAsync(ProcessIdentity identity, IReadOnlyList<RunningProgram> programs, CancellationToken token)
    {
        var targets = programs.Where(p => p.Identity == identity).SelectMany(p => p.Windows).Where(w =>
        {
            if (!ProgramNative.WindowMatches(w)) return false;
            var name = new StringBuilder(256); ProgramNative.GetClassName(w.Handle, name, name.Capacity);
            return name.ToString() is "CabinetWClass" or "ExploreWClass";
        }).ToArray();
        if (targets.Length == 0) return new(ProgramResultCode.AlreadyExited, "Окна Проводника уже закрыты");
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            if (ProgramNative.WindowMatches(target) && !ProgramNative.PostMessage(target.Handle, 0x0010, 0, 0))
                return ProgramNative.Error(Marshal.GetLastWin32Error());
        }
        for (int i = 0; i < 30; i++)
        {
            token.ThrowIfCancellationRequested();
            if (targets.All(w => !ProgramNative.WindowMatches(w))) return new(ProgramResultCode.Success, "Окна Проводника закрыты");
            await Task.Delay(100, token);
        }
        return new(ProgramResultCode.TimedOut, "Проводник пока не подтвердил закрытие окон");
    }
    public async Task<ProgramResult> TerminateAsync(ProcessIdentity identity, CancellationToken cancellationToken = default)
    {
        if (identity.Pid == Environment.ProcessId || identity.Pid <= 0) return new(ProgramResultCode.Unsupported, "Этой программой управлять нельзя");
        return await Task.Run(async () => {
            using var handle = ProgramNative.OpenProcess(ProgramNative.Query | ProgramNative.Synchronize, false, identity.Pid);
            if (handle.IsInvalid) return ProgramNative.Error(Marshal.GetLastWin32Error());
            if (ProgramNative.Identity(handle, identity.Pid) != identity) return new ProgramResult(ProgramResultCode.TargetChanged, "Выбранный экземпляр программы изменился");
            if (ProgramNative.WaitForSingleObject(handle, 0) == 0) return new ProgramResult(ProgramResultCode.AlreadyExited, "Программа уже закрыта");
            if (!ProgramNative.SameUserSession(handle, identity.Pid)) return new ProgramResult(ProgramResultCode.AccessDenied, "Программа другого пользователя недоступна");
            var programs = await new ProgramWindowService().GetProgramsAsync(cancellationToken);
            if (!programs.Any(p => p.Identity == identity)) return new ProgramResult(ProgramResultCode.TargetChanged, "Пользовательское окно программы больше недоступно");
            // Explorer hosts the taskbar and desktop too. Never terminate this
            // process, even if a caller bypasses the user interface's label.
            if (IsExplorer(handle)) return await CloseExplorerWindowsAsync(identity, programs, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var terminationHandle = ProgramNative.OpenProcess(ProgramNative.Query | ProgramNative.Synchronize | ProgramNative.Terminate, false, identity.Pid);
            if (terminationHandle.IsInvalid) return ProgramNative.Error(Marshal.GetLastWin32Error());
            if (ProgramNative.Identity(terminationHandle, identity.Pid) != identity || IsExplorer(terminationHandle))
                return new ProgramResult(ProgramResultCode.TargetChanged, "Выбранный экземпляр программы изменился");
            if (!ProgramNative.TerminateProcess(terminationHandle, 1)) return ProgramNative.Error(Marshal.GetLastWin32Error());
            uint wait = ProgramNative.WaitForSingleObject(terminationHandle, 3000);
            return wait switch
            {
                0 => new ProgramResult(ProgramResultCode.Success, "Программа завершена"),
                258 => new ProgramResult(ProgramResultCode.TimedOut, "Завершение пока не подтверждено"),
                _ => ProgramNative.Error(Marshal.GetLastWin32Error())
            };
        }, cancellationToken);
    }
}
