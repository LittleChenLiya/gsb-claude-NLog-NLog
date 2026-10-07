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

namespace NLog.UnitTests.Layouts
{
    using System;
    using NLog.Layouts;
    using NLog.Targets;
    using Xunit;

    public class JsonLayoutEventPropertyValidationTests : NLogTestBase
    {
        private static JsonLayout CreateJsonLayout(bool validateEventPropertyNames, StringComparison comparison = StringComparison.Ordinal)
        {
            return new JsonLayout
            {
                Attributes =
                {
                    new JsonAttribute("level", "${level}"),
                    new JsonAttribute("message", "${message}"),
                    new JsonAttribute("logger", "${logger}"),
                },
                IncludeEventProperties = true,
                ValidateEventPropertyNames = validateEventPropertyNames,
                EventPropertyNameComparison = comparison,
            };
        }

        private static LogEventInfo CreateLogEventWithThreeProperties(string thirdPropertyName, object thirdPropertyValue)
        {
            var logEvent = new LogEventInfo(LogLevel.Info, "TestLogger", "hello, world");
            logEvent.Properties["RequestId"] = 42;
            logEvent.Properties["User"] = "alice";
            logEvent.Properties[thirdPropertyName] = thirdPropertyValue;
            return logEvent;
        }

        private static Tuple<LogFactory, MemoryTarget> CreateLogFactory(JsonLayout jsonLayout)
        {
            var memoryTarget = new MemoryTarget("memory") { Layout = jsonLayout };
            var logFactory = new LogFactory().Setup().LoadConfiguration(builder =>
            {
                builder.ForLogger().WriteTo(memoryTarget);
            }).LogFactory;
            return Tuple.Create(logFactory, memoryTarget);
        }

        [Theory]
        [InlineData(StringComparison.Ordinal)]
        [InlineData(StringComparison.OrdinalIgnoreCase)]
        public void ReservedNameExactConflict_RejectsEvent_TargetNotCalled(StringComparison comparison)
        {
            var jsonLayout = CreateJsonLayout(true, comparison);
            var factory = CreateLogFactory(jsonLayout);
            var logFactory = factory.Item1;
            var memoryTarget = factory.Item2;

            var logEvent = CreateLogEventWithThreeProperties("message", "oops");    // Conflicts with reserved attribute name
            logFactory.GetLogger("TestLogger").Info(logEvent);

            Assert.Empty(memoryTarget.Logs);    // Target never called

            // Original event dictionary not modified
            Assert.Equal(3, logEvent.Properties.Count);
            Assert.Equal(42, logEvent.Properties["RequestId"]);
            Assert.Equal("alice", logEvent.Properties["User"]);
            Assert.Equal("oops", logEvent.Properties["message"]);
        }

        [Fact]
        public void ReservedNameCaseConflict_Ordinal_WritesStructuredOutput()
        {
            var jsonLayout = CreateJsonLayout(true, StringComparison.Ordinal);
            var factory = CreateLogFactory(jsonLayout);
            var logFactory = factory.Item1;
            var memoryTarget = factory.Item2;

            var logEvent = CreateLogEventWithThreeProperties("Message", "hello");   // No conflict when case-sensitive
            logFactory.GetLogger("TestLogger").Info(logEvent);

            var json = Assert.Single(memoryTarget.Logs);
            Assert.Contains("\"message\":\"hello, world\"", json);  // Reserved attribute
            Assert.Contains("\"Message\":\"hello\"", json);         // Event property
            Assert.Contains("\"RequestId\":42", json);
            Assert.Contains("\"User\":\"alice\"", json);
        }

        [Fact]
        public void ReservedNameCaseConflict_OrdinalIgnoreCase_RejectsEvent_TargetNotCalled()
        {
            var jsonLayout = CreateJsonLayout(true, StringComparison.OrdinalIgnoreCase);
            var factory = CreateLogFactory(jsonLayout);
            var logFactory = factory.Item1;
            var memoryTarget = factory.Item2;

            var logEvent = CreateLogEventWithThreeProperties("Message", "hello");   // Conflicts with reserved 'message'
            logFactory.GetLogger("TestLogger").Info(logEvent);

            Assert.Empty(memoryTarget.Logs);    // Target never called
            Assert.Equal(3, logEvent.Properties.Count); // Original event dictionary not modified
        }

        [Fact]
        public void MutualNameCaseConflict_Ordinal_WritesBothProperties()
        {
            var jsonLayout = CreateJsonLayout(true, StringComparison.Ordinal);
            var factory = CreateLogFactory(jsonLayout);
            var logFactory = factory.Item1;
            var memoryTarget = factory.Item2;

            var logEvent = CreateLogEventWithThreeProperties("user", "bob");    // 'User' + 'user' no conflict when case-sensitive
            logFactory.GetLogger("TestLogger").Info(logEvent);

            var json = Assert.Single(memoryTarget.Logs);
            Assert.Contains("\"User\":\"alice\"", json);
            Assert.Contains("\"user\":\"bob\"", json);
        }

        [Fact]
        public void MutualNameCaseConflict_OrdinalIgnoreCase_RejectsEvent_TargetNotCalled()
        {
            var jsonLayout = CreateJsonLayout(true, StringComparison.OrdinalIgnoreCase);
            var factory = CreateLogFactory(jsonLayout);
            var logFactory = factory.Item1;
            var memoryTarget = factory.Item2;

            var logEvent = CreateLogEventWithThreeProperties("user", "bob");    // 'User' + 'user' conflicts when case-insensitive
            logFactory.GetLogger("TestLogger").Info(logEvent);

            Assert.Empty(memoryTarget.Logs);    // Target never called
            Assert.Equal(3, logEvent.Properties.Count); // Original event dictionary not modified
        }

        [Theory]
        [InlineData(StringComparison.Ordinal)]
        [InlineData(StringComparison.OrdinalIgnoreCase)]
        public void NoConflict_WritesNormalStructuredOutput(StringComparison comparison)
        {
            var jsonLayout = CreateJsonLayout(true, comparison);
            var factory = CreateLogFactory(jsonLayout);
            var logFactory = factory.Item1;
            var memoryTarget = factory.Item2;

            var logEvent = CreateLogEventWithThreeProperties("Action", "login");
            logFactory.GetLogger("TestLogger").Info(logEvent);

            var json = Assert.Single(memoryTarget.Logs);
            Assert.Contains("\"level\":\"Info\"", json);
            Assert.Contains("\"message\":\"hello, world\"", json);
            Assert.Contains("\"logger\":\"TestLogger\"", json);
            Assert.Contains("\"RequestId\":42", json);
            Assert.Contains("\"User\":\"alice\"", json);
            Assert.Contains("\"Action\":\"login\"", json);
        }

        [Fact]
        public void ValidationDisabled_KeepsOriginalBehavior()
        {
            var jsonLayout = CreateJsonLayout(false, StringComparison.OrdinalIgnoreCase);
            var factory = CreateLogFactory(jsonLayout);
            var logFactory = factory.Item1;
            var memoryTarget = factory.Item2;

            var logEvent = CreateLogEventWithThreeProperties("message", "oops");    // Would conflict, but validation disabled
            logFactory.GetLogger("TestLogger").Info(logEvent);

            var json = Assert.Single(memoryTarget.Logs);    // Original behavior, duplicate keys in output
            Assert.Contains("\"message\":\"hello, world\"", json);
            Assert.Contains("\"message\":\"oops\"", json);

            // Original interface still works without target
            var directJson = jsonLayout.Render(logEvent);
            Assert.Contains("\"message\":\"oops\"", directJson);
        }

        [Fact]
        public void ValidateEventProperties_DoesNotModifyEventDictionary()
        {
            var jsonLayout = CreateJsonLayout(true, StringComparison.OrdinalIgnoreCase);
            var logEvent = CreateLogEventWithThreeProperties("Message", "hello");

            Assert.False(jsonLayout.ValidateEventProperties(logEvent));
            Assert.False(((ILogEventValidator)jsonLayout).ValidateLogEvent(logEvent));

            Assert.Equal(3, logEvent.Properties.Count);
            Assert.Equal(42, logEvent.Properties["RequestId"]);
            Assert.Equal("alice", logEvent.Properties["User"]);
            Assert.Equal("hello", logEvent.Properties["Message"]);

            var validEvent = CreateLogEventWithThreeProperties("Action", "login");
            Assert.True(jsonLayout.ValidateEventProperties(validEvent));
            Assert.Equal(3, validEvent.Properties.Count);
        }

        [Fact]
        public void BatchWrite_FiltersRejectedEvents()
        {
            var jsonLayout = CreateJsonLayout(true, StringComparison.OrdinalIgnoreCase);
            var factory = CreateLogFactory(jsonLayout);
            var memoryTarget = factory.Item2;

            var validEvent = CreateLogEventWithThreeProperties("Action", "login");
            var invalidEvent = CreateLogEventWithThreeProperties("Message", "hello");

            int continuationCount = 0;
            memoryTarget.WriteAsyncLogEvents(
                validEvent.WithContinuation(ex => { Assert.Null(ex); continuationCount++; }),
                invalidEvent.WithContinuation(ex => { Assert.Null(ex); continuationCount++; }));

            var json = Assert.Single(memoryTarget.Logs);    // Only valid event written
            Assert.Contains("\"Action\":\"login\"", json);
            Assert.Equal(2, continuationCount);             // Both continuations signaled
            Assert.Equal(3, invalidEvent.Properties.Count); // Original event dictionary not modified
        }
    }
}
