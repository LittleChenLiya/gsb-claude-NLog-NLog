using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using NLog;
using NLog.Config;
using NLog.LayoutRenderers;
using NLog.Targets;

namespace NLog
{
    /// <summary>
    /// Example demonstrating the explicit scope snapshot that seals thread and scope
    /// properties into a LogEventInfo at enqueue time.
    ///
    /// Three nested scopes are pushed, then the scope values are changed after enqueue.
    /// The queued in-memory target renders the layout on a background worker thread using
    /// the sealed snapshot, so the event keeps the enqueued values instead of reading
    /// the current thread's (changed) context.
    /// </summary>
    class Example
    {
        static void Main(string[] args)
        {
            // Register a custom in-memory queuing target that captures the explicit
            // scope snapshot at enqueue time and renders on a background worker.
            LogManager.Setup().SetupExtensions(ext => ext.RegisterTarget<InMemoryQueuingTarget>("InMemoryQueuing"));

            var target = new InMemoryQueuingTarget
            {
                Layout = "${scopeproperty:item=outer}|${scopeproperty:item=middle}|${scopeproperty:item=inner} ${message}"
            };

            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Trace, LogLevel.Fatal, target);
            LogManager.Configuration = config;

            Logger logger = LogManager.GetLogger("Example");

            // Push three nested scopes, then log while all three are active.
            using (logger.PushScopeProperty("outer", "outer-enqueued"))
            using (logger.PushScopeProperty("middle", "middle-enqueued"))
            using (logger.PushScopeProperty("inner", "inner-enqueued"))
            {
                logger.Debug("nested");
            }

            // Change the scope values after enqueue - the event must keep the enqueued values.
            using (logger.PushScopeProperty("outer", "outer-changed"))
            using (logger.PushScopeProperty("middle", "middle-changed"))
            using (logger.PushScopeProperty("inner", "inner-changed"))
            {
                logger.Debug("nested2");
            }

            // Wait for the background worker to process the queued events.
            target.WaitForProcessing(expectedCount: 2);

            foreach (var received in target.Received)
            {
                Console.WriteLine(received);
            }
            // Expected output:
            //   outer-enqueued|middle-enqueued|inner-enqueued nested
            //   outer-changed|middle-changed|inner-changed nested2
        }
    }

    /// <summary>
    /// In-memory queuing target fixture. Captures the explicit scope snapshot at enqueue
    /// time and renders the layout on a background worker thread.
    /// </summary>
    [Target("InMemoryQueuing")]
    public sealed class InMemoryQueuingTarget : TargetWithLayout
    {
        private readonly BlockingCollection<LogEventInfo> _queue = new BlockingCollection<LogEventInfo>();
        private readonly Thread _worker;
        private readonly List<string> _received = new List<string>();
        private readonly object _syncRoot = new object();
        private int _enqueuedCount;

        public InMemoryQueuingTarget()
        {
            _worker = new Thread(ProcessQueue) { IsBackground = true, Name = "InMemoryQueuingWorker" };
            _worker.Start();
        }

        public int EnqueuedCount => Volatile.Read(ref _enqueuedCount);

        public IReadOnlyList<string> Received
        {
            get { lock (_syncRoot) return _received.ToArray(); }
        }

        public void WaitForProcessing(int expectedCount, int timeoutMilliseconds = 5000)
        {
            var deadline = Environment.TickCount + timeoutMilliseconds;
            while (EnqueuedCount < expectedCount)
            {
                if (Environment.TickCount > deadline)
                    throw new TimeoutException("Timed out waiting for " + expectedCount + " events.");
                Thread.Sleep(10);
            }
        }

        protected override void Write(LogEventInfo logEvent)
        {
            // Explicit snapshot: seal thread and scope properties into the event at enqueue time.
            logEvent.CaptureScopeContextSnapshot();
            Interlocked.Increment(ref _enqueuedCount);
            _queue.Add(logEvent);
        }

        private void ProcessQueue()
        {
            foreach (var logEvent in _queue.GetConsumingEnumerable())
            {
                var rendered = Layout.Render(logEvent);
                lock (_syncRoot)
                {
                    _received.Add(rendered);
                }
            }
        }

        protected override void CloseTarget()
        {
            _queue.CompleteAdding();
        }
    }
}
