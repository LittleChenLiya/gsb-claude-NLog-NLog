//
// Copyright (c) 2004-2024 Jaroslaw Kowalski <jaak@jkowalski.net>, Kim Christensen, Julian Verdurmen
//
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions
// are met:
//
// * Redistributions of source code must retain the above copyright notice,
//   this list of conditions and the following disclaimer.
//
// * Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution.
//
// * Neither the name of Jaroslaw Kowalski nor the names of its
//   contributors may be used to endorse or promote products derived from this
//   software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
// ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF
// THE POSSIBILITY OF SUCH DAMAGE.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using NLog.Common;
using NLog.Config;
using NLog.LayoutRenderers;
using NLog.Targets;
using Xunit;

namespace NLog.UnitTests.LayoutRenderers
{
    public class LayoutSnapshotTests : NLogTestBase
    {
        /// <summary>
        /// In-memory queue fixture. Seals the layout-evaluation snapshot into every
        /// logging event at enqueue-time, and performs the actual layout rendering
        /// later (deferred), when <see cref="Flush"/> is called.
        /// </summary>
        private sealed class InMemoryQueueTarget : TargetWithLayout
        {
            private readonly Queue<AsyncLogEventInfo> _pending = new Queue<AsyncLogEventInfo>();
            private readonly List<LogEventInfo> _events = new List<LogEventInfo>();
            private readonly List<string> _rendered = new List<string>();
            private readonly List<Exception> _failures = new List<Exception>();

            public IReadOnlyList<LogEventInfo> Events
            {
                get { lock (_pending) return _events.ToArray(); }
            }

            public IReadOnlyList<string> Rendered
            {
                get { lock (_pending) return _rendered.ToArray(); }
            }

            public IReadOnlyList<Exception> Failures
            {
                get { lock (_pending) return _failures.ToArray(); }
            }

            public int PendingCount
            {
                get { lock (_pending) return _pending.Count; }
            }

            protected override void Write(AsyncLogEventInfo logEvent)
            {
                logEvent.LogEvent.CaptureLayoutSnapshot();  // Seal thread + scope context at enqueue-time
                lock (_pending)
                {
                    _events.Add(logEvent.LogEvent);
                    _pending.Enqueue(logEvent);
                }
            }

            public void Flush()
            {
                for (;;)
                {
                    AsyncLogEventInfo next;
                    lock (_pending)
                    {
                        if (_pending.Count == 0)
                            return;
                        next = _pending.Dequeue();
                    }

                    Exception failure = null;
                    try
                    {
                        var output = Layout?.Render(next.LogEvent);
                        lock (_pending)
                            _rendered.Add(output);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        lock (_pending)
                            _failures.Add(ex);
                    }

                    next.Continuation(failure);
                }
            }
        }

        [LayoutRenderer("counting")]
        public sealed class CountingLayoutRenderer : LayoutRenderer
        {
            private static int _renderCount;

            public static int RenderCount => _renderCount;

            public static void ResetCounter() => Interlocked.Exchange(ref _renderCount, 0);

            protected override void Append(StringBuilder builder, LogEventInfo logEvent)
            {
                Interlocked.Increment(ref _renderCount);
                builder.Append("counted");
            }
        }

        public sealed class ExplodingPayload
        {
            public override string ToString() => throw new InvalidOperationException("Unknown objects must not be ToString-ed for reporting.");
        }

        private static LogFactory CreateLogFactory(InMemoryQueueTarget target, string layout, Action<ISetupExtensionsBuilder> registerExtensions = null)
        {
            var logFactory = new LogFactory();
            if (registerExtensions != null)
                logFactory.Setup().SetupExtensions(registerExtensions);

            var config = new LoggingConfiguration(logFactory);
            target.Name = "queue";
            target.Layout = layout;
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, target);
            logFactory.Configuration = config;
            return logFactory;
        }

        [Fact]
        public void ScopeProperty_StringValue_SealedAtEnqueue()
        {
            // Arrange
            var target = new InMemoryQueueTarget();
            var logFactory = CreateLogFactory(target, "${scopeproperty:requestId}");
            var logger = logFactory.GetLogger("LayoutSnapshotTests");

            // Act - enqueue inside one scope
            using (ScopeContext.PushProperty("requestId", "enqueue-value"))
            {
                logger.Info("first");
            }

            Assert.Equal(1, target.PendingCount);
            Assert.Empty(target.Rendered);

            // Change the ambient scope AFTER enqueue, before the queued event is rendered
            using (ScopeContext.PushProperty("requestId", "after-value"))
            {
                target.Flush();
            }

            // Assert - late evaluation used the sealed enqueue-time value, not the foreign ambient scope
            Assert.Equal("enqueue-value", Assert.Single(target.Rendered));
            Assert.Equal(Environment.CurrentManagedThreadId, target.Events[0].LayoutSnapshot?.ManagedThreadId);
        }

        [Fact]
        public void ScopeProperty_IntValue_TwoThreads_DoNotCross()
        {
            // Arrange
            var target = new InMemoryQueueTarget();
            var logFactory = CreateLogFactory(target, "${scopeproperty:workerId}|${threadname}|${threadid}");
            var logger = logFactory.GetLogger("LayoutSnapshotTests");

            // Act - two worker threads enqueue while holding their own scope
            var workers = new[]
            {
                new Thread(() =>
                {
                    Thread.CurrentThread.Name = "snapshot-worker-1";
                    using (ScopeContext.PushProperty("workerId", 1))
                        logger.Info("one");
                }),
                new Thread(() =>
                {
                    Thread.CurrentThread.Name = "snapshot-worker-2";
                    using (ScopeContext.PushProperty("workerId", 2))
                        logger.Info("two");
                }),
            };

            foreach (var worker in workers)
                worker.Start();
            foreach (var worker in workers)
                worker.Join();

            // Late evaluation happens here, on a foreign thread with an empty scope
            target.Flush();

            // Assert - each event kept its own sealed context, nothing crossed
            var rendered = target.Rendered;
            Assert.Equal(2, rendered.Count);

            var line1 = Assert.Single(rendered, line => line.StartsWith("1|", StringComparison.Ordinal));
            var line2 = Assert.Single(rendered, line => line.StartsWith("2|", StringComparison.Ordinal));

            var parts1 = line1.Split('|');
            var parts2 = line2.Split('|');
            Assert.Equal("snapshot-worker-1", parts1[1]);
            Assert.Equal("snapshot-worker-2", parts2[1]);
            Assert.NotEqual(parts1[2], parts2[2]);                                          // sealed on different threads
            Assert.NotEqual(Environment.CurrentManagedThreadId.ToString(), parts1[2]);      // not the flushing thread
            Assert.NotEqual(Environment.CurrentManagedThreadId.ToString(), parts2[2]);      // not the flushing thread
        }

        [Fact]
        public void ScopeProperty_UnknownObject_NotToStringForReport_AndExceptionHasRendererSource()
        {
            // Arrange
            var target = new InMemoryQueueTarget();
            var logFactory = CreateLogFactory(target, "payload=${scopeproperty:payload}");
            var logger = logFactory.GetLogger("LayoutSnapshotTests");

            // Act
            using (ScopeContext.PushProperty("payload", new ExplodingPayload()))
            {
                logger.Info("explodes when rendered");
            }

            // Assert - snapshot report describes the unknown object by type-name, without calling ToString()
            var snapshot = target.Events[0].LayoutSnapshot;
            Assert.NotNull(snapshot);
            var report = snapshot.ToString();   // Would throw, if it called ToString() on the payload
            Assert.Contains(nameof(ExplodingPayload), report);
            Assert.Contains("<", report);

            // Act - late evaluation fails, because rendering DOES format the payload value
            target.Flush();

            // Assert - the exception carries the source layout renderer
            var failure = Assert.Single(target.Failures);
            Assert.IsType<InvalidOperationException>(failure);
            Assert.Equal("Layout Renderer: ${scopeproperty}", failure.Data[LayoutRenderer.RendererSourceExceptionDataKey]);
        }

        [Fact]
        public void RendererInvocation_ExactlyOnce()
        {
            // Arrange
            CountingLayoutRenderer.ResetCounter();
            var target = new InMemoryQueueTarget();
            var logFactory = CreateLogFactory(target, "${counting} ${message}",
                ext => ext.RegisterLayoutRenderer<CountingLayoutRenderer>("counting"));
            var logger = logFactory.GetLogger("LayoutSnapshotTests");

            // Act
            using (ScopeContext.PushProperty("requestId", "once"))
            {
                logger.Info("count me");
            }

            // Assert - enqueue only sealed the snapshot, it did not render anything
            Assert.Equal(1, target.PendingCount);
            Assert.Equal(0, CountingLayoutRenderer.RenderCount);

            target.Flush();

            // Assert - actual layout evaluation happened exactly once
            Assert.Equal("counted count me", Assert.Single(target.Rendered));
            Assert.Equal(1, CountingLayoutRenderer.RenderCount);
        }

        [Fact]
        public void RendererPurity_DeclarationsAreSeparated()
        {
            // Pure renderers - output only depends on the LogEventInfo itself
            Assert.True(new LiteralLayoutRenderer().IsPure);
            Assert.True(new MessageLayoutRenderer().IsPure);
            Assert.True(new LevelLayoutRenderer().IsPure);
            Assert.True(new LoggerNameLayoutRenderer().IsPure);
            Assert.True(new NewLineLayoutRenderer().IsPure);
            Assert.True(new EventPropertiesLayoutRenderer().IsPure);

            // Impure renderers - read ambient thread / scope context, consult the sealed snapshot
            Assert.False(new ThreadIdLayoutRenderer().IsPure);
            Assert.False(new ThreadNameLayoutRenderer().IsPure);
            Assert.False(new ScopeContextPropertyLayoutRenderer().IsPure);
        }

        [Fact]
        public void SyncTarget_WithoutSnapshot_KeepsDefaultBehavior()
        {
            // Arrange - plain synchronous target, no snapshot sealing involved
            var logFactory = new LogFactory();
            var config = new LoggingConfiguration(logFactory);
            var memoryTarget = new MemoryTarget { Name = "memory", Layout = "${scopeproperty:requestId}" };
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, memoryTarget);
            logFactory.Configuration = config;
            var logger = logFactory.GetLogger("LayoutSnapshotTests");

            // Act
            using (ScopeContext.PushProperty("requestId", "ambient-value"))
            {
                logger.Info("sync write");
            }

            // Assert - synchronous targets still evaluate the ambient scope, and no snapshot was captured
            Assert.Equal("ambient-value", Assert.Single(memoryTarget.Logs));
        }
    }
}
