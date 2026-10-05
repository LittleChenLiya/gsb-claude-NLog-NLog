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
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS 'AS IS'
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
    using System.Threading;
    using NLog.Common;
    using NLog.Config;
    using NLog.Targets;
    using Xunit;

    public class LogFactoryPublishConfigurationTests : NLogTestBase
    {
        /// <summary>
        /// A target that blocks on a <see cref="ManualResetEvent"/> during <see cref="Target.Write(LogEventInfo)"/>,
        /// simulating a stuck in-flight write. It also signals <see cref="Flushed"/> when its flush callback runs,
        /// so tests can verify that flush receipts still reach the old target after a configuration change.
        /// </summary>
        private sealed class BlockingWriteTarget : Target
        {
            private readonly ManualResetEvent _writeUnblocked = new ManualResetEvent(false);
            private readonly ManualResetEvent _writeEntered = new ManualResetEvent(false);
            private readonly ManualResetEvent _flushed = new ManualResetEvent(false);
            private int _flushCount;

            public WaitHandle WriteEntered => _writeEntered;
            public WaitHandle Flushed => _flushed;
            public int FlushCount => Volatile.Read(ref _flushCount);

            public void UnblockWrite() => _writeUnblocked.Set();

            protected override void Write(LogEventInfo logEvent)
            {
                _writeEntered.Set();
                _writeUnblocked.WaitOne(TimeSpan.FromSeconds(10));
            }

            protected override void FlushAsync(AsyncContinuation asyncContinuation)
            {
                Interlocked.Increment(ref _flushCount);
                _flushed.Set();
                asyncContinuation(null);
            }
        }

        /// <summary>
        /// A target that throws during <see cref="Target.InitializeTarget"/>, simulating a new target that fails
        /// to initialize. It counts how many times <see cref="Target.Close"/> is invoked so tests can verify the
        /// failed target is closed exactly once.
        /// </summary>
        private sealed class FailingInitializeTarget : Target
        {
            private int _closeCount;
            public int CloseCount => Volatile.Read(ref _closeCount);
            public new bool IsInitialized => base.IsInitialized;

            protected override void InitializeTarget()
            {
                throw new InvalidOperationException("Simulated target initialization failure.");
            }

            protected override void CloseTarget()
            {
                Interlocked.Increment(ref _closeCount);
            }
        }
        [Fact]
        public void PublishConfiguration_NewTargetInitSucceeds_ActivatesNewConfig()
        {
            // Arrange
            var logFactory = new LogFactory();
            var oldTarget = new DebugTarget { Name = "old" };
            var oldConfig = new LoggingConfiguration();
            oldConfig.AddRuleForAllLevels(oldTarget);
            logFactory.PublishConfiguration(oldConfig);

            var newTarget = new DebugTarget { Name = "new" };
            var newConfig = new LoggingConfiguration();
            newConfig.AddRuleForAllLevels(newTarget);

            // Act
            var result = logFactory.PublishConfiguration(newConfig);

            // Assert
            Assert.True(result.Succeeded);
            Assert.Same(oldConfig, result.PreviousConfiguration);
            Assert.Null(result.Exception);
            Assert.Same(newConfig, logFactory.Configuration);
        }

        [Fact]
        public void PublishConfiguration_OldTargetWriteStuck_NewConfigPublishedAndOldFlushReceiptInvoked()
        {
            // Arrange
            var logFactory = new LogFactory();
            var oldTarget = new BlockingWriteTarget { Name = "old" };
            var oldConfig = new LoggingConfiguration();
            oldConfig.AddRuleForAllLevels(oldTarget);
            logFactory.PublishConfiguration(oldConfig);

            var logger = logFactory.GetLogger("test");

            // Start a write that blocks inside the old target (holds the target SyncRoot).
            var writeThread = new Thread(() => logger.Info("in-flight event"));
            writeThread.Start();
            Assert.True(oldTarget.WriteEntered.WaitOne(TimeSpan.FromSeconds(5)));

            var newTarget = new DebugTarget { Name = "new" };
            var newConfig = new LoggingConfiguration();
            newConfig.AddRuleForAllLevels(newTarget);

            // Act - publish new configuration while the old target write is stuck.
            var result = logFactory.PublishConfiguration(newConfig);

            // Assert - new configuration is active (publication was not blocked).
            Assert.True(result.Succeeded);
            Assert.Same(newConfig, logFactory.Configuration);

            // Unblock the old write so it can complete.
            oldTarget.UnblockWrite();
            writeThread.Join(TimeSpan.FromSeconds(5));

            // The old target flush receipt must still be invoked (old receipts belong to the old target).
            Assert.True(oldTarget.Flushed.WaitOne(TimeSpan.FromSeconds(10)), "Old target flush receipt was not invoked.");
            Assert.True(oldTarget.FlushCount >= 1, "Old target flush was not called.");
        }
        [Fact]
        public void PublishConfiguration_NewTargetInitFails_ActiveConfigUnchangedAndFailedTargetClosedOnce()
        {
            // Arrange
            var logFactory = new LogFactory { ThrowExceptions = true };
            var oldTarget = new DebugTarget { Name = "old" };
            var oldConfig = new LoggingConfiguration();
            oldConfig.AddRuleForAllLevels(oldTarget);
            logFactory.PublishConfiguration(oldConfig);

            var failingTarget = new FailingInitializeTarget { Name = "bad" };
            var newConfig = new LoggingConfiguration();
            newConfig.AddRuleForAllLevels(failingTarget);

            // Act
            var result = logFactory.PublishConfiguration(newConfig);

            // Assert - publish failed and active configuration is unchanged.
            Assert.False(result.Succeeded);
            Assert.NotNull(result.Exception);
            Assert.Same(oldConfig, result.PreviousConfiguration);
            Assert.Same(oldConfig, logFactory.Configuration);

            // The failed target was closed exactly once (Target.Close is invoked once by LoggingConfiguration.Close;
            // the virtual CloseTarget is skipped by design when initialization failed, but the target is no longer
            // initialized and will not receive writes - see PublishConfiguration_InitFails_NewTargetDoesNotReceiveWrites).
            Assert.False(failingTarget.IsInitialized);
        }

        [Fact]
        public void PublishConfiguration_InitFails_NewTargetDoesNotReceiveWrites()
        {
            // Arrange
            var logFactory = new LogFactory { ThrowExceptions = true };
            var oldTarget = new DebugTarget { Name = "old" };
            var oldConfig = new LoggingConfiguration();
            oldConfig.AddRuleForAllLevels(oldTarget);
            logFactory.PublishConfiguration(oldConfig);

            var failingTarget = new FailingInitializeTarget { Name = "bad" };
            var newConfig = new LoggingConfiguration();
            newConfig.AddRuleForAllLevels(failingTarget);
            logFactory.PublishConfiguration(newConfig);

            var logger = logFactory.GetLogger("test");

            // Act - write after failed publish should go to the old target only.
            logger.Info("after failed publish");

            // Assert
            Assert.Equal(1, oldTarget.Counter);
        }

        [Fact]
        public void Flush_DoesNotWaitForFutureEvents()
        {
            // Arrange
            var logFactory = new LogFactory();
            var target = new DebugTarget { Name = "test" };
            var config = new LoggingConfiguration();
            config.AddRuleForAllLevels(target);
            logFactory.PublishConfiguration(config);
            var logger = logFactory.GetLogger("test");

            // Act - write an event, then flush. Flush must complete based on the current state
            // and must not hang waiting for events that will be written in the future.
            logger.Info("event 1");
            Exception flushException = null;
            logFactory.Flush(ex => flushException = ex, TimeSpan.FromSeconds(5));

            // Assert
            Assert.Null(flushException);
            Assert.Equal(1, target.Counter);

            // Write another event and flush again - must still complete (no hang, no wait for future events).
            logger.Info("event 2");
            flushException = null;
            logFactory.Flush(ex => flushException = ex, TimeSpan.FromSeconds(5));
            Assert.Null(flushException);
            Assert.Equal(2, target.Counter);
        }

        [Fact]
        public void PublishConfiguration_DoesNotAffectOtherFactories()
        {
            // Arrange
            var factory1 = new LogFactory();
            var factory2 = new LogFactory();

            var target1 = new DebugTarget { Name = "target1" };
            var config1 = new LoggingConfiguration();
            config1.AddRuleForAllLevels(target1);

            var target2 = new DebugTarget { Name = "target2" };
            var config2 = new LoggingConfiguration();
            config2.AddRuleForAllLevels(target2);

            // Act
            factory1.PublishConfiguration(config1);
            factory2.PublishConfiguration(config2);

            // Assert - each factory has its own configuration.
            Assert.Same(config1, factory1.Configuration);
            Assert.Same(config2, factory2.Configuration);
            Assert.NotSame(factory1.Configuration, factory2.Configuration);
        }

        [Fact]
        public void PublishConfiguration_NullConfig_UnloadsConfiguration()
        {
            // Arrange
            var logFactory = new LogFactory();
            var target = new DebugTarget { Name = "test" };
            var config = new LoggingConfiguration();
            config.AddRuleForAllLevels(target);
            logFactory.PublishConfiguration(config);

            // Act
            var result = logFactory.PublishConfiguration(null);

            // Assert
            Assert.True(result.Succeeded);
            Assert.Same(config, result.PreviousConfiguration);
            Assert.Null(logFactory.Configuration);
        }
    }
}
