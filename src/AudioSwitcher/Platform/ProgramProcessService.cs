using System.Runtime.InteropServices;
using AudioSwitcher.Core;

namespace AudioSwitcher.Platform;

public sealed class ProgramProcessService
{
    public async Task<ProgramResult> TerminateAsync(ProcessIdentity identity, CancellationToken cancellationToken = default)
    {
        if (identity.Pid == Environment.ProcessId || identity.Pid <= 0) return new(ProgramResultCode.Unsupported, "Этой программой управлять нельзя");
        return await Task.Run(async () => {
            using var handle = ProgramNative.OpenProcess(ProgramNative.Query | ProgramNative.Synchronize | ProgramNative.Terminate, false, identity.Pid);
            if (handle.IsInvalid) return ProgramNative.Error(Marshal.GetLastWin32Error());
            if (ProgramNative.Identity(handle, identity.Pid) != identity) return new ProgramResult(ProgramResultCode.TargetChanged, "Выбранный экземпляр программы изменился");
            if (ProgramNative.WaitForSingleObject(handle, 0) == 0) return new ProgramResult(ProgramResultCode.AlreadyExited, "Программа уже закрыта");
            if (!ProgramNative.SameUserSession(handle, identity.Pid)) return new ProgramResult(ProgramResultCode.AccessDenied, "Программа другого пользователя недоступна");
            var programs = await new ProgramWindowService().GetProgramsAsync(cancellationToken);
            if (!programs.Any(p => p.Identity == identity)) return new ProgramResult(ProgramResultCode.TargetChanged, "Пользовательское окно программы больше недоступно");
            cancellationToken.ThrowIfCancellationRequested();
            // The same handle was verified above and remains held during termination.
            if (!ProgramNative.TerminateProcess(handle, 1)) return ProgramNative.Error(Marshal.GetLastWin32Error());
            uint wait = ProgramNative.WaitForSingleObject(handle, 3000);
            return wait switch
            {
                0 => new ProgramResult(ProgramResultCode.Success, "Программа завершена"),
                258 => new ProgramResult(ProgramResultCode.TimedOut, "Завершение пока не подтверждено"),
                _ => ProgramNative.Error(Marshal.GetLastWin32Error())
            };
        }, cancellationToken);
    }
}
