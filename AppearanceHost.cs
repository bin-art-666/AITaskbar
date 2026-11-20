using System.Security.Principal;
using Forms = System.Windows.Forms;

namespace AITaskbar;

internal sealed class AppearanceHost
{
    private readonly object gate = new();
    private TaskCompletionSource? ready, finished;
    private TaskCompletionSource? stopFailure;
    private LumaBar.TrayApp? context;
    private bool stopRequested;
    private bool formsInitialized;
    private LumaBar.BarAppearance? barAppearance;
    internal LumaBar.BarAppearance? CurrentBarAppearance => Volatile.Read(ref barAppearance);
    internal event Action? BarAppearanceChanged;

    private void UpdateBarAppearance(LumaBar.BarAppearance? value)
    {
        if (Interlocked.Exchange(ref barAppearance, value) != value)
            BarAppearanceChanged?.Invoke();
    }

    public Task ShowAsync(string dataDirectory, bool hidden, bool monitorOnly = false)
    {
        lock (gate)
        {
            if (context is not null)
            {
                if (!hidden) context.ShowFromHost();
                return Task.CompletedTask;
            }
            if (ready is not null) return ready.Task;

            stopRequested = false;
            stopFailure = null;
            ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var startup = ready;
            var completion = finished;
            var worker = new Thread(() => Run(dataDirectory, hidden, monitorOnly, startup, completion))
            {
                IsBackground = true,
                Name = "AITaskbar appearance"
            };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            return startup.Task;
        }
    }

    public Task StopAsync()
    {
        lock (gate)
        {
            if (finished is null) return Task.CompletedTask;
            stopRequested = true;
            stopFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
            RequestAppearanceStop();
            return AwaitStop(finished.Task, stopFailure.Task);
        }
    }

    private static async Task AwaitStop(Task finished, Task failed)
    {
        var outcome = await Task.WhenAny(finished, failed);
        await outcome;
    }

    private void RequestAppearanceStop()
    {
        var failure = stopFailure!;
        context?.StopFromHost(error => failure.TrySetException(error));
    }

    private void Run(string dataDirectory, bool hidden, bool monitorOnly, TaskCompletionSource startup, TaskCompletionSource completion)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var mutexPrefix = monitorOnly ? @"Local\AITaskbar-LumaBar-Verification-" : @"Local\LumaBar-";
            using var owner = new Mutex(false, mutexPrefix + identity.User!.Value);
            bool acquired;
            try { acquired = owner.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
                throw new InvalidOperationException("LumaBar 已在运行。请先从原版托盘选择「退出并恢复」，再启用 AITaskbar 的外观模块。");

            try
            {
                if (!formsInitialized)
                {
                    Forms.Application.EnableVisualStyles();
                    Forms.Application.SetCompatibleTextRenderingDefault(false);
                    formsInitialized = true;
                }
                Directory.CreateDirectory(dataDirectory);
                LumaBar.Files.Root = dataDirectory;
                LumaBar.IThemeStore store = monitorOnly ? new VerificationThemeStore() : new LumaBar.RegistryThemeStore();
                var controller = new LumaBar.ThemeController(store, new LumaBar.FileJournal());
                var settings = LumaBar.Files.ReadSettings();
                controller.Restore();
                using var app = new LumaBar.TrayApp(settings, controller, monitorOnly, startHidden: hidden,
                    publishBarAppearance: UpdateBarAppearance);
                lock (gate) context = app;
                Forms.Application.Idle += OnIdle;
                try { Forms.Application.Run(app); }
                finally { Forms.Application.Idle -= OnIdle; }
            }
            finally { owner.ReleaseMutex(); }
        }
        catch (Exception error)
        {
            if (!startup.TrySetException(error)) LumaBar.Files.Log(error.ToString());
        }
        finally
        {
            UpdateBarAppearance(null);
            lock (gate)
            {
                context = null;
                ready = null;
                finished = null;
            }
            startup.TrySetException(new InvalidOperationException("外观模块在启动完成前退出。"));
            completion.TrySetResult();
        }

        void OnIdle(object? sender, EventArgs args)
        {
            Forms.Application.Idle -= OnIdle;
            startup.TrySetResult();
            lock (gate)
                if (stopRequested) RequestAppearanceStop();
        }
    }

    private sealed class VerificationThemeStore : LumaBar.IThemeStore
    {
        public LumaBar.ThemeValue Read() => new(true, 1);
        public void Refresh() { }
        public void Write(LumaBar.ThemeValue value) => throw new InvalidOperationException("Appearance verification cannot write the system theme.");
    }
}
