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

namespace NLog.UnitTests.LayoutRenderers
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading;
    using NLog.Config;
    using NLog.LayoutRenderers;
    using NLog.Layouts;
    using NLog.Targets;
    using Xunit;

    /// <summary>
    /// Tests for the explicit <see cref="ScopeContextSnapshot"/> that seals thread and scope
    /// properties into a <see cref="LogEventInfo"/> at enqueue time, so late layout evaluation
    /// on another thread uses the enqueued context instead of the current thread context.
    /// </summary>
    public class ScopeContextSnapshotTests : NLogTestBase
    {
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
            private readonly List<Exception> _errors = new List<Exception>();
            private readonly object _syncRoot = new object();
            private int _enqueuedCount;

            public InMemoryQueuingTarget()
            {
                _worker = new Thread(ProcessQueue) { IsBackground = true, Name = "InMemoryQueuingWorker" };
                _worker.Start();
            }

            /// <summary>
            /// Gets the number of log events enqueued.
            /// </summary>
            public int EnqueuedCount => Volatile.Read(ref _enqueuedCount);

            /// <summary>
            /// Gets the rendered output received by the background worker.
            /// </summary>
            public IReadOnlyList<string> Received
            {
                get { lock (_syncRoot) return _received.ToArray(); }
            }

            /// <summary>
            /// Gets the exceptions captured while rendering on the background worker.
            /// </summary>
            public IReadOnlyList<Exception> Errors
            {
                get { lock (_syncRoot) return _errors.ToArray(); }
            }

            /// <inheritdoc/>
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
                    try
                    {
                        var rendered = Layout.Render(logEvent);
                        lock (_syncRoot)
                        {
                            _received.Add(rendered);
                        }
                    }
                    catch (Exception exception)
                    {
                        lock (_syncRoot)
                        {
                            _errors.Add(exception);
                        }
                    }
                }
            }

            /// <inheritdoc/>
            protected override void CloseTarget()
            {
                _queue.CompleteAdding();
            }
        }

        /// <summary>
        /// Layout renderer that counts how many times its <see cref="Append"/> is called.
        /// </summary>
        [LayoutRenderer("counting")]
        public sealed class CountingLayoutRenderer : LayoutRenderer
        {
            private int _count;

            /// <summary>
            /// Gets the number of times <see cref="Append"/> has been called.
            /// </summary>
            public int Count => Volatile.Read(ref _count);

            /// <inheritdoc/>
            protected override void Append(StringBuilder builder, LogEventInfo logEvent)
            {
                Interlocked.Increment(ref _count);
                builder.Append("count");
            }
        }

        /// <summary>
        /// Layout renderer that always throws, to verify the exception carries the renderer source.
        /// </summary>
        [LayoutRenderer("throwing")]
        public sealed class ThrowingLayoutRenderer : LayoutRenderer
        {
            /// <inheritdoc/>
            protected override void Append(StringBuilder builder, LogEventInfo logEvent)
            {
                throw new InvalidOperationException("Renderer failed");
            }
        }

        private static LogFactory CreateLogFactory(string layout, out InMemoryQueuingTarget target)
        {
            var logFactory = new LogFactory();
            logFactory.Setup().SetupExtensions(ext => ext
                .RegisterLayoutRenderer<CountingLayoutRenderer>("counting")
                .RegisterLayoutRenderer<ThrowingLayoutRenderer>("throwing"));

            target = new InMemoryQueuingTarget { Layout = layout };
            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Trace, LogLevel.Fatal, target);
            logFactory.Configuration = config;
            return logFactory;
        }

        private static void WaitForProcessing(InMemoryQueuingTarget target, int expectedCount, int timeoutMilliseconds = 5000)
        {
            var deadline = Environment.TickCount + timeoutMilliseconds;
            while (target.EnqueuedCount < expectedCount || target.Received.Count + target.Errors.Count < expectedCount)
            {
                if (Environment.TickCount > deadline)
                    throw new TimeoutException("Timed out waiting for " + expectedCount + " events. Enqueued=" + target.EnqueuedCount + ", Received=" + target.Received.Count + ", Errors=" + target.Errors.Count);
                Thread.Sleep(10);
            }
        }

        [Fact]
        public void ScopeSnapshot_AfterEnqueue_UsesEnqueuedValue()
        {
            // Arrange
            ScopeContext.Clear();
            var logFactory = CreateLogFactory("${scopeproperty:item=myitem} ${message}", out var target);
            var logger = logFactory.GetCurrentClassLogger();

            // Act
            using (logger.PushScopeProperty("myitem", "enqueued"))
            {
                logger.Debug("first");
            }

            // Change the scope value after enqueue - the event must keep the enqueued value.
            using (logger.PushScopeProperty("myitem", "changed"))
            {
                logger.Debug("second");
            }

            WaitForProcessing(target, 2);

            // Assert
            Assert.Equal(2, target.Received.Count);
            Assert.Equal("enqueued first", target.Received[0]);
            Assert.Equal("changed second", target.Received[1]);
        }

        [Fact]
        public void ScopeSnapshot_TwoThreads_DoNotMix()
        {
            // Arrange
            ScopeContext.Clear();
            var logFactory = CreateLogFactory("${scopeproperty:item=thread} ${message}", out var target);
            var logger = logFactory.GetCurrentClassLogger();
            const int messagesPerThread = 20;

            // Act
            var thread1 = new Thread(() =>
            {
                using (logger.PushScopeProperty("thread", "thread1"))
                {
                    for (int i = 0; i < messagesPerThread; ++i)
                    {
                        logger.Debug("t1-" + i);
                    }
                }
            });
            var thread2 = new Thread(() =>
            {
                using (logger.PushScopeProperty("thread", "thread2"))
                {
                    for (int i = 0; i < messagesPerThread; ++i)
                    {
                        logger.Debug("t2-" + i);
                    }
                }
            });

            thread1.Start();
            thread2.Start();
            thread1.Join();
            thread2.Join();

            WaitForProcessing(target, messagesPerThread * 2);

            // Assert - no cross-thread context bleed
            Assert.Equal(messagesPerThread * 2, target.Received.Count);
            foreach (var received in target.Received)
            {
                var messagePart = received.Substring(received.IndexOf(' '));
                Assert.DoesNotContain("thread1", messagePart);
                Assert.DoesNotContain("thread2", messagePart);
            }

            var thread1Count = 0;
            var thread2Count = 0;
            foreach (var received in target.Received)
            {
                if (received.StartsWith("thread1 "))
                    ++thread1Count;
                else if (received.StartsWith("thread2 "))
                    ++thread2Count;
            }
            Assert.Equal(messagesPerThread, thread1Count);
            Assert.Equal(messagesPerThread, thread2Count);
        }

        [Fact]
        public void Renderer_CalledOnce_PerEvent()
        {
            // Arrange
            ScopeContext.Clear();
            var logFactory = CreateLogFactory("${counting} ${message}", out var target);
            var logger = logFactory.GetCurrentClassLogger();

            // Act
            logger.Debug("one");
            logger.Debug("two");
            logger.Debug("three");

            WaitForProcessing(target, 3);

            // Assert - the counting renderer is invoked exactly once per event
            Assert.Equal(3, target.Received.Count);
            Assert.Equal("count one", target.Received[0]);
            Assert.Equal("count two", target.Received[1]);
            Assert.Equal("count three", target.Received[2]);
        }

        [Fact]
        public void Renderer_Exception_HasRendererSource()
        {
            // Arrange
            ScopeContext.Clear();
            var logFactory = CreateLogFactory("${throwing} ${message}", out var target);
            var logger = logFactory.GetCurrentClassLogger();
            var originalThrowExceptions = LogManager.ThrowExceptions;
            LogManager.ThrowExceptions = true;

            try
            {
                // Act
                logger.Debug("boom");

                WaitForProcessing(target, 1);

                // Assert - the exception carries the renderer source
                Assert.Single(target.Errors);
                var exception = Assert.IsType<NLogRuntimeException>(target.Errors[0]);
                Assert.Contains("throwing", exception.Message);
                Assert.IsType<InvalidOperationException>(exception.InnerException);
            }
            finally
            {
                LogManager.ThrowExceptions = originalThrowExceptions;
            }
        }

        [Fact]
        public void ScopeSnapshot_ThreeScopes_UsesEnqueuedValues()
        {
            // Arrange
            ScopeContext.Clear();
            var logFactory = CreateLogFactory("${scopeproperty:item=outer}|${scopeproperty:item=middle}|${scopeproperty:item=inner} ${message}", out var target);
            var logger = logFactory.GetCurrentClassLogger();

            // Act - three nested scopes, then change values after enqueue
            using (logger.PushScopeProperty("outer", "outer-enqueued"))
            using (logger.PushScopeProperty("middle", "middle-enqueued"))
            using (logger.PushScopeProperty("inner", "inner-enqueued"))
            {
                logger.Debug("nested");
            }

            // Change scope values after enqueue - the event must keep the enqueued values.
            using (logger.PushScopeProperty("outer", "outer-changed"))
            using (logger.PushScopeProperty("middle", "middle-changed"))
            using (logger.PushScopeProperty("inner", "inner-changed"))
            {
                logger.Debug("nested2");
            }

            WaitForProcessing(target, 2);

            // Assert
            Assert.Equal(2, target.Received.Count);
            Assert.Equal("outer-enqueued|middle-enqueued|inner-enqueued nested", target.Received[0]);
            Assert.Equal("outer-changed|middle-changed|inner-changed nested2", target.Received[1]);
        }
    }
}
