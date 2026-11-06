using NaxExplorer.Core;
using NaxExplorer.Search;
using NaxExplorer.Services;

namespace NaxExplorer.Tests;

internal static class SearchServiceTests
{
    public static IEnumerable<(string, string, Action)> All()
    {
        yield return ("stopping_search_cancels_a_queued_start", "Turning AI search off before its startup worker runs never starts indexing or creates an index afterwards.", () =>
        {
            RequireModel();
            var scheduler = new QueuedScheduler();
            using var service = new SearchService(scheduler);
            service.Start(new AppSettings { AiSearch = true });
            service.Apply(new AppSettings { AiSearch = false }, restart: false);
            string originalRoot = DataFolders.Root;
            string temporaryRoot = Fixtures.TempFolder("cancelled-search-start");
            try
            {
                DataFolders.Root = temporaryRoot;
                scheduler.RunNext();
                Check.That(!service.IsRunning, "cancelled startup ran");
                Check.That(!Directory.Exists(DataFolders.Index), "cancelled startup created an index");
            }
            finally
            {
                service.Stop();
                DataFolders.Root = originalRoot;
            }
        });

        yield return ("a_later_search_start_supersedes_queued_old_settings", "When settings change before startup runs, the old worker does nothing and only the newest indexed folders are used.", () =>
        {
            RequireModel();
            string oldFolder = Fixtures.TempFolder("old-search-root");
            string newFolder = Fixtures.TempFolder("new-search-root");
            var scheduler = new QueuedScheduler();
            using var service = new SearchService(scheduler);
            service.Start(new AppSettings { AiSearch = true, IndexedFolders = [oldFolder] });
            service.Apply(new AppSettings { AiSearch = true, IndexedFolders = [newFolder], PauseIndexingInFullScreen = false, PauseIndexingOnBattery = false }, restart: false);
            string originalRoot = DataFolders.Root;
            try
            {
                DataFolders.Root = Fixtures.TempFolder("latest-search-start");
                scheduler.RunNext();
                Check.That(!service.IsRunning, "outdated startup ran");
                scheduler.RunNext();
                Check.That(service.IsRunning, "latest startup did not run");
                Check.Equal(Coverage.Full, service.CoverageOf(newFolder));
                Check.Equal(Coverage.None, service.CoverageOf(oldFolder));
            }
            finally
            {
                service.Stop();
                DataFolders.Root = originalRoot;
            }
        });
    }

    private static void RequireModel()
    {
        if (!EmbeddingModel.IsInstalled()) throw new SkipException("the AI model is not installed; no download was started");
    }

    private sealed class QueuedScheduler : TaskScheduler
    {
        private readonly Queue<Task> _tasks = new();
        protected override IEnumerable<Task>? GetScheduledTasks() => _tasks.ToArray();
        protected override void QueueTask(Task task) => _tasks.Enqueue(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        public void RunNext()
        {
            Check.That(_tasks.TryDequeue(out Task? task), "no startup was queued");
            Check.That(TryExecuteTask(task!), "startup could not run");
            task!.GetAwaiter().GetResult();
        }
    }
}
