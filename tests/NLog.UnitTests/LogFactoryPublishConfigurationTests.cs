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

namespace NLog.UnitTests
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using NLog.Common;
    using NLog.Config;
    using NLog.Targets;
    using Xunit;

    public class LogFactoryPublishConfigurationTests : NLogTestBase
    {
        private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

        [Fact]
        public void PublishConfiguration_BlockedOldWrite_OldReceiptStaysWithOldTarget()
        {
            var logFactory = new LogFactory();
            try
            {
                var oldTarget = new BlockingTarget { Name = "old" };
                var config1 = new LoggingConfiguration(logFactory);
                config1.AddRuleForAllLevels(oldTarget);
                logFactory.Configuration = config1; // Default reconfig API stays supported
                var logger = logFactory.GetLogger("A");

                // Start an old write that gets stuck inside the old target
                var writeDone = new ManualResetEventSlim(false);
                var writeThread = new Thread(() =>
                {
                    try { logger.Info("old-event"); }
                    catch { /* Avoid unhandled exceptions on background thread */ }
                    writeDone.Set();
                }) { IsBackground = true };
                writeThread.Start();
                Assert.True(oldTarget.WriteEntered.Wait(WaitTimeout), "Old write should be in-flight");

                // Publish new configuration while the old target-write is blocked
                var newTarget = new BlockingTarget { Name = "new" };
                newTarget.ReleaseWrite.Set();
                var config2 = new LoggingConfiguration(logFactory);
                config2.AddRuleForAllLevels(newTarget);
                var receipt = logFactory.PublishConfiguration(config2);

                Assert.True(receipt.Succeeded);
                Assert.Null(receipt.Error);
                Assert.Same(config2, receipt.ActivatedConfiguration);
                Assert.Same(config1, receipt.PreviousConfiguration);
                Assert.Same(config2, logFactory.Configuration);
                Assert.Equal(0, oldTarget.CloseCount);            // In-flight write => not closed early
                Assert.False(receipt.PreviousConfigurationClosed);

                // New events follow the new target-collection
                logger.Info("new-event");
                Assert.Contains("new-event", newTarget.Messages);

                // Release the blocked old write. The old receipt still belongs to the old target
                oldTarget.ReleaseWrite.Set();
                Assert.True(writeDone.Wait(WaitTimeout), "Old write should complete");
                Assert.True(receipt.WaitForPreviousConfigurationClosed(WaitTimeout), "Previous configuration should close after drain");
                Assert.Equal(1, oldTarget.CloseCount);            // Closed exactly once
                Assert.Equal(0, newTarget.CloseCount);
                Assert.Equal(0, config1.ActiveTargetWriteCount);
                Assert.Contains("old-event", oldTarget.Messages); // Old event stayed on the old target
                Assert.DoesNotContain("old-event", newTarget.Messages); // No mixing with the new configuration
            }
            finally
            {
                logFactory.Shutdown();
            }
        }

        [Fact]
        public void PublishConfiguration_FailingTarget_ActiveConfigurationUnchanged()
        {
            LogManager.ThrowExceptions = false; // Ensure all targets of the failed attempt are initialized, so they can be recycled
            var logFactory = new LogFactory();
            try
            {
                var activeTarget = new BlockingTarget { Name = "active" };
                activeTarget.ReleaseWrite.Set();
                var config1 = new LoggingConfiguration(logFactory);
                config1.AddRuleForAllLevels(activeTarget);
                logFactory.Configuration = config1;
                var logger = logFactory.GetLogger("A");

                var failingTarget = new FailingInitTarget { Name = "failing" };
                var collateralTarget = new BlockingTarget { Name = "collateral" };
                var config2 = new LoggingConfiguration(logFactory);
                config2.AddRuleForAllLevels(failingTarget);
                config2.AddRuleForAllLevels(collateralTarget);

                var receipt = logFactory.PublishConfiguration(config2);

                Assert.False(receipt.Succeeded);
                Assert.NotNull(receipt.Error);
                Assert.Null(receipt.ActivatedConfiguration);
                Assert.True(receipt.PreviousConfigurationClosed); // Nothing to retire
                Assert.Same(config1, logFactory.Configuration);     // Active collection unchanged
                Assert.Equal(1, failingTarget.InitializeAttempts);
                Assert.Equal(1, collateralTarget.CloseCount);       // Only this attempt's targets are recycled, once
                Assert.Equal(0, activeTarget.CloseCount);           // Active targets are not touched

                // Active configuration still works after the rejected publish
                logger.Info("still-active");
                Assert.Contains("still-active", activeTarget.Messages);
            }
            finally
            {
                logFactory.Shutdown();
            }
        }

        [Fact]
        public void PublishConfiguration_Flush_BindsToCallTimeCollection()
        {
            var logFactory = new LogFactory();
            try
            {
                var oldTarget = new BlockingTarget { Name = "old" };
                var config1 = new LoggingConfiguration(logFactory);
                config1.AddRuleForAllLevels(oldTarget);
                logFactory.Configuration = config1;
                var logger = logFactory.GetLogger("A");

                var writeDone = new ManualResetEventSlim(false);
                var writeThread = new Thread(() =>
                {
                    try { logger.Info("old-event"); }
                    catch { /* Avoid unhandled exceptions on background thread */ }
                    writeDone.Set();
                }) { IsBackground = true };
                writeThread.Start();
                Assert.True(oldTarget.WriteEntered.Wait(WaitTimeout));

                var newTarget = new BlockingTarget { Name = "new" };
                newTarget.ReleaseWrite.Set();
                var config2 = new LoggingConfiguration(logFactory);
                config2.AddRuleForAllLevels(newTarget);
                var receipt = logFactory.PublishConfiguration(config2);
                Assert.True(receipt.Succeeded);

                // Flush binds to the target-collection that is active at call-time,
                // and does not wait for the in-flight old write or for future new events
                var flushCompleted = new ManualResetEventSlim(false);
                Exception flushError = null;
                logFactory.Flush(ex => { flushError = ex; flushCompleted.Set(); }, TimeSpan.FromSeconds(30));
                Assert.True(flushCompleted.Wait(WaitTimeout), "Flush should not wait for in-flight old write or future events");
                Assert.Null(flushError);
                Assert.Equal(1, newTarget.FlushCount);  // Flushed the call-time collection
                Assert.Equal(0, oldTarget.FlushCount);  // Did not flush the retired collection

                oldTarget.ReleaseWrite.Set();
                Assert.True(writeDone.Wait(WaitTimeout));
                Assert.True(receipt.WaitForPreviousConfigurationClosed(WaitTimeout));
                Assert.Equal(1, oldTarget.CloseCount);
            }
            finally
            {
                logFactory.Shutdown();
            }
        }

        [Fact]
        public void PublishConfiguration_DoesNotAffectOtherFactories()
        {
            var factory1 = new LogFactory();
            var factory2 = new LogFactory();
            try
            {
                var target1 = new BlockingTarget { Name = "factory1-old" };
                target1.ReleaseWrite.Set();
                var config1 = new LoggingConfiguration(factory1);
                config1.AddRuleForAllLevels(target1);
                factory1.Configuration = config1;

                var target2 = new BlockingTarget { Name = "factory2" };
                target2.ReleaseWrite.Set();
                var config2 = new LoggingConfiguration(factory2);
                config2.AddRuleForAllLevels(target2);
                factory2.Configuration = config2;

                var target1b = new BlockingTarget { Name = "factory1-new" };
                target1b.ReleaseWrite.Set();
                var config1b = new LoggingConfiguration(factory1);
                config1b.AddRuleForAllLevels(target1b);

                var receipt = factory1.PublishConfiguration(config1b);

                Assert.True(receipt.Succeeded);
                Assert.True(receipt.WaitForPreviousConfigurationClosed(WaitTimeout));
                Assert.Equal(1, target1.CloseCount);            // Own old configuration closed once
                Assert.Same(config2, factory2.Configuration);   // Other factory configuration untouched
                Assert.Equal(0, target2.CloseCount);            // Other factory targets not closed

                factory2.GetLogger("B").Info("other-factory");
                Assert.Contains("other-factory", target2.Messages);
            }
            finally
            {
                factory1.Shutdown();
                factory2.Shutdown();
            }
        }

        [Fact]
        public void PublishConfiguration_NoPreviousConfiguration_ClosedReceipt()
        {
            var logFactory = new LogFactory();
            try
            {
                var target = new BlockingTarget { Name = "first" };
                target.ReleaseWrite.Set();
                var config = new LoggingConfiguration(logFactory);
                config.AddRuleForAllLevels(target);

                var receipt = logFactory.PublishConfiguration(config);

                Assert.True(receipt.Succeeded);
                Assert.Same(config, receipt.ActivatedConfiguration);
                Assert.Null(receipt.PreviousConfiguration);
                Assert.True(receipt.PreviousConfigurationClosed);
                Assert.Same(config, logFactory.Configuration);
            }
            finally
            {
                logFactory.Shutdown();
            }
        }

        private sealed class BlockingTarget : Target
        {
            public readonly ManualResetEventSlim WriteEntered = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim ReleaseWrite = new ManualResetEventSlim(false);
            private readonly List<string> _messages = new List<string>();
            public int InitializeCount;
            public int CloseCount;
            public int FlushCount;

            public string[] Messages
            {
                get { lock (_messages) return _messages.ToArray(); }
            }

            protected override void InitializeTarget()
            {
                Interlocked.Increment(ref InitializeCount);
                base.InitializeTarget();
            }

            protected override void Write(LogEventInfo logEvent)
            {
                WriteEntered.Set();
                ReleaseWrite.Wait(TimeSpan.FromSeconds(30));
                lock (_messages)
                    _messages.Add(logEvent.FormattedMessage);
            }

            protected override void CloseTarget()
            {
                Interlocked.Increment(ref CloseCount);
                base.CloseTarget();
            }

            protected override void FlushAsync(AsyncContinuation asyncContinuation)
            {
                Interlocked.Increment(ref FlushCount);
                base.FlushAsync(asyncContinuation);
            }
        }

        private sealed class FailingInitTarget : Target
        {
            public int InitializeAttempts;

            protected override void InitializeTarget()
            {
                Interlocked.Increment(ref InitializeAttempts);
                throw new InvalidOperationException("Simulated target initialization failure.");
            }
        }
    }
}
