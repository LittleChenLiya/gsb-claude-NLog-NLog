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
    using System.Collections.Generic;
    using NLog.Layouts;
    using NLog.Targets;
    using Xunit;

    public class JsonLayoutEventPropertyNameCheckTests : NLogTestBase
    {
        /// <summary>
        /// Memory-fixture that counts how many logevents that reached the target
        /// </summary>
        [Target("CountingMemoryTarget")]
        private sealed class CountingMemoryTarget : TargetWithLayout
        {
            public int WriteCount { get; private set; }

            public List<string> RenderedOutput { get; } = new List<string>();

            protected override void Write(LogEventInfo logEvent)
            {
                ++WriteCount;
                RenderedOutput.Add(RenderLogEvent(Layout, logEvent));
            }
        }

        private static JsonLayout CreateJsonLayout(bool checkEventPropertyNames, bool? eventPropertyNameIgnoreCase)
        {
            var jsonLayout = new JsonLayout
            {
                IncludeEventProperties = true,
                SuppressSpaces = true,
                CheckEventPropertyNames = checkEventPropertyNames,
            };
            if (eventPropertyNameIgnoreCase.HasValue)
                jsonLayout.EventPropertyNameIgnoreCase = eventPropertyNameIgnoreCase.Value;

            jsonLayout.Attributes.Add(new JsonAttribute("Level", "${level}"));
            jsonLayout.Attributes.Add(new JsonAttribute("Message", "${message}"));
            return jsonLayout;
        }

        private static LogEventInfo CreateLogEvent(string name1, object value1, string name2, object value2, string name3, object value3)
        {
            var logEvent = new LogEventInfo(LogLevel.Info, "Test", "Hello");
            logEvent.Properties[name1] = value1;
            logEvent.Properties[name2] = value2;
            logEvent.Properties[name3] = value3;
            return logEvent;
        }

        private static CountingMemoryTarget CreateTarget(JsonLayout jsonLayout, out Logger logger)
        {
            var target = new CountingMemoryTarget { Layout = jsonLayout };
            logger = new LogFactory().Setup().LoadConfiguration(builder =>
            {
                builder.ForLogger().WriteTo(target);
            }).GetLogger("Test");
            return target;
        }

        [Fact]
        public void CaseSensitive_NoConflict_WritesStructuredOutput()
        {
            var target = CreateTarget(CreateJsonLayout(true, false), out var logger);

            var logEvent = CreateLogEvent("Alpha", 1, "Beta", 2, "Gamma", 3);
            logger.Log(logEvent);

            Assert.Equal(1, target.WriteCount);
            Assert.Equal("{\"Level\":\"Info\",\"Message\":\"Hello\",\"Alpha\":1,\"Beta\":2,\"Gamma\":3}", target.RenderedOutput[0]);
        }

        [Fact]
        public void CaseSensitive_ReservedNameConflict_RejectsEvent()
        {
            var target = CreateTarget(CreateJsonLayout(true, false), out var logger);

            var logEvent = CreateLogEvent("Message", "from-event", "Alpha", 1, "Beta", 2);
            logger.Log(logEvent);

            Assert.Equal(0, target.WriteCount); // Target not called
            Assert.Empty(target.RenderedOutput);
            Assert.Equal(3, logEvent.Properties.Count); // Original event dictionary not modified
            Assert.Equal("from-event", logEvent.Properties["Message"]);
            Assert.Equal(1, logEvent.Properties["Alpha"]);
            Assert.Equal(2, logEvent.Properties["Beta"]);
        }

        [Fact]
        public void CaseSensitive_DifferentCasing_WritesStructuredOutput()
        {
            var target = CreateTarget(CreateJsonLayout(true, false), out var logger);

            var logEvent = CreateLogEvent("message", "from-event", "Alpha", 1, "Beta", 2);
            logger.Log(logEvent);

            Assert.Equal(1, target.WriteCount);
            Assert.Equal("{\"Level\":\"Info\",\"Message\":\"Hello\",\"message\":\"from-event\",\"Alpha\":1,\"Beta\":2}", target.RenderedOutput[0]);
        }

        [Fact]
        public void CaseSensitive_MutualDifferentCasing_WritesStructuredOutput()
        {
            var target = CreateTarget(CreateJsonLayout(true, false), out var logger);

            var logEvent = CreateLogEvent("Alpha", 1, "alpha", 2, "Beta", 3);
            logger.Log(logEvent);

            Assert.Equal(1, target.WriteCount);
            Assert.Equal("{\"Level\":\"Info\",\"Message\":\"Hello\",\"Alpha\":1,\"alpha\":2,\"Beta\":3}", target.RenderedOutput[0]);
        }

        [Fact]
        public void IgnoreCase_ReservedNameConflict_RejectsEvent()
        {
            var target = CreateTarget(CreateJsonLayout(true, true), out var logger);

            var logEvent = CreateLogEvent("message", "from-event", "Alpha", 1, "Beta", 2);
            logger.Log(logEvent);

            Assert.Equal(0, target.WriteCount); // Target not called
            Assert.Empty(target.RenderedOutput);
            Assert.Equal(3, logEvent.Properties.Count); // Original event dictionary not modified
            Assert.Equal("from-event", logEvent.Properties["message"]);
            Assert.Equal(1, logEvent.Properties["Alpha"]);
            Assert.Equal(2, logEvent.Properties["Beta"]);
        }

        [Fact]
        public void IgnoreCase_MutualConflict_RejectsEvent()
        {
            var target = CreateTarget(CreateJsonLayout(true, true), out var logger);

            var logEvent = CreateLogEvent("Alpha", 1, "alpha", 2, "Beta", 3);
            logger.Log(logEvent);

            Assert.Equal(0, target.WriteCount); // Target not called
            Assert.Empty(target.RenderedOutput);
            Assert.Equal(3, logEvent.Properties.Count); // Original event dictionary not modified
            Assert.Equal(1, logEvent.Properties["Alpha"]);
            Assert.Equal(2, logEvent.Properties["alpha"]);
            Assert.Equal(3, logEvent.Properties["Beta"]);
        }

        [Fact]
        public void IgnoreCase_NoConflict_WritesStructuredOutput()
        {
            var target = CreateTarget(CreateJsonLayout(true, true), out var logger);

            var logEvent = CreateLogEvent("Alpha", 1, "Beta", 2, "Gamma", 3);
            logger.Log(logEvent);

            Assert.Equal(1, target.WriteCount);
            Assert.Equal("{\"Level\":\"Info\",\"Message\":\"Hello\",\"Alpha\":1,\"Beta\":2,\"Gamma\":3}", target.RenderedOutput[0]);
        }

        [Fact]
        public void CheckDisabled_ReservedNameConflict_KeepsOriginalBehavior()
        {
            var target = CreateTarget(CreateJsonLayout(false, null), out var logger);

            var logEvent = CreateLogEvent("Message", "from-event", "Alpha", 1, "Beta", 2);
            logger.Log(logEvent);

            Assert.Equal(1, target.WriteCount); // Original behavior, target called
            Assert.Equal("{\"Level\":\"Info\",\"Message\":\"Hello\",\"Message\":\"from-event\",\"Alpha\":1,\"Beta\":2}", target.RenderedOutput[0]);
        }

        [Fact]
        public void CheckEnabled_WithoutExplicitCaseStrategy_Throws()
        {
            var jsonLayout = CreateJsonLayout(true, null);
            var logEvent = CreateLogEvent("Alpha", 1, "Beta", 2, "Gamma", 3);

            Assert.Throws<NLogConfigurationException>(() => jsonLayout.ValidateEventPropertyNames(logEvent));

            var target = new CountingMemoryTarget { Layout = jsonLayout };
            var logFactory = new LogFactory { ThrowExceptions = true };
            Assert.Throws<NLogConfigurationException>(() => logFactory.Setup().LoadConfiguration(builder =>
            {
                builder.ForLogger().WriteTo(target);
            }));
        }
    }
}
