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

    internal Task<T> EditAsync<T>(Func<LumaBar.TrayApp, T> edit)
    {
        lock (gate)
            return context?.ReadOrEditFromHost(edit).WaitAsync(TimeSpan.FromSeconds(10))
                ?? Task.FromException<T>(new InvalidOperationException("任务栏外观尚未启动。"));
    }

    private void UpdateBarAppearance(LumaBar.BarAppearance? value)
    {
        var previous = Interlocked.Exchange(ref barAppearance, value);
        if (previous is null && value is not null)
            LumaBar.Files.Log($"AITaskbar preview appearance published: material={value.Material}, mouseGlow={value.MouseGlow}.");
        if (previous != value)
            BarAppearanceChanged?.Invoke();
    }

    public Task ShowAsync(string dataDirectory, bool hidden, bool verification = false)
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
            var worker = new Thread(() => Run(dataDirectory, hidden, verification, startup, completion))
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

    private void Run(string dataDirectory, bool hidden, bool verification, TaskCompletionSource startup, TaskCompletionSource completion)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var mutexPrefix = verification ? @"Local\AITaskbar-LumaBar-Verification-" : @"Local\AITaskbar-LumaBar-Preview-";
            using var owner = new Mutex(false, mutexPrefix + identity.User!.Value);
            bool acquired;
            try { acquired = owner.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
                throw new InvalidOperationException("AITaskbar 外观模块已在运行。");

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
                var state = new PreviewThemeState();
                var controller = new LumaBar.ThemeController(state, state);
                var settings = LumaBar.Files.ReadSettings();
                using var app = new LumaBar.TrayApp(settings, controller, monitorOnly: false, startHidden: hidden,
                    publishBarAppearance: UpdateBarAppearance, previewOnly: true);
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

    private sealed class PreviewThemeState : LumaBar.IThemeStore, LumaBar.ISessionJournal
    {
        private LumaBar.ThemeValue current = new LumaBar.RegistryThemeStore().Read();
        private LumaBar.SessionRecord? session;
        public LumaBar.ThemeValue Read() => current;
        public void Refresh() { }
        public void Write(LumaBar.ThemeValue value) => current = value;
        public LumaBar.SessionRecord Load() => session!;
        public void Save(LumaBar.SessionRecord record) => session = record;
        public void Delete() => session = null;
    }
}
