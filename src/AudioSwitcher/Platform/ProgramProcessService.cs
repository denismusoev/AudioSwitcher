using System.Runtime.InteropServices;
using AudioSwitcher.Core;

namespace AudioSwitcher.Platform;

public sealed class ProgramProcessService
{
    public Task<ProgramResult> CloseAsync(WindowTarget target, CancellationToken cancellationToken = default) =>
        CloseAllAsync([target], cancellationToken);

    public Task<ProgramResult> CloseAllAsync(IReadOnlyList<WindowTarget> targets, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var unique = targets.DistinctBy(target => target.Handle).ToArray();
            if (unique.Length == 0) return new ProgramResult(ProgramResultCode.AlreadyExited, "Окна уже закрыты");

            foreach (var target in unique)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var process = ProgramNative.OpenProcess(ProgramNative.Query | ProgramNative.Synchronize, false, target.Process.Pid);
                if (process.IsInvalid) return ProgramNative.Error(Marshal.GetLastWin32Error());
                if (ProgramNative.Identity(process, target.Process.Pid) != target.Process || !ProgramNative.WindowMatches(target))
                    return new ProgramResult(ProgramResultCode.TargetChanged, "Выбранное окно изменилось или уже закрыто");
                if (!ProgramNative.SameUserSession(process, target.Process.Pid))
                    return new ProgramResult(ProgramResultCode.AccessDenied, "Окно другого пользователя недоступно");
            }

            foreach (var target in unique)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ProgramNative.WindowMatches(target) && !ProgramNative.PostMessage(target.Handle, 0x0010, 0, 0))
                    return ProgramNative.Error(Marshal.GetLastWin32Error());
            }

            for (int i = 0; i < 30; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (unique.All(target => !ProgramNative.WindowMatches(target)))
                    return new ProgramResult(ProgramResultCode.Success, unique.Length == 1 ? "Окно закрыто" : "Все окна закрыты");
                await Task.Delay(100, cancellationToken);
            }

            int remaining = unique.Count(ProgramNative.WindowMatches);
            return new ProgramResult(ProgramResultCode.TimedOut,
                remaining == 1 ? "Окно пока не подтвердило закрытие" : $"Не закрыто окон: {remaining}");
        }, cancellationToken);
}
